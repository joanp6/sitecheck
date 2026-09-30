using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using SiteCheck.Checks;
using SiteCheck.Running;
using SiteCheck.Sites;

namespace SiteCheck.Reporting;

/// <summary>
/// Writes a run as JSON for machines, and reads it back.
/// </summary>
/// <remarks>
/// This is a contract, not a dump of the types: scripts parse it, and <c>watch</c> keeps its
/// history in it. It is written field by field rather than serialised from the records, so that
/// renaming a C# property cannot silently rename a field under a history file written last week.
/// <code>
/// {
///   "format": 1,
///   "sites": [
///     {
///       "name": "Perruqueria Angela",            // omitted when the site has none
///       "url": "https://perruqueria-angela.pages.dev/",
///       "checkedAt": "2026-09-29T08:00:00+00:00",
///       "verdict": "pass",
///       "results": [
///         {
///           "check": "ssl-certificate",
///           "status": "pass",                     // pass | warn | fail | error | skip
///           "detail": "The certificate is valid until ...",
///           "durationMs": 212,
///           "validUntil": "2026-11-01T12:00:00+00:00"   // omitted when it does not apply
///         }
///       ]
///     }
///   ]
/// }
/// </code>
/// <c>detail</c> is prose for people and may be reworded at any time; see
/// <see cref="CheckOutcome.Detail"/>. Every fact a machine needs is its own field.
/// </remarks>
public static class JsonReport
{
    /// <summary>Raised when a change would make older files unreadable.</summary>
    public const int FormatVersion = 1;

    public static string Render(IReadOnlyList<SiteReport> reports)
    {
        ArgumentNullException.ThrowIfNull(reports);

        using var buffer = new MemoryStream();

        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = true,

            // Accents and quotes as themselves, so a person opening the history file can read
            // "Perruqueria Àngela" and "Not secure" rather than their escape codes. Still valid
            // JSON; the relaxed encoder is only unsafe for pasting into HTML, which this never is.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            json.WriteStartObject();
            json.WriteNumber("format", FormatVersion);
            json.WriteStartArray("sites");

            foreach (var report in reports)
            {
                WriteSite(json, report);
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <exception cref="FormatException">The text is not a report this version can read.</exception>
    public static IReadOnlyList<SiteReport> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;

            var format = root.GetProperty("format").GetInt32();

            if (format != FormatVersion)
            {
                throw new FormatException($"This report is format {format}; this version of sitecheck reads format {FormatVersion}.");
            }

            return [.. root.GetProperty("sites").EnumerateArray().Select(ReadSite)];
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            throw new FormatException($"This is not a sitecheck JSON report: {ex.Message}", ex);
        }
    }

    public static string StatusName(CheckStatus status) => status.ToString().ToLowerInvariant();

    private static void WriteSite(Utf8JsonWriter json, SiteReport report)
    {
        json.WriteStartObject();

        if (report.Site.Name is not null)
        {
            json.WriteString("name", report.Site.Name);
        }

        json.WriteString("url", report.Site.Url.AbsoluteUri);
        json.WriteString("checkedAt", report.CheckedAt);
        json.WriteString("verdict", StatusName(report.Verdict));
        json.WriteStartArray("results");

        foreach (var result in report.Results)
        {
            json.WriteStartObject();
            json.WriteString("check", result.CheckName);
            json.WriteString("status", StatusName(result.Status));
            json.WriteString("detail", result.Detail);
            json.WriteNumber("durationMs", (long)result.Duration.TotalMilliseconds);

            if (result.ValidUntil is { } validUntil)
            {
                json.WriteString("validUntil", validUntil);
            }

            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    private static SiteReport ReadSite(JsonElement site)
    {
        var name = site.TryGetProperty("name", out var label) ? label.GetString() : null;
        var url = new Uri(site.GetProperty("url").GetString()!, UriKind.Absolute);

        return new SiteReport(
            new Site(url, name),
            site.GetProperty("checkedAt").GetDateTimeOffset(),
            [.. site.GetProperty("results").EnumerateArray().Select(ReadResult)]);
    }

    private static CheckResult ReadResult(JsonElement result) => new(
        result.GetProperty("check").GetString()!,
        ParseStatus(result.GetProperty("status").GetString()),
        result.GetProperty("detail").GetString()!,
        TimeSpan.FromMilliseconds(result.GetProperty("durationMs").GetInt64()),
        result.TryGetProperty("validUntil", out var validUntil) ? validUntil.GetDateTimeOffset() : null);

    private static CheckStatus ParseStatus(string? name) =>
        Enum.TryParse<CheckStatus>(name, ignoreCase: true, out var status) && Enum.IsDefined(status) && !int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            ? status
            : throw new FormatException($"\"{name}\" is not a check status.");
}
