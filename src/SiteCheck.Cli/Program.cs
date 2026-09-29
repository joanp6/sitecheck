using System.CommandLine;
using System.Text;
using SiteCheck.Cli;
using SiteCheck.Reporting;
using SiteCheck.Running;
using SiteCheck.Sites;

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
formatOption.AcceptOnlyFromAmong("text", "json");

var outputOption = new Option<FileInfo?>("--output", "-o")
{
    Description = "Write the report to this file instead of the console.",
};

var audit = new Command("audit", "Audit one or more sites once and report what was found.")
{
    urlsArgument,
    sitesOption,
    formatOption,
    outputOption,
};

audit.SetAction(async (parse, cancellationToken) =>
{
    var sites = await CollectSitesAsync(parse.GetValue(urlsArgument) ?? [], parse.GetValue(sitesOption), cancellationToken);

    if (sites is null)
    {
        return ExitCodes.Usage;
    }

    using var checks = new SiteChecks();

    try
    {
        var reports = new List<SiteReport>(sites.Count);

        foreach (var site in sites)
        {
            reports.Add(await checks.Runner.AuditAsync(site, cancellationToken));
        }

        var report = parse.GetValue(formatOption) == "json" ? JsonReport.Render(reports) : ConsoleReport.Render(reports);
        await WriteAsync(report, parse.GetValue(outputOption), cancellationToken);

        return ConsoleReport.ExitCodeFor(reports);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        Console.Error.WriteLine("Cancelled.");
        return ExitCodes.Cancelled;
    }
});

var root = new RootCommand("sitecheck audits and monitors small-business websites.") { audit };
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
