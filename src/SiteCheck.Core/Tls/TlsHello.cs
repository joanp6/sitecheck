using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SiteCheck.Tls;

/// <summary>The retired versions of TLS, by the number each uses on the wire.</summary>
public enum LegacyTlsVersion
{
    /// <summary>TLS 1.0, from 1999.</summary>
    Tls10 = 0x0301,

    /// <summary>TLS 1.1, from 2006.</summary>
    Tls11 = 0x0302,
}

/// <summary>
/// Writes the first message of a TLS handshake offering one old version, and reads whether the
/// server took it.
/// </summary>
/// <remarks>
/// <para>
/// Why by hand, when .NET has <see cref="System.Net.Security.SslStream"/>: because the question
/// is "would this server still talk TLS 1.0?", and a current Windows or Linux will not let a
/// program even offer TLS 1.0. Asked through the operating system, every server would look
/// modern on a modern machine, and the check would pass sites for the wrong reason, differently
/// on each platform. Writing the bytes here gives the same answer everywhere.
/// </para>
/// <para>
/// Only the opening exchange is needed. A client begins with a ClientHello naming the highest
/// version it speaks; the server answers with a ServerHello naming the version it chose, or
/// with an alert, or by hanging up. This offers exactly one old version, so a ServerHello that
/// names it means the server accepts it. No keys are exchanged and nothing is encrypted: the
/// conversation is dropped after the first reply. (RFC 2246 and RFC 4346, section 7.4.1.)
/// </para>
/// </remarks>
public static class TlsHello
{
    /// <summary>Record header (5) + handshake header (4) + the version the server chose (2).</summary>
    public const int ReplyBytesNeeded = 11;

    private const byte HandshakeRecord = 0x16;
    private const byte ClientHelloMessage = 0x01;
    private const byte ServerHelloMessage = 0x02;

    /// <summary>
    /// The cipher suites servers of that era accept, most common first. A server that still
    /// speaks TLS 1.0 but shares none of these would be reported as refusing it; in practice
    /// every one of them supports at least AES-CBC with RSA or ECDHE.
    /// </summary>
    private static readonly ushort[] CipherSuites =
    [
        0xC014, 0xC013, // ECDHE-RSA with AES-256 and AES-128
        0xC00A, 0xC009, // ECDHE-ECDSA with AES-256 and AES-128
        0x0039, 0x0033, // DHE-RSA with AES-256 and AES-128
        0x0035, 0x002F, // RSA with AES-256 and AES-128
        0xC012, 0x000A, // 3DES, with ECDHE-RSA and with RSA
        0x0005,         // RC4
        0x00FF,         // not a cipher: tells the server this client knows about safe renegotiation
    ];

    /// <summary>The bytes to send first: a ClientHello offering <paramref name="version"/> and nothing newer.</summary>
    /// <param name="host">The name asked for (SNI), so a server hosting many sites answers as the right one. An IP address is left out.</param>
    /// <param name="version">The one version offered.</param>
    public static byte[] ClientHello(string host, LegacyTlsVersion version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        var body = new List<byte>(128);

        AddUInt16(body, (ushort)version);
        body.AddRange(RandomNumberGenerator.GetBytes(32));
        body.Add(0); // no session to resume

        AddUInt16(body, (ushort)(CipherSuites.Length * 2));

        foreach (var suite in CipherSuites)
        {
            AddUInt16(body, suite);
        }

        body.Add(1); // one compression method...
        body.Add(0); // ...which is "none"

        var extensions = new List<byte>(64);

        if (!System.Net.IPAddress.TryParse(host.Trim('[', ']'), out _))
        {
            var name = Encoding.ASCII.GetBytes(host);

            AddUInt16(extensions, 0x0000);                      // server_name
            AddUInt16(extensions, (ushort)(name.Length + 5));
            AddUInt16(extensions, (ushort)(name.Length + 3));
            extensions.Add(0);                                  // a host name
            AddUInt16(extensions, (ushort)name.Length);
            extensions.AddRange(name);
        }

        // Without these two a server has no curve to offer for the ECDHE suites above.
        extensions.AddRange([0x00, 0x0A, 0x00, 0x08, 0x00, 0x06, 0x00, 0x1D, 0x00, 0x17, 0x00, 0x18]); // x25519, P-256, P-384
        extensions.AddRange([0x00, 0x0B, 0x00, 0x02, 0x01, 0x00]);                                     // uncompressed points

        AddUInt16(body, (ushort)extensions.Count);
        body.AddRange(extensions);

        var message = new byte[5 + 4 + body.Count];

        message[0] = HandshakeRecord;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(1), (ushort)LegacyTlsVersion.Tls10); // the record layer's own, lowest, version
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(3), (ushort)(4 + body.Count));
        message[5] = ClientHelloMessage;
        message[6] = 0; // a handshake length is three bytes; this one fits in two
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(7), (ushort)body.Count);
        body.CopyTo(message, 9);

        return message;
    }

    /// <summary>
    /// Whether the start of the server's reply is a ServerHello choosing <paramref name="offered"/>.
    /// </summary>
    /// <param name="reply">
    /// The first bytes the server sent, up to <see cref="ReplyBytesNeeded"/>. Fewer, an alert, or
    /// anything else is a refusal: a server that will not speak the version says so in different
    /// ways, and all of them are "no".
    /// </param>
    /// <param name="offered">The version the ClientHello offered.</param>
    public static bool Accepts(ReadOnlySpan<byte> reply, LegacyTlsVersion offered) =>
        reply.Length >= ReplyBytesNeeded
        && reply[0] == HandshakeRecord
        && reply[5] == ServerHelloMessage
        && BinaryPrimitives.ReadUInt16BigEndian(reply[9..]) == (ushort)offered;

    private static void AddUInt16(List<byte> bytes, ushort value)
    {
        bytes.Add((byte)(value >> 8));
        bytes.Add((byte)value);
    }
}
