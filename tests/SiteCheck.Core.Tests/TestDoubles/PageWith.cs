using System.Net;
using System.Text;

namespace SiteCheck.Core.Tests.TestDoubles;

/// <summary>
/// A page that arrives with given response headers, for checks that read those rather than the HTML.
/// </summary>
internal static class PageWith
{
    /// <param name="html">The page's markup.</param>
    /// <param name="headers">Each written as it appears on the wire, <c>Name: value</c>. A name may repeat.</param>
    public static StubHttpMessageHandler Headers(string html, params string[] headers) =>
        StubHttpMessageHandler.Routing(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };

            foreach (var header in headers)
            {
                var colon = header.IndexOf(':', StringComparison.Ordinal);
                response.Headers.TryAddWithoutValidation(header[..colon], header[(colon + 1)..].Trim());
            }

            return response;
        });
}
