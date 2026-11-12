using System.Net;
using SiteCheck.Checks;
using SiteCheck.Core.Tests.TestDoubles;

namespace SiteCheck.Core.Tests.Checks;

public sealed class SecurityHeadersCheckTests
{
    private static readonly Uri Site = new("https://salon.test/");

    private const string Hsts = "Strict-Transport-Security: max-age=31536000; includeSubDomains";
    private const string NoSniff = "X-Content-Type-Options: nosniff";
    private const string NoFraming = "X-Frame-Options: SAMEORIGIN";

    /// <summary>Serves a page with exactly <paramref name="headers"/>, each written as <c>Name: value</c>.</summary>
    private static async Task<CheckOutcome> RunWith(params string[] headers)
    {
        var handler = StubHttpMessageHandler.Routing(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html></html>") };

            foreach (var header in headers)
            {
                var colon = header.IndexOf(':', StringComparison.Ordinal);
                response.Headers.TryAddWithoutValidation(header[..colon], header[(colon + 1)..].Trim());
            }

            return response;
        });

        using var client = new HttpClient(handler);
        return await new SecurityHeadersCheck(client).RunAsync(Site, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RunAsync_WithTheThreeBasicProtections_Passes_AndSaysWhatWouldComeNext()
    {
        var outcome = await RunWith(Hsts, NoSniff, NoFraming);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Contains("Content-Security-Policy", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("Referrer-Policy", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WithEverything_PassesWithNothingLeftToSuggest()
    {
        var outcome = await RunWith(
            Hsts, NoSniff, "Content-Security-Policy: default-src 'self'; frame-ancestors 'none'", "Referrer-Policy: strict-origin-when-cross-origin");

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.DoesNotContain("next step", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WithNoProtectionsAtAll_WarnsAboutAllThree_AndNeverFails()
    {
        var outcome = await RunWith();

        // Nothing is broken for a visitor today, so this must not turn a watch run red as a defect.
        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("3 of the 3", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("Strict-Transport-Security", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("nosniff", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("X-Frame-Options", outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { NoSniff, NoFraming }, "Strict-Transport-Security")]
    [InlineData(new[] { Hsts, NoFraming }, "nosniff")]
    [InlineData(new[] { Hsts, NoSniff }, "X-Frame-Options")]
    public async Task RunAsync_WithOneMissing_WarnsAboutThatOneOnly(string[] present, string named)
    {
        var outcome = await RunWith(present);

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("1 of the 3", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains(named, outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Strict-Transport-Security: max-age=0")]            // how a site switches HSTS off
    [InlineData("Strict-Transport-Security: includeSubDomains")]    // no lifetime, so browsers ignore it
    [InlineData("Strict-Transport-Security: max-age=soon")]
    public async Task RunAsync_WithAnHstsHeaderThatProtectsNothing_CountsItAsMissing(string hsts)
    {
        var outcome = await RunWith(hsts, NoSniff, NoFraming);

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("Strict-Transport-Security", outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("strict-transport-security: MAX-AGE=600")]
    [InlineData("Strict-Transport-Security: includeSubDomains; max-age=\"31536000\"; preload")]
    public async Task RunAsync_ReadsHstsHoweverItIsWritten(string hsts)
    {
        var outcome = await RunWith(hsts, NoSniff, NoFraming);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
    }

    [Theory]
    [InlineData("X-Frame-Options: DENY")]
    [InlineData("X-Frame-Options: sameorigin")]
    [InlineData("Content-Security-Policy: frame-ancestors 'self'")]
    [InlineData("Content-Security-Policy: default-src 'self'; FRAME-ANCESTORS 'none'")]
    public async Task RunAsync_AcceptsEitherWayOfRefusingToBeFramed(string header)
    {
        var outcome = await RunWith(Hsts, NoSniff, header);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
    }

    [Theory]
    [InlineData("X-Frame-Options: ALLOW-FROM https://partner.test")] // browsers ignore it now
    [InlineData("Content-Security-Policy: frame-ancestors *")]        // allows everyone
    [InlineData("Content-Security-Policy: default-src 'self'")]       // a policy, but not about frames
    public async Task RunAsync_WhenFramingIsStillOpen_Warns(string header)
    {
        var outcome = await RunWith(Hsts, NoSniff, header);

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("X-Frame-Options", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenNosniffIsMisspelt_CountsItAsMissing()
    {
        var outcome = await RunWith(Hsts, "X-Content-Type-Options: no-sniff", NoFraming);

        Assert.Equal(CheckStatus.Warn, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_WhenThePageIsServedOverPlainHttp_Skips()
    {
        using var client = new HttpClient(StubHttpMessageHandler.Serving("<html></html>"));

        var outcome = await new SecurityHeadersCheck(client).RunAsync(new Uri("http://salon.test/"), TestContext.Current.CancellationToken);

        Assert.Equal(CheckStatus.Skip, outcome.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, CheckStatus.Skip)]            // a robot shield, not a finding
    [InlineData(HttpStatusCode.InternalServerError, CheckStatus.Error)] // load-time reports the defect
    public async Task RunAsync_WhenThePageCannotBeRead_SaysSoWithoutBlamingTheHeaders(HttpStatusCode status, CheckStatus expected)
    {
        using var client = new HttpClient(StubHttpMessageHandler.Serving("nope", status));

        var outcome = await new SecurityHeadersCheck(client).RunAsync(Site, TestContext.Current.CancellationToken);

        Assert.Equal(expected, outcome.Status);
        Assert.Equal((int)status, outcome.StatusCode);
    }
}
