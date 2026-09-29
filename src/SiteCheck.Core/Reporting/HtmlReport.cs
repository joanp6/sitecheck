using System.Globalization;
using System.Net;
using System.Text;
using SiteCheck.Checks;
using SiteCheck.Running;

namespace SiteCheck.Reporting;

/// <summary>
/// Renders a run as one self-contained HTML page, written for the owner of the site rather than for
/// whoever ran the tool.
/// </summary>
/// <remarks>
/// One file with its styles inline, so it survives being attached to an email. Problems come first
/// within each site, since that is what the reader has to act on. Every status is spelled out in
/// words as well as colour, so the page reads the same printed in black and white or by someone who
/// cannot tell red from green.
/// <para>
/// Every piece of text is HTML-encoded. Details quote addresses found on the audited page (a broken
/// link, for one), and that page is not ours: unencoded, it could put script into this report.
/// </para>
/// </remarks>
public static class HtmlReport
{
    private static readonly Dictionary<string, string> Titles = new(StringComparer.Ordinal)
    {
        ["ssl-certificate"] = "Security certificate",
        ["https-redirect"] = "Secure address",
        ["domain-expiry"] = "Domain registration",
        ["load-time"] = "Speed",
        ["mobile-viewport"] = "Phones",
        ["broken-links"] = "Links",
        ["contact-form"] = "Contact form",
        ["pagespeed"] = "Real-world speed (Google)",
    };

    public static string Render(IReadOnlyList<SiteReport> reports, DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(reports);

        var html = new StringBuilder();

        html.Append("""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Website check</title>
            <style>
            :root { --bg:#f6f7f9; --card:#fff; --text:#1d2330; --muted:#5b6475; --line:#e3e6eb;
                    --fail:#b42318; --fail-bg:#fde8e7; --warn:#8a5a00; --warn-bg:#fff3d6;
                    --pass:#1a7f37; --pass-bg:#e3f5e8; --neutral:#4b5565; --neutral-bg:#eceef2; }
            @media (prefers-color-scheme: dark) {
              :root { --bg:#12151b; --card:#1b1f27; --text:#e8eaef; --muted:#9aa3b2; --line:#2c323d;
                      --fail:#ff8a80; --fail-bg:#3a1d1b; --warn:#ffd27a; --warn-bg:#382c12;
                      --pass:#7ee2a0; --pass-bg:#15301f; --neutral:#b8c0cc; --neutral-bg:#2a2f38; }
            }
            * { box-sizing: border-box; }
            body { margin:0; background:var(--bg); color:var(--text);
                   font:16px/1.5 system-ui, -apple-system, "Segoe UI", Roboto, sans-serif; }
            main { max-width:760px; margin:0 auto; padding:32px 16px 48px; }
            h1 { font-size:1.6rem; margin:0 0 4px; }
            .generated { color:var(--muted); margin:0 0 28px; }
            section { background:var(--card); border:1px solid var(--line); border-radius:12px;
                      padding:20px; margin-bottom:20px; }
            .site { display:flex; flex-wrap:wrap; gap:8px 12px; align-items:baseline; justify-content:space-between; }
            h2 { font-size:1.25rem; margin:0; }
            .url { color:var(--muted); font-size:.9rem; overflow-wrap:anywhere; width:100%; margin:2px 0 14px; }
            ul { list-style:none; margin:0; padding:0; }
            li { display:grid; grid-template-columns:9.5rem 1fr; gap:4px 16px; padding:12px 0; border-top:1px solid var(--line); }
            .check { font-weight:600; }
            .detail { grid-column:2; color:var(--muted); overflow-wrap:anywhere; }
            .pill { display:inline-block; border-radius:999px; padding:1px 10px; font-size:.85rem; font-weight:600; white-space:nowrap; }
            .fail { color:var(--fail); background:var(--fail-bg); }
            .warn { color:var(--warn); background:var(--warn-bg); }
            .pass { color:var(--pass); background:var(--pass-bg); }
            .neutral { color:var(--neutral); background:var(--neutral-bg); }
            @media (max-width:560px) { li { grid-template-columns:1fr; } .detail { grid-column:1; } }
            footer { color:var(--muted); font-size:.85rem; }
            </style>
            </head>
            <body>
            <main>
            <h1>Website check</h1>

            """);

        html.Append(CultureInfo.InvariantCulture,
            $"<p class=\"generated\">{Encode(Summary(reports))} · {Encode(generatedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture))}</p>\n");

        foreach (var report in reports)
        {
            AppendSite(html, report);
        }

        html.Append("""
            <footer>
            <p><strong>Needs fixing</strong>: visitors are affected now. <strong>Worth a look</strong>: fine today, but close to becoming a problem.
            <strong>Not applicable</strong>: the question does not apply to this site. <strong>Could not check</strong>: a problem with the checking tool, not with the site.</p>
            <p>Made with sitecheck.</p>
            </footer>
            </main>
            </body>
            </html>

            """);

        return html.ToString();
    }

    private static void AppendSite(StringBuilder html, SiteReport report)
    {
        html.Append("<section>\n<div class=\"site\">");
        html.Append(CultureInfo.InvariantCulture, $"<h2>{Encode(report.Site.DisplayName)}</h2>{Pill(report.Verdict, overall: true)}</div>\n");
        html.Append(CultureInfo.InvariantCulture,
            $"<div class=\"url\">{Encode(report.Site.Url.ToString())} · checked {Encode(report.CheckedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture))}</div>\n<ul>\n");

        // Stable, so checks of equal urgency keep the order they ran in.
        foreach (var result in report.Results.OrderBy(r => Urgency(r.Status)))
        {
            html.Append(CultureInfo.InvariantCulture,
                $"<li><span class=\"check\">{Encode(Titles.GetValueOrDefault(result.CheckName, result.CheckName))}</span><span>{Pill(result.Status, overall: false)}</span><span class=\"detail\">{Encode(result.Detail)}</span></li>\n");
        }

        html.Append("</ul>\n</section>\n");
    }

    private static string Pill(CheckStatus status, bool overall)
    {
        var (css, words) = status switch
        {
            CheckStatus.Fail => ("fail", "Needs fixing"),
            CheckStatus.Warn => ("warn", "Worth a look"),
            CheckStatus.Pass => ("pass", overall ? "All good" : "OK"),
            CheckStatus.Error => ("neutral", "Could not check"),
            CheckStatus.Skip => ("neutral", "Not applicable"),
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown check status."),
        };

        return $"<span class=\"pill {css}\">{words}</span>";
    }

    private static int Urgency(CheckStatus status) => status switch
    {
        CheckStatus.Fail => 0,
        CheckStatus.Warn => 1,
        CheckStatus.Error => 2,
        CheckStatus.Pass => 3,
        CheckStatus.Skip => 4,
        _ => 5,
    };

    private static string Summary(IReadOnlyList<SiteReport> reports)
    {
        var needFixing = reports.Count(r => r.Verdict == CheckStatus.Fail);

        return needFixing switch
        {
            _ when reports.Count == 0 => "No sites were checked",
            0 => reports.Count == 1 ? "Nothing needs fixing" : $"{reports.Count} sites checked, nothing needs fixing",
            _ => reports.Count == 1 ? "Something needs fixing" : $"{reports.Count} sites checked, {needFixing} need fixing",
        };
    }

    private static string Encode(string text) => WebUtility.HtmlEncode(text);
}
