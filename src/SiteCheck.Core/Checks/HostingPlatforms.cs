namespace SiteCheck.Checks;

/// <summary>
/// Domains under which a hosting platform hands out addresses of its own, such as
/// <c>salon.pages.dev</c>.
/// </summary>
/// <remarks>
/// A site at such an address has no domain of its own: the owner renews nothing, and the domain's
/// mail settings are the platform's, not the business's. Checks that judge the domain skip these.
/// </remarks>
internal static class HostingPlatforms
{
    private static readonly string[] Domains =
    [
        "pages.dev", "workers.dev", "github.io", "netlify.app", "vercel.app", "web.app", "firebaseapp.com",
        "herokuapp.com", "azurewebsites.net", "azurestaticapps.net", "onrender.com", "fly.dev", "wixsite.com",
        "blogspot.com", "wordpress.com", "weebly.com", "square.site", "myshopify.com", "godaddysites.com",
        "webflow.io", "framer.website", "carrd.co", "surge.sh", "glitch.me", "replit.app",
    ];

    /// <summary>The platform domain <paramref name="host"/> lives under, or <see langword="null"/>.</summary>
    /// <param name="host">A lower-case host name, without a trailing dot.</param>
    public static string? Find(string host) =>
        Domains.FirstOrDefault(domain => host == domain || host.EndsWith("." + domain, StringComparison.Ordinal));
}
