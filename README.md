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

### Report formats

| `--format` | For |
|---|---|
| `text` (default) | The terminal |
| `json` | Scripts. The fields are a contract, documented in [`JsonReport`](src/SiteCheck.Core/Reporting/JsonReport.cs) |
| `html` | The site's owner: one self-contained page, problems first, in plain words. Attach it to an email |

`-o report.html` writes to a file instead of the console.

In `json`, `detail` is a sentence for people and may be reworded at any time. Read the facts from
their own fields instead:

| Field | On | Meaning |
|---|---|---|
| `status` | every result | `pass`, `warn`, `fail`, `error` or `skip` |
| `durationMs` | every result | How long the check ran |
| `validUntil` | `ssl-certificate`, `domain-expiry` | When the certificate or domain expires. Omitted when unknown |
| `elapsedMs` | `load-time` | How long the page took to arrive in full, in whole milliseconds. Omitted when no answer came |
| `statusCode` | `load-time`; also `mobile-viewport`, `broken-links`, `contact-form` and the checks added since (`mixed-content`, `click-to-call`, `search-snippet`, `local-business`, `legal-pages`, `page-weight`, `share-preview`, `booking-link`, `copyright-year`) when they skip a site that turns robots away; the checks added since also when the page answers with an error | The HTTP status the site answered with, errors included (a 403 or 429 means the site turns robots away, which is a `skip`, not a defect). Omitted when no response arrived |
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
`booking-link`, `copyright-year`)
report it as an ERROR instead: `load-time` already fails a site that is down, and saying it again
in every new check would bury the real findings.

A page that answers 403 or 429 is a SKIP in every check that reads it (`load-time`,
`mobile-viewport`, `broken-links`, `contact-form`, `mixed-content`, `click-to-call`, `search-snippet`, `local-business`, `legal-pages`, `page-weight`, `share-preview`, `booking-link`, `copyright-year`), never a FAIL: it is what a firewall or bot
shield answers sitecheck while people open the page fine, so it says nothing about the business.
The result carries `statusCode` so a script can say why the page went unchecked. A 401 still fails,
since a page behind a password is what every visitor gets.

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
