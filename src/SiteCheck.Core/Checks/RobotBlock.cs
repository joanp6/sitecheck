using System.Net;

namespace SiteCheck.Checks;

/// <summary>
/// Recognises a site that turns automated visitors away, and words the verdict for it.
/// </summary>
/// <remarks>
/// A 403 (Forbidden) or 429 (Too Many Requests) on the page itself is what a firewall or a bot
/// shield such as Cloudflare's answers a robot, while the same page opens fine in a browser. It is
/// a fact about us, not about the business, so it must never be graded as a defect of theirs.
/// <para>
/// It is a <see cref="CheckStatus.Skip"/> rather than an <see cref="CheckStatus.Error"/>: nothing
/// in sitecheck went wrong, and asking again tomorrow gets the same answer. An Error would make
/// every audit of such a site exit 2, which reads as "the tool broke". The status code travels as
/// <see cref="CheckOutcome.StatusCode"/>, so a reader can still say why the page went unchecked.
/// </para>
/// <para>
/// 401 is deliberately not included: a page behind a password is what every visitor gets too.
/// </para>
/// </remarks>
internal static class RobotBlock
{
    public static bool TurnsRobotsAway(HttpStatusCode status) =>
        status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests;

    /// <param name="response">The answer to the request for the page.</param>
    /// <param name="unchecked">What could not be looked at, completing "so sitecheck could not …".</param>
    public static CheckOutcome Skip(HttpResponseMessage response, string @unchecked) =>
        Skip((int)response.StatusCode, response.ReasonPhrase, @unchecked);

    /// <param name="statusCode">The status the site answered the request for the page with.</param>
    /// <param name="reasonPhrase">The words the site sent with it, when they are still at hand.</param>
    /// <param name="unchecked">What could not be looked at, completing "so sitecheck could not …".</param>
    public static CheckOutcome Skip(int statusCode, string? reasonPhrase, string @unchecked)
    {
        var answer = string.IsNullOrWhiteSpace(reasonPhrase) ? $"{statusCode}" : $"{statusCode} ({reasonPhrase})";

        return CheckOutcome.Skip(
            $"The site answered {answer}, which usually means it turns automated visitors away, so sitecheck could not {@unchecked}. This says nothing about what people see in a browser.")
            with { StatusCode = statusCode };
    }
}
