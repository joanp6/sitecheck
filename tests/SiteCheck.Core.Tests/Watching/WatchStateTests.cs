using SiteCheck.Checks;
using SiteCheck.Running;
using SiteCheck.Sites;
using SiteCheck.Watching;

namespace SiteCheck.Core.Tests.Watching;

public sealed class WatchStateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"sitecheck-state-{Guid.NewGuid():N}");

    private string StatePath => Path.Combine(_directory, "nested", "state.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_WhenThereIsNoHistoryYet_ReturnsNothing() =>
        Assert.Empty(await WatchState.LoadAsync(StatePath, TestContext.Current.CancellationToken));

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsAndCreatesTheFolder()
    {
        var history = new[]
        {
            new SiteReport(
                new Site(new Uri("https://a.test/"), "A"),
                new DateTimeOffset(2026, 1, 15, 6, 0, 0, TimeSpan.Zero),
                [new CheckResult("ssl", CheckStatus.Warn, "Soon.", TimeSpan.FromMilliseconds(3))]),
        };

        await WatchState.SaveAsync(StatePath, history, TestContext.Current.CancellationToken);
        var loaded = await WatchState.LoadAsync(StatePath, TestContext.Current.CancellationToken);

        Assert.Equal(history[0].Results, Assert.Single(loaded).Results);
        Assert.False(File.Exists(StatePath + ".tmp"));
    }

    [Fact]
    public async Task LoadAsync_WhenTheFileIsCorrupt_RefusesInsteadOfStartingOver()
    {
        // Starting over would re-announce every known problem as new and lose the history.
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        await File.WriteAllTextAsync(StatePath, "{ half a fil", TestContext.Current.CancellationToken);

        var failure = await Assert.ThrowsAsync<FormatException>(() => WatchState.LoadAsync(StatePath, TestContext.Current.CancellationToken));

        Assert.Contains(StatePath, failure.Message, StringComparison.Ordinal);
    }
}
