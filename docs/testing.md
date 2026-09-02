# Testing

Two suites, two different meanings when they go red.

| | `tests/SiteCheck.Core.Tests` | `tests/SiteCheck.Core.IntegrationTests` |
|---|---|---|
| What it covers | All logic: check policies, the runner | `SslStreamCertificateProvider`, the TLS I/O adapter |
| Network | None | Real sockets, third-party hosts |
| Speed | ~1 s | ~5 s, at the mercy of the internet |
| Runs by default | Yes | No |
| Red means | The code is broken. Act now. | Maybe the code, maybe someone else's outage. Investigate before acting. |

Fusing those two signals into one red/green light trains you to ignore red, which is why
they are separate projects rather than two categories in one.

## Running them

Unit tests — this is what `dotnet test` does, and what CI runs:

```bash
dotnet test
```

Integration tests, which need outbound internet access:

```bash
SITECHECK_INTEGRATION=1 dotnet test tests/SiteCheck.Core.IntegrationTests
```

On PowerShell:

```powershell
$env:SITECHECK_INTEGRATION = "1"; dotnet test tests/SiteCheck.Core.IntegrationTests
```

Coverage has its own single entry point — see [Coverage](#coverage) below.

## Coverage

One command, used both locally and in CI so that the two can never disagree:

```bash
powershell -ExecutionPolicy Bypass -File scripts/coverage.ps1
```

On Linux and macOS, `pwsh scripts/coverage.ps1`.

It runs the unit suite, writes `TestResults/coverage.cobertura.xml`, prints line coverage per
type worst-first, and exits non-zero if the total falls below the floor. Pass
`-MinimumLineCoverage 0` to report the number without enforcing anything.

Today: **97.22 % lines, 100 % branches.**

### What is measured, and what is not

Only `tests/SiteCheck.Core.Tests`. The integration suite is left out **by construction** — the
script never starts it — rather than by an exclude flag that someone has to remember to pass.

`SslStreamCertificateProvider` is excluded **explicitly**, with
`[ExcludeFromCodeCoverage(Justification = "…")]` on the type itself rather than a glob in a
settings file, so the reason travels with the code and turns up in review when someone touches
it. It is not untested — it has the most realistic tests in this repo, over real sockets, in a
suite that cannot run in CI. Counting it here would print 0 % next to the one type whose
testing we are most confident about, and that red would push someone to write a hollow unit
test to silence it.

### Why the floor is 70 % and not 100 %

The number is high today because the core is small, not because the tests are good. A 100 %
gate does not buy the next percent of quality; it buys a test that executes the next
hard-to-test class without asserting anything, written for no reason except to stop the build
going red. The floor is there to catch a collapse — a suite quietly disabled, a module landing
with no tests at all — and nothing finer than that. The thing worth reading is the per-type
table the script prints, not the total.

### Known gaps

Listed so they stay visible instead of being rounded away by a healthy-looking total. Neither
has been filled, because a test written to close a coverage gap tends to assert whatever the
code already happens to do:

- **`CheckRunner.RunOneAsync`** — the `catch (OperationCanceledException) when
  (cancellationToken.IsCancellationRequested) { throw; }` path, reached only when a check
  itself throws while the caller's token is already cancelled. The existing cancellation test
  cancels *between* checks, so the loop guard fires first and this branch never runs.
- **`LoadTimeCheck.Name` and `SslCertificateCheck.Name`** — never read by a unit test, because
  the runner tests use stubs. A typo in either identifier would ship unnoticed.

### CI

There is no CI pipeline in this repo yet. When one lands it should call `scripts/coverage.ps1`
unchanged, so the floor is enforced by the same code that reports the number locally.

## Why the integration tests are out of CI

They borrow hosts nobody here controls. A [badssl.com](https://badssl.com) outage would turn
the build red for a reason that has nothing to do with the commit under test, and a build
that goes red for reasons outside the commit stops being read.

They are excluded by a **skip**, not by a filter. An ordinary `dotnet test` still lists them
and prints why each one did not run:

```
omitido SslStreamCertificateProviderTests.GetAsync_AgainstAHealthyHost_ReturnsATrustedCertificate
  Integration test. Set SITECHECK_INTEGRATION=1 to run it; see docs/testing.md.
```

A `--filter` would make them silently absent, and would have to be typed correctly at every
call site and in every IDE "run all" click. Safety that depends on remembering an incantation
is not safety.

### When a borrowed host is down

Every test that touches a third-party host runs a TCP preflight first. If the host does not
answer, the test is **skipped with a sentence** naming the host and the socket error, rather
than failing with a `SocketException` that reads like a defect in this repo.

## Hosts used, and why

| Host | Case |
|---|---|
| `example.com` | Healthy certificate |
| `expired.badssl.com` | Expired |
| `wrong.host.badssl.com` | Name does not match the host |
| `self-signed.badssl.com` | Self-signed |
| `untrusted-root.badssl.com` | Unknown issuer |
| `sitecheck.invalid` | Host that cannot resolve (`.invalid` is reserved by RFC 2606, so it can never be "down") |
| `127.0.0.1`, local `BlackHoleListener` | Accepts the connection, then never answers |

The healthy case deliberately uses a **different domain** from the broken ones. If they shared
one, a single outage would take out the whole suite, and "the host is down" would be
indistinguishable from "the certificate is genuinely bad".

Assertions are relative — "expires after now", not "expires on 2026-10-27" — so that no test
here starts failing on a calendar date when a borrowed host renews. The 30-day expiry warning
window is a business rule of ours, so it is pinned by unit tests with a fake clock instead, in
`SslCertificateCheckTests`, where the dates are under our control and can never go stale.

## Known limitation: TLS chain validation is platform-dependent

`incomplete-chain.badssl.com` is **not** in the table above, and its absence is the interesting
part.

Windows (SChannel) completes a broken certificate chain by downloading the missing intermediate
over AIA, and reports `SslPolicyErrors.None`. OpenSSL, on Linux and macOS, generally does not,
and reports `RemoteCertificateChainErrors`. Verified against a live handshake on Windows
11 / .NET 10:

```
incomplete-chain.badssl.com   errors=None   notAfter=2026-10-26
```

A test on that host would pass here and fail in a Linux container — worse than having no test.

**This is not only a testing problem.** The same divergence applies to the tool itself:

- `audit`, run by hand on Windows, will report a customer site with an incomplete chain as clean.
- `watch`, running weekly on a Linux runner, will report the same site as broken.

Neither is a bug in this code, and chasing it as one will cost an afternoon. Real browsers differ
the same way, so neither verdict is even wrong — the site genuinely is broken for some visitors
and fine for others.

**Action for when `watch` lands:** every report must record the platform and TLS stack it was
produced on, so that two reports that disagree can be told apart from a regression.

## Currently skipped

`SslCertificateCheckOverRealHostsTests.RunAsync_WhenTheCertificateHasExpired_SaysSoInsteadOfBlamingTheChain`
is skipped pending [#1](https://github.com/joanp6/sitecheck/issues/1).

It asserts the **correct** behaviour, not today's. Writing it against the current wrong message
would have blessed the bug in the test suite and made the eventual fix look like a regression.
It is a real, verified failure: unskipping it today produces

```
Assert.Contains() Failure: Sub-string not found
String:    "Browsers will not trust the certificate for expire"···
Not found: "expired on 2015-04-13"
```
