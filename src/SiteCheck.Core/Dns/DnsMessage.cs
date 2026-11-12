using System.Net;
using System.Buffers.Binary;
using System.Text;

namespace SiteCheck.Dns;

/// <summary>A DNS response, reduced to what sitecheck reads from it.</summary>
/// <param name="Id">The query identifier it answers.</param>
/// <param name="Truncated">The answer did not fit in UDP and must be asked again over TCP.</param>
/// <param name="ResponseCode">0 for an answer, 3 for "no such name", anything else a failure.</param>
/// <param name="Records">The records of the type asked for; see <see cref="DnsAnswer.Records"/>.</param>
public sealed record DnsResponse(ushort Id, bool Truncated, int ResponseCode, IReadOnlyList<string> Records);

/// <summary>
/// Writes DNS queries and reads DNS responses, in the wire format of RFC 1035.
/// </summary>
/// <remarks>
/// Only what two record types need: one question, recursion asked for, and EDNS(0) advertising
/// 1232-byte answers, so that a domain with many TXT records still fits in one UDP packet. Names
/// are expected in their ASCII form (punycode for international ones), as <see cref="Uri.IdnHost"/>
/// gives them. Public so that the encoding can be tested byte by byte without a network.
/// </remarks>
public static class DnsMessage
{
    public const int ResponseCodeNoError = 0;
    public const int ResponseCodeNameError = 3;

    private const ushort EdnsPayloadSize = 1232;
    private const int MaxCompressionJumps = 64;

    /// <exception cref="ArgumentException">The name is not a valid DNS name.</exception>
    public static byte[] EncodeQuery(ushort id, string name, DnsRecordType type)
    {
        ArgumentNullException.ThrowIfNull(name);

        var labels = name.TrimEnd('.').Split('.');

        if (labels.Any(label => label.Length is 0 or > 63 || label.Any(c => c > 127)))
        {
            throw new ArgumentException($"\"{name}\" is not a DNS name in ASCII form.", nameof(name));
        }

        // On the wire each label carries a length byte, and the root adds one more.
        if (labels.Sum(label => label.Length + 1) + 1 > 255)
        {
            throw new ArgumentException($"\"{name}\" is longer than a DNS name may be.", nameof(name));
        }

        using var buffer = new MemoryStream();

        Write16(buffer, id);
        Write16(buffer, 0x0100); // a standard query, recursion desired
        Write16(buffer, 1);      // one question
        Write16(buffer, 0);
        Write16(buffer, 0);
        Write16(buffer, 1);      // one additional record: the EDNS(0) OPT

        foreach (var label in labels)
        {
            buffer.WriteByte((byte)label.Length);
            buffer.Write(Encoding.ASCII.GetBytes(label));
        }

        buffer.WriteByte(0);
        Write16(buffer, (ushort)type);
        Write16(buffer, 1);      // class IN

        // OPT pseudo-record: root name, type 41, the payload size in the class field, no options.
        buffer.WriteByte(0);
        Write16(buffer, 41);
        Write16(buffer, EdnsPayloadSize);
        Write16(buffer, 0);
        Write16(buffer, 0);
        Write16(buffer, 0);

        return buffer.ToArray();
    }

    /// <exception cref="FormatException">The bytes are not a DNS response.</exception>
    public static DnsResponse Decode(ReadOnlySpan<byte> message, DnsRecordType type)
    {
        try
        {
            return DecodeUnchecked(message, type);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            throw new FormatException("The DNS response ends in the middle of a record.", ex);
        }
    }

    private static DnsResponse DecodeUnchecked(ReadOnlySpan<byte> message, DnsRecordType type)
    {
        if (message.Length < 12)
        {
            throw new FormatException("The DNS response is shorter than its header.");
        }

        var id = BinaryPrimitives.ReadUInt16BigEndian(message);
        var flags = BinaryPrimitives.ReadUInt16BigEndian(message[2..]);

        if ((flags & 0x8000) == 0)
        {
            throw new FormatException("The DNS message is a query, not a response.");
        }

        var truncated = (flags & 0x0200) != 0;
        var responseCode = flags & 0x000F;
        var questions = BinaryPrimitives.ReadUInt16BigEndian(message[4..]);
        var answers = BinaryPrimitives.ReadUInt16BigEndian(message[6..]);

        var offset = 12;

        for (var i = 0; i < questions; i++)
        {
            SkipName(message, ref offset);
            offset += 4;
        }

        var records = new List<string>();

        for (var i = 0; i < answers; i++)
        {
            SkipName(message, ref offset);

            var recordType = BinaryPrimitives.ReadUInt16BigEndian(message[offset..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(message[(offset + 8)..]);
            var data = offset + 10;

            if (data + length > message.Length)
            {
                throw new FormatException("A DNS record claims more data than the response holds.");
            }

            // Answers can include the CNAME chain that led here; only the asked-for type counts.
            if (recordType == (ushort)type)
            {
                records.Add(type switch
                {
                    DnsRecordType.Txt => ReadText(message.Slice(data, length)),
                    DnsRecordType.Mx => ReadName(message, data + 2),
                    DnsRecordType.Cname => ReadName(message, data),
                    DnsRecordType.A when length == 4 => new IPAddress(message.Slice(data, length)).ToString(),
                    DnsRecordType.Aaaa when length == 16 => new IPAddress(message.Slice(data, length)).ToString(),
                    DnsRecordType.A or DnsRecordType.Aaaa => throw new FormatException("A DNS address record is not the size of an address."),
                    _ => throw new FormatException($"Record type {type} is not one sitecheck reads."),
                });
            }

            offset = data + length;
        }

        return new DnsResponse(id, truncated, responseCode, records);
    }

    // A TXT record is one or more length-prefixed strings. SPF (RFC 7208, 3.3) and DMARC read
    // them joined with nothing in between, which is how long policies are split to fit.
    private static string ReadText(ReadOnlySpan<byte> data)
    {
        var text = new StringBuilder();
        var offset = 0;

        while (offset < data.Length)
        {
            var length = data[offset];

            if (offset + 1 + length > data.Length)
            {
                throw new FormatException("A TXT string runs past the end of its record.");
            }

            text.Append(Encoding.UTF8.GetString(data.Slice(offset + 1, length)));
            offset += 1 + length;
        }

        return text.ToString();
    }

    private static void SkipName(ReadOnlySpan<byte> message, ref int offset)
    {
        while (true)
        {
            var length = message[offset];

            if (length == 0)
            {
                offset++;
                return;
            }

            if ((length & 0xC0) == 0xC0)
            {
                offset += 2; // a pointer ends the name
                return;
            }

            offset += 1 + length;
        }
    }

    private static string ReadName(ReadOnlySpan<byte> message, int offset)
    {
        var labels = new List<string>();

        for (var jumps = 0; ; )
        {
            var length = message[offset];

            if (length == 0)
            {
                return string.Join('.', labels);
            }

            if ((length & 0xC0) == 0xC0)
            {
                // Compression: the rest of the name is elsewhere in the message. A hostile answer
                // can point in a circle, hence the limit.
                if (++jumps > MaxCompressionJumps)
                {
                    throw new FormatException("A name in the DNS response points in a circle.");
                }

                offset = BinaryPrimitives.ReadUInt16BigEndian(message[offset..]) & 0x3FFF;
                continue;
            }

            labels.Add(Encoding.ASCII.GetString(message.Slice(offset + 1, length)));
            offset += 1 + length;
        }
    }

    private static void Write16(Stream stream, ushort value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }
}
