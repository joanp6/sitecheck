using System.Globalization;
using System.Text;
using System.Xml;
using SiteCheck.Checks;
using SiteCheck.Running;

namespace SiteCheck.Reporting;

/// <summary>
/// Renders a run in the JUnit XML format, which every CI server knows how to show as test results.
/// </summary>
/// <remarks>
/// <para>
/// Jenkins, GitLab, Azure DevOps and GitHub (through an action) all read this format and turn it
/// into a table of passed and failed tests with history and trends, with nothing to install.
/// Publishing a run this way puts "the salon's certificate expires in a week" where the team
/// already looks every morning.
/// </para>
/// <para>
/// The mapping: a site is a test suite, a check is a test case.
/// </para>
/// <list type="table">
/// <item><term>FAIL</term><description><c>&lt;failure&gt;</c>: the site has a defect</description></item>
/// <item><term>ERROR</term><description><c>&lt;error&gt;</c>: sitecheck could not tell, which CI servers show apart from failures, as intended</description></item>
/// <item><term>SKIP</term><description><c>&lt;skipped&gt;</c></description></item>
/// <item><term>WARN</term><description>a passing test with the warning in <c>&lt;system-out&gt;</c>. The format has no "warning", and a red build for something that is fine today would teach people to ignore red builds</description></item>
/// <item><term>PASS</term><description>a passing test</description></item>
/// </list>
/// <para>
/// There is no official schema; this follows the dialect Jenkins and Ant write, which the others
/// accept.
/// </para>
/// </remarks>
public static class JUnitReport
{
    public static string Render(IReadOnlyList<SiteReport> reports)
    {
        ArgumentNullException.ThrowIfNull(reports);

        var text = new StringBuilder();

        // The writer escapes every value, so a detail quoting the audited page cannot break out of
        // its attribute. What it cannot do is write characters XML forbids outright; see Clean.
        using (var xml = XmlWriter.Create(text, new XmlWriterSettings { Indent = true, OmitXmlDeclaration = true }))
        {
            xml.WriteStartElement("testsuites");
            xml.WriteAttributeString("name", "sitecheck");
            WriteCounts(xml, [.. reports.SelectMany(report => report.Results)]);

            foreach (var report in reports)
            {
                xml.WriteStartElement("testsuite");
                xml.WriteAttributeString("name", Clean(report.Site.DisplayName));
                WriteCounts(xml, report.Results);
                xml.WriteAttributeString("timestamp", report.CheckedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));

                xml.WriteStartElement("properties");
                xml.WriteStartElement("property");
                xml.WriteAttributeString("name", "url");
                xml.WriteAttributeString("value", report.Site.Url.AbsoluteUri);
                xml.WriteEndElement();
                xml.WriteEndElement();

                foreach (var result in report.Results)
                {
                    WriteCase(xml, report, result);
                }

                xml.WriteEndElement();
            }

            xml.WriteEndElement();
        }

        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + Environment.NewLine + text + Environment.NewLine;
    }

    private static void WriteCase(XmlWriter xml, SiteReport report, CheckResult result)
    {
        xml.WriteStartElement("testcase");
        xml.WriteAttributeString("name", result.CheckName);

        // CI servers group cases by this, so the host keeps two sites' checks apart.
        xml.WriteAttributeString("classname", report.Site.Url.Host);
        xml.WriteAttributeString("time", Seconds(result.Duration));

        var detail = Clean(result.Detail);

        switch (result.Status)
        {
            case CheckStatus.Fail:
                xml.WriteStartElement("failure");
                xml.WriteAttributeString("message", detail);
                xml.WriteAttributeString("type", "defect");
                xml.WriteEndElement();
                break;

            case CheckStatus.Error:
                xml.WriteStartElement("error");
                xml.WriteAttributeString("message", detail);
                xml.WriteAttributeString("type", "could-not-check");
                xml.WriteEndElement();
                break;

            case CheckStatus.Skip:
                xml.WriteStartElement("skipped");
                xml.WriteAttributeString("message", detail);
                xml.WriteEndElement();
                break;

            case CheckStatus.Warn:
                xml.WriteElementString("system-out", "WARN: " + detail);
                break;

            default:
                xml.WriteElementString("system-out", detail);
                break;
        }

        xml.WriteEndElement();
    }

    private static void WriteCounts(XmlWriter xml, IReadOnlyList<CheckResult> results)
    {
        string Count(CheckStatus status) => results.Count(result => result.Status == status).ToString(CultureInfo.InvariantCulture);

        xml.WriteAttributeString("tests", results.Count.ToString(CultureInfo.InvariantCulture));
        xml.WriteAttributeString("failures", Count(CheckStatus.Fail));
        xml.WriteAttributeString("errors", Count(CheckStatus.Error));
        xml.WriteAttributeString("skipped", Count(CheckStatus.Skip));
        xml.WriteAttributeString("time", Seconds(TimeSpan.FromTicks(results.Sum(result => result.Duration.Ticks))));
    }

    private static string Seconds(TimeSpan duration) => duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// Drops the characters XML 1.0 has no way to write at all, such as most control codes. A page
    /// can contain them, a detail can quote the page, and the writer would throw rather than
    /// produce a file no reader accepts.
    /// </summary>
    private static string Clean(string text) =>
        text.All(XmlConvert.IsXmlChar) ? text : string.Concat(text.Where(XmlConvert.IsXmlChar));
}
