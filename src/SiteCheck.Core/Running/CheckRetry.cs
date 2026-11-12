namespace SiteCheck.Running;

/// <summary>
/// How many times a check runs before its bad news is believed.
/// </summary>
/// <remarks>
/// <para>
/// The internet drops a connection now and then. Without a second look, one dropped packet at
/// 06:00 is a FAIL, <c>watch</c> announces that the site got worse, and the next morning announces
/// that it got better. Two alerts about nothing teach whoever reads them to stop reading, which
/// costs more than the alert that is real.
/// </para>
/// <para>
/// Only <see cref="Checks.CheckStatus.Fail"/> and <see cref="Checks.CheckStatus.Error"/> are tried
/// again: they are the verdicts a network hiccup produces, and the ones that raise an alarm or an
/// exit code. A pass needs no second opinion, and a warning or a skip comes from what the site
/// said, not from failing to hear it. The last attempt is the one reported, so a check that fails
/// twice reads exactly as it did the first time.
/// </para>
/// <para>
/// It lives in the runner, around the whole check, rather than around each request: every check
/// gets it without knowing, including the ones that talk DNS or TLS instead of HTTP, and a check
/// that follows fifty links looks at all fifty again instead of guessing which answer was the
/// blip.
/// </para>
/// </remarks>
/// <param name="Attempts">How many times a check may run in total. 1 means no retry.</param>
/// <param name="Pause">How long to wait before trying again, so the same hiccup is not met twice.</param>
/// <param name="GiveUpAfter">
/// How many of one site's checks may fail a second time before the rest are believed the first
/// time. A site that is down fails every check that reads it; once a few have failed twice the
/// retries have answered their question, and repeating them only doubles how long a dead site
/// takes to report.
/// </param>
public sealed record CheckRetry(int Attempts, TimeSpan Pause, int GiveUpAfter = 3)
{
    /// <summary>Every check runs once.</summary>
    public static CheckRetry None { get; } = new(Attempts: 1, Pause: TimeSpan.Zero);

    /// <summary>A failing check gets one more try, two seconds later, until three of a site's checks have failed twice.</summary>
    public static CheckRetry Once { get; } = new(Attempts: 2, Pause: TimeSpan.FromSeconds(2));

    public int Attempts { get; } = Attempts > 0
        ? Attempts
        : throw new ArgumentOutOfRangeException(nameof(Attempts), Attempts, "A check has to run at least once.");

    public TimeSpan Pause { get; } = Pause >= TimeSpan.Zero
        ? Pause
        : throw new ArgumentOutOfRangeException(nameof(Pause), Pause, "The pause cannot be negative.");

    public int GiveUpAfter { get; } = GiveUpAfter > 0
        ? GiveUpAfter
        : throw new ArgumentOutOfRangeException(nameof(GiveUpAfter), GiveUpAfter, "At least one check has to be allowed a second look.");
}
