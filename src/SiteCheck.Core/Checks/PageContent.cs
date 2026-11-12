namespace SiteCheck.Checks;

/// <summary>
/// Tells a web page from the other things an address can serve, for checks that read HTML.
/// </summary>
/// <remarks>
/// An address that answers with a PDF or an image is not a page with a missing viewport or no
/// title: the questions do not apply, which is a <see cref="CheckStatus.Skip"/>. Any <c>text/*</c>
/// type counts as a page, and so does no type at all, because servers that send HTML as
/// <c>text/plain</c> or unlabelled exist and browsers show their pages anyway.
/// </remarks>
internal static class PageContent
{
    /// <param name="response">The answer to the request for the page.</param>
    /// <param name="unchecked">What could not be looked at, completing "so sitecheck could not …".</param>
    /// <returns>The outcome to report when the answer is not a web page, or <see langword="null"/> when it is.</returns>
    public static CheckOutcome? SkipUnlessPage(HttpResponseMessage response, string @unchecked)
    {
        var type = response.Content.Headers.ContentType?.MediaType;

        if (string.IsNullOrEmpty(type)
            || type.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || type.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return CheckOutcome.Skip($"The address serves a file of type {type}, not a web page, so sitecheck could not {@unchecked}.")
            with { StatusCode = (int)response.StatusCode };
    }
}
