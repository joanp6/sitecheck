# sitecheck

[![build](https://github.com/joanp6/sitecheck/actions/workflows/ci.yml/badge.svg)](https://github.com/joanp6/sitecheck/actions/workflows/ci.yml)

CLI that audits and monitors small-business websites: SSL, domain renewal, speed, mobile, broken
links and contact forms. It reports in words a site's owner can act on, and it can watch a list of
sites every day and say only what changed.

## Usage

```bash
dotnet run --project src/SiteCheck.Cli -- audit perruqueria-angela.pages.dev
```

```text
sitecheck report for https://perruqueria-angela.pages.dev/

  PASS   ssl-certificate  The certificate is valid until 2026-11-01 UTC, 32 day(s) from now.
  PASS   https-redirect   Visitors who open http://perruqueria-angela.pages.dev are sent on to the secure site.
  SKIP   domain-expiry    The site lives at an address on pages.dev, which belongs to the hosting platform, so there is no domain of its own to renew.
  PASS   load-time        The page loaded in 0.1 s.
  PASS   mobile-viewport  The page declares a mobile viewport that follows the device width.
  PASS   broken-links     All 11 link(s) on the page work.
  SKIP   contact-form     The page has no form that sends anything, so there is nothing to check.

7 check(s): 5 passed, 0 warned, 0 failed, 0 errored, 2 skipped.
```

An address can be a bare host (`example.com`, read as `https://example.com/`) or a full `http://` /
`https://` URL. `sitecheck --help` and `sitecheck audit --help` list every option.

### Many sites

Pass several addresses, or list them in a file:

```bash
dotnet run --project src/SiteCheck.Cli -- audit --sites sites.json
```

```json
{
  "sites": [
    { "name": "Perruqueria Àngela", "url": "perruqueria-angela.pages.dev" },
    "example.com"
  ]
}
```

### Report formats

| `--format` | For |
|---|---|
| `text` (default) | The terminal |
| `json` | Scripts. The fields are a contract, documented in [`JsonReport`](src/SiteCheck.Core/Reporting/JsonReport.cs) |
| `html` | The site's owner: one self-contained page, problems first, in plain words. Attach it to an email |

`-o report.html` writes to a file instead of the console.

### Verdicts

| | Meaning |
|---|---|
| PASS | Fine |
| WARN | Fine today, but close to becoming a problem |
| FAIL | A defect visitors are affected by |
| ERROR | sitecheck could not evaluate the check. Not a finding about the site |
| SKIP | The question does not apply to this site, such as a domain on a registry that publishes no dates |

A host that cannot be reached is a FAIL in every check, never an ERROR.

### Exit codes

| Code | Meaning |
|------|---------|
| 0 | Every check passed, warned or was skipped |
| 1 | At least one check found a defect in a site |
| 2 | A check could not be evaluated and nothing failed |
| 64 | Bad command line, address or sites file |
| 130 | Cancelled with Ctrl+C |

A defect wins over a tooling error: if both happen, the exit code is 1.

## Checks

| Check | What it reports |
|-------|-----------------|
| `ssl-certificate` | The https certificate is trusted, currently valid, and not within 30 days of expiry. Given an `http://` address, it checks the https site |
| `https-redirect` | A visitor on `http://` is sent on to `https://`. Staying on http fails; nothing answering on http only warns, since browsers try https first |
| `domain-expiry` | When the domain stops being registered, from the registry over RDAP; warns 30 days ahead. Skipped for registries without RDAP (`.es`, `.eu`, `.io`) and for addresses on a hosting platform (`pages.dev`, `github.io`, …) |
| `load-time` | How long the HTML takes to arrive for a first-time visitor: warns past 1.5 s, fails past 4 s |
| `mobile-viewport` | The page declares `width=device-width` (or `initial-scale=1`); warns if it blocks pinch zoom. Reads the tag only: it does not render the page, and it sees the desktop version of sites that serve phones a different page |
| `broken-links` | Links on the page that lead nowhere: 404, 410, 5xx or no answer. Own pages fail, other sites warn. Follows the first 50 links, not the whole site; sites that refuse robots (403, 429) are reported as unverified, not broken |
| `contact-form` | Forms that send (`method="post"` or `mailto:`) point at an address that exists, over https, and not through the visitor's mail program. **It never submits a form**, so it cannot confirm that messages arrive |
| `pagespeed` | Opt-in with `--pagespeed`: Google's mobile performance score for the whole page. Needs a free API key; see [docs/pagespeed.md](docs/pagespeed.md) |

## Watching sites every day

```bash
dotnet run --project src/SiteCheck.Cli -- watch --sites sites.json --state state.json
```

`watch` audits every site, compares with the previous run, and reports only what got worse or
better, so a site that stays broken is reported once rather than every morning. It exits 1 when
something got worse. [`.github/workflows/watch.yml`](.github/workflows/watch.yml) runs it daily on
GitHub Actions, emails you through GitHub when something breaks, and can message you on Telegram.
[docs/watch.md](docs/watch.md) explains the rules and the ten-minute setup.

## Testing

`dotnet test` runs the unit suite. [docs/testing.md](docs/testing.md) covers the rest: the
xUnit v3 / Microsoft.Testing.Platform setup and why `global.json` is required for it, the
integration suite, and why the coverage floor is 70 % rather than 100 %.
