using SiteCheck.Reporting;

namespace SiteCheck.Core.Tests.Reporting;

public sealed class SiteUrlTests
{
    [Theory]
    [InlineData("example.com", "https://example.com/")]
    [InlineData("  example.com  ", "https://example.com/")]
    [InlineData("www.example.com/menu", "https://www.example.com/menu")]
    [InlineData("https://example.com", "https://example.com/")]
    [InlineData("http://example.com/", "http://example.com/")]
    [InlineData("example.com:8443", "https://example.com:8443/")]
    public void TryParse_AcceptsAddressesASiteOwnerWouldType(string input, string expected)
    {
        Assert.True(SiteUrl.TryParse(input, out var url));
        Assert.Equal(expected, url.AbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://example.com/")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://")]
    [InlineData("not a url")]
    public void TryParse_RejectsInputNoCheckCanRunAgainst(string? input) =>
        Assert.False(SiteUrl.TryParse(input, out _));

    [Theory]
    [InlineData("--no-rety")]          // a mistyped option, which used to be audited as a site
    [InlineData("-o")]
    [InlineData("--crawl=5")]
    [InlineData("https://--nope/")]
    [InlineData("-salon.es")]
    [InlineData("salon-.es")]
    [InlineData("www.-salon.es")]
    public void TryParse_RejectsAHostThatStartsOrEndsWithAHyphen(string input) =>
        Assert.False(SiteUrl.TryParse(input, out _));

    [Theory]
    [InlineData("perruqueria-angela.pages.dev")]   // hyphens inside a name are ordinary
    [InlineData("xn--perruqueria-ngela-9ub.es")]   // and punycode is full of them
    [InlineData("a-b-c.example.com")]
    [InlineData("https://[::1]:8080/")]
    [InlineData("203.0.113.7")]
    public void TryParse_StillAcceptsHostsWithHyphensInside(string input) =>
        Assert.True(SiteUrl.TryParse(input, out _));
}
