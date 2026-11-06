using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace SiteCheck.Checks;

/// <summary>
/// Tuning for <see cref="PageWeightCheck"/>, in bytes as sent over the network.
/// </summary>
/// <param name="ImageWarn">One image this heavy or heavier is reported as <see cref="CheckStatus.Warn"/>.</param>
/// <param name="ImageFail">One image this heavy or heavier is reported as <see cref="CheckStatus.Fail"/>.</param>
/// <param name="TotalWarn">A page whose parts add up to this or more is reported as <see cref="CheckStatus.Warn"/>.</param>
/// <param name="TotalFail">A page whose parts add up to this or more is reported as <see cref="CheckStatus.Fail"/>.</param>
public sealed record PageWeightCheckOptions(long ImageWarn, long ImageFail, long TotalWarn, long TotalFail)
{
    /// <summary>
    /// For a small business's site, opened on a phone: a photo past 500 kB was not resized for the
    /// web, past 2 MB it holds the page up visibly. A whole page past 3 MB is heavier than most of
    /// the web; past 6 MB it costs a visitor on mobile data real seconds and real money.
    /// </summary>
    public static PageWeightCheckOptions Default { get; } = new(500_000, 2_000_000, 3_000_000, 6_000_000);

    // Guarded because swapped thresholds would silently make one of the statuses unreachable.
    public long ImageWarn { get; } = ImageWarn > 0
        ? ImageWarn
        : throw new ArgumentOutOfRangeException(nameof(ImageWarn), ImageWarn, "The thresholds must be positive.");

    public long ImageFail { get; } = ImageFail > ImageWarn
        ? ImageFail
        : throw new ArgumentOutOfRangeException(nameof(ImageFail), ImageFail, "The failure threshold must be greater than the warning one.");

    public long TotalWarn { get; } = TotalWarn > 0
        ? TotalWarn
        : throw new ArgumentOutOfRangeException(nameof(TotalWarn), TotalWarn, "The thresholds must be positive.");

    public long TotalFail { get; } = TotalFail > TotalWarn
        ? TotalFail
        : throw new ArgumentOutOfRangeException(nameof(TotalFail), TotalFail, "The failure threshold must be greater than the warning one.");
}

/// <summary>
/// Reports how much a first visit to the page downloads, and which images make it heavy.
/// </summary>
/// <remarks>
/// A photo straight from a camera or a phone is several megabytes. On a brochure site that is the
/// usual reason a page is slow on a phone, and the cheapest one to fix: resized and saved as WebP
/// or a good JPEG, the same photo is a tenth of the size and looks the same.
/// <para>
/// It weighs the HTML and what it names: images (for a <c>&lt;picture&gt;</c>, the source a browser
/// picks rather than the fallback <c>&lt;img&gt;</c>), scripts and stylesheets, up to
/// <see cref="MaxParts"/> of them, each asked with a HEAD request for its size (a GET when the
/// server will not say). Compressed sizes are asked for, as a browser would. It does not run the
/// page, so fonts, images named inside stylesheets and anything a script loads are not counted:
/// the total is what the HTML alone commits a visitor to, a floor rather than the full figure.
/// <c>pagespeed</c> measures the page as a browser loads it.
/// </para>
/// <para>
/// A part that cannot be weighed (gone, refused, too slow) is left out of the total and said so;
/// it is <c>broken-links</c>' business whether it exists. The total travels as
/// <see cref="CheckOutcome.Bytes"/>.
/// </para>
/// </remarks>
public sealed partial class PageWeightCheck : ISiteCheck
{
    public const int MaxParts = 60;

    private const int Parallelism = 4;

    // A part whose size the server will not say is read up to this much and then counted as this.
    private const long ReadCap = 20_000_000;

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(45);

    private readonly HttpClient _httpClient;
    private readonly PageWeightCheckOptions _options;

    public PageWeightCheck(HttpClient httpClient, PageWeightCheckOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        _httpClient = httpClient;
        _options = options ?? PageWeightCheckOptions.Default;
    }

    public string Name => "page-weight";

    private enum Kind
    {
        Image,
        Script,
        Stylesheet,
    }

    private sealed record Part(Uri Address, Kind Kind);

    private sealed record Weighed(Part Part, long? Bytes);

    public async Task<CheckOutcome> RunAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        var (page, unread) = await PageReader
            .ReadAsync(_httpClient, url, "weigh the page", cancellationToken)
            .ConfigureAwait(false);

        if (page is null)
        {
            return unread!;
        }

        Part[] parts;

        try
        {
            parts = [.. PartsOf(page.Html, page.Address).DistinctBy(part => part.Address.AbsoluteUri).Take(MaxParts)];
        }
        catch (RegexMatchTimeoutException)
        {
            return CheckOutcome.Error("The page's HTML is too tangled to read in reasonable time.");
        }

        var weighed = await WeighAllAsync(parts, cancellationToken).ConfigureAwait(false);

        return Evaluate(Encoding.UTF8.GetByteCount(page.Html), weighed);
    }

    private CheckOutcome Evaluate(long htmlBytes, IReadOnlyList<Weighed> weighed)
    {
        var total = htmlBytes + weighed.Sum(w => w.Bytes ?? 0);
        var measured = weighed.Count(w => w.Bytes is not null);
        var unmeasured = weighed.Count - measured;

        var heavyImages = weighed
            .Where(w => w.Part.Kind == Kind.Image && w.Bytes >= _options.ImageWarn)
            .OrderByDescending(w => w.Bytes)
            .ToArray();

        var heaviestImage = heavyImages.FirstOrDefault()?.Bytes ?? 0;

        var status = total >= _options.TotalFail || heaviestImage >= _options.ImageFail ? CheckStatus.Fail
            : total >= _options.TotalWarn || heaviestImage >= _options.ImageWarn ? CheckStatus.Warn
            : CheckStatus.Pass;

        var sentence = new StringBuilder();

        sentence.Append(CultureInfo.InvariantCulture, $"A first visit downloads {Size(total)}: the page and {measured} of the images, scripts and stylesheets it names.");

        if (status == CheckStatus.Pass)
        {
            sentence.Append(" That is light enough for a phone.");
        }
        else if (total >= _options.TotalWarn)
        {
            sentence.Append(CultureInfo.InvariantCulture,
                $" That is heavy for a phone{(total >= _options.TotalFail ? ", and costs visitors on mobile data seconds and money" : string.Empty)}.");
        }

        if (heavyImages.Length > 0)
        {
            var listed = string.Join(", ", heavyImages.Take(3).Select(w => $"{FileName(w.Part.Address)} ({Size(w.Bytes!.Value)})"));
            var more = heavyImages.Length > 3 ? $" and {heavyImages.Length - 3} more" : string.Empty;

            sentence.Append(CultureInfo.InvariantCulture,
                $" {(heavyImages.Length == 1 ? "One image is" : $"{heavyImages.Length} images are")} far heavier than a web page needs: {listed}{more}. Resized to the size it is shown at and saved as WebP or JPEG, a photo like that is a tenth of the weight and looks the same.");
        }
        else if (status != CheckStatus.Pass)
        {
            var heaviest = weighed.Where(w => w.Bytes is not null).OrderByDescending(w => w.Bytes).Take(3)
                .Select(w => $"{FileName(w.Part.Address)} ({Size(w.Bytes!.Value)})");

            sentence.Append(CultureInfo.InvariantCulture, $" The heaviest parts: {string.Join(", ", heaviest)}.");
        }

        if (unmeasured > 0)
        {
            sentence.Append(CultureInfo.InvariantCulture,
                $" {unmeasured} more could not be weighed and are not counted, so the real figure is higher.");
        }

        return new CheckOutcome(status, sentence.ToString()) { Bytes = total };
    }

    private async Task<IReadOnlyList<Weighed>> WeighAllAsync(Part[] parts, CancellationToken cancellationToken)
    {
        var results = new Weighed[parts.Length];

        // A page with sixty parts on a slow server must not hold the whole audit up; whatever is
        // not weighed in time is reported as not weighed.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Budget);

        try
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, parts.Length),
                new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = budget.Token },
                async (index, token) => results[index] = new Weighed(parts[index], await WeighAsync(parts[index].Address, token).ConfigureAwait(false)))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Out of time. What was weighed stands.
        }

        return [.. results.Select((weighed, index) => weighed ?? new Weighed(parts[index], null))];
    }

    /// <summary>The part's size as sent over the network, or <see langword="null"/> when it cannot be had.</summary>
    private async Task<long?> WeighAsync(Uri address, CancellationToken cancellationToken)
    {
        try
        {
            using (var head = await SendAsync(HttpMethod.Head, address, cancellationToken).ConfigureAwait(false))
            {
                if (head.IsSuccessStatusCode && head.Content.Headers.ContentLength is { } length)
                {
                    return length;
                }

                // Gone is gone; asking again with a GET will not make it exist.
                if (head.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                {
                    return null;
                }
            }

            using var get = await SendAsync(HttpMethod.Get, address, cancellationToken).ConfigureAwait(false);

            if (!get.IsSuccessStatusCode)
            {
                return null;
            }

            if (get.Content.Headers.ContentLength is { } declared)
            {
                return declared;
            }

            return await CountAsync(get, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri address, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, address);

        // Ask for what a browser gets. Without this, servers send text uncompressed and a script
        // weighs three to five times what a visitor downloads.
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "br, gzip");

        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> CountAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        await using (stream.ConfigureAwait(false))
        {
            var buffer = new byte[81920];
            long total = 0;
            int read;

            while (total < ReadCap && (read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
            }

            return Math.Min(total, ReadCap);
        }
    }

    private static IEnumerable<Part> PartsOf(string html, Uri page)
    {
        foreach (var link in HtmlTags.Find(html, "link"))
        {
            if (link.GetValueOrDefault("rel", string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("stylesheet", StringComparer.OrdinalIgnoreCase)
                && Resolve(page, link.GetValueOrDefault("href")) is { } stylesheet)
            {
                yield return new Part(stylesheet, Kind.Stylesheet);
            }
        }

        // A <picture> lets the browser choose: it loads the first <source> it can show, and the
        // <img> inside only when none fits. Weighing that <img> would grade the JPEG fallback a
        // site ships for old browsers while every visitor downloads the WebP beside it.
        // Pictures and plain images are read in one pass, in document order, so the limit of
        // parts keeps the first ones on the page whichever kind they are: a hero image above sixty
        // pictures is still weighed.
        foreach (Match match in PictureOrImage().Matches(html))
        {
            var source = match.Value.StartsWith("<picture", StringComparison.OrdinalIgnoreCase)
                ? PictureSource(match.Value)
                : HtmlTags.Find(match.Value, "img").Select(ImageSource).FirstOrDefault();

            if (Resolve(page, source) is { } address)
            {
                yield return new Part(address, Kind.Image);
            }
        }

        foreach (var script in HtmlTags.Find(html, "script"))
        {
            if (Resolve(page, script.GetValueOrDefault("src")) is { } address)
            {
                yield return new Part(address, Kind.Script);
            }
        }
    }

    /// <summary>
    /// The address a browser loads for a <c>&lt;picture&gt;</c>: the first <c>&lt;source&gt;</c>
    /// in a format it shows, preferring one with no <c>media</c> condition, else the
    /// <c>&lt;img&gt;</c> inside.
    /// </summary>
    /// <remarks>
    /// A source for a media condition (<c>(max-width: 600px)</c>) is for some screens only, and
    /// which screen is a guess sitecheck cannot make, so an unconditional one wins. If all are
    /// conditional, the first is taken, which on a mobile-first page is the phone's.
    /// </remarks>
    private static string? PictureSource(string picture)
    {
        // A source with an empty srcset offers nothing; the browser moves on, and so does this.
        var sources = HtmlTags.Find(picture, "source")
            .Where(source => BrowsersShow(source.GetValueOrDefault("type")))
            .Select(source => (Address: FirstCandidate(source.GetValueOrDefault("srcset", string.Empty)), Conditional: source.ContainsKey("media")))
            .Where(source => source.Address is not null)
            .ToArray();

        var chosen = sources.FirstOrDefault(source => !source.Conditional).Address ?? sources.FirstOrDefault().Address;

        return chosen ?? HtmlTags.Find(picture, "img").Select(ImageSource).FirstOrDefault();
    }

    // Every current browser shows these. A source in another format, such as JPEG XL, is skipped
    // the way most browsers skip it.
    private static bool BrowsersShow(string? type) =>
        string.IsNullOrWhiteSpace(type)
        || type.Trim().ToLowerInvariant() is "image/webp" or "image/avif" or "image/jpeg" or "image/png" or "image/gif" or "image/svg+xml";

    // The address the browser loads for an <img>: src, or failing that the first candidate of srcset.
    private static string? ImageSource(IReadOnlyDictionary<string, string> image)
    {
        var source = image.GetValueOrDefault("src");

        return string.IsNullOrWhiteSpace(source) && image.TryGetValue("srcset", out var srcset)
            ? FirstCandidate(srcset)
            : source;
    }

    private static string? FirstCandidate(string srcset) =>
        srcset.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(candidate => candidate.Split(' ', 2)[0])
            .FirstOrDefault();

    // A whole <picture>, or an <img> outside one. The alternation tries <picture> first at each
    // position, so an <img> inside a picture is consumed with it and never read on its own.
    [GeneratedRegex(
        """<picture\b.*?</picture\s*>|<img(?=[\s/>])(?:[^>"']|"[^"]*"|'[^']*')*>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex PictureOrImage();

    private static Uri? Resolve(Uri page, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Uri.TryCreate(page, WebUtility.HtmlDecode(value).Trim(), out var resolved) && resolved.Scheme is "http" or "https"
            ? resolved
            : null;
    }

    // The file name a person recognises, rather than a whole address.
    private static string FileName(Uri address)
    {
        var name = Uri.UnescapeDataString(address.Segments.LastOrDefault()?.Trim('/') ?? string.Empty);

        return name.Length == 0 ? address.Host : name;
    }

    private static string Size(long bytes) => bytes switch
    {
        >= 1_000_000 => (bytes / 1_000_000d).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
        >= 1_000 => (bytes / 1_000d).ToString("0", CultureInfo.InvariantCulture) + " kB",
        _ => bytes.ToString(CultureInfo.InvariantCulture) + " bytes",
    };
}
