using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace SiteCheck.Checks;

/// <summary>
/// Reports whether the forms on the page send what visitors type somewhere that exists, securely.
/// </summary>
/// <remarks>
/// A contact form that has stopped working fails in silence: the visitor sees "thank you", the
/// owner sees nothing, and the lost customer is never known about. This check looks for the
/// causes that can be seen from outside: a form that sends to an address that no longer exists,
/// one that sends over plain http, and one that relies on <c>mailto:</c>.
/// <para>
/// It never sends a form. Submitting a stranger's contact form is spam, whatever the reason. The
/// address a form sends to is asked with a GET, which a form endpoint answers without acting on,
/// so this can say a form points somewhere real, but not that a message sent through it arrives.
/// Only sending one can say that, and only the owner should.
/// </para>
/// <para>
/// Only forms that send are looked at: <c>method="post"</c>, or a <c>mailto:</c> address. A search
/// box sends nothing anywhere worth checking. A form sent by script has no address to look at,
/// and is reported as such rather than guessed at.
/// </para>
/// </remarks>
public sealed partial class ContactFormCheck : ISiteCheck
{
    private const int MaxForms = 5;

    private readonly HttpClient _httpClient;

    public ContactFormCheck(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        _httpClient = httpClient;
    }

    public string Name => "contact-form";

    public async Task<CheckOutcome> RunAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        string html;
        Uri page;

        try
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return CheckOutcome.Fail(
                    $"The site answered {(int)response.StatusCode} ({response.ReasonPhrase}), so there was no page to look for forms on.");
            }

            page = response.RequestMessage?.RequestUri ?? url;
            html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CheckOutcome.Fail($"The site did not respond within {_httpClient.Timeout.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture)} s.");
        }
        catch (HttpRequestException ex)
        {
            return CheckOutcome.Fail($"The site could not be reached: {ex.Message}");
        }

        var forms = HtmlTags.Find(html, "form").Where(Sends).Take(MaxForms).ToArray();

        if (forms.Length == 0)
        {
            return ScriptedFormField().IsMatch(html)
                ? CheckOutcome.Skip("The page has a form that is sent by script, which can only be tested by sending it.")
                : CheckOutcome.Skip("The page has no form that sends anything, so there is nothing to check.");
        }

        var findings = new List<CheckOutcome>(forms.Length);

        foreach (var form in forms)
        {
            findings.Add(await ExamineAsync(form, page, cancellationToken).ConfigureAwait(false));
        }

        var problems = findings.Where(f => f.Status != CheckStatus.Pass).ToArray();

        if (problems.Length == 0)
        {
            return CheckOutcome.Pass(
                $"The page has {forms.Length} form(s), and each sends to an address that answers, over https. Whether messages arrive can only be tested by sending one.");
        }

        var worst = problems.Any(p => p.Status == CheckStatus.Fail) ? CheckStatus.Fail : CheckStatus.Warn;
        var detail = string.Join(" ", problems.Select(p => p.Detail).Distinct(StringComparer.Ordinal));

        return new CheckOutcome(worst, detail);
    }

    private async Task<CheckOutcome> ExamineAsync(IReadOnlyDictionary<string, string> form, Uri page, CancellationToken cancellationToken)
    {
        var action = WebUtility.HtmlDecode(form.GetValueOrDefault("action", string.Empty)).Trim();

        if (action.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            return CheckOutcome.Warn(
                "A form sends through the visitor's own email program (mailto:), which does nothing on most phones or on computers without one set up. A form service or the site's own backend is reliable.");
        }

        // No action means the form sends to the page it is on, which has just answered.
        if (action.Length == 0)
        {
            return CheckOutcome.Pass("The form sends to its own page.");
        }

        if (!Uri.TryCreate(page, action, out var target) || target.Scheme is not ("http" or "https"))
        {
            return CheckOutcome.Pass("The form is handled by script.");
        }

        if (InsecureOnSecurePage(page, target) is { } insecure)
        {
            return insecure;
        }

        if (target.GetLeftPart(UriPartial.Path) == page.GetLeftPart(UriPartial.Path))
        {
            return CheckOutcome.Pass("The form sends to its own page.");
        }

        try
        {
            using var response = await _httpClient
                .GetAsync(target, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            return (int)response.StatusCode switch
            {
                404 or 410 => CheckOutcome.Fail(
                    $"A form sends to {target}, which does not exist ({(int)response.StatusCode}). Messages sent through it are lost."),

                // A form endpoint is built for POST, and some answer a GET with a server error while
                // accepting posts fine. Worth a look, not proof of a problem.
                >= 500 => CheckOutcome.Warn(
                    $"A form sends to {target}, which answered a plain visit with a server error ({(int)response.StatusCode}). It may still accept messages; send a test one to be sure."),

                // 2xx, 3xx, and the 4xx a POST-only endpoint gives a GET (400, 401, 403, 405):
                // something is there.
                _ => CheckOutcome.Pass($"The form sends to {target}, which answers."),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException
                                   || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return CheckOutcome.Fail($"A form sends to {target}, which could not be reached. Messages sent through it are lost.");
        }
    }

    private static CheckOutcome? InsecureOnSecurePage(Uri page, Uri target) =>
        page.Scheme == Uri.UriSchemeHttps && target.Scheme == Uri.UriSchemeHttp
            ? CheckOutcome.Fail(
                $"A form sends what visitors type to {target} over plain http. Browsers warn them before sending, and anyone on the same network can read it.")
            : null;

    private static bool Sends(IReadOnlyDictionary<string, string> form) =>
        string.Equals(form.GetValueOrDefault("method"), "post", StringComparison.OrdinalIgnoreCase)
        || form.GetValueOrDefault("action", string.Empty).TrimStart().StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);

    // A textarea or an email field is what a contact form is made of, even when script sends it.
    [GeneratedRegex("""<textarea\b|<input\b[^>]*\btype\s*=\s*["']?email\b""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ScriptedFormField();
}
