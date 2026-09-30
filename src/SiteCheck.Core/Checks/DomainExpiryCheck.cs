using System.Globalization;
using System.Net;
using System.Text.Json;

namespace SiteCheck.Checks;

/// <summary>
/// Tuning for <see cref="DomainExpiryCheck"/>.
/// </summary>
/// <param name="WarnWithinDays">How close to expiry a domain has to be before it is worth raising.</param>
public sealed record DomainExpiryCheckOptions(int WarnWithinDays = 30)
{
    public int WarnWithinDays { get; } = WarnWithinDays >= 0
        ? WarnWithinDays
        : throw new ArgumentOutOfRangeException(nameof(WarnWithinDays), WarnWithinDays, "The warning window cannot be negative.");
}

/// <summary>
/// Reports when the site's domain name stops being registered.
/// </summary>
/// <remarks>
/// A lapsed domain takes the website and the business's email down with it, and for a small
/// business it is one of the most common ways to disappear overnight: the card on file expired,
/// or the person who registered it left. The date comes from the registry over RDAP, the
/// structured successor to WHOIS, found through the bootstrap file IANA publishes.
/// <para>
/// Not every registry publishes RDAP (<c>.es</c>, <c>.eu</c> and <c>.io</c> do not), and a site
/// on a hosting platform's address has no domain of its own to renew. Both are
/// <see cref="CheckStatus.Skip"/>: nothing is wrong, there is just nothing to check.
/// </para>
/// </remarks>
public sealed class DomainExpiryCheck : ISiteCheck
{
    /// <summary>Where IANA publishes which RDAP server answers for which top-level domain.</summary>
    public static readonly Uri BootstrapUrl = new("https://data.iana.org/rdap/dns.json");

    // Addresses a platform hands out under its own domain. The owner of such a site renews
    // nothing, and reporting the platform's own renewal date as theirs would be misleading.
    private static readonly string[] HostingPlatformDomains =
    [
        "pages.dev", "workers.dev", "github.io", "netlify.app", "vercel.app", "web.app", "firebaseapp.com",
        "herokuapp.com", "azurewebsites.net", "azurestaticapps.net", "onrender.com", "fly.dev", "wixsite.com",
        "blogspot.com", "wordpress.com", "weebly.com", "square.site", "myshopify.com", "godaddysites.com",
        "webflow.io", "framer.website", "carrd.co", "surge.sh", "glitch.me", "replit.app",
    ];

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly DomainExpiryCheckOptions _options;

    // Fetched once per instance: auditing twenty sites should not download the bootstrap twenty
    // times. Not synchronised, because CheckRunner never runs a check concurrently with itself.
    private Dictionary<string, Uri>? _registries;

    public DomainExpiryCheck(HttpClient httpClient, TimeProvider timeProvider, DomainExpiryCheckOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _httpClient = httpClient;
        _timeProvider = timeProvider;
        _options = options ?? new DomainExpiryCheckOptions();
    }

    public string Name => "domain-expiry";

    public async Task<CheckOutcome> RunAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (url.HostNameType is not UriHostNameType.Dns)
        {
            return CheckOutcome.Skip("The site is addressed by IP, so there is no domain name to renew.");
        }

        // Punycode, because that is the form registries index names under.
        var host = url.IdnHost.TrimEnd('.').ToLowerInvariant();
        var labels = host.Split('.');

        if (labels.Length < 2)
        {
            return CheckOutcome.Skip($"{host} is not a registered domain name.");
        }

        var platform = HostingPlatformDomains.FirstOrDefault(
            domain => host == domain || host.EndsWith("." + domain, StringComparison.Ordinal));

        if (platform is not null)
        {
            return CheckOutcome.Skip(
                $"The site lives at an address on {platform}, which belongs to the hosting platform, so there is no domain of its own to renew.");
        }

        _registries ??= await LoadRegistriesAsync(cancellationToken).ConfigureAwait(false);

        var tld = labels[^1];

        if (!_registries.TryGetValue(tld, out var registry))
        {
            return CheckOutcome.Skip(
                $"The .{tld} registry does not publish expiry dates in a form that can be read automatically (RDAP). Check the date with whoever you registered the domain through.");
        }

        // Longest name first, stopping at the first one the registry knows. Shortest first gets
        // the wrong answer: registries under a two-part suffix answer for the suffix itself
        // (Nominet returns a record for co.uk), so bbc.co.uk would be graded on co.uk's date.
        // Subdomains are not registrations, and registries answer 404 or 400 for them.
        for (var take = labels.Length; take >= 2; take--)
        {
            var name = string.Join('.', labels[^take..]);
            var record = await LookUpAsync(registry, name, cancellationToken).ConfigureAwait(false);

            if (record is not null)
            {
                return Grade(name, record.Value);
            }
        }

        return CheckOutcome.Skip($"The .{tld} registry has no record for {host}.");
    }

    private CheckOutcome Grade(string domain, JsonElement record)
    {
        var expiry = ExpiryOf(record);

        if (expiry is null)
        {
            return CheckOutcome.Skip($"The registry does not say when {domain} expires.");
        }

        var now = _timeProvider.GetUtcNow();
        var date = expiry.Value.UtcDateTime.ToString("yyyy-MM-dd 'UTC'", CultureInfo.InvariantCulture);
        var daysLeft = (int)(expiry.Value - now).TotalDays;

        CheckOutcome outcome;

        if (now >= expiry.Value)
        {
            outcome = CheckOutcome.Fail($"The domain {domain} expired on {date}. The site and any email on it stop working once the registry releases it.");
        }
        else if (daysLeft <= _options.WarnWithinDays)
        {
            // Registrars often renew automatically, and some only do so on the day, so this
            // cannot know it is a real problem. It can make sure somebody looks.
            outcome = CheckOutcome.Warn($"The domain {domain} expires on {date}, in {daysLeft} day(s). Unless it renews automatically, renew it now or the site and its email go down.");
        }
        else
        {
            outcome = CheckOutcome.Pass($"The domain {domain} is registered until {date}, {daysLeft} day(s) from now.");
        }

        return outcome with { ValidUntil = expiry };
    }

    private static DateTimeOffset? ExpiryOf(JsonElement record)
    {
        if (!record.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var entry in events.EnumerateArray())
        {
            if (entry.TryGetProperty("eventAction", out var action)
                && action.ValueKind == JsonValueKind.String
                && string.Equals(action.GetString(), "expiration", StringComparison.OrdinalIgnoreCase)
                && entry.TryGetProperty("eventDate", out var date)
                && date.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(date.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    /// <summary>
    /// The registry's record for <paramref name="name"/>, or <see langword="null"/> if it is not a
    /// registered name.
    /// </summary>
    /// <remarks>
    /// Anything else the registry says (a 5xx, a rate limit) throws, and the runner records an
    /// <see cref="CheckStatus.Error"/>: the registry being down says nothing about the domain.
    /// </remarks>
    private async Task<JsonElement?> LookUpAsync(Uri registry, string name, CancellationToken cancellationToken)
    {
        using var response = await _httpClient
            .GetAsync(new Uri(registry, "domain/" + name), cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);

        return document.RootElement.Clone();
    }

    private async Task<Dictionary<string, Uri>> LoadRegistriesAsync(CancellationToken cancellationToken)
    {
        await using var body = await _httpClient.GetStreamAsync(BootstrapUrl, cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);

        var registries = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase);

        // RFC 9224: "services" is a list of [ [tld, ...], [server url, ...] ] pairs.
        foreach (var service in document.RootElement.GetProperty("services").EnumerateArray())
        {
            var servers = service[1].EnumerateArray()
                .Select(server => server.GetString())
                .OfType<string>()
                .Select(server => server.EndsWith('/') ? server : server + "/")
                .ToArray();

            // https where offered: the answer decides whether an owner is told their domain is safe.
            var chosen = servers.FirstOrDefault(server => server.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                         ?? servers.FirstOrDefault();

            if (chosen is null)
            {
                continue;
            }

            foreach (var tld in service[0].EnumerateArray())
            {
                if (tld.GetString() is { } name)
                {
                    registries.TryAdd(name, new Uri(chosen));
                }
            }
        }

        return registries;
    }
}
