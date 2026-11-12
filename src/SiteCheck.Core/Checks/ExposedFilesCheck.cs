using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SiteCheck.Checks;

/// <summary>
/// Reports files left on the server that were never meant for visitors and give the site away.
/// </summary>
/// <remarks>
/// <para>
/// Most small sites that get broken into are not attacked with anything clever. Someone uploaded
/// the whole project folder, or left a backup beside the live files, and a robot that asks every
/// site on the internet for <c>/.env</c> found the database password in it. This check asks for a
/// short, fixed list of those leftovers, the same ones those robots ask for first.
/// </para>
/// <para>
/// A file counts as exposed only when what comes back <em>is</em> that file, recognised by its
/// first bytes: a <c>.git/HEAD</c> that starts <c>ref: refs/</c>, a <c>.env</c> made of
/// <c>NAME=value</c> lines. The status code is not enough, because many sites answer 200 with the
/// home page for every address; believing the code would accuse them all.
/// </para>
/// <para>
/// Files that hold or lead to secrets (source history, configuration, database dumps, password
/// files, backups) are a <see cref="CheckStatus.Fail"/>: anyone can take them today. Pages that
/// only describe the server (<c>phpinfo</c>, Apache's status page, a folder listing) are a
/// <see cref="CheckStatus.Warn"/>: a map for an attacker, not the keys.
/// </para>
/// <para>
/// What it does not do matters as much. It reads at most the first 4 kB of an answer, only to
/// recognise it, and never puts a file's contents in the report, only its address. It does not
/// guess names beyond the list, try passwords, or send anything but plain <c>GET</c> requests for
/// public addresses. It is still a dozen requests no visitor would make, which a site's firewall
/// may notice, so it is opt-in: run it on sites you look after, or have been asked to look at.
/// </para>
/// </remarks>
public sealed partial class ExposedFilesCheck : ISiteCheck
{
    /// <summary>Enough to recognise any file on the list, and no more of someone's secrets than that.</summary>
    private const int MaxRead = 4096;

    /// <summary>After this many probes go unanswered the site is down, and the rest would only wait.</summary>
    private const int MaxUnanswered = 3;

    private const int MaxListed = 4;

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);

    private static readonly Probe[] Probes =
    [
        new(".git/HEAD", "the site's source code and its history", Secret: true,
            (text, _) => text.StartsWith("ref: refs/", StringComparison.Ordinal) || CommitHash().IsMatch(text)),
        new(".git/config", "the site's source code and its history", Secret: true,
            (text, _) => !LooksLikeHtml(text) && text.Contains("[core]", StringComparison.Ordinal)),
        new(".env", "passwords and keys", Secret: true,
            (text, _) => !LooksLikeHtml(text) && EnvLine().IsMatch(text)),
        new("wp-config.php.bak", "the WordPress database password", Secret: true, IsWordPressConfig),
        new("wp-config.php~", "the WordPress database password", Secret: true, IsWordPressConfig),
        new("wp-config.php.old", "the WordPress database password", Secret: true, IsWordPressConfig),
        new(".htpasswd", "password hashes", Secret: true,
            (text, _) => !LooksLikeHtml(text) && HtpasswdLine().IsMatch(text)),
        new("backup.sql", "a copy of the database", Secret: true, IsSqlDump),
        new("dump.sql", "a copy of the database", Secret: true, IsSqlDump),
        new("database.sql", "a copy of the database", Secret: true, IsSqlDump),
        new("backup.zip", "a backup of the site", Secret: true,
            (_, bytes) => bytes.StartsWith("PK\u0003\u0004"u8)),
        new(".DS_Store", "the names of the files in the folder", Secret: false,
            (_, bytes) => bytes.Length >= 8 && bytes[4..8].SequenceEqual("Bud1"u8)),
        new("phpinfo.php", "the server's full configuration", Secret: false, IsPhpInfo),
        new("info.php", "the server's full configuration", Secret: false, IsPhpInfo),
        new("server-status", "who is visiting the site right now", Secret: false,
            (text, _) => text.Contains("Apache Server Status", StringComparison.OrdinalIgnoreCase)),
        new("wp-content/uploads/", "a browsable list of every uploaded file", Secret: false,
            (text, _) => text.Contains("<title>Index of /", StringComparison.OrdinalIgnoreCase)),
    ];

    private readonly HttpClient _httpClient;

    public ExposedFilesCheck(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        _httpClient = httpClient;
    }

    private delegate bool Recognise(string text, ReadOnlySpan<byte> bytes);

    private enum Answer
    {
        /// <summary>The site answered, and not with this file.</summary>
        Absent,

        /// <summary>The file itself came back.</summary>
        Exposed,

        /// <summary>No answer at all.</summary>
        Unanswered,
    }

    public string Name => "exposed-files";

    public async Task<CheckOutcome> RunAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        // The leftovers sit at the top of the site, wherever on it the audited page is.
        var root = new Uri(url.GetLeftPart(UriPartial.Authority) + "/");
        var exposed = new List<Probe>();
        var unanswered = 0;

        // One at a time on purpose: this is the part of an audit most likely to look like an
        // attack, and a burst of requests for /.env and /.git is exactly what a firewall bans.
        foreach (var probe in Probes)
        {
            switch (await AskAsync(new Uri(root, probe.Path), probe, cancellationToken).ConfigureAwait(false))
            {
                case Answer.Exposed:
                    exposed.Add(probe);
                    break;

                case Answer.Unanswered when ++unanswered >= MaxUnanswered && exposed.Count == 0:
                    return CheckOutcome.Error(
                        "The site stopped answering, so sitecheck could not look for leftover files.");
            }
        }

        return Grade(root, exposed, unanswered);
    }

    private static CheckOutcome Grade(Uri root, List<Probe> exposed, int unanswered)
    {
        var asked = Probes.Length.ToString(CultureInfo.InvariantCulture);
        var unansweredNote = unanswered == 0
            ? string.Empty
            : $" {unanswered.ToString(CultureInfo.InvariantCulture)} of the requests got no answer, so those files are not ruled out.";

        if (exposed.Count == 0)
        {
            return CheckOutcome.Pass(
                $"None of the {asked} leftover files sitecheck asks for (source history, configuration, backups) can be downloaded from the site.{unansweredNote}");
        }

        var listed = string.Join("; ", exposed.Take(MaxListed).Select(probe => $"{new Uri(root, probe.Path)} ({probe.Gives})"));
        var more = exposed.Count > MaxListed
            ? $"; and {(exposed.Count - MaxListed).ToString(CultureInfo.InvariantCulture)} more"
            : string.Empty;

        return exposed.Any(probe => probe.Secret)
            ? CheckOutcome.Fail(
                $"Anyone can download files from the site that were never meant to be public: {listed}{more}. "
                + $"Remove them from the server or block them, and change any password they contain, since it may already have been copied.{unansweredNote}")
            : CheckOutcome.Warn(
                $"The site shows pages that describe its server to anyone who asks: {listed}{more}. "
                + $"They hold no passwords, but they tell an attacker where to look. Remove or block them.{unansweredNote}");
    }

    private async Task<Answer> AskAsync(Uri address, Probe probe, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ProbeTimeout);

        try
        {
            using var response = await _httpClient
                .GetAsync(address, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);

            // 403 and 404 are both the right answer here: the file is not handed out.
            if (!response.IsSuccessStatusCode)
            {
                return Answer.Absent;
            }

            var bytes = await ReadStartAsync(response.Content, deadline.Token).ConfigureAwait(false);

            // Latin-1 maps every byte to one character, so text checks work on any file and
            // binary ones are not mangled on the way.
            return probe.Recognise(Encoding.Latin1.GetString(bytes), bytes) ? Answer.Exposed : Answer.Absent;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Answer.Unanswered;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return Answer.Unanswered;
        }
        catch (RegexMatchTimeoutException)
        {
            return Answer.Absent;
        }
    }

    private static async Task<byte[]> ReadStartAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        await using (stream.ConfigureAwait(false))
        {
            var buffer = new byte[MaxRead];
            var total = 0;
            int read;

            // A stream may hand over less than it was asked for, so one read is not "the start".
            while (total < MaxRead
                   && (read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
            }

            return buffer[..total];
        }
    }

    private static bool LooksLikeHtml(string text)
    {
        var start = text.AsSpan().TrimStart();

        return start.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase)
               || start.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
               || text.Contains("<body", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWordPressConfig(string text, ReadOnlySpan<byte> bytes) =>
        !LooksLikeHtml(text) && text.Contains("DB_PASSWORD", StringComparison.Ordinal);

    private static bool IsSqlDump(string text, ReadOnlySpan<byte> bytes) =>
        !LooksLikeHtml(text)
        && (text.Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase)
            || text.Contains("INSERT INTO", StringComparison.OrdinalIgnoreCase)
            || text.Contains("-- MySQL dump", StringComparison.OrdinalIgnoreCase)
            || text.Contains("PostgreSQL database dump", StringComparison.OrdinalIgnoreCase));

    private static bool IsPhpInfo(string text, ReadOnlySpan<byte> bytes) =>
        text.Contains("<title>phpinfo()", StringComparison.OrdinalIgnoreCase)
        || (text.Contains("<title>PHP ", StringComparison.Ordinal) && text.Contains("phpinfo()", StringComparison.Ordinal));

    [GeneratedRegex(@"^[0-9a-f]{40}\s*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CommitHash();

    /// <summary>A <c>NAME=value</c> line, as in <c>DB_PASSWORD=hunter2</c>.</summary>
    [GeneratedRegex(@"^\s*(export\s+)?[A-Z][A-Z0-9_]{2,}\s*=", RegexOptions.Multiline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EnvLine();

    /// <summary><c>user:hash</c>, with a hash in one of the formats Apache writes.</summary>
    [GeneratedRegex(@"^[\w.@-]+:(\$apr1\$|\$2[aby]\$|\$[156]\$|\{SHA\})\S+\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HtpasswdLine();

    /// <param name="Path">Where the file would be, from the top of the site.</param>
    /// <param name="Gives">What whoever downloads it gets, completing "… (…)".</param>
    /// <param name="Secret">Whether it holds or leads to secrets, rather than only describing the server.</param>
    /// <param name="Recognise">Whether an answer's first bytes are this file and not some other page.</param>
    private sealed record Probe(string Path, string Gives, bool Secret, Recognise Recognise);
}
