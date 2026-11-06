using System.Text;
using SiteCheck.Dns;

namespace SiteCheck.Core.Tests.Dns;

/// <summary>
/// Pins the wire format byte by byte, since a mistake here shows up only as a server that never
/// answers, or an answer read wrong.
/// </summary>
public sealed class DnsMessageTests
{
    [Fact]
    public void EncodeQuery_WritesTheHeaderTheQuestionAndAnEdnsRecord()
    {
        var query = DnsMessage.EncodeQuery(0xBEEF, "salon.test", DnsRecordType.Txt);

        byte[] expected =
        [
            0xBE, 0xEF, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, // header: id, RD, 1 question, 1 additional
            5, (byte)'s', (byte)'a', (byte)'l', (byte)'o', (byte)'n',
            4, (byte)'t', (byte)'e', (byte)'s', (byte)'t', 0,
            0x00, 0x10, 0x00, 0x01,                                                 // TXT, IN
            0x00, 0x00, 0x29, 0x04, 0xD0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,       // OPT, 1232 bytes
        ];

        Assert.Equal(expected, query);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a..test")]
    [InlineData("salón.test")]                      // not punycode
    public void EncodeQuery_RejectsWhatIsNotADnsName(string name) =>
        Assert.Throws<ArgumentException>(() => DnsMessage.EncodeQuery(1, name, DnsRecordType.Txt));

    [Fact]
    public void EncodeQuery_RejectsLabelsAndNamesThatAreTooLong()
    {
        Assert.Throws<ArgumentException>(() => DnsMessage.EncodeQuery(1, new string('a', 64) + ".test", DnsRecordType.Txt));
        Assert.Throws<ArgumentException>(() => DnsMessage.EncodeQuery(1, string.Join('.', Enumerable.Repeat(new string('a', 63), 4)) + ".test", DnsRecordType.Txt));
    }

    [Fact]
    public void Decode_ReadsTxtRecordsAndJoinsTheirParts()
    {
        // A long policy split into two strings is one record, read with nothing in between.
        var response = Response(0x1234, rcode: 0, DnsRecordType.Txt, Txt("v=spf1 include:a.test ", "-all"), Txt("other=1"));

        var decoded = DnsMessage.Decode(response, DnsRecordType.Txt);

        Assert.Equal(0x1234, decoded.Id);
        Assert.Equal(0, decoded.ResponseCode);
        Assert.False(decoded.Truncated);
        Assert.Equal(["v=spf1 include:a.test -all", "other=1"], decoded.Records);
    }

    [Fact]
    public void Decode_ReadsMxRecordsFollowingCompressedNames()
    {
        // mx1.salon.test, with "salon.test" pointing back at the question's name (offset 12).
        byte[] mx = [0x00, 0x0A, 3, (byte)'m', (byte)'x', (byte)'1', 0xC0, 12];

        var decoded = DnsMessage.Decode(Response(1, 0, DnsRecordType.Mx, mx), DnsRecordType.Mx);

        Assert.Equal(["mx1.salon.test"], decoded.Records);
    }

    [Fact]
    public void Decode_KeepsOnlyTheTypeAskedFor()
    {
        // A CNAME in the answer section, as a resolver sends on the way to the real record.
        byte[] cname = [3, (byte)'w', (byte)'w', (byte)'w', 0xC0, 12];
        var response = ResponseWith(1, 0, DnsRecordType.Txt, (5, cname), (16, Txt("v=DMARC1; p=reject")));

        Assert.Equal(["v=DMARC1; p=reject"], DnsMessage.Decode(response, DnsRecordType.Txt).Records);
    }

    [Fact]
    public void Decode_ReportsNoSuchNameAndTruncation()
    {
        var nxdomain = Response(1, rcode: 3, DnsRecordType.Txt);
        var truncated = Response(1, rcode: 0, DnsRecordType.Txt);
        truncated[2] |= 0x02;

        Assert.Equal(DnsMessage.ResponseCodeNameError, DnsMessage.Decode(nxdomain, DnsRecordType.Txt).ResponseCode);
        Assert.True(DnsMessage.Decode(truncated, DnsRecordType.Txt).Truncated);
    }

    [Fact]
    public void Decode_RejectsWhatIsNotAResponse()
    {
        Assert.Throws<FormatException>(() => DnsMessage.Decode(new byte[5], DnsRecordType.Txt));
        Assert.Throws<FormatException>(() => DnsMessage.Decode(DnsMessage.EncodeQuery(1, "salon.test", DnsRecordType.Txt), DnsRecordType.Txt));
    }

    [Fact]
    public void Decode_RejectsATruncatedRecord()
    {
        var response = Response(1, 0, DnsRecordType.Txt, Txt("v=spf1 -all"));

        Assert.Throws<FormatException>(() => DnsMessage.Decode(response.AsSpan(0, response.Length - 3), DnsRecordType.Txt));
    }

    [Fact]
    public void Decode_RejectsANameThatPointsInACircle()
    {
        // An MX whose name points at itself.
        var response = Response(1, 0, DnsRecordType.Mx, new byte[] { 0x00, 0x0A, 0xC0, 0x00 });
        var mxNameOffset = response.Length - 2;
        response[^2] = (byte)(0xC0 | (mxNameOffset >> 8));
        response[^1] = (byte)mxNameOffset;

        Assert.Throws<FormatException>(() => DnsMessage.Decode(response, DnsRecordType.Mx));
    }

    private static byte[] Txt(params string[] parts) =>
        [.. parts.SelectMany(part => new[] { (byte)part.Length }.Concat(Encoding.ASCII.GetBytes(part)))];

    private static byte[] Response(ushort id, int rcode, DnsRecordType type, params byte[][] rdatas) =>
        ResponseWith(id, rcode, type, [.. rdatas.Select(rdata => ((ushort)type, rdata))]);

    /// <summary>A response to "salon.test", with one answer per (type, rdata), names compressed to the question.</summary>
    private static byte[] ResponseWith(ushort id, int rcode, DnsRecordType asked, params (ushort Type, byte[] Data)[] answers)
    {
        var message = new List<byte>
        {
            (byte)(id >> 8), (byte)id, 0x81, (byte)(0x80 | rcode),
            0, 1, 0, (byte)answers.Length, 0, 0, 0, 0,
        };

        message.AddRange([5, .. "salon"u8.ToArray(), 4, .. "test"u8.ToArray(), 0, 0, (byte)asked, 0, 1]);

        foreach (var (type, data) in answers)
        {
            message.AddRange([0xC0, 12, (byte)(type >> 8), (byte)type, 0, 1, 0, 0, 0x0E, 0x10, (byte)(data.Length >> 8), (byte)data.Length]);
            message.AddRange(data);
        }

        return [.. message];
    }
}
