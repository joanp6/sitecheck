namespace SiteCheck.Hosting;

/// <summary>
/// What an audit of a folder on this machine cannot say anything about.
/// </summary>
/// <remarks>
/// A folder served from <c>127.0.0.1</c> has pages, links, images and markup, which is what
/// most checks read. It has no certificate, no domain, no mail, and no server of its own: the
/// headers and the speed measured would be this tool's little server's, not the host the site
/// will be published on. Reporting on those would be reporting on sitecheck.
/// </remarks>
public static class LocalFolder
{
    /// <summary>The checks about a live server, left out when the site is a folder.</summary>
    public static IReadOnlySet<string> NotApplicable { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // There is no https, domain or mail on a loopback address.
        "ssl-certificate", "https-redirect", "tls-config", "domain-expiry", "email-auth", "www-address",

        // These would describe the local server, not the real host.
        "security-headers", "info-leak", "cookies", "load-time", "exposed-files", "pagespeed",
    };
}
