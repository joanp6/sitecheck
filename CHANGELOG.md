# Changelog

What changed in each version, for someone deciding whether to upgrade. The format follows
[Keep a Changelog](https://keepachangelog.com/).

## 0.2.0 — 2026-11-12

From 17 checks to 24 by default, a tool that can be installed, and a safer way of talking to the
network.

### Read this before upgrading

- **`localhost` and private addresses need `--allow-private`.** Without it the checks report
  ERROR and the audit exits 2.
- **The first `watch` run will be busy.** Seven checks are new to the history, so everything they
  find is announced as "first seen". The sites did not change; more of them is being looked at.
  The run after it is quiet again.
- **Sites are audited four at a time.** `--concurrency 1` restores one after another.
- **A mistyped option is now an error** (exit 64). It used to be audited as if it were a site.
- **A failing check takes a little longer**, because it gets a second look.

### Added

- **Security checks**, on by default: `security-headers`, `cookies`, `info-leak`, `sri`,
  `cms-version`, `tls-config` and `www-address`. And `exposed-files`, with `--exposed-files`
  only, because it asks a site for files no visitor would.
- **`sitecheck project <folder>`** audits a site from the folder it was built into, before it is
  published.
- **`--crawl N`** audits up to N pages of each site, found through its sitemap.
- **`--only`, `--skip` and `--profile`** (`security`, `seo`, `business`) choose which checks run.
  A sites file entry can carry `"skip": [...]` to leave checks out for that site.
- **A grade per site**, A to F, in every report and as `grade` in the JSON.
- **`--format markdown`** and **`--format junit`**.
- **A progress line** per finished site, on a terminal.
- **Installable as a .NET tool**, so `sitecheck` is a command on the PATH. See
  [Install](README.md#install).
- `--allow-private`, `--no-retry`, `--concurrency`.

### Changed

- **The page is downloaded once per site** and shared by every check that reads it, instead of
  once per check.
- **A check that fails or errors is run once more** two seconds later before it is believed, up
  to three confirmed failures per site.
- **sitecheck only connects to the public internet**, redirects and links included.
- A page over 5 MB is not read, redirects stop at 10, and an address that serves a file rather
  than a page is a SKIP in the checks that read HTML.
- When sitecheck itself declines to read a page, every check reports ERROR. Five older checks
  used to report a FAIL.
- The `User-Agent` carries the real version.

### For the repository

- Dependabot keeps NuGet packages and GitHub Actions current; actions are pinned to commits.
- A package with a known vulnerability, direct or transitive, fails the build.
- `System.CommandLine` 3.0.0.

## 0.1.0

The first seventeen checks, `audit` and `watch`, and reports as text, JSON and HTML: certificate,
https redirect, domain expiry, email authentication, load time, mobile viewport, broken links,
contact form, mixed content, click-to-call, search snippet, local business, legal pages, page
weight, share preview, booking link and copyright year. `pagespeed` on request.
