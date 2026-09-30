using System.Text.Json;
using SiteCheck.Reporting;

namespace SiteCheck.Sites;

/// <summary>
/// Reads the file that lists the sites to audit.
/// </summary>
/// <remarks>
/// The format, with either form of entry:
/// <code>
/// {
///   "sites": [
///     { "name": "Perruqueria Angela", "url": "perruqueria-angela.pages.dev" },
///     "example.com"
///   ]
/// }
/// </code>
/// Addresses are read exactly as the command line reads them (see <see cref="SiteUrl"/>), so a
/// bare host works in both places. Everything wrong with the file is reported at once, with the
/// entry it concerns, because it is edited by hand and fixed one save at a time otherwise.
/// </remarks>
public static class SiteList
{
    public static async Task<IReadOnlyList<Site>> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string json;

        try
        {
            json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new SiteListException($"The sites file {path} does not exist.", ex);
        }

        return Parse(json);
    }

    /// <exception cref="SiteListException">The file is not a usable list of sites.</exception>
    public static IReadOnlyList<Site> Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            throw new SiteListException($"The sites file is not valid JSON: {ex.Message}", ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("sites", out var entries)
                || entries.ValueKind != JsonValueKind.Array)
            {
                throw new SiteListException("The sites file must be an object with a \"sites\" array.");
            }

            var sites = new List<Site>();
            var problems = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var position = 0;

            foreach (var entry in entries.EnumerateArray())
            {
                position++;

                if (!TryRead(entry, out var site, out var problem))
                {
                    problems.Add($"entry {position}: {problem}");
                }
                else if (!seen.Add(site.Url.AbsoluteUri))
                {
                    // Two entries for one address would overwrite each other's history in watch.
                    problems.Add($"entry {position}: {site.Url} is already listed.");
                }
                else
                {
                    sites.Add(site);
                }
            }

            if (problems.Count > 0)
            {
                throw new SiteListException("The sites file has problems:" + Environment.NewLine + string.Join(Environment.NewLine, problems.Select(p => "  " + p)));
            }

            return sites.Count > 0 ? sites : throw new SiteListException("The sites file lists no sites.");
        }
    }

    private static bool TryRead(JsonElement entry, out Site site, out string problem)
    {
        site = null!;
        problem = string.Empty;

        string? address;
        string? name = null;

        switch (entry.ValueKind)
        {
            case JsonValueKind.String:
                address = entry.GetString();
                break;

            case JsonValueKind.Object:
                address = entry.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String ? url.GetString() : null;

                if (entry.TryGetProperty("name", out var label))
                {
                    if (label.ValueKind != JsonValueKind.String)
                    {
                        problem = "\"name\" must be text.";
                        return false;
                    }

                    name = label.GetString();
                }

                if (address is null)
                {
                    problem = "it has no \"url\".";
                    return false;
                }

                break;

            default:
                problem = "it must be an address or an object with a \"url\".";
                return false;
        }

        if (!SiteUrl.TryParse(address, out var parsed))
        {
            problem = $"\"{address}\" is not a website address.";
            return false;
        }

        site = new Site(parsed, name);
        return true;
    }
}

/// <summary>The sites file could not be used. The message is written for the person editing it.</summary>
public sealed class SiteListException : Exception
{
    public SiteListException()
    {
    }

    public SiteListException(string message)
        : base(message)
    {
    }

    public SiteListException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
