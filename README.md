# sitecheck

[![build](https://github.com/joanp6/sitecheck/actions/workflows/ci.yml/badge.svg)](https://github.com/joanp6/sitecheck/actions/workflows/ci.yml)

CLI that audits and monitors small-business websites: SSL, domain renewal, speed, mobile, broken
links and contact forms. It reports in words a site's owner can act on, and it can watch a list of
sites every day and say only what changed.

## Install

sitecheck is a [.NET tool](https://learn.microsoft.com/dotnet/core/tools/global-tools). With the
[.NET 10 SDK](https://dotnet.microsoft.com/download) installed, build the package once and install
it from the folder it lands in:

```bash
dotnet pack src/SiteCheck.Cli -c Release
```

```bash
dotnet tool install --global sitecheck --add-source ./artifacts
```

From then on `sitecheck` is a command anywhere: `sitecheck audit example.com`. The examples below
use `dotnet run --project src/SiteCheck.Cli --` instead, which runs it from a checkout without
installing anything; the two are interchangeable.

## Usage

```bash
dotnet run --project src/SiteCheck.Cli -- audit perruqueria-angela.pages.dev
```

```text
sitecheck report for https://perruqueria-angela.pages.dev/

  PASS   ssl-certificate  The certificate is valid until 2026-11-01 UTC, 32 day(s) from now.
  PASS   https-redirect   Visitors who open http://perruqueria-angela.pages.dev are sent on to the secure site.
  SKIP   domain-expiry    The site lives at an address on pages.dev, which belongs to the hosting platform, so there is no domain of its own to renew.
  SKIP   email-auth       The site lives at an address on pages.dev, which belongs to the hosting platform, so its mail settings are not the business's.
  PASS   load-time        The page loaded in 0.1 s.
  PASS   mobile-viewport  The page declares a mobile viewport that follows the device width.
  PASS   broken-links     All 11 link(s) on the page work.
  SKIP   contact-form     The page has no form that sends anything, so there is nothing to check.
  PASS   mixed-content    Everything the page's HTML loads comes over https.
  PASS   click-to-call    Visitors on a phone can call with one tap and open a WhatsApp chat.
  PASS   search-snippet   The page gives Google its own title, "Hair salon in Mataró", and a description to show in results.
  PASS   local-business   The page tells Google it is a HairSalon, with its address and opening hours.
  PASS   legal-pages      The page links to a legal notice and a privacy policy. It loads nothing known to set cookies, so no cookie policy was looked for. sitecheck only sees the links, not what the pages say.
  PASS   page-weight      A first visit downloads 640 kB: the page and 14 of the images, scripts and stylesheets it names. That is light enough for a phone.
  PASS   share-preview    Shared on WhatsApp or Facebook, a link to the page shows its own title and picture, and the page has a favicon.
  PASS   booking-link     Visitors can book online through Booksy and find the shop on a map.
  PASS   copyright-year   The page's copyright notice says 2026, which looks current.

17 check(s): 14 passed, 0 warned, 0 failed, 0 errored, 3 skipped.
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

Up to four sites are audited at the same time, and the reports still come out in the order the
sites were given. Two addresses on the same host are never audited together, and the checks of one
site always run one after another, so `load-time` is not measured against a server sitecheck is
itself keeping busy. `--concurrency 8` audits more at once; `--concurrency 1` goes back to one
site at a time.

With more than one site, a line is printed to the terminal as each one finishes
(`[3/12] Perruqueria Àngela: 1 failed, 2 warned (6.1 s)`), so a long list does not look like a
hang. It goes to stderr and only when a person is watching: piped or in CI, it is not written.

An entry can also say which checks do not apply to that site, for the finding that is known and
accepted, like a shop that takes bookings by phone on purpose:

```json
{ "name": "Perruqueria Àngela", "url": "perruqueria-angela.pages.dev", "skip": ["booking-link"] }
```

A skipped check is not run and does not appear in that site's report.

### More than the home page

```bash
dotnet run --project src/SiteCheck.Cli -- audit perruqueria-angela.pages.dev --crawl 10
```

`--crawl 10` audits up to ten pages of each site instead of only the address given. The pages come
from the site's sitemap (`/sitemap.xml`, or wherever `robots.txt` points), nearest the top first.
A site with no sitemap gets the pages its given page links to that answer. Links are not followed
from page to page, so the number of requests is known before the first is made, and the limit is 50.

Each extra page is reported as a site of its own, named `Site › /path`. The checks whose answer
belongs to the server rather than the page (certificate, domain, mail, TLS, headers, `www`) run
once, on the address given. `watch` does not crawl: its history is kept per address.

### Before publishing

```bash
dotnet run --project src/SiteCheck.Cli -- project ./dist --crawl 10
```

`project` audits a site from the folder it was built into, before it has an address. The folder is
served on this machine (on `127.0.0.1`, reachable from nowhere else) for the length of the run, the
way static hosts serve it: a folder answers with its `index.html`, `/about` with `about.html`, and
a missing file with a 404 and the folder's `404.html`. Nothing outside the folder is served.

Checks about a live server are left out, because there is none yet: certificate, domain, mail,
TLS, `www`, headers, cookies and speed. `mixed-content` skips too, since the local copy is served
over plain http. It exits like `audit`, so a build can refuse to deploy a site with a broken link.

### Choosing checks

| Option | Runs |
|---|---|
| `--only ssl-certificate,tls-config` | These and no others. Naming an opt-in check (`pagespeed`, `exposed-files`) here is enough to include it |
| `--skip booking-link,copyright-year` | Everything else |
| `--profile security` | A named set: `security` (could someone abuse the site or its name?), `seo` (will people find it, and does it open quickly?) or `business` (can a customer do what they came for?) |

They combine: `--profile` and `--only` add up, and `--skip` trims the result. A name that is not a
check is an error, not an empty run. A profile does not switch on an opt-in check by listing it.

### Grade

Each site gets a letter, for comparing sites at a glance or one site across months:

| | |
|---|---|
| A | Nothing to fix and nothing to look at |
| B | No defects, up to 3 warnings |
| C | No defects, 4 or more warnings |
| D | 1 defect |
| F | 2 or more defects |

It is a rule you can recite, not a score with weights. Errors and skips count neither way, but when
fewer than half the checks reached a verdict (a site behind a robot shield, say) no grade is given.
The exit code and `watch` do not use it.

### Report formats

| `--format` | For |
|---|---|
| `text` (default) | The terminal |
| `markdown` | Pasting into an issue, a pull request or a chat. Problems only, worst first; what passed is counted, not listed |
| `junit` | CI servers. Jenkins, GitLab and Azure DevOps show it as test results with history: a site is a suite, a check is a test. FAIL is a failure, ERROR an error, SKIP skipped; WARN passes with the warning in its output, so a build does not go red for something that is fine today |
| `json` | Scripts. The fields are a contract, documented in [`JsonReport`](src/SiteCheck.Core/Reporting/JsonReport.cs) |
| `html` | The site's owner: one self-contained page, problems first, in plain words. Attach it to an email |

`-o report.html` writes to a file instead of the console.

In `json`, `detail` is a sentence for people and may be reworded at any time. Read the facts from
their own fields instead:

| Field | On | Meaning |
|---|---|---|
| `grade` | every site | `A`, `B`, `C`, `D` or `F`; see [Grade](#grade). Omitted when too little could be checked to give one |
| `status` | every result | `pass`, `warn`, `fail`, `error` or `skip` |
| `durationMs` | every result | How long the check ran |
| `validUntil` | `ssl-certificate`, `domain-expiry` | When the certificate or domain expires. Omitted when unknown |
| `elapsedMs` | `load-time` | How long the page took to arrive in full, in whole milliseconds. Omitted when no answer came |
| `statusCode` | `load-time`; also `mobile-viewport`, `broken-links`, `contact-form` and the checks added since (`mixed-content`, `click-to-call`, `search-snippet`, `local-business`, `legal-pages`, `page-weight`, `share-preview`, `booking-link`, `copyright-year`, `security-headers`, `cookies`, `info-leak`, `sri`, `cms-version`) when they skip a site that turns robots away; the checks added since also when the page answers with an error | The HTTP status the site answered with, errors included (a 403 or 429 means the site turns robots away, which is a `skip`, not a defect). Omitted when no response arrived |
| `bytes` | `page-weight` | How much a first visit downloads: the HTML plus the images, scripts and stylesheets it names, as sent over the network (compressed). A floor: fonts and what scripts load are not counted |

New fields are only ever added within format `1`, so reports written by older versions still read.

### Verdicts

| | Meaning |
|---|---|
| PASS | Fine |
| WARN | Fine today, but close to becoming a problem |
| FAIL | A defect visitors are affected by |
| ERROR | sitecheck could not evaluate the check. Not a finding about the site |
| SKIP | The question does not apply to this site, such as a domain on a registry that publishes no dates |

A host that cannot be reached is a FAIL in the original checks. Checks added since (`mixed-content`,
`click-to-call`, `search-snippet`, `local-business`, `legal-pages`, `page-weight`, `share-preview`,
`booking-link`, `copyright-year`, `security-headers`, `cookies`, `info-leak`, `sri`, `cms-version`)
report it as an ERROR instead: `load-time` already fails a site that is down, and saying it again
in every new check would bury the real findings.

A page that answers 403 or 429 is a SKIP in every check that reads it (`load-time`,
`mobile-viewport`, `broken-links`, `contact-form`, `mixed-content`, `click-to-call`, `search-snippet`, `local-business`, `legal-pages`, `page-weight`, `share-preview`, `booking-link`, `copyright-year`, `security-headers`, `cookies`, `info-leak`, `sri`, `cms-version`), never a FAIL: it is what a firewall or bot
shield answers sitecheck while people open the page fine, so it says nothing about the business.
The result carries `statusCode` so a script can say why the page went unchecked. A 401 still fails,
since a page behind a password is what every visitor gets.

An address that serves a file rather than a web page (a PDF, an image) is a SKIP in the checks
that read HTML: there is no page to find a defect in. A page larger than 5 MB is not read.

The page is downloaded once per site and every check that reads it gets that same copy, so an
audit does not hit a site with a dozen requests for one page. `load-time` still makes its own
request, since it measures a first visit.

### Second looks

A check that comes back FAIL or ERROR is run once more, two seconds later, and the second answer
is the one reported. Once three of a site's checks have failed twice the rest are believed the first
time, so a site that is really down is not asked everything twice. A connection dropped at the wrong moment would otherwise be a broken site
today and a fixed one tomorrow, and `watch` would announce both. `durationMs` covers both tries
and the wait. `--no-retry` turns it off, which is quicker against a site you know is down.

### Private addresses

sitecheck only connects to addresses on the public internet. A site at `localhost`, on
`192.168.x.x` or `10.x.x.x`, or a page that redirects or links to one, is refused, and the report
says so. Otherwise a page could send sitecheck to look at what is behind your firewall (your
router, a database's admin page, a cloud machine's credentials at `169.254.169.254`) and read the
answer from the report. To audit a site of your own that is not on the internet yet, pass
`--allow-private`:

```bash
dotnet run --project src/SiteCheck.Cli -- audit http://localhost:5173 --allow-private
```

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
| `email-auth` | Whether mail forged in the name of the site's domain is stopped, from its public DNS records (SPF, DMARC, MX), asked of this machine's own DNS servers; the site is not touched. No DMARC, two of them, two SPF records or `+all` fail; `p=none`, `pct` under 100 or no SPF warn. A domain with no mail, or a hosting platform's address, skips; DNS that cannot be asked is an ERROR. DKIM cannot be checked from outside |
| `load-time` | How long the HTML takes to arrive for a first-time visitor: warns past 1.5 s, fails past 4 s |
| `mobile-viewport` | The page declares `width=device-width` (or `initial-scale=1`); warns if it blocks pinch zoom. Reads the tag only: it does not render the page, and it sees the desktop version of sites that serve phones a different page |
| `broken-links` | Links on the page that lead nowhere: 404, 410, 5xx or no answer. Own pages fail, other sites warn. Follows the first 50 links, not the whole site; sites that refuse robots (403, 429) are reported as unverified, not broken |
| `contact-form` | Forms that send (`method="post"` or `mailto:`) point at an address that exists, over https, and not through the visitor's mail program. **It never submits a form**, so it cannot confirm that messages arrive |
| `mixed-content` | A secure page loads nothing over plain http. Scripts, stylesheets and frames over http fail, since browsers block them and the page shows up incomplete; images and media only warn, since browsers try https first. Reads the HTML only: addresses inside CSS files or added by script are not seen |
| `click-to-call` | A visitor on a phone can call (`tel:`) or open WhatsApp (`wa.me`, `api.whatsapp.com/send`) with one tap. Neither only warns, and says which printed number to turn into a link; a link to no real number (`tel:` with nothing, a template's `123456789`) fails. Reads the given page only, usually the home page |
| `search-snippet` | What Google shows in results: the page's `<title>` and `<meta name="description">`. No title fails. A template's title ("Home", "Document"…), one over 60 characters, no description, a template's or placeholder description, or one outside 50–160 characters warns |
| `local-business` | The page tells search engines, in schema.org's format (JSON-LD, or microdata read loosely), that it is a `LocalBusiness` or a kind of it (`HairSalon`, `HobbyShop`, …) with an address and opening hours. Missing any of that, or a JSON-LD block that does not parse, warns. Never fails: visitors are not affected |
| `legal-pages` | Links to a legal notice (*aviso legal*, LSSI), a privacy policy (GDPR) and, when the page loads something known to set cookies (analytics, ad pixels, YouTube or Google Maps embeds), a cookie policy or a consent tool. Recognised by their words and addresses in Spanish, Catalan and English. A missing one warns, never fails: it sees only links, not what the pages say, and is not legal advice |
| `page-weight` | What a first visit downloads: the HTML and up to 60 images (for a `<picture>`, the source a browser picks, not the fallback `<img>`), scripts and stylesheets it names, each sized with a HEAD request. Warns on an image over 500 kB or a page over 3 MB; fails on an image over 2 MB or a page over 6 MB, naming the heaviest. Does not run the page, so fonts and what scripts load are not counted |
| `share-preview` | A link shared on WhatsApp or Facebook unfolds into a preview: the page has an `og:image` (a full address that loads an image, not a web page), and a favicon (declared, or at `/favicon.ico`). Missing or broken warns, never fails; an address the site will not let sitecheck ask about concludes nothing |
| `booking-link` | A visitor can book online (a link to, or widget from, Booksy, Treatwell, Fresha, Calendly…, or a link of the site's own that reads "Reservar cita", "Demana cita", "Book now"…) or find the shop on a map (Google Maps, Apple Maps, OpenStreetMap, Waze, `g.page`). Either one passes, saying what the other would add; neither warns. Never fails. Links are recognised, not followed |
| `copyright-year` | The year next to ©, (c) or "Copyright" in the page's text (the later one of a range, the latest of several). Warns when it is 3 or more years old, since it makes the site look abandoned; never fails, since a kept site may simply never have updated its footer. No year, or one written by script, skips |
| `security-headers` | The site switches on the three protections browsers offer but leave off: `Strict-Transport-Security` (always use https), `X-Content-Type-Options: nosniff` (do not guess file types) and `X-Frame-Options` or a CSP `frame-ancestors` (do not let other sites frame it). Any missing warns, never fails: nothing is broken for a visitor, the site is only easier to abuse. A missing `Content-Security-Policy` or `Referrer-Policy` is mentioned on a pass, not held against the site. Skipped for a page served over plain http |
| `cookies` | The cookies the server sets with the page are guarded: `Secure` (never sent over plain http), and `HttpOnly` on the ones that look like a login by their name (`PHPSESSID`, `wordpress_logged_in_…`, anything with "session", "auth" or "token", anti-forgery tokens excepted). `SameSite=None` without `Secure`, which browsers throw away, is reported too. Any of these warns, never fails. A missing `SameSite` is fine, since browsers default to `Lax`. Sees only cookies set in the headers, not the ones scripts add, and never reports a cookie's value |
| `info-leak` | The server does not announce which version of its software it runs, in `Server`, `X-Powered-By`, `X-AspNet-Version`, `X-AspNetMvc-Version` or `X-Generator`. A version number warns, since robots use it to pick out servers with known holes; a bare name (`nginx`, `cloudflare`) passes. Does not judge whether the version is out of date |
| `sri` | Scripts and stylesheets from public library servers (`cdnjs.cloudflare.com`, `cdn.jsdelivr.net`, `unpkg.com`, `code.jquery.com`…) carry an `integrity` fingerprint, so a tampered copy is refused. Without one warns. Analytics, maps and widgets are left alone: their files change by design, so they cannot be fingerprinted. A script from a domain taken over in 2024 to serve malware (`polyfill.io`, `bootcss.com`…) fails. Reads the HTML only |
| `cms-version` | What the page says it is built with, from its `<meta name="generator">` tags, plugins included. Announcing an exact version warns; for WordPress the version is compared with the current release, asked of wordpress.org once per run, and being behind is said. On a WordPress site it also asks for `/xmlrpc.php` and mentions it when open. A page that names no system skips. Never fails. A site that hides its generator tag is not seen through |
| `tls-config` | The server refuses TLS 1.0 and 1.1, retired in 2021. Accepting either warns, never fails: no current browser uses them, but card processors forbid them and it suggests a server nobody maintains. Asked by sending the opening message of a handshake offering only that version, written by hand, because a current operating system will not offer them and would make every server look modern |
| `www-address` | The other spelling of the address, with or without `www.`, leads somewhere. Nowhere warns: visitors who type it get an error. An alias (CNAME) to a name that no longer exists fails, since whoever registers that name decides what the address shows (a subdomain takeover). Asks the DNS only; the site is not touched. Subdomains and hosting-platform addresses skip |
| `pagespeed` | Opt-in with `--pagespeed`: Google's mobile performance score for the whole page. Needs a free API key; see [docs/pagespeed.md](docs/pagespeed.md) |
| `exposed-files` | Opt-in with `--exposed-files`: files left on the server that were never meant to be public. Asks for 16 well-known ones (`/.git/HEAD`, `/.env`, `wp-config.php.bak`, `backup.sql`, `backup.zip`, `phpinfo.php`…) and counts one as exposed only when the answer **is** that file, recognised by its first bytes, since many sites answer 200 to everything. Source history, configuration, dumps and backups fail; pages that only describe the server (`phpinfo`, Apache status, a folder listing) warn. Reads at most 4 kB of an answer and never reports a file's contents, only its address. Opt-in because these are requests no visitor makes and a firewall may take them for an attack: use it on sites you look after or were asked to look at |

## Watching sites every day

```bash
dotnet run --project src/SiteCheck.Cli -- watch --sites sites.json --state state.json
```

`watch` audits every site, compares with the previous run, and reports only what got worse or
better, so a site that stays broken is reported once rather than every morning. It exits 1 when
something got worse. [`.github/workflows/watch.yml`](.github/workflows/watch.yml) runs it daily on
GitHub Actions, emails you through GitHub when something breaks, and can message you on Telegram.
[docs/watch.md](docs/watch.md) explains the rules and the ten-minute setup.

## Dependencies

sitecheck has one runtime dependency, `System.CommandLine`. Three things keep it and the build honest:

- Every restore asks NuGet whether a package in use, or one it pulls in, has a known vulnerability
  (`NuGetAudit` in [`Directory.Build.props`](Directory.Build.props)). Warnings are errors here, so
  a vulnerable package stops the build.
- The GitHub Actions the workflows use are pinned to a commit, not a tag: a tag can be moved to
  different code, a commit cannot.
- [Dependabot](.github/dependabot.yml) opens a pull request each week for packages and actions
  with a newer version, which is also what moves those pins forward.

## Testing

`dotnet test` runs the unit suite. [docs/testing.md](docs/testing.md) covers the rest: the
xUnit v3 / Microsoft.Testing.Platform setup and why `global.json` is required for it, the
integration suite, and why the coverage floor is 70 % rather than 100 %.
