using System.Globalization;

namespace SiteCheck.Checks;

/// <summary>
/// Tuning for <see cref="LoadTimeCheck"/>.
/// </summary>
/// <param name="Warn">Loading slower than this is reported as <see cref="CheckStatus.Warn"/>.</param>
/// <param name="Fail">Loading slower than this is reported as <see cref="CheckStatus.Fail"/>.</param>
public sealed record LoadTimeCheckOptions(TimeSpan Warn, TimeSpan Fail)
{
    /// <summary>
    /// Thresholds for a small-business brochure site: comfortable under 1.5 s,
    /// losing visitors past 4 s.
    /// </summary>
    public static LoadTimeCheckOptions Default { get; } = new(TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(4));

    public TimeSpan Warn { get; } = Warn > TimeSpan.Zero
        ? Warn
        : throw new ArgumentOutOfRangeException(nameof(Warn), Warn, "The warning threshold must be positive.");

    // Guarded because swapped thresholds would silently make one of the two statuses unreachable.
    public TimeSpan Fail { get; } = Fail > Warn
        ? Fail
        : throw new ArgumentOutOfRangeException(nameof(Fail), Fail, "The failure threshold must be greater than the warning threshold.");
}

/// <summary>
/// Reports how long the page takes to arrive in full for a first-time visitor.
/// </summary>
public sealed class LoadTimeCheck : ISiteCheck
{
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly LoadTimeCheckOptions _options;

    public LoadTimeCheck(HttpClient httpClient, TimeProvider timeProvider, LoadTimeCheckOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _httpClient = httpClient;
        _timeProvider = timeProvider;
        _options = options ?? LoadTimeCheckOptions.Default;
    }

    public string Name => "load-time";

    public async Task<CheckOutcome> RunAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        var startedAt = _timeProvider.GetTimestamp();

        // Kept outside the try: once the headers are in, the site has answered, and that answer
        // is still worth reporting if the body then fails to arrive.
        int? statusCode = null;

        try
        {
            using var response = await _httpClient
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            statusCode = (int)response.StatusCode;

            // Drain the body before stopping the clock: headers can arrive quickly in
            // front of a page that then takes seconds to finish streaming.
            _ = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            var elapsed = _timeProvider.GetElapsedTime(startedAt);

            // The measured facts travel as values beside the prose, so nothing downstream has
            // to read them back out of the sentence.
            return Grade(response, elapsed) with { Elapsed = elapsed, StatusCode = statusCode };
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient reports its own timeout as a cancellation. To a visitor this is
            // indistinguishable from the site being down, so it is the site's failure.
            return CheckOutcome.Fail($"The site did not respond within {Seconds(_httpClient.Timeout)}.") with { StatusCode = statusCode };
        }
        catch (HttpRequestException ex)
        {
            return (statusCode is { } answered
                ? CheckOutcome.Fail($"The site answered {answered} but the page stopped arriving: {ex.Message}")
                : CheckOutcome.Fail($"The site could not be reached: {ex.Message}")) with { StatusCode = statusCode };
        }
    }

    private CheckOutcome Grade(HttpResponseMessage response, TimeSpan elapsed)
    {
        if (!response.IsSuccessStatusCode)
        {
            return CheckOutcome.Fail(
                $"The site answered {(int)response.StatusCode} ({response.ReasonPhrase}) after {Seconds(elapsed)}.");
        }

        if (elapsed >= _options.Fail)
        {
            return CheckOutcome.Fail($"The page took {Seconds(elapsed)} to load, over the {Seconds(_options.Fail)} budget.");
        }

        return elapsed >= _options.Warn
            ? CheckOutcome.Warn($"The page took {Seconds(elapsed)} to load, over the {Seconds(_options.Warn)} target.")
            : CheckOutcome.Pass($"The page loaded in {Seconds(elapsed)}.");
    }

    private static string Seconds(TimeSpan duration) =>
        $"{duration.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture)} s";
}
