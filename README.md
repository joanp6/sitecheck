# sitecheck

[![build](https://github.com/joanp6/sitecheck/actions/workflows/ci.yml/badge.svg)](https://github.com/joanp6/sitecheck/actions/workflows/ci.yml)

CLI that audits and monitors small-business websites: SSL, mobile, load time, broken links, form delivery

## Usage

```bash
dotnet run --project src/SiteCheck.Cli -- audit example.com
```

```text
sitecheck report for https://example.com/

  PASS   ssl-certificate  The certificate is valid until 2026-12-25 UTC, 87 day(s) from now.
  PASS   load-time        The page loaded in 0.12 s.
  PASS   mobile-viewport  The page declares a mobile viewport that follows the device width.

3 check(s): 3 passed, 0 warned, 0 failed, 0 errored.
```

The address can be a bare host (`example.com`, read as `https://example.com/`) or a full
`http://` / `https://` URL.

### Exit codes

Scripts and CI can act on the result without parsing the report.

| Code | Meaning |
|------|---------|
| 0 | Every check passed or only warned |
| 1 | At least one check found a defect in the site |
| 2 | A check could not be evaluated and nothing failed: a problem with sitecheck, not with the site |
| 64 | Bad command line or address |
| 130 | Cancelled with Ctrl+C |

A defect wins over a tooling error: if both happen, the exit code is 1.

### Checks

| Check | Status | What it reports |
|-------|--------|-----------------|
| `ssl-certificate` | ✅ | Trusted, currently valid, and not within 30 days of expiry |
| `load-time` | ✅ | Full page download: warns past 1.5 s, fails past 4 s |
| `mobile-viewport` | ✅ | Declares `width=device-width` (or `initial-scale=1`); warns if it blocks pinch zoom. Reads the tag only: it does not render the page |
| broken links | planned | |
| form delivery | planned | |

A host that cannot be reached is a `FAIL` in every check, never an `ERROR`.

## Testing

`dotnet test` runs the unit suite. [docs/testing.md](docs/testing.md) covers the rest: the
xUnit v3 / Microsoft.Testing.Platform setup and why `global.json` is required for it, the
integration suite, and why the coverage floor is 70 % rather than 100 %.
