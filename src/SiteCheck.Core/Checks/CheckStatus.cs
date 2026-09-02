namespace SiteCheck.Checks;

/// <summary>
/// The verdict a check reaches about a website.
/// </summary>
public enum CheckStatus
{
    /// <summary>The site meets the expectation.</summary>
    Pass,

    /// <summary>The site still meets the expectation, but is close to not meeting it.</summary>
    Warn,

    /// <summary>The site does not meet the expectation.</summary>
    Fail,

    /// <summary>
    /// The expectation could not be evaluated. This says something about our tooling,
    /// not about the site, and must never be reported to a customer as a defect.
    /// </summary>
    Error,
}
