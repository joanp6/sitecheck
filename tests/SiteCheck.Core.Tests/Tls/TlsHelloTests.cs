using System.Buffers.Binary;
using System.Text;
using SiteCheck.Tls;

namespace SiteCheck.Core.Tests.Tls;

public sealed class TlsHelloTests
{
    [Theory]
    [InlineData(LegacyTlsVersion.Tls10, 0x03, 0x01)]
    [InlineData(LegacyTlsVersion.Tls11, 0x03, 0x02)]
    public void ClientHello_OffersExactlyTheVersionAsked(LegacyTlsVersion version, byte major, byte minor)
    {
        var hello = TlsHello.ClientHello("salon.test", version);

        // Record: a handshake, at the record layer's lowest version, whatever is offered inside.
        Assert.Equal([0x16, 0x03, 0x01], hello[..3]);

        // Handshake: a ClientHello, whose first field is the version on offer.
        Assert.Equal(0x01, hello[5]);
        Assert.Equal([major, minor], hello[9..11]);
    }

    [Fact]
    public void ClientHello_DeclaresLengthsThatMatchWhatItSends()
    {
        // A wrong length is the classic way to write a hello every server hangs up on, which
        // the check would then read as "refuses old TLS" for every site there is.
        var hello = TlsHello.ClientHello("salon.test", LegacyTlsVersion.Tls10);

        var recordLength = BinaryPrimitives.ReadUInt16BigEndian(hello.AsSpan(3));
        var handshakeLength = (hello[6] << 16) | BinaryPrimitives.ReadUInt16BigEndian(hello.AsSpan(7));

        Assert.Equal(hello.Length - 5, recordLength);
        Assert.Equal(hello.Length - 9, handshakeLength);

        // version (2) + random (32) + empty session id (1), then the cipher suites.
        var suitesLength = BinaryPrimitives.ReadUInt16BigEndian(hello.AsSpan(9 + 35));
        var afterSuites = 9 + 35 + 2 + suitesLength;

        Assert.Equal(0, suitesLength % 2);
        Assert.Equal([1, 0], hello[afterSuites..(afterSuites + 2)]); // one compression method: none

        var extensionsLength = BinaryPrimitives.ReadUInt16BigEndian(hello.AsSpan(afterSuites + 2));

        Assert.Equal(hello.Length, afterSuites + 4 + extensionsLength);
    }

    [Fact]
    public void ClientHello_NamesTheHostItWants()
    {
        // Without it a server hosting many sites answers as its default one, or not at all.
        var hello = TlsHello.ClientHello("salon.test", LegacyTlsVersion.Tls10);

        Assert.Contains("salon.test", Encoding.ASCII.GetString(hello), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("203.0.113.7")]
    [InlineData("[2001:db8::1]")]
    public void ClientHello_ForAnIpAddress_NamesNoHost(string address)
    {
        // The standard forbids an address in the server-name field.
        var hello = TlsHello.ClientHello(address, LegacyTlsVersion.Tls10);

        Assert.DoesNotContain(address.Trim('[', ']'), Encoding.ASCII.GetString(hello), StringComparison.Ordinal);
    }

    [Fact]
    public void ClientHello_UsesFreshRandomBytesEachTime()
    {
        var first = TlsHello.ClientHello("salon.test", LegacyTlsVersion.Tls10);
        var second = TlsHello.ClientHello("salon.test", LegacyTlsVersion.Tls10);

        Assert.NotEqual(first[11..43], second[11..43]);
    }

    /// <summary>The start of a ServerHello choosing <paramref name="major"/>.<paramref name="minor"/>.</summary>
    private static byte[] ServerHello(byte major, byte minor) =>
        [0x16, major, minor, 0x00, 0x51, 0x02, 0x00, 0x00, 0x4D, major, minor];

    [Theory]
    [InlineData(LegacyTlsVersion.Tls10, 0x01)]
    [InlineData(LegacyTlsVersion.Tls11, 0x02)]
    public void Accepts_WhenTheServerChoosesTheVersionOffered_IsTrue(LegacyTlsVersion offered, byte minor)
    {
        Assert.True(TlsHello.Accepts(ServerHello(0x03, minor), offered));
    }

    [Fact]
    public void Accepts_WhenTheServerAnswersWithADifferentVersion_IsFalse()
    {
        // Offered 1.1 and the server came down to 1.0: that is an answer about 1.0, not 1.1.
        Assert.False(TlsHello.Accepts(ServerHello(0x03, 0x01), LegacyTlsVersion.Tls11));
    }

    [Fact]
    public void Accepts_WhenTheServerSendsAnAlert_IsFalse()
    {
        // A fatal protocol_version alert: the polite way of saying no.
        byte[] alert = [0x15, 0x03, 0x01, 0x00, 0x02, 0x02, 0x46];

        Assert.False(TlsHello.Accepts(alert, LegacyTlsVersion.Tls10));
    }

    [Theory]
    [InlineData(0)]  // hung up without a word
    [InlineData(5)]  // a record header and nothing in it
    [InlineData(10)] // one byte short of the version
    public void Accepts_WhenTheReplyIsCutShort_IsFalse(int length)
    {
        Assert.False(TlsHello.Accepts(ServerHello(0x03, 0x01).AsSpan(0, length), LegacyTlsVersion.Tls10));
    }

    [Fact]
    public void Accepts_WhenTheReplyIsNotTlsAtAll_IsFalse()
    {
        // A plain web server on that port answers in HTTP.
        Assert.False(TlsHello.Accepts("HTTP/1.1 400 Bad Request"u8, LegacyTlsVersion.Tls10));
    }

    [Fact]
    public void Accepts_WhenTheHandshakeMessageIsNotAServerHello_IsFalse()
    {
        var reply = ServerHello(0x03, 0x01);
        reply[5] = 0x0B; // a certificate message

        Assert.False(TlsHello.Accepts(reply, LegacyTlsVersion.Tls10));
    }
}
