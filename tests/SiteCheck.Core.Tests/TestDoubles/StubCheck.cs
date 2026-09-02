using Microsoft.Extensions.Time.Testing;
using SiteCheck.Checks;

namespace SiteCheck.Core.Tests.TestDoubles;

/// <summary>
/// A check with no logic, used to observe what the runner does around it.
/// </summary>
internal sealed class StubCheck : ISiteCheck
{
    private readonly Func<CheckOutcome> _behaviour;

    private StubCheck(string name, Func<CheckOutcome> behaviour)
    {
        Name = name;
        _behaviour = behaviour;
    }

    public string Name { get; }

    public int Invocations { get; private set; }

    public Uri? LastUrl { get; private set; }

    public static StubCheck Returning(string name, CheckOutcome outcome) => new(name, () => outcome);

    public static StubCheck Throwing(string name, Exception exception) => new(name, () => throw exception);

    /// <summary>A check that appears to take <paramref name="duration"/> without waiting.</summary>
    public static StubCheck Taking(string name, FakeTimeProvider clock, TimeSpan duration) =>
        new(name, () =>
        {
            clock.Advance(duration);
            return CheckOutcome.Pass("Done.");
        });

    /// <summary>A check that cancels the run from the inside, part way through.</summary>
    public static StubCheck Cancelling(string name, CancellationTokenSource source) =>
        new(name, () =>
        {
            source.Cancel();
            return CheckOutcome.Pass("Done, then cancelled.");
        });

    /// <summary>
    /// A check that cancels the run and then throws, the way a check with a deadline of
    /// its own behaves once the caller's deadline has fired.
    /// </summary>
    public static StubCheck CancellingAndThrowing(string name, CancellationTokenSource source) =>
        new(name, () =>
        {
            source.Cancel();
            throw new OperationCanceledException(source.Token);
        });

    public Task<CheckOutcome> RunAsync(Uri url, CancellationToken cancellationToken = default)
    {
        Invocations++;
        LastUrl = url;
        return Task.FromResult(_behaviour());
    }
}
