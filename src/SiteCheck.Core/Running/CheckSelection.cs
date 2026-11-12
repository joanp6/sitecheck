namespace SiteCheck.Running;

/// <summary>
/// Which checks a run includes, from what was asked on the command line.
/// </summary>
/// <remarks>
/// <para>
/// Twenty-odd checks answer different questions for different people: whoever looks after the
/// server wants the security ones, whoever writes the pages wants what Google sees. A run can be
/// narrowed three ways, which combine:
/// </para>
/// <list type="bullet">
/// <item><c>--only a,b</c>: these and no others.</item>
/// <item><c>--profile security</c>: a named set, for not having to remember the names.</item>
/// <item><c>--skip a,b</c>: everything else. Applied last, so it also trims a profile.</item>
/// </list>
/// <para>
/// Checks that are opt-in (<c>pagespeed</c>, <c>exposed-files</c>) stay out unless their own
/// option is given or <c>--only</c> names them: naming a check is asking for it. A profile does
/// not switch them on, since their reasons for being opt-in (sending the address to Google,
/// asking a site for files no visitor would) do not go away because a profile lists them.
/// </para>
/// </remarks>
public sealed class CheckSelection
{
    /// <summary>The named sets. A check may be in more than one.</summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> Profiles { get; } =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            // Could someone abuse the site, or its name?
            ["security"] = Set(
                "ssl-certificate", "https-redirect", "email-auth", "mixed-content", "security-headers", "cookies",
                "info-leak", "sri", "cms-version", "tls-config", "www-address", "exposed-files"),

            // Will people find it, and will it open quickly when they do?
            ["seo"] = Set(
                "search-snippet", "local-business", "share-preview", "mobile-viewport", "load-time", "page-weight",
                "broken-links", "copyright-year", "pagespeed"),

            // Can a customer do what they came for?
            ["business"] = Set(
                "click-to-call", "booking-link", "contact-form", "legal-pages", "domain-expiry", "broken-links",
                "load-time", "ssl-certificate"),
        };

    private readonly HashSet<string> _only;
    private readonly HashSet<string> _skip;
    private readonly IReadOnlySet<string>? _profile;

    /// <param name="only">Check names to run and no others. Empty means no such limit.</param>
    /// <param name="skip">Check names to leave out.</param>
    /// <param name="profile">A key of <see cref="Profiles"/>, or <see langword="null"/> for none.</param>
    /// <exception cref="ArgumentException"><paramref name="profile"/> is not a profile.</exception>
    public CheckSelection(IEnumerable<string>? only = null, IEnumerable<string>? skip = null, string? profile = null)
    {
        _only = Names(only);
        _skip = Names(skip);

        if (profile is not null)
        {
            _profile = Profiles.TryGetValue(profile.Trim(), out var named)
                ? named
                : throw new ArgumentException(
                    $"\"{profile}\" is not a profile. The profiles are: {string.Join(", ", Profiles.Keys.Order(StringComparer.Ordinal))}.", nameof(profile));
        }
    }

    /// <summary>Every check that is not opt-in.</summary>
    public static CheckSelection Everything { get; } = new();

    /// <summary>Whether a run includes the check called <paramref name="name"/>.</summary>
    /// <param name="name">The check's <see cref="Checks.ISiteCheck.Name"/>.</param>
    /// <param name="optIn">The check only runs when asked for.</param>
    /// <param name="optedIn">The check's own option was given.</param>
    public bool Includes(string name, bool optIn = false, bool optedIn = false)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_skip.Contains(name) || (optIn && !optedIn && !_only.Contains(name)))
        {
            return false;
        }

        if (_only.Count > 0 || _profile is not null)
        {
            // Together they add up: --profile seo --only tls-config is the profile plus one.
            return _only.Contains(name) || _profile?.Contains(name) == true;
        }

        return true;
    }

    /// <summary>
    /// The names given to <c>--only</c> or <c>--skip</c> that are not checks, so a typo is
    /// reported instead of quietly selecting nothing.
    /// </summary>
    public IReadOnlyList<string> Unknown(IEnumerable<string> known)
    {
        ArgumentNullException.ThrowIfNull(known);

        var real = known.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return [.. _only.Concat(_skip).Where(name => !real.Contains(name)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)];
    }

    /// <summary>Names as people type them: several per option, separated by commas, in any case.</summary>
    private static HashSet<string> Names(IEnumerable<string>? given) =>
        (given ?? [])
        .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> Set(params string[] names) => new(names, StringComparer.OrdinalIgnoreCase);
}
