using System.Runtime.CompilerServices;
using SiteCheck.Checks;
using SiteCheck.Sites;

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

    private readonly CheckRetry _retry;

    /// <param name="checks">The checks to run, in the order they should run.</param>
    /// <param name="timeProvider">The clock checks are timed with, and the pause before a retry waits on.</param>
    /// <param name="retry">When to run a check again. By default a check runs once.</param>
    public CheckRunner(IEnumerable<ISiteCheck> checks, TimeProvider timeProvider, CheckRetry? retry = null)
    {
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _checks = [.. checks];
        _timeProvider = timeProvider;
        _retry = retry ?? CheckRetry.None;
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

        // Per audit, not per runner: one site being down says nothing about the next.
        var confirmed = new StrongBox<int>(0);

        foreach (var check in _checks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await RunOneAsync(check, url, confirmed, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>
    /// Runs every registered check against <paramref name="site"/> and records when.
    /// </summary>
    public async Task<SiteReport> AuditAsync(Site site, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(site);

        var startedAt = _timeProvider.GetUtcNow();
        var results = await RunAsync(site.Url, cancellationToken).ConfigureAwait(false);

        return new SiteReport(site, startedAt, results);
    }

    private async Task<CheckResult> RunOneAsync(ISiteCheck check, Uri url, StrongBox<int> confirmed, CancellationToken cancellationToken)
    {
        var startedAt = _timeProvider.GetTimestamp();
        var outcome = await AttemptAsync(check, url, cancellationToken).ConfigureAwait(false);

        // A second look before believing bad news. See CheckRetry for why here and why these two.
        // Not once enough of this site's checks have failed twice: by then it is the site, not the
        // network, and asking again would only make a dead site take twice as long to report.
        if (confirmed.Value < _retry.GiveUpAfter)
        {
            var retried = false;

            for (var attempt = 1; attempt < _retry.Attempts && outcome.Status is CheckStatus.Fail or CheckStatus.Error; attempt++)
            {
                await Task.Delay(_retry.Pause, _timeProvider, cancellationToken).ConfigureAwait(false);
                outcome = await AttemptAsync(check, url, cancellationToken).ConfigureAwait(false);
                retried = true;
            }

            if (retried && outcome.Status is CheckStatus.Fail or CheckStatus.Error)
            {
                confirmed.Value++;
            }
        }

        return new CheckResult(
            check.Name,
            outcome.Status,
            outcome.Detail,
            _timeProvider.GetElapsedTime(startedAt),
            outcome.ValidUntil,
            outcome.Elapsed,
            outcome.StatusCode,
            outcome.Bytes);
    }

    private static async Task<CheckOutcome> AttemptAsync(ISiteCheck check, Uri url, CancellationToken cancellationToken)
    {
        try
        {
            return await check.RunAsync(url, cancellationToken).ConfigureAwait(false);
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
            return CheckOutcome.Error($"The check threw {ex.GetType().Name}: {ex.Message}");
        }
    }
}
