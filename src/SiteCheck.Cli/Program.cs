using SiteCheck.Certificates;
using SiteCheck.Checks;
using SiteCheck.Reporting;
using SiteCheck.Running;

// The console front end depends on SiteCheck.Core, never the other way. This file only wires
// arguments to the core and prints; every decision worth testing lives in the core.
const string Usage = "Usage: sitecheck audit <url>";

if (args is not ["audit", var target])
{
    Console.Error.WriteLine(Usage);
    return 64; // EX_USAGE
}

if (!SiteUrl.TryParse(target, out var url))
{
    Console.Error.WriteLine($"\"{target}\" is not a website address. {Usage}");
    return 64;
}

// Ctrl+C stops the run instead of killing the process mid-write.
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};

using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

// Many sites answer 403 to a request with no User-Agent, which would grade a healthy site as
// broken because of us. Say who we are and where to read about it.
httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("sitecheck/0.1 (+https://github.com/joanp6/sitecheck)");

var runner = new CheckRunner(
    [
        new SslCertificateCheck(new SslStreamCertificateProvider(), TimeProvider.System),
        new LoadTimeCheck(httpClient, TimeProvider.System),
        new MobileCheck(httpClient),
    ],
    TimeProvider.System);

try
{
    var results = await runner.RunAsync(url, cancel.Token);
    Console.Write(ConsoleReport.Render(url, results));
    return ConsoleReport.ExitCodeFor(results);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130; // 128 + SIGINT, the shell convention
}
