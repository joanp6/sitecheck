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

    /// <summary>
    /// The question does not apply to this site, or the facts needed to answer it are not
    /// published anywhere the check can read: a page with no form, a registry that does not
    /// say when its domains expire. Says nothing about the site either way.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="Error"/> because nothing went wrong: asking again tomorrow
    /// gives the same answer. Treating it as an error would fail every run for every site on a
    /// registry that publishes no dates, and teach people to ignore the exit code.
    /// </remarks>
    Skip,
}
