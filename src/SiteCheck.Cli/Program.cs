using System.CommandLine;
using System.Text;
using SiteCheck.Cli;
using SiteCheck.Hosting;
using SiteCheck.Reporting;
using SiteCheck.Running;
using SiteCheck.Sites;
using SiteCheck.Watching;

// The console front end depends on SiteCheck.Core, never the other way. This file only wires
// arguments to the core and prints; every decision worth testing lives in the core.

// The Windows console defaults to a legacy code page, which prints "Àngela" as "�ngela". No BOM:
// it would land as stray bytes at the start of anything redirected to a file.
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

var urlsArgument = new Argument<string[]>("url")
{
    Description = "Addresses to audit. A bare host such as example.com is read as https://example.com/.",
    Arity = ArgumentArity.ZeroOrMore,
};

var sitesOption = new Option<FileInfo?>("--sites")
{
    Description = "A JSON file listing the sites to audit. See the README for its format.",
};

var formatOption = new Option<string>("--format")
{
    Description = "How to write the report.",
    DefaultValueFactory = _ => "text",
};
formatOption.AcceptOnlyFromAmong("text", "json", "html", "markdown", "junit");

var outputOption = new Option<FileInfo?>("--output", "-o")
{
    Description = "Write the report to this file instead of the console.",
};

var pageSpeedOption = new Option<bool>("--pagespeed")
{
    Description = "Also ask Google PageSpeed Insights how fast each page feels on a phone. Sends the addresses to Google, "
        + "takes up to a minute per site, and needs a free API key in SITECHECK_PAGESPEED_KEY. See docs/pagespeed.md.",
};

// One option object on both commands: it means the same thing in each.
var allowPrivateOption = new Option<bool>("--allow-private")
{
    Description = "Also connect to this machine and to private networks (localhost, 192.168.x.x, 10.x.x.x...). "
        + "Off by default, so that a page cannot send sitecheck to look behind your firewall. "
        + "Use it to audit a site of your own that is not on the internet yet.",
};

var noRetryOption = new Option<bool>("--no-retry")
{
    Description = "Believe a failure the first time. By default a check that fails or errors is run once more "
        + "two seconds later, so a dropped connection is not reported as a broken site. "
        + "Faster against a site you already know is down.",
};

var exposedFilesOption = new Option<bool>("--exposed-files")
{
    Description = "Also ask each site for files that should never be public: source history (.git), configuration (.env), "
        + "database dumps and backups. Makes about 16 requests no visitor would, which a firewall may take for an attack, "
        + "so use it on sites you look after or have been asked to look at.",
};

var concurrencyOption = new Option<int>("--concurrency")
{
    Description = "How many sites to audit at the same time. Sites on the same host are never audited together, "
        + "and the checks of one site always run in order. 1 audits the sites one after another.",
    DefaultValueFactory = _ => SiteBatch.DefaultConcurrency,
};
concurrencyOption.Validators.Add(result =>
{
    if (result.GetValueOrDefault<int>() < 1)
    {
        result.AddError("--concurrency must be 1 or more.");
    }
});

var onlyOption = new Option<string[]>("--only")
{
    Description = "Run these checks and no others, by name: --only ssl-certificate,tls-config. "
        + "Naming an opt-in check here (pagespeed, exposed-files) is enough to include it.",
    AllowMultipleArgumentsPerToken = true,
};

var skipOption = new Option<string[]>("--skip")
{
    Description = "Leave these checks out, by name: --skip booking-link,copyright-year. "
        + "To skip a check for one site only, list it under \"skip\" in the sites file.",
    AllowMultipleArgumentsPerToken = true,
};

var profileOption = new Option<string?>("--profile")
{
    Description = "Run a named set of checks: "
        + string.Join(", ", CheckSelection.Profiles.Keys.Order(StringComparer.Ordinal))
        + ". Combines with --only (adds to it) and --skip (trims it).",
};

var crawlOption = new Option<int>("--crawl")
{
    Description = "Audit up to this many pages of each site, not only the address given. The pages come from the site's sitemap, "
        + "or from the links on the given page when it has none; links are not followed further. "
        + "Checks about the server (certificate, domain, headers) still run once.",
    DefaultValueFactory = _ => 1,
};
crawlOption.Validators.Add(result =>
{
    if (result.GetValueOrDefault<int>() is < 1 or > 50)
    {
        result.AddError("--crawl must be between 1 and 50.");
    }
});

var audit = new Command("audit", "Audit one or more sites once and report what was found.")
{
    urlsArgument,
    sitesOption,
    formatOption,
    outputOption,
    pageSpeedOption,
    allowPrivateOption,
    noRetryOption,
    concurrencyOption,
    exposedFilesOption,
    onlyOption,
    skipOption,
    profileOption,
    crawlOption,
};

audit.SetAction(async (parse, cancellationToken) =>
{
    var sites = await CollectSitesAsync(parse.GetValue(urlsArgument) ?? [], parse.GetValue(sitesOption), cancellationToken);

    if (sites is null)
    {
        return ExitCodes.Usage;
    }

    using var checks = CreateChecks(parse, sites, selection => new SiteCheckOptions(
        selection,
        PageSpeed: parse.GetValue(pageSpeedOption),
        ExposedFiles: parse.GetValue(exposedFilesOption),
        AllowPrivate: parse.GetValue(allowPrivateOption),
        Retry: !parse.GetValue(noRetryOption)));

    if (checks is null)
    {
        return ExitCodes.Usage;
    }

    return await AuditAndReportAsync(parse, sites, checks, parse.GetValue(crawlOption), cancellationToken);
});

var watchSitesOption = new Option<FileInfo>("--sites")
{
    Description = "A JSON file listing the sites to watch. See the README for its format.",
    Required = true,
};

var stateOption = new Option<FileInfo>("--state")
{
    Description = "Where the history lives between runs. Created on the first run.",
    Required = true,
};

var watch = new Command(
    "watch",
    "Audit every site, compare with the last run, and report what got worse or better. "
    + "Exits 1 when something got worse, so a scheduler can raise the alarm. "
    + "Set SITECHECK_TELEGRAM_TOKEN and SITECHECK_TELEGRAM_CHAT_ID to be messaged about changes.")
{
    watchSitesOption,
    stateOption,
    allowPrivateOption,
    noRetryOption,
    concurrencyOption,
    exposedFilesOption,
    onlyOption,
    skipOption,
    profileOption,
};

watch.SetAction(async (parse, cancellationToken) =>
{
    IReadOnlyList<Site> sites;
    IReadOnlyList<SiteReport> previous;
    var statePath = parse.GetRequiredValue(stateOption).FullName;

    try
    {
        sites = await SiteList.LoadAsync(parse.GetRequiredValue(watchSitesOption).FullName, cancellationToken);
        previous = await WatchState.LoadAsync(statePath, cancellationToken);
    }
    catch (Exception ex) when (ex is SiteListException or FormatException)
    {
        Console.Error.WriteLine(ex.Message);
        return ExitCodes.Usage;
    }

    var telegram = TelegramSettings.FromEnvironment();

    if (telegram.Problem is not null)
    {
        Console.Error.WriteLine(telegram.Problem);
        return ExitCodes.Usage;
    }

    using var checks = CreateChecks(parse, sites, selection => new SiteCheckOptions(
        selection,
        ExposedFiles: parse.GetValue(exposedFilesOption),
        AllowPrivate: parse.GetValue(allowPrivateOption),
        Retry: !parse.GetValue(noRetryOption)));

    if (checks is null)
    {
        return ExitCodes.Usage;
    }

    try
    {
        var reports = await SiteBatch.AuditAsync(sites, WithProgress(checks.AuditAsync, sites.Count), parse.GetValue(concurrencyOption), cancellationToken);

        var comparison = WatchComparison.Of(previous, reports);

        // Saved before anything that can fail, so a notification that does not get through cannot
        // cost the history, and the next run still compares against today.
        await WatchState.SaveAsync(statePath, comparison.History, cancellationToken);

        var summary = WatchReport.RenderText(comparison);
        Console.Write(summary);
        Console.WriteLine();
        Console.Write(ConsoleReport.Render(reports));

        if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } stepSummary)
        {
            await File.AppendAllTextAsync(stepSummary, WatchReport.RenderMarkdown(comparison), cancellationToken);
        }

        if (telegram.Notifier is { } notifier && comparison.Changes.Count > 0)
        {
            try
            {
                await notifier.SendAsync(summary, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                // The alert is the point of watch, so failing to deliver one fails the run: a red
                // run is the alarm of last resort.
                Console.Error.WriteLine($"Could not send the Telegram message: {ex.Message}");
                return ConsoleReport.ExitToolError;
            }
        }

        return comparison.AnythingWorse ? ConsoleReport.ExitFailed : ConsoleReport.ExitOk;
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        Console.Error.WriteLine("Cancelled. The history was left as it was.");
        return ExitCodes.Cancelled;
    }
});

var folderArgument = new Argument<DirectoryInfo>("folder")
{
    Description = "The folder a build wrote the site to, such as dist or public. Its index.html is the home page.",
};

var project = new Command(
    "project",
    "Audit a site that is not published yet, from the folder it was built into. "
    + "The folder is served on this machine for the length of the run and audited like any other site. "
    + "Checks about a live server (certificate, domain, mail, headers, speed) are left out.")
{
    folderArgument,
    formatOption,
    outputOption,
    onlyOption,
    skipOption,
    profileOption,
    crawlOption,
};

project.SetAction(async (parse, cancellationToken) =>
{
    StaticFolderServer server;

    try
    {
        server = StaticFolderServer.Start(parse.GetRequiredValue(folderArgument).FullName);
    }
    catch (Exception ex) when (ex is DirectoryNotFoundException or System.Net.HttpListenerException)
    {
        Console.Error.WriteLine($"The folder could not be served: {ex.Message}");
        return ExitCodes.Usage;
    }

    await using (server)
    {
        Site[] sites = [new Site(server.Address, parse.GetRequiredValue(folderArgument).Name) { Skip = LocalFolder.NotApplicable }];

        // The folder is on this machine, which the network guard would refuse; and a file that is
        // missing from a folder is missing the second time too, so there is nothing to retry.
        using var checks = CreateChecks(parse, sites, selection => new SiteCheckOptions(selection, AllowPrivate: true, Retry: false));

        if (checks is null)
        {
            return ExitCodes.Usage;
        }

        return await AuditAndReportAsync(parse, sites, checks, parse.GetValue(crawlOption), cancellationToken);
    }
});

var root = new RootCommand("sitecheck audits and monitors small-business websites.") { audit, watch, project };
var parsed = root.Parse(args);

if (parsed.Errors.Count > 0)
{
    // Reported here rather than by the library so that a usage error keeps its own exit code:
    // the library's default is 1, which already means "a site failed".
    foreach (var error in parsed.Errors)
    {
        Console.Error.WriteLine(error.Message);
    }

    Console.Error.WriteLine("Run 'sitecheck --help' for usage.");
    return ExitCodes.Usage;
}

return await parsed.InvokeAsync();

// The checks for a run, or null, with the reason already printed, when what was asked for cannot
// be done: a check name that does not exist, or a selection that leaves nothing to run.
SiteChecks? CreateChecks(ParseResult parse, IReadOnlyList<Site> sites, Func<CheckSelection, SiteCheckOptions> options)
{
    CheckSelection selection;

    try
    {
        selection = new CheckSelection(parse.GetValue(onlyOption), parse.GetValue(skipOption), parse.GetValue(profileOption));
    }
    catch (ArgumentException ex)
    {
        Console.Error.WriteLine(ex.Message.Split(" (Parameter", 2)[0]);
        return null;
    }

    var checks = new SiteChecks(options(selection));

    // A typo must not quietly select nothing, or quietly skip nothing.
    var known = checks.AllCheckNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
    var unknown = selection.Unknown(known)
        .Concat(sites.SelectMany(site => site.Skip).Where(name => !known.Contains(name)))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    string? problem = unknown.Length > 0
        ? $"No such check: {string.Join(", ", unknown)}. The checks are: {string.Join(", ", checks.AllCheckNames.Order(StringComparer.Ordinal))}."
        : checks.CheckNames.Count == 0 ? "--only, --skip and --profile leave no check to run."
        : null;

    if (problem is null)
    {
        return checks;
    }

    Console.Error.WriteLine(problem);
    checks.Dispose();
    return null;
}

// The part every one-off audit shares: find the pages, run the checks, write the report.
async Task<int> AuditAndReportAsync(ParseResult parse, IReadOnlyList<Site> given, SiteChecks checks, int pagesPerSite, CancellationToken cancellationToken)
{
    try
    {
        var sites = given;

        if (pagesPerSite > 1)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var expanded = new List<Site>();

            foreach (var site in given)
            {
                // A page reached from two sites listed side by side is still audited once.
                expanded.AddRange((await checks.ExpandAsync(site, pagesPerSite, cancellationToken)).Where(page => seen.Add(page.Url.AbsoluteUri)));
            }

            sites = expanded;
        }

        var concurrency = parse.CommandResult.Command.Options.Contains(concurrencyOption) ? parse.GetValue(concurrencyOption) : 1;
        var reports = await SiteBatch.AuditAsync(sites, WithProgress(checks.AuditAsync, sites.Count), concurrency, cancellationToken);

        var report = parse.GetValue(formatOption) switch
        {
            "json" => JsonReport.Render(reports),
            "html" => HtmlReport.Render(reports, TimeProvider.System.GetUtcNow()),
            "markdown" => MarkdownReport.Render(reports),
            "junit" => JUnitReport.Render(reports),
            _ => ConsoleReport.Render(reports),
        };
        await WriteAsync(report, parse.GetValue(outputOption), cancellationToken);

        return ConsoleReport.ExitCodeFor(reports);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        Console.Error.WriteLine("Cancelled.");
        return ExitCodes.Cancelled;
    }
}

// Says on the terminal when each site is done. A long list is otherwise minutes of silence, and
// silence looks like a hang. On stderr, so the report on stdout stays clean for a pipe, and only
// to a person: redirected (a file, a CI log) it would be noise.
static Func<Site, CancellationToken, Task<SiteReport>> WithProgress(Func<Site, CancellationToken, Task<SiteReport>> audit, int total)
{
    if (Console.IsErrorRedirected || total < 2)
    {
        return audit;
    }

    var done = 0;

    return async (site, cancellationToken) =>
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var report = await audit(site, cancellationToken);
        var line = ConsoleReport.ProgressLine(Interlocked.Increment(ref done), total, report, System.Diagnostics.Stopwatch.GetElapsedTime(started));

        // One call per line: sites finish on different threads, and two lines must not interleave.
        Console.Error.WriteLine(line);

        return report;
    };
}

static async Task<IReadOnlyList<Site>?> CollectSitesAsync(string[] addresses, FileInfo? sitesFile, CancellationToken cancellationToken)
{
    var sites = new List<Site>();

    foreach (var address in addresses)
    {
        if (!SiteUrl.TryParse(address, out var url))
        {
            Console.Error.WriteLine($"\"{address}\" is not a website address.");
            return null;
        }

        sites.Add(new Site(url));
    }

    if (sitesFile is not null)
    {
        try
        {
            sites.AddRange(await SiteList.LoadAsync(sitesFile.FullName, cancellationToken));
        }
        catch (SiteListException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return null;
        }
    }

    if (sites.Count == 0)
    {
        Console.Error.WriteLine("Give at least one address, or a file with --sites. Run 'sitecheck audit --help' for usage.");
        return null;
    }

    return sites;
}

static async Task WriteAsync(string report, FileInfo? output, CancellationToken cancellationToken)
{
    if (output is null)
    {
        Console.Write(report);
        return;
    }

    // UTF-8 without a BOM, whatever the shell would have used for a redirect: Windows PowerShell's
    // '>' writes UTF-16, which some readers of the file then misread.
    await File.WriteAllTextAsync(output.FullName, report, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
    Console.Error.WriteLine($"Report written to {output.FullName}");
}

internal static class ExitCodes
{
    /// <summary>EX_USAGE: the command line or the sites file was wrong.</summary>
    public const int Usage = 64;

    /// <summary>128 + SIGINT, the shell convention for Ctrl+C.</summary>
    public const int Cancelled = 130;
}
