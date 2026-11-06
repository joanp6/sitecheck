using System.Net;
using Microsoft.Extensions.Time.Testing;
using SiteCheck.Checks;
using SiteCheck.Core.Tests.TestDoubles;

namespace SiteCheck.Core.Tests.Checks;

public sealed class CopyrightYearCheckTests
{
    private static readonly Uri Site = new("https://salon.test/");

    // A fixed "today", so the tests mean the same next year.
    private static readonly DateTimeOffset Now = new(2026, 3, 15, 10, 0, 0, TimeSpan.Zero);

    private static async Task<CheckOutcome> Run(StubHttpMessageHandler handler)
    {
        using var client = new HttpClient(handler);
        return await new CopyrightYearCheck(client, new FakeTimeProvider(Now)).RunAsync(Site, TestContext.Current.CancellationToken);
    }

    private static Task<CheckOutcome> RunAgainst(string footer, HttpStatusCode status = HttpStatusCode.OK) =>
        Run(StubHttpMessageHandler.Serving(
            $"<!doctype html><html><head><title>Salon</title></head><body><main>Welcome</main><footer>{footer}</footer></body></html>",
            status));

    [Theory]
    [InlineData("© 2026 Salon Test", 2026)]
    [InlineData("© 2025 Salon Test", 2025)]
    [InlineData("&copy; 2024 Salon Test. Todos los derechos reservados.", 2024)]  // two years behind: still fine
    [InlineData("© 2012–2026 Salon Test", 2026)]                                  // a range: the later year counts
    [InlineData("© 2012 - 2025", 2025)]
    [InlineData("Copyright © Salon Test 2026", 2026)]                             // the name between mark and year
    [InlineData("2026 © Salon Test", 2026)]                                       // the year first
    [InlineData("<p>© 2015 Old Name</p><p>© 2026 Salon Test</p>", 2026)]           // several notices: the latest
    [InlineData("<span>©</span> <span>2026</span> Salon Test", 2026)]               // split across tags
    public async Task RunAsync_WhenTheYearIsRecent_PassesAndSaysIt(string footer, int year)
    {
        var outcome = await RunAgainst(footer);

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Contains($"says {year}", outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("© 2023 Salon Test", 2023, 3)]                                    // exactly at the limit
    [InlineData("(c) 2017 Salon Test", 2017, 9)]
    [InlineData("COPYRIGHT 2019. ALL RIGHTS RESERVED", 2019, 7)]
    [InlineData("© 2010-2019 Salon Test", 2019, 7)]
    public async Task RunAsync_WhenTheYearIsOld_WarnsAndSaysHowOld(string footer, int year, int behind)
    {
        var outcome = await RunAgainst(footer);

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains($"says {year}, {behind} years ago", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("look abandoned", outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<p>Salon Test · Carrer de Prova 12 · 08301 Mataró</p>")]                         // numbers, but no notice
    [InlineData("<p>Since 2009</p>")]                                                              // a year, but not a copyright
    [InlineData("© <script>document.write(new Date().getFullYear())</script> Salon Test")]          // written by script
    [InlineData("<!-- © 2015 Salon Test -->")]                                                     // commented out
    [InlineData("© 1850 Salon Test")]                                                              // not a plausible year
    [InlineData("© 2031 Salon Test")]                                                              // nor one in the future
    public async Task RunAsync_WhenThereIsNoYearToJudge_Skips(string footer)
    {
        var outcome = await RunAgainst(footer);

        Assert.Equal(CheckStatus.Skip, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_WhenAnotherYearEndsTheTextBeforeTheNotice_ReadsTheNoticesOwnYear()
    {
        // Tags removed, this reads "Established 2010 © 2026 Salon Test": the notice says 2026.
        var outcome = await Run(StubHttpMessageHandler.Serving(
            "<html><body><p>Established 2010</p><footer>© 2026 Salon Test</footer></body></html>"));

        Assert.Equal(CheckStatus.Pass, outcome.Status);
        Assert.Contains("says 2026", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_DoesNotLetAYearBeforeTheMarkHideAnOldNotice()
    {
        // "Updated 2026 © 2017 Salon Test": the notice is 2017, whatever came before it.
        var outcome = await Run(StubHttpMessageHandler.Serving(
            "<html><body><p>Updated 2026</p><footer>© 2017 Salon Test</footer></body></html>"));

        Assert.Equal(CheckStatus.Warn, outcome.Status);
        Assert.Contains("says 2017", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_DoesNotReadAPhoneNumberNextToTheMarkAsTheYear()
    {
        // No other digits may sit between the mark and the year, so 600 111 222 is never read as 1112.
        var outcome = await RunAgainst("© Salon Test · Tel. 600 111 222");

        Assert.Equal(CheckStatus.Skip, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_NeverFails()
    {
        var outcome = await RunAgainst("© 1995 Salon Test");

        Assert.Equal(CheckStatus.Warn, outcome.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task RunAsync_WhenThePageTurnsRobotsAway_SkipsInsteadOfCallingItADefect(HttpStatusCode status)
    {
        var outcome = await RunAgainst("© 2015", status);

        Assert.Equal(CheckStatus.Skip, outcome.Status);
        Assert.Equal((int)status, outcome.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task RunAsync_WhenThePageAnswersWithAnError_IsAnErrorNotAFinding(HttpStatusCode status)
    {
        var outcome = await RunAgainst("© 2015", status);

        Assert.Equal(CheckStatus.Error, outcome.Status);
        Assert.Equal((int)status, outcome.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_WhenThePageCannotBeFetched_IsAnErrorNotAFail(bool timedOut)
    {
        Exception failure = timedOut ? new TaskCanceledException("timed out") : new HttpRequestException("refused");

        var outcome = await Run(StubHttpMessageHandler.Throwing(failure));

        Assert.Equal(CheckStatus.Error, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_WhenTheCallerCancels_PropagatesInsteadOfReportingAnError()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        using var client = new HttpClient(StubHttpMessageHandler.Throwing(new TaskCanceledException()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new CopyrightYearCheck(client, new FakeTimeProvider(Now)).RunAsync(Site, cancellation.Token));
    }
}
