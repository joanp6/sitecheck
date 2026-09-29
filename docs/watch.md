# Watching sites every day

`sitecheck watch` audits a list of sites, compares the result with the previous run, and reports
only what **changed**. [`.github/workflows/watch.yml`](../.github/workflows/watch.yml) runs it every
morning on GitHub Actions, for free and without a server.

## What counts as a change

| Yesterday | Today | Reported as |
|---|---|---|
| PASS | WARN or FAIL | worse |
| WARN | FAIL | worse |
| FAIL | WARN or PASS | better |
| WARN | PASS | better |
| anything | the same | nothing |
| a finding | ERROR or SKIP | nothing, and yesterday's finding is kept |
| nothing (first run, new site, new check) | WARN or FAIL | worse, marked "first seen" |

The rules come from what makes an alert worth reading:

- **A site that stays broken is reported once**, the day it breaks. An alert that repeats every
  morning is an alert people learn to ignore.
- **ERROR and SKIP never move anything.** They say nothing about the site. If they counted, one bad
  night for the tooling on a failing site would send two false alerts: "better" when it errored,
  "worse" when it answered again.
- **The first run reports everything already wrong.** Otherwise a problem present from day one would
  never be reported at all.

`watch` exits **1 when anything got worse**, and 0 otherwise (improvements included).

## Running it on your machine

```bash
dotnet run --project src/SiteCheck.Cli -- watch --sites sites.json --state state.json
```

`state.json` is created on the first run. It is a normal [JSON report](../README.md#json), so you can
open it to see what the tool last knew. If it gets damaged, `watch` refuses to run rather than
overwrite it; move it aside to start fresh.

## Setting up the daily run on GitHub

1. **Add the sites.** In the repository, go to *Settings → Secrets and variables → Actions → New
   repository secret*. Name it `SITECHECK_SITES` and paste the contents of a sites file:

   ```json
   {
     "sites": [
       { "name": "Perruqueria Àngela", "url": "perruqueria-angela.pages.dev" },
       "example.com"
     ]
   }
   ```

2. **Try it.** *Actions → watch → Run workflow*. The first run is red if any site already has a
   warning or failure, and green otherwise. Its summary page shows where every site stands.

3. **That's it.** From now on it runs every day at 06:00 UTC. When something gets worse the run goes
   red and **GitHub emails you**. Check that *Settings → Notifications → Actions* on your GitHub
   account (not the repository) has email turned on for failed workflows.

### Optional: Telegram messages

Email from GitHub says "the run failed". A Telegram message says what changed and what to do.

1. In Telegram, talk to [@BotFather](https://t.me/BotFather), send `/newbot`, and follow the steps.
   It gives you a **token** like `123456:ABC-...`.
2. Send any message to your new bot, then open
   `https://api.telegram.org/bot<TOKEN>/getUpdates` in a browser. The number under
   `"chat":{"id":...}` is your **chat id**.
3. Add two more repository secrets: `SITECHECK_TELEGRAM_TOKEN` and `SITECHECK_TELEGRAM_CHAT_ID`.

Messages are only sent when something changed. If a message cannot be delivered, the run goes red,
because a silent failure to alert is worse than no alerting at all. Setting only one of the two
secrets is also an error, for the same reason.

## Things to know

- **The history lives in the Actions cache**, not in the repository, so the list of sites is never
  committed. It is not a secret store either: anyone who can run workflows in this repository can
  read it. For clients' sites, use a **private repository**.
- **GitHub pauses scheduled workflows in public repositories after 60 days without activity** in the
  repository. It emails a warning first; re-enable it from the Actions tab.
- **Every check runs from GitHub's servers in the US.** A site that blocks traffic from outside
  Spain will look down.
