using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SiteCheck.Checks;

/// <summary>
/// Reports what the site is built with when it says so, and whether that is out of date.
/// </summary>
/// <remarks>
/// <para>
/// Most small-business sites run on a ready-made system such as WordPress, and most of the ones
/// that get broken into were running an old version of it or of a plugin: the holes are
/// published with each update, and robots then go looking for the sites that did not take it.
/// A page usually says what built it, in a <c>&lt;meta name="generator"&gt;</c> tag that the
/// system and its plugins add by themselves, version included.
/// </para>
/// <para>
/// A <see cref="CheckStatus.Warn"/> when a version is announced, since that is exactly what those
/// robots read. For WordPress the version is also compared with the current release, asked of
/// wordpress.org: being behind is the finding that matters, and the warning says by how much.
/// When wordpress.org cannot be asked the version is still reported, just not compared.
/// </para>
/// <para>
/// On a WordPress site it also asks for <c>/xmlrpc.php</c>, an old remote-control door that is
/// open by default and that password-guessing robots prefer because one request can try
/// hundreds of passwords. Open, it is mentioned; it is not a warning by itself, since it is how
/// WordPress ships and some sites need it (the mobile app, Jetpack).
/// </para>
/// <para>
/// A site that names no system is a <see cref="CheckStatus.Skip"/>: hand-written pages and site
/// builders that update themselves have no version of their own to fall behind. Never a
/// <see cref="CheckStatus.Fail"/>: an old version is a risk, not a break-in.
/// </para>
/// <para>
/// It only reads what the page announces. A site that hides its generator tag while running an
/// old version passes unseen, and plugins that add no tag are not found at all.
/// </para>
/// </remarks>
public sealed partial class CmsVersionCheck : ISiteCheck
{
    /// <summary>Where wordpress.org publishes the current release.</summary>
    public static readonly Uri WordPressReleases = new("https://api.wordpress.org/core/version-check/1.7/");

    private const int MaxListed = 4;

    private readonly HttpClient _httpClient;

    // Asked once per run, not once per site, and behind a gate because sites are audited in
    // parallel. A failed lookup is not remembered, so the next site tries again.
    private readonly SemaphoreSlim _latestGate = new(1, 1);
    private Version? _latestWordPress;

    public CmsVersionCheck(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        _httpClient = httpClient;
    }

    public string Name => "cms-version";

    public async Task<CheckOutcome> RunAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        var (fetched, unread) = await PageReader
            .ReadAsync(_httpClient, url, "read the page to see what it is built with", cancellationToken)
            .ConfigureAwait(false);

        if (fetched is null)
        {
            return unread!;
        }

        Generator[] generators;

        try
        {
            generators = [.. Generators(fetched.Html)];
        }
        catch (RegexMatchTimeoutException)
        {
            return CheckOutcome.Error("The page's HTML is too tangled to read in reasonable time.");
        }

        var wordPress = generators.FirstOrDefault(generator => generator.Name.Equals("WordPress", StringComparison.OrdinalIgnoreCase));
        var isWordPress = wordPress is not null
                          || fetched.Html.Contains("/wp-content/", StringComparison.OrdinalIgnoreCase)
                          || fetched.Html.Contains("/wp-includes/", StringComparison.OrdinalIgnoreCase);

        if (generators.Length == 0 && !isWordPress)
        {
            return CheckOutcome.Skip(
                "The page does not say what it is built with, so there is no version to check. Nothing wrong with that.");
        }

        var remoteControl = isWordPress && await RemoteControlOpenAsync(fetched.Address, cancellationToken).ConfigureAwait(false)
            ? " Its /xmlrpc.php also answers: robots use that door to guess passwords in bulk, so block it unless you use the WordPress mobile app or Jetpack."
            : string.Empty;

        var behind = wordPress?.Version is { } running
            ? Behind(running, await LatestWordPressAsync(cancellationToken).ConfigureAwait(false))
            : null;

        var versioned = generators.Where(generator => generator.Version is not null).ToArray();

        if (versioned.Length == 0)
        {
            var built = isWordPress ? "WordPress" : generators[0].Name;

            return CheckOutcome.Pass($"The site is built with {built} and does not announce which version.{remoteControl}");
        }

        var listed = string.Join(", ", versioned.Take(MaxListed).Select(generator => $"{generator.Name} {generator.Version}"));
        var more = versioned.Length > MaxListed
            ? $" and {(versioned.Length - MaxListed).ToString(CultureInfo.InvariantCulture)} more"
            : string.Empty;

        return CheckOutcome.Warn(
            $"The page announces exactly what it is built with: {listed}{more}.{behind} "
            + $"Robots read this to find sites running versions with known holes. Keep everything updated, and hide the version.{remoteControl}");
    }

    private static string? Behind(Version running, Version? latest) =>
        latest is null || (running.Major, running.Minor).CompareTo((latest.Major, latest.Minor)) >= 0
            ? null
            : $" WordPress is now at {latest.Major}.{latest.Minor}, so this site is behind and missing security fixes.";

    /// <summary>Every <c>&lt;meta name="generator"&gt;</c>, split into what it names and its version.</summary>
    private static IEnumerable<Generator> Generators(string html)
    {
        foreach (var meta in HtmlTags.Find(html, "meta"))
        {
            if (!string.Equals(meta.GetValueOrDefault("name"), "generator", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var content = WebUtility.HtmlDecode(meta.GetValueOrDefault("content", string.Empty)).Trim();
            var match = NameAndVersion().Match(content);

            if (!match.Success)
            {
                continue;
            }

            // The tag is the site's text: kept short, and its version only if it parses as one.
            var name = match.Groups["name"].Value.Trim();

            yield return new Generator(
                name.Length <= 40 ? name : name[..40],
                Version.TryParse(match.Groups["version"].Value, out var version) ? version : null);
        }
    }

    /// <summary>
    /// Whether <c>/xmlrpc.php</c> is switched on. WordPress answers a GET there with 405 and a
    /// fixed sentence, which tells it apart from a page that merely exists at that address.
    /// </summary>
    private async Task<bool> RemoteControlOpenAsync(Uri page, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient
                .GetAsync(new Uri(page, "/xmlrpc.php"), cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.MethodNotAllowed)
            {
                return false;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return body.Contains("XML-RPC server accepts POST requests only", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is HttpRequestException
                                   || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Not knowing is not a finding.
            return false;
        }
    }

    private async Task<Version?> LatestWordPressAsync(CancellationToken cancellationToken)
    {
        if (_latestWordPress is { } known)
        {
            return known;
        }

        await _latestGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return _latestWordPress ??= await AskWordPressAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _latestGate.Release();
        }
    }

    private async Task<Version?> AskWordPressAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(WordPressReleases, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

            return document.RootElement.TryGetProperty("offers", out var offers)
                   && offers.ValueKind == JsonValueKind.Array
                   && offers.GetArrayLength() > 0
                   && offers[0].ValueKind == JsonValueKind.Object
                   && offers[0].TryGetProperty("version", out var current)
                   && current.ValueKind == JsonValueKind.String
                   && Version.TryParse(current.GetString(), out var version)
                ? version
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
                                   || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // The comparison is a bonus. Without it the version is still reported.
            return null;
        }
    }

    /// <summary><c>WordPress 6.4.2</c>, <c>Joomla! - Open Source Content Management</c>, <c>Elementor 3.18.3; features: …</c>.</summary>
    [GeneratedRegex(
        @"^(?<name>[^\d;,(]*[^\d;,(\s\-–v])[\s\-–v]*(?<version>\d+(?:\.\d+){1,3})?",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex NameAndVersion();

    private sealed record Generator(string Name, Version? Version);
}
