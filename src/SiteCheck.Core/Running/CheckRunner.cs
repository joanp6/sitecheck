using SiteCheck.Checks;

namespace SiteCheck.Running;

/// <summary>
/// Runs every registered check against a site and times each one.
/// </summary>
/// <remarks>
/// The runner knows nothing about the individual checks: they arrive as
/// <see cref="ISiteCheck"/> instances, so registering a new one is the only step
/// needed to include it in a report.
/// </remarks>
public sealed class CheckRunner
{
    private readonly IReadOnlyList<ISiteCheck> _checks;
    private readonly TimeProvider _timeProvider;

    public CheckRunner(IEnumerable<ISiteCheck> checks, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _checks = [.. checks];
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Runs every registered check against <paramref name="url"/>, in registration order.
    /// </summary>
    /// <remarks>
    /// Execution is sequential on purpose. One of the checks measures load time, and
    /// firing the other requests at the same host concurrently would distort it.
    /// </remarks>
    public async Task<IReadOnlyList<CheckResult>> RunAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        var results = new List<CheckResult>(_checks.Count);

        foreach (var check in _checks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await RunOneAsync(check, url, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    private async Task<CheckResult> RunOneAsync(ISiteCheck check, Uri url, CancellationToken cancellationToken)
    {
        var startedAt = _timeProvider.GetTimestamp();
        CheckOutcome outcome;

        try
        {
            outcome = await check.RunAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller asked us to stop. That is not a finding about the site.
            throw;
        }
        catch (Exception ex)
        {
            // A check that throws is a gap in our tooling, not a verdict on the site.
            // Recording it as Error keeps one broken check from sinking the whole report.
            outcome = CheckOutcome.Error($"The check threw {ex.GetType().Name}: {ex.Message}");
        }

        return new CheckResult(
            check.Name,
            outcome.Status,
            outcome.Detail,
            _timeProvider.GetElapsedTime(startedAt));
    }
}
