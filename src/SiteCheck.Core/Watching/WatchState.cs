using System.Text;
using SiteCheck.Reporting;
using SiteCheck.Running;

namespace SiteCheck.Watching;

/// <summary>
/// Loads and saves the history <c>watch</c> compares each run against.
/// </summary>
/// <remarks>
/// The history is a <see cref="JsonReport"/>: one format to maintain, and a file a person can open
/// to see what the tool last knew.
/// </remarks>
public static class WatchState
{
    /// <summary>The saved history, or nothing if this is the first run.</summary>
    /// <exception cref="FormatException">
    /// The file exists but cannot be read. Deliberately not treated as a first run: that would
    /// overwrite the history and re-announce every known problem as new.
    /// </exception>
    public static async Task<IReadOnlyList<SiteReport>> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return [];
        }

        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);

        try
        {
            return JsonReport.Parse(text);
        }
        catch (FormatException ex)
        {
            throw new FormatException($"The watch history in {path} cannot be read: {ex.Message} Move it aside to start a fresh history.", ex);
        }
    }

    /// <summary>
    /// Writes the history through a temporary file, so a run killed half way leaves the last good
    /// history in place instead of half of a new one.
    /// </summary>
    public static async Task SaveAsync(string path, IReadOnlyList<SiteReport> history, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(history);

        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        var temporary = full + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonReport.Render(history), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, full, overwrite: true);
    }
}
