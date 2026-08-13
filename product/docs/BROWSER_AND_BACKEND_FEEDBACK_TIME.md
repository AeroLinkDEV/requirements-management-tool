# Feedback Time: Where a Pull Request's Wall Clock Actually Goes

Date: 2026-08-13

> **Live measurement record.** This supersedes the shard-count reasoning in
> [CI_COST_AND_READINESS_REVIEW.md](CI_COST_AND_READINESS_REVIEW.md), whose numbers describe a suite roughly a
> quarter of its present size. The workflows themselves remain the authority on what runs.

## Why this exists

A pull request that is green is not a pull request that is merged. Branch protection is **strict** (a branch
must be up to date with `main`) and **auto-merge is disabled**, so every unrelated merge to `main` invalidates
a passing run and starts the whole gate again. On a day when `main` moved six times, one parity pull request
was re-tested end to end six times and found nothing new on any of them.

That makes elapsed time per run the number worth optimizing, and it makes it worth knowing which job actually
governs it. Optimizing the wrong job costs runners and buys nothing.

## Measured, 2026-08-13

Job durations across three consecutive runs of one pull request:

| Job | Duration | On the critical path? |
|---|---|---|
| **Build, test, and exercise product journeys** (`validate`) | **21m37s / 25m53s / 26m53s** | **Yes** |
| Browser journeys (1/2) | 11m24s / 13m19s / 15m36s | No |
| Browser journeys (2/2) | 11m28s / 14m48s / 15m30s | No |
| Browser journeys on the production build | 5m26s | No |
| PostgreSQL migrations and secure bootstrap | 2m11s | No |
| Classify changed product areas | ~15s | No |

Inside `validate`, measured locally on the same commit:

| Assembly | Duration |
|---|---|
| `AeroLink.Api.Tests` (473 tests) | **~12m** |
| `AeroLink.Infrastructure.Tests` (355 tests) | ~3m |
| `AeroLink.Domain.Tests` (400 tests) | ~0.2s |

`validate` also runs the client lint, type-check and build **after** the backend tests, in the same job, in
series.

## What this rules out

**Adding browser shards.** The July increment moved three shards to two and reasoned that "two shards are
~123s each against a ~200s+ backend job". The ratio still holds even though both numbers have grown roughly
5×: the journeys are 11–16 minutes against a 21–27 minute `validate`. A third or fourth shard would shorten a
job nobody is waiting on, and pay a full runner setup to do it. This was proposed on 2026-08-13, measured, and
dropped before it shipped.

The lever is `AeroLink.Api.Tests`, which is roughly half the critical-path job on its own.

## Implemented here

### One retry on CI, none locally

`retries: process.env.CI ? 1 : 0` in `playwright.config.ts`.

The journey suite produces roughly one load-induced flake per full run — a different test each time, each one
passing in isolation when re-run. With no retries, a single flake cost a complete re-run of the gate: about
twenty-five minutes to re-learn that nothing was wrong. Three of the day's re-runs were exactly that.

This hides nothing. Playwright reports a test that passes on retry as **flaky** rather than as passing, the
count appears in the run summary, and the trace from the failed attempt is still uploaded. What it removes is
a flake's ability to end the run.

### Shards report independently

`fail-fast: false` on `browser-pr`.

The opposite cancelled the sibling shard the moment one failed, so a rerun was needed purely to discover
whether anything else had also failed — twice on 2026-08-13. A cancelled shard reports nothing, and then costs
a full gate to obtain the nothing it should have reported the first time.

## Not implemented here, and why

Tracked as a GitHub issue rather than done in this increment, because each is a real piece of work rather than
a setting:

1. **`AeroLink.Api.Tests` is the critical path.** Most tests stand up a `WebApplicationFactory` over a fresh
   SQLite file. The repository already contains the fix in miniature: `ShowcaseApiFixture` copies a pre-seeded
   template database instead of re-seeding, and cut 177 of 552 CPU-seconds across **three** tests. Generalizing
   that to the tests that seed a workspace per test is the single largest available saving.
2. **Splitting `validate` into parallel backend and client jobs.** The client checks run after the backend
   tests in the same job. Splitting them removes ~3 minutes from the critical path but changes the required
   check topology and the "refuse a pass that validated nothing" guard, so it needs deliberate review.
3. **Journey parallelism (`workers: 1`).** Every journey shares one API and one database and mutates it, so
   workers cannot simply be raised. Per-test isolation — each test owning its Program and Project, as the
   newer specs already do via `seedWorkspace` — is the prerequisite.
4. **The strict-branch-protection treadmill.** A merge queue would let GitHub perform the
   rebase-retest-merge cycle without a human holding the window open, which is the actual cause of repeated
   full-gate runs. This is a repository setting rather than a code change.

## For anyone changing CI after this

Measure before optimizing, and measure the *job*, not the suite. Both times someone has reasoned about shard
counts here, the reasoning was about the browser suite in isolation and the answer depended entirely on what
`validate` was doing at the time. The numbers in this file will go stale the same way; re-read the run
summaries before trusting them.
