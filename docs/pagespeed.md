# PageSpeed Insights

`sitecheck audit --pagespeed` adds a `pagespeed` check. It asks Google's
[PageSpeed Insights](https://developers.google.com/speed/docs/insights/v5/get-started) to load the
page in a real Chrome, on a throttled connection that behaves like a mid-range phone, and reports
Lighthouse's performance score:

| Score | Verdict |
|---|---|
| 90–100 | PASS |
| 50–89 | WARN |
| 0–49 | FAIL |

It complements `load-time`, which only times the HTML arriving. This one waits for images, fonts and
scripts too, which is what a visitor actually waits for.

## Why it is opt-in

- **It sends the site's address to Google.** Fine for a public site, but it should be a choice.
- **It is slow**: 10 seconds to a minute per site, because Google loads the whole page first.
- **Its score moves a few points between runs** of the same page. That is also why `watch` does not
  use it: a page scoring around 90 would be reported "worse" and "better" on alternate mornings.

## Getting an API key

Without a key the check uses Google's anonymous quota, which is shared by everyone calling the API
and is, in practice, always used up (it answered 429 on the first try while this was being built).
A key is free and allows 25,000 requests a day.

1. Open the [Google Cloud console](https://console.cloud.google.com/) and create a project (any name).
2. Open *APIs & Services → Library*, search for **PageSpeed Insights API**, and enable it.
3. Open *APIs & Services → Credentials → Create credentials → API key*.
4. Edit the key and, under *API restrictions*, restrict it to the PageSpeed Insights API. A key that
   can only do this is harmless if it leaks.
5. Set it before running sitecheck:

   ```powershell
   $env:SITECHECK_PAGESPEED_KEY = "your-key"
   ```

   ```bash
   export SITECHECK_PAGESPEED_KEY="your-key"
   ```

The key is never written to reports or error messages: it travels in the request address, and those
end up in logs.

## When it says ERROR

`pagespeed` reports ERROR, not FAIL, when Google cannot answer: the quota is spent, the key is wrong,
or Lighthouse could not load the page. None of these is a finding about the site. If the site itself
is down, `load-time` already says so as a FAIL.
