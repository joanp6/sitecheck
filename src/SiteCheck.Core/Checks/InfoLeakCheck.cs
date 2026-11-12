using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace SiteCheck.Checks;

/// <summary>
/// Reports whether the server announces exactly which software, and which version, it runs.
/// </summary>
/// <remarks>
/// <para>
/// Servers like to introduce themselves: <c>Server: Apache/2.4.41 (Ubuntu)</c>,
/// <c>X-Powered-By: PHP/7.4.3</c>. No visitor reads those lines, but the robots that scan the
/// internet do, and a version number is all they need to look up which known holes that version
/// has and whether this server is worth their time. Hiding it fixes nothing by itself, which is
/// why this is only ever a <see cref="CheckStatus.Warn"/>, but it takes the site off the list of
/// easy targets and costs one line of configuration.
/// </para>
/// <para>
/// Only a version number counts. <c>Server: nginx</c> or <c>Server: cloudflare</c> says what
/// every attacker could work out anyway and is a pass. The headers that exist only to name the
/// software (<c>X-Powered-By</c>, <c>X-AspNet-Version</c>, <c>X-AspNetMvc-Version</c>,
/// <c>X-Generator</c>) are checked along with <c>Server</c>.
/// </para>
/// <para>
/// It does not say whether the version is out of date: that needs a list of releases that goes
/// stale the day it is written. What it reports is the version, so whoever maintains the site
/// can check.
/// </para>
/// </remarks>
public sealed partial class InfoLeakCheck : ISiteCheck
{
    private static readonly string[] Headers = ["Server", "X-Powered-By", "X-AspNet-Version", "X-AspNetMvc-Version", "X-Generator"];

    private readonly HttpClient _httpClient;

    public InfoLeakCheck(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        _httpClient = httpClient;
    }

    public string Name => "info-leak";

    public async Task<CheckOutcome> RunAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        var (fetched, unread) = await PageReader
            .ReadAsync(_httpClient, url, "read what the server says about itself", cancellationToken)
            .ConfigureAwait(false);

        if (fetched is null)
        {
            return unread!;
        }

        try
        {
            return Evaluate(fetched.Headers);
        }
        catch (RegexMatchTimeoutException)
        {
            return CheckOutcome.Error("The server's headers are too tangled to read in reasonable time.");
        }
    }

    private static CheckOutcome Evaluate(HttpResponseHeaders headers)
    {
        var leaks = Headers
            .SelectMany(name => headers.TryGetValues(name, out var values)
                ? values.Where(value => Version().IsMatch(value)).Select(value => $"{name}: {Shorten(value)}")
                : [])
            .ToArray();

        return leaks.Length == 0
            ? CheckOutcome.Pass("The server does not announce which version of its software it runs.")
            : CheckOutcome.Warn(
                $"The server tells every visitor exactly what it runs ({string.Join("; ", leaks)}). "
                + "Robots use that to pick out servers with known holes. Ask whoever hosts the site to hide the version numbers, "
                + "and to check that these versions still receive security updates.");
    }

    // A header is the site's text, not ours, and a report is no place for a kilobyte of it.
    private static string Shorten(string value) => value.Length <= 60 ? value : value[..60] + "…";

    /// <summary>A number with at least one dot, as in <c>2.4.41</c> or <c>PHP/8.2</c>.</summary>
    [GeneratedRegex(@"\d+\.\d+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Version();
}
