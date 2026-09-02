namespace SiteCheck.Core.IntegrationTests;

/// <summary>
/// Keeps the integration suite out of an ordinary test run.
/// </summary>
/// <remarks>
/// A skip rather than a filter, on purpose: an ordinary <c>dotnet test</c> still
/// lists these tests and says in one sentence why they did not run. A
/// <c>--filter</c> would make them silently absent, and would have to be repeated
/// correctly at every call site and in every IDE "run all" click.
/// </remarks>
internal static class IntegrationGate
{
    public const string EnvironmentVariable = "SITECHECK_INTEGRATION";

    private static readonly string Reason =
        $"Integration test. Set {EnvironmentVariable}=1 to run it; see docs/testing.md.";

    public static void RequireEnabled() =>
        Assert.SkipUnless(Environment.GetEnvironmentVariable(EnvironmentVariable) == "1", Reason);
}
