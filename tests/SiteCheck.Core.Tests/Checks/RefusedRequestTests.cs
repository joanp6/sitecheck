using Microsoft.Extensions.Time.Testing;
using SiteCheck.Checks;
using SiteCheck.Core.Tests.TestDoubles;
using SiteCheck.Http;

namespace SiteCheck.Core.Tests.Checks;

/// <summary>
/// When sitecheck itself declines to read a page, no check may blame the site for it.
/// </summary>
/// <remarks>
/// The five checks here fetch the page themselves and grade "could not be reached" as a defect,
/// which is right when the site refused. These pin the other case: the refusal was sitecheck's.
/// </remarks>
public sealed class RefusedRequestTests
{
    private static readonly Uri Site = new("https://intranet.test/");

    public static TheoryData<string, Exception> Refusals => new()
    {
        // As it arrives in real life: wrapped by the HTTP stack, which is why the type is looked
        // for down the chain and not only at the top.
        { "private address, wrapped", new HttpRequestException("connect failed", new PrivateAddressException("intranet.test is at 10.0.0.5. Pass --allow-private.")) },
        { "private address, bare", new PrivateAddressException("intranet.test is at 10.0.0.5. Pass --allow-private.") },
        { "page too large", new PageTooLargeException("The page is larger than 5 MB, which is more than sitecheck reads.") },
    };

    private static ISiteCheck[] ChecksThatFetchForThemselves(HttpClient client) =>
    [
        new LoadTimeCheck(client, new FakeTimeProvider()),
        new MobileCheck(client),
        new BrokenLinksCheck(client),
        new ContactFormCheck(client),
        new HttpsRedirectCheck(client),
    ];

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task WhenSitecheckDeclinesToReadThePage_EveryCheckSaysWhy_AndNoneCallsItADefect(string _, Exception refusal)
    {
        using var client = new HttpClient(StubHttpMessageHandler.Throwing(refusal));
        var said = Refusal.In(refusal)!.Message;

        foreach (var check in ChecksThatFetchForThemselves(client))
        {
            var outcome = await check.RunAsync(Site, TestContext.Current.CancellationToken);

            Assert.True(outcome.Status == CheckStatus.Error, $"{check.Name} reported {outcome.Status}: {outcome.Detail}");
            Assert.Equal(said, outcome.Detail);
        }
    }

    [Fact]
    public async Task WhenTheSiteItselfCannotBeReached_ItIsStillTheSitesFailure()
    {
        // The other side of the line: nothing sitecheck decided, so the old verdicts stand.
        using var client = new HttpClient(StubHttpMessageHandler.Throwing(new HttpRequestException("connection refused")));

        var outcome = await new MobileCheck(client).RunAsync(Site, TestContext.Current.CancellationToken);

        Assert.Equal(CheckStatus.Fail, outcome.Status);
    }

    [Fact]
    public void In_FindsARefusalHoweverDeepItIsWrapped_AndNothingWhenThereIsNone()
    {
        var refusal = new PrivateAddressException("no");
        var wrapped = new InvalidOperationException("outer", new HttpRequestException("middle", refusal));

        Assert.Same(refusal, Refusal.In(wrapped));
        Assert.Null(Refusal.In(new HttpRequestException("just the network")));
        Assert.Null(Refusal.In(null));
    }
}
