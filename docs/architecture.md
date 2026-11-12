# How sitecheck is built

What the pieces are, how a run moves through them, and why they are shaped the way they are. For
what the tool does from the outside, see the [README](../README.md).

## Two projects, one direction

| Project | What is in it |
|---|---|
| `src/SiteCheck.Core` | Everything that decides something: the checks, the runner, the reports, the history |
| `src/SiteCheck.Cli` | The command line: reads arguments, wires the pieces together, prints |

The command line depends on the core and never the other way. Anything worth testing lives in the
core, where a test can reach it without starting a process; `Program.cs` only connects arguments
to code and has no tests of its own. That is also its weak spot: a mistake in the wiring is
caught by running the tool, not by the suite.

Inside the core, each folder is one concern:

| Folder | Concern |
|---|---|
| `Checks/` | One file per check, plus what they share for reading a page |
| `Running/` | Running checks against a site, retrying, auditing several sites at once, choosing checks |
| `Http/` | The two layers every request passes through: the shared page and the network guard |
| `Reporting/` | Turning results into text, JSON, HTML, Markdown and JUnit |
| `Sites/` | What a site is, the sites file, and finding more pages of a site |
| `Watching/` | Comparing today with yesterday, the history file, Telegram |
| `Dns/`, `Tls/`, `Certificates/` | The three protocols sitecheck speaks itself, below HTTP |
| `Hosting/` | Serving a local folder for `sitecheck project` |

## What happens in a run

`sitecheck audit salon.es other.es --crawl 5` goes through these steps:

1. **`Program.cs`** turns the arguments into a list of `Site`s and a `CheckSelection` (`--only`,
   `--skip`, `--profile`).
2. **`SiteChecks`** (in the CLI project) builds every check once, with the HTTP clients they
   share, and keeps the ones the selection includes. It is the only place that knows which checks
   exist and how they are wired.
3. **`PageDiscovery`** expands each site into up to five pages, from its sitemap. Each extra page
   becomes a `Site` of its own that skips the checks whose answer belongs to the host.
4. **`SiteBatch`** audits the sites, several hosts at a time, never two addresses on the same host
   together.
5. **`CheckRunner`** runs one site's checks in order, times each one, turns an exception into an
   ERROR result, and gives a check that failed a second look.
6. Each **check** returns a `CheckOutcome`: a verdict and a sentence, plus any measured fact as a
   value of its own.
7. A **report** renders the list of `SiteReport`s, and `ConsoleReport.ExitCodeFor` picks the exit
   code from the worst verdict.

`watch` does the same up to step 6, then `WatchComparison` sets the results against the history
file and only the changes are announced.

## A check

Every check implements one small interface:

```csharp
public interface ISiteCheck
{
    string Name { get; }
    Task<CheckOutcome> RunAsync(Uri url, CancellationToken cancellationToken = default);
}
```

`Name` is an identifier, not a caption (`tls-config`). It is the key results are stored and
compared under, so renaming one is a breaking change; `CheckNameTests` pins that every name is
unique, well formed and listed.

A check does not time itself, catch its own crashes or retry. `CheckRunner` does those for every
check the same way, so a new check cannot forget to.

### The five verdicts

| Verdict | Means | Exit code |
|---|---|---|
| PASS | Fine | 0 |
| WARN | Fine today, close to becoming a problem, or a risk rather than a defect | 0 |
| FAIL | A defect a visitor is affected by, or something a stranger can use today | 1 |
| ERROR | sitecheck could not tell. Says nothing about the site | 2 |
| SKIP | The question does not apply to this site | 0 |

Keeping ERROR and SKIP apart from FAIL is the rule the rest follows from. A tool that reports its
own trouble as the site's defect, or "does not apply" as a failure, teaches whoever reads it to
ignore red, and then the real failure is ignored with the rest. So:

- A site that refuses the connection is a FAIL: a visitor is refused too.
- A request sitecheck declined to make (a private address, a page over 5 MB) is an ERROR.
  `RefusedException` is the type that marks the difference.
- A 403 or 429 is a SKIP: a firewall turning a robot away says nothing about what people see.
- A security check warns about a risk and fails only for something usable now: a leaked file, a
  script from a hijacked domain, an alias pointing at a name anyone can register.

### The sentence is for a person

`CheckOutcome.Detail` is prose for a site's owner. It may be reworded at any time, and nothing may
parse a fact back out of it. A fact a machine needs gets a field of its own (`ValidUntil`,
`Elapsed`, `StatusCode`, `Bytes`), which is what the JSON report carries.

Details quote text from the audited page: a link, a cookie's name, a header. That text is not
ours. Each report escapes it for its own format, and checks cut it to a length fit for a report
and never include a secret's value (a cookie's value, a leaked file's contents).

## Reading the page once

Most checks read the page's HTML. Each used to ask the site for it: thirteen requests for one page
in a few seconds, which is slow, lets two checks grade different versions of a page, and is the
burst that makes a firewall start answering 403.

`PageSnapshotHandler` sits inside the `HttpClient` and answers every `GET` for the audited page
from the first download. It is a `DelegatingHandler`, a layer in the client's pipeline, so the
checks did not change: each still asks for the page as if it were alone, and is still tested
alone. Two things are deliberately not remembered: a request that failed, and a 5xx answer, since
both may be different a moment later.

`load-time` uses a client of its own without this layer and without connection reuse, because it
measures a first visit and a remembered page arrives in no time.

## The network guard

sitecheck follows what a page tells it to: redirects, links, images, form targets. A page could
point it at `http://localhost/admin`, a router, or `169.254.169.254`, where cloud machines keep
credentials, and learn from the report whether something answered. That is server-side request
forgery (SSRF).

`PublicNetworkGuard` refuses any address that is not on the public internet. It checks at the
moment of connecting, on the address actually dialled, rather than checking the name first and
connecting afterwards. Checking first would resolve the name twice, and a DNS server can answer
a public address the first time and a private one the second (DNS rebinding). It also covers
redirects for free: each hop opens a connection, and each connection passes through the guard.

The certificate check and the TLS probe open their own sockets, so they call the guard directly.
`--allow-private` turns it off for auditing a site on your own network.

## Three protocols written by hand

sitecheck has one runtime dependency, `System.CommandLine`. Where a library would have been the
usual answer, it speaks the protocol itself:

- **DNS** (`Dns/`): a query and its answer are a few dozen bytes. `UdpDnsResolver` sends them to
  the machine's own DNS servers and ignores any reply that does not match the question, which is
  how a forged answer is kept out.
- **TLS, the first message only** (`Tls/`): `tls-config` asks whether a server still accepts
  TLS 1.0. A current operating system will not let a program offer that version, so through
  `SslStream` every server would look modern. `TlsHello` writes the opening message by hand and
  reads the version the server picks.
- **HTML** (`Checks/HtmlTags.cs`): tags and attributes are read with regular expressions rather
  than a parser. That is a known limit, not an oversight: it does not run scripts, so a page built
  in the browser is mostly invisible. Every regular expression has a one-second timeout, so a
  hostile page cannot stall an audit.

## Testing seams

Three interfaces exist so that a check can be tested without a network:

| Interface | Real | In tests |
|---|---|---|
| `ICertificateProvider` | `SslStreamCertificateProvider` | `FakeCertificateProvider` |
| `IDnsResolver` | `UdpDnsResolver` | `FakeDnsResolver` |
| `ITlsProbe` | `RawTlsProbe` | `FakeTlsProbe` |

HTTP needs no interface: tests hand `HttpClient` a `StubHttpMessageHandler` that answers from
memory. Time is a `TimeProvider`, so a four-second page or a two-second retry pause costs a test
nothing. [`testing.md`](testing.md) covers the suites and the toolchain.

## Adding a check

1. Write `Checks/YourCheck.cs` implementing `ISiteCheck`. Read the page with `PageReader.ReadAsync`
   so that a page that cannot be read gets the shared verdicts. Say in the class comment what a
   WARN and a FAIL mean and what the check cannot see.
2. Register it in `SiteChecks` (CLI project), and in `CheckNameTests`, which fails if a check
   exists that is not listed.
3. Give it a caption in `HtmlReport.Titles`.
4. Add it to a profile in `CheckSelection.Profiles` if it belongs in one. If its answer is the
   same for every page of a host, add it to `PageDiscovery.OncePerHost`; if it makes no sense for
   a local folder, to `LocalFolder.NotApplicable`.
5. Add its row to the README's table of checks.
6. Write its tests: every verdict it can give, and what it must not mistake for a finding.

## What other programs may rely on

- **The JSON report, format `1`.** Fields are only ever added. A report written by an older
  version still reads, which is what lets `watch` keep its history across upgrades.
- **Check names.** Stable once shipped.
- **Exit codes**: 0, 1, 2, 64 and 130, as in the README.
- **Not the sentences.** `detail` is for people.

A program that maps check names to its own wording, to phrase findings in another language for
instance, keeps working when a check is added: it just has nothing to say about the new one until
it learns its name. Adding a check here is a reason to tell whoever maintains such a program.
