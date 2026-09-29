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
}
