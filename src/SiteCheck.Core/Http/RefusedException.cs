namespace SiteCheck.Http;

/// <summary>
/// sitecheck chose not to read something: the request was never a fair test of the site.
/// </summary>
/// <remarks>
/// <para>
/// A check that cannot read the page has to say whose fault that is. A connection the site
/// refused is the site's, and a visitor would be refused too. A connection sitecheck declined to
/// open (a private address) or a page it stopped reading (too large to be a page) is its own
/// decision, and reporting it as "the site could not be reached" would blame the site for a rule
/// of the tool. So these carry their own type, and a check that meets one reports an
/// <see cref="Checks.CheckStatus.Error"/> with the reason, never a defect.
/// </para>
/// <para>
/// An <see cref="HttpRequestException"/> all the same, so code that has never heard of this type
/// still treats it as a request that did not work rather than as a crash.
/// </para>
/// </remarks>
public abstract class RefusedException(string message) : HttpRequestException(message);

/// <summary>The page went on past the size sitecheck reads, so it was not read.</summary>
public sealed class PageTooLargeException(string message) : RefusedException(message);

/// <summary>Finds a <see cref="RefusedException"/> however deep it has been wrapped.</summary>
public static class Refusal
{
    /// <summary>
    /// The refusal behind <paramref name="exception"/>, or <see langword="null"/> when the request
    /// failed for a reason of the site's or the network's.
    /// </summary>
    /// <remarks>
    /// Looked for down the chain of inner exceptions, because the HTTP stack wraps what a
    /// connection attempt throws in an exception of its own before a check ever sees it.
    /// </remarks>
    public static RefusedException? In(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is RefusedException refused)
            {
                return refused;
            }
        }

        return null;
    }
}
