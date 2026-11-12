using SiteCheck.Running;

namespace SiteCheck.Core.Tests.Running;

public sealed class CheckSelectionTests
{
    [Fact]
    public void Everything_IncludesEveryOrdinaryCheck_AndNoOptInOne()
    {
        Assert.True(CheckSelection.Everything.Includes("load-time"));
        Assert.False(CheckSelection.Everything.Includes("exposed-files", optIn: true));
    }

    [Fact]
    public void AnOptInCheck_IsIncludedWhenItsOwnOptionIsGiven()
    {
        Assert.True(CheckSelection.Everything.Includes("exposed-files", optIn: true, optedIn: true));
    }

    [Fact]
    public void Only_IncludesWhatItNames_AndNothingElse()
    {
        var selection = new CheckSelection(only: ["ssl-certificate", "tls-config"]);

        Assert.True(selection.Includes("ssl-certificate"));
        Assert.True(selection.Includes("tls-config"));
        Assert.False(selection.Includes("load-time"));
    }

    [Theory]
    [InlineData("ssl-certificate,tls-config")]     // one token with commas
    [InlineData(" ssl-certificate , TLS-Config ")] // as people type it
    public void Only_ReadsNamesSeparatedByCommas_InAnyCase(string typed)
    {
        var selection = new CheckSelection(only: [typed]);

        Assert.True(selection.Includes("ssl-certificate"));
        Assert.True(selection.Includes("tls-config"));
    }

    [Fact]
    public void Only_NamingAnOptInCheck_IsAskingForIt()
    {
        var selection = new CheckSelection(only: ["exposed-files"]);

        Assert.True(selection.Includes("exposed-files", optIn: true, optedIn: false));
    }

    [Fact]
    public void Skip_LeavesOutWhatItNames_AndKeepsTheRest()
    {
        var selection = new CheckSelection(skip: ["booking-link"]);

        Assert.False(selection.Includes("booking-link"));
        Assert.True(selection.Includes("load-time"));
    }

    [Fact]
    public void Skip_WinsOverEverythingElse()
    {
        var selection = new CheckSelection(only: ["tls-config", "exposed-files"], skip: ["tls-config", "exposed-files"], profile: "security");

        Assert.False(selection.Includes("tls-config"));
        Assert.False(selection.Includes("exposed-files", optIn: true, optedIn: true));
    }

    [Fact]
    public void Profile_IncludesItsChecks_AndNoOthers()
    {
        var selection = new CheckSelection(profile: "security");

        Assert.True(selection.Includes("security-headers"));
        Assert.False(selection.Includes("booking-link"));
    }

    [Fact]
    public void Profile_DoesNotSwitchOnAnOptInCheckByListingIt()
    {
        // exposed-files is in "security", and still asks a site for files no visitor would.
        var selection = new CheckSelection(profile: "security");

        Assert.False(selection.Includes("exposed-files", optIn: true, optedIn: false));
        Assert.True(selection.Includes("exposed-files", optIn: true, optedIn: true));
    }

    [Fact]
    public void ProfileAndOnly_AddUp()
    {
        var selection = new CheckSelection(only: ["booking-link"], profile: "security");

        Assert.True(selection.Includes("booking-link"));
        Assert.True(selection.Includes("security-headers"));
        Assert.False(selection.Includes("click-to-call"));
    }

    [Fact]
    public void Profile_IsReadInAnyCase()
    {
        Assert.True(new CheckSelection(profile: " SEO ").Includes("search-snippet"));
    }

    [Fact]
    public void AnUnknownProfile_Throws_NamingTheRealOnes()
    {
        var thrown = Assert.Throws<ArgumentException>(() => new CheckSelection(profile: "speed"));

        Assert.Contains("business, security, seo", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_ListsTheNamesThatAreNotChecks_SoATypoIsNotSilent()
    {
        var selection = new CheckSelection(only: ["ssl-certificate", "tls-confg"], skip: ["bookin-link", "load-time"]);

        Assert.Equal(["bookin-link", "tls-confg"], selection.Unknown(["ssl-certificate", "load-time", "tls-config", "booking-link"]));
    }

    [Fact]
    public void Unknown_WhenEveryNameIsReal_IsEmpty()
    {
        Assert.Empty(new CheckSelection(only: ["LOAD-TIME"]).Unknown(["load-time"]));
    }
}
