using System.Globalization;
using System.Net.Http.Headers;

namespace SiteCheck.Checks;

/// <summary>
/// Reports whether the site switches on the protections browsers offer and leave off by default.
/// </summary>
/// <remarks>
/// <para>
/// A browser will defend a site's visitors against several well-known tricks, but only when the
/// site asks, with a line in the headers it sends before the page. Three of those lines cost
/// nothing, break nothing on an ordinary site, and are what this check asks for:
/// </para>
/// <list type="bullet">
/// <item>
/// <c>Strict-Transport-Security</c> (HSTS): "always come back over https". Without it, a visitor
/// on café wifi who types the bare address makes one insecure request first, and whoever runs
/// the wifi can answer it. The redirect <c>https-redirect</c> looks for does not close that gap,
/// because the redirect itself travels insecurely.
/// </item>
/// <item>
/// <c>X-Content-Type-Options: nosniff</c>: "believe the file type I state". Without it a browser
/// may decide an uploaded image is really a script and run it.
/// </item>
/// <item>
/// <c>X-Frame-Options</c>, or <c>frame-ancestors</c> in a Content-Security-Policy: "do not show
/// me inside someone else's page". Without it another site can load this one invisibly under a
/// fake button and have the visitor click on it (clickjacking).
/// </item>
/// </list>
/// <para>
/// Any of the three missing is a <see cref="CheckStatus.Warn"/>, never a
/// <see cref="CheckStatus.Fail"/>: nothing is broken for a visitor today, the site is only easier
/// to abuse than it needs to be. A <c>Content-Security-Policy</c> and a <c>Referrer-Policy</c>
/// are worth having too, but writing a policy that does not break the page's own scripts takes
/// someone who knows the site, so their absence is mentioned on a pass and held against nobody.
/// </para>
/// <para>
/// Only headers count. A <c>&lt;meta http-equiv&gt;</c> tag cannot set any of the three:
/// browsers ignore HSTS, nosniff and frame protection written in the page.
/// </para>
/// <para>
/// A page served over plain http is a <see cref="CheckStatus.Skip"/>: browsers ignore HSTS sent
/// insecurely, and <c>https-redirect</c> already reports the larger problem.
/// </para>
/// </remarks>
public sealed class SecurityHeadersCheck : ISiteCheck
{
    private readonly HttpClient _httpClient;

    public SecurityHeadersCheck(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        _httpClient = httpClient;
    }

    public string Name => "security-headers";

    public async Task<CheckOutcome> RunAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        var (fetched, unread) = await PageReader
            .ReadAsync(_httpClient, url, "read the protections it asks browsers for", cancellationToken)
            .ConfigureAwait(false);

        if (fetched is null)
        {
            return unread!;
        }

        if (fetched.Address.Scheme != Uri.UriSchemeHttps)
        {
            return CheckOutcome.Skip(
                "The page is served over plain http, where browsers ignore these protections. https-redirect reports that.");
        }

        return Evaluate(fetched.Headers);
    }

    private static CheckOutcome Evaluate(HttpResponseHeaders headers)
    {
        var missing = new List<string>(3);

        if (!AlwaysHttps(headers))
        {
            missing.Add(
                "it does not tell browsers to always use the secure address (Strict-Transport-Security), so a visitor on shared wifi can be intercepted on their first request");
        }

        if (!Has(headers, "X-Content-Type-Options", value => value.Trim().Equals("nosniff", StringComparison.OrdinalIgnoreCase)))
        {
            missing.Add(
                "it lets browsers guess what kind of file they were sent (X-Content-Type-Options: nosniff), which can turn an uploaded file into a running script");
        }

        if (!RefusesFraming(headers))
        {
            missing.Add(
                "it lets any other site show it inside a frame (X-Frame-Options), which is how a visitor is tricked into clicking something on it they cannot see");
        }

        if (missing.Count > 0)
        {
            var count = missing.Count.ToString(CultureInfo.InvariantCulture);

            return CheckOutcome.Warn(
                $"The site leaves {count} of the 3 basic browser protections switched off: {string.Join("; ", missing)}. "
                + "These are settings on the server or in the hosting panel, not changes to the page; ask whoever hosts the site to add them.");
        }

        var extras = new List<string>(2);

        if (!headers.Contains("Content-Security-Policy"))
        {
            extras.Add("a Content-Security-Policy, which limits where the page may load scripts from");
        }

        if (!headers.Contains("Referrer-Policy"))
        {
            extras.Add("a Referrer-Policy, which limits what other sites learn about where a visitor came from");
        }

        return CheckOutcome.Pass(extras.Count == 0
            ? "The site switches on the basic browser protections, and has a content security policy and a referrer policy."
            : $"The site switches on the 3 basic browser protections. A next step would be {string.Join(", and ", extras)}.");
    }

    /// <summary>HSTS with a lifetime: <c>max-age=0</c> is how a site switches it back off.</summary>
    private static bool AlwaysHttps(HttpResponseHeaders headers) =>
        Has(headers, "Strict-Transport-Security", value => Directives(value).Any(directive =>
        {
            var (name, argument) = Split(directive, '=');

            return name.Equals("max-age", StringComparison.OrdinalIgnoreCase)
                   && long.TryParse(argument.Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                   && seconds > 0;
        }));

    private static bool RefusesFraming(HttpResponseHeaders headers) =>

        // ALLOW-FROM is not counted: current browsers ignore it, which leaves framing open.
        Has(headers, "X-Frame-Options", value => value.Trim().ToUpperInvariant() is "DENY" or "SAMEORIGIN")
        || Has(headers, "Content-Security-Policy", policy => Directives(policy).Any(directive =>
        {
            var (name, sources) = Split(directive, ' ');

            // "frame-ancestors *" is a policy that allows everyone, so it protects from no one.
            return name.Equals("frame-ancestors", StringComparison.OrdinalIgnoreCase)
                   && sources.Length > 0
                   && !sources.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("*");
        }));

    private static bool Has(HttpResponseHeaders headers, string name, Func<string, bool> accepts) =>
        headers.TryGetValues(name, out var values) && values.Any(accepts);

    private static IEnumerable<string> Directives(string value) =>
        value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static (string Name, string Value) Split(string directive, char separator)
    {
        var at = directive.IndexOf(separator, StringComparison.Ordinal);

        return at < 0 ? (directive.Trim(), string.Empty) : (directive[..at].Trim(), directive[(at + 1)..].Trim());
    }
}
