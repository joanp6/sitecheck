using SiteCheck.Checks;
using SiteCheck.Core.Tests.TestDoubles;

namespace SiteCheck.Core.Tests.Checks;

public sealed class CookiesCheckTests
{
    private static async Task<CheckOutcome> RunWith(string[] cookies, string url = "https://salon.test/")
    {
        using var client = new HttpClient(PageWith.Headers("<html></html>", [.. cookies.Select(cookie => "Set-Cookie: " + cookie)]));
        return await new CookiesCheck(client).RunAsync(new Uri(url), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RunAsync_WhenThePageSetsNoCookies_Passes_SayingWhatItCannotSee()
    {
        var outcome = await RunWith([]);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Contains("scripts", outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("PHPSESSID=abc; path=/; Secure; HttpOnly")]
    [InlineData("PHPSESSID=abc; secure; httponly; SameSite=Lax")] // attributes are case-insensitive
    [InlineData("lang=ca; Secure")]                               // a preference: scripts may read it
    [InlineData("cookie_consent=yes; Secure; SameSite=Strict")]
    [InlineData("csrftoken=abc; Secure")]                         // meant to be read by the page
    [InlineData("XSRF-TOKEN=abc; Secure")]
    [InlineData("widget=1; Secure; SameSite=None")]
    public async Task RunAsync_WhenCookiesAreGuarded_Passes(string cookie)
    {
        var outcome = await RunWith([cookie]);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
    }

    [Theory]
    [InlineData("lang=ca; path=/")]
    [InlineData("PHPSESSID=abc; HttpOnly")]
    public async Task RunAsync_WhenACookieIsNotSecure_Warns_NamingIt(string cookie)
    {
        var outcome = await RunWith([cookie]);

        // An unlocked door, not a break-in.
        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("not marked Secure", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains(cookie[..cookie.IndexOf('=', StringComparison.Ordinal)], outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("PHPSESSID")]
    [InlineData("JSESSIONID")]
    [InlineData("ASP.NET_SessionId")]
    [InlineData("wordpress_logged_in_5f2c")]
    [InlineData("connect.sid")]
    [InlineData("auth_token")]
    [InlineData(".AspNetCore.Identity.Application")]
    public async Task RunAsync_WhenALoginCookieCanBeReadByScripts_Warns(string name)
    {
        var outcome = await RunWith([$"{name}=abc; Secure"]);

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("HttpOnly", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenSameSiteNoneComesWithoutSecure_SaysBrowsersThrowItAway()
    {
        var outcome = await RunWith(["widget=1; SameSite=None"]);

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("throw it away", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_NeverPutsACookiesValueInTheReport()
    {
        // The value may be someone's session, and the report gets emailed around.
        var outcome = await RunWith(["PHPSESSID=s3cr3t-session-value; path=/"]);

        Assert.DoesNotContain("s3cr3t-session-value", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WithManyLooseCookies_ListsAFewAndCountsTheRest()
    {
        var outcome = await RunWith([.. Enumerable.Range(0, 9).Select(i => $"pref{i}=x")]);

        Assert.Contains("and 5 more", outcome.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("pref8", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_IgnoresALineThatIsNotACookie()
    {
        var outcome = await RunWith(["garbage-without-equals", "lang=ca; Secure"]);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Contains("1 cookie", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenThePageIsServedOverPlainHttp_Skips()
    {
        var outcome = await RunWith(["PHPSESSID=abc"], "http://salon.test/");

        Assert.Equal(CheckStatus.Skip, outcome.Status);
    }
}
