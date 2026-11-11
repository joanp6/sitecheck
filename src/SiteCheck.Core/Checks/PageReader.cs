using System.Globalization;
using System.Net.Http.Headers;

namespace SiteCheck.Checks;

/// <summary>A page as a check that reads HTML gets it: where it ended up, its headers and its markup.</summary>
/// <param name="Address">The address after redirects, so relative links resolve against where the visitor lands.</param>
internal sealed record FetchedPage(Uri Address, HttpResponseHeaders Headers, string Html);

/// <summary>
/// Fetches the page for checks that read its HTML, with the verdicts those checks share when it
/// cannot be read.
/// </summary>
/// <remarks>
/// For checks added after the original seven. When the page cannot be read they report an
/// <see cref="CheckStatus.Error"/>, not a <see cref="CheckStatus.Fail"/>: they had nothing to
/// look at, and <c>load-time</c> already reports a site that is down or broken as the defect it
/// is. A 403 or 429 is a <see cref="CheckStatus.Skip"/>; see <see cref="RobotBlock"/>.
/// </remarks>
internal static class PageReader
{
    /// <param name="unchecked">What could not be looked at, completing "so sitecheck could not …".</param>
    /// <returns>The page, or the outcome to report instead when there is none to read.</returns>
    public static async Task<(FetchedPage? Page, CheckOutcome? Unread)> ReadAsync(
        HttpClient httpClient, Uri url, string @unchecked, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);

            if (RobotBlock.TurnsRobotsAway(response.StatusCode))
            {
                return (null, RobotBlock.Skip(response, @unchecked));
            }

            if (!response.IsSuccessStatusCode)
            {
                return (null, CheckOutcome.Error(
                        $"The site answered {(int)response.StatusCode} ({response.ReasonPhrase}), so sitecheck could not {@unchecked}.")
                    with { StatusCode = (int)response.StatusCode });
            }

            if (PageContent.SkipUnlessPage(response, @unchecked) is { } notAPage)
            {
                return (null, notAPage);
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return (new FetchedPage(response.RequestMessage?.RequestUri ?? url, response.Headers, html), null);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient reports its own timeout as a cancellation. See LoadTimeCheck.
            return (null, CheckOutcome.Error(
                $"The site did not respond within {httpClient.Timeout.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture)} s, so sitecheck could not {@unchecked}."));
        }
        catch (HttpRequestException ex)
        {
            return (null, CheckOutcome.Error($"The page could not be read, so sitecheck could not {@unchecked}: {ex.Message}"));
        }
    }
}
