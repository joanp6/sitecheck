using System.Text.RegularExpressions;
using SiteCheck.Checks;
using SiteCheck.Core.Tests.TestDoubles;

namespace SiteCheck.Core.Tests.Checks;

/// <summary>
/// Pins <see cref="ISiteCheck.Name"/> as an identifier rather than a caption.
/// </summary>
/// <remarks>
/// It ends up as the key a report is written against — the column in a CSV, and the field
/// <c>watch</c> will join on to compare one week against the last — so a malformed or
/// duplicated one is a defect, and renaming one is a breaking change.
/// <para>
/// These assert properties of the whole set, never <c>Assert.Equal("load-time", …)</c> against
/// an individual constant: that only restates the implementation, cannot fail except on a
/// deliberate edit, and then fails without telling anyone anything.
/// </para>
/// </remarks>
public sealed class CheckNameTests : IDisposable
{
    private static readonly Regex KebabCase = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant);

    private readonly HttpClient _httpClient = new();
    private readonly ISiteCheck[] _allChecks;

    public CheckNameTests() =>
        _allChecks =
        [
            new SslCertificateCheck(FakeCertificateProvider.NeverCalled(), TimeProvider.System),
            new LoadTimeCheck(_httpClient, TimeProvider.System),
        ];

    public void Dispose() => _httpClient.Dispose();

    [Fact]
    public void EveryCheckInTheLibraryIsListedHere()
    {
        // Without this, adding a check and forgetting to list it would leave the tests below
        // quietly passing over a smaller set than the one that ships.
        var declared = typeof(ISiteCheck).Assembly
            .GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && type.IsAssignableTo(typeof(ISiteCheck)))
            .Select(type => type.Name)
            .Order();

        Assert.Equal(declared, _allChecks.Select(check => check.GetType().Name).Order());
    }

    [Fact]
    public void CheckNamesAreUnique()
    {
        // The bug this is here for: copying a check to write the next one and leaving the
        // original's name behind. Two checks under one key overwrite each other's row in a
        // report, and the report looks complete while it is silently short one result.
        Assert.Distinct(_allChecks.Select(check => check.Name).ToArray());
    }

    [Fact]
    public void CheckNamesAreWellFormedIdentifiers()
    {
        // The pattern also rules out empty and whitespace-only names.
        var offenders = _allChecks
            .Where(check => !KebabCase.IsMatch(check.Name))
            .Select(check => $"{check.GetType().Name} => '{check.Name}'")
            .ToArray();

        Assert.Equal([], offenders);
    }
}
