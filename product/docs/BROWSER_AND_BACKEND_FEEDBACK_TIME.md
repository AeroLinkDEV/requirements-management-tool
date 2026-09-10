# Feedback Time: Where a Pull Request's Wall Clock Actually Goes

Date: 2026-08-13; merge-queue cutover: 2026-09-04

> **Live measurement record.** This supersedes the shard-count reasoning in
> [CI_COST_AND_READINESS_REVIEW.md](CI_COST_AND_READINESS_REVIEW.md), whose numbers describe a suite roughly a
> quarter of its present size. The workflows themselves remain the authority on what runs.

> **Read this before quoting any figure below as current.** The `10m14s` critical path and the other timings
> in this document are **dated historical measurements** taken against much smaller suites. They have not been
> re-measured on the same basis. A later descriptive observation study and the adopted owner scope decision
> governing #942's closeout are recorded in
> [Throughput review closeout, September 2026 (#942)](#throughput-review-closeout-september-2026-942) at the
> end of this document. That section's figures use different definitions and a different method, and the two
> sets must not be compared as though they shared a clock.

## Why this exists

A pull request that is green is not necessarily a pull request that is merged. When this was first measured,
branch protection was **strict** (a branch had to be up to date with `main`), so every unrelated merge to
`main` invalidated a passing run and started the whole gate again. On a day when `main` moved six times, one
parity pull request was re-tested end to end six times and found nothing new on any of them.

Auto-merge first removed the delay from a green run sitting unnoticed. The 4 September merge-queue cutover
removed the remaining strict-protection treadmill: GitHub now validates the exact composed candidate against
current `main` and entries ahead of it, without a manual rebase. See [Merging into main](MERGING.md).

That makes elapsed time per run the number worth optimizing, and it makes it worth knowing which job actually
governs it. Optimizing the wrong job costs runners and buys nothing.

## Merge-ready Full-CI cadence, 2026-08-17

Issue #561 changes **when** the existing Full evidence is purchased, not what it proves. Ordinary pull-request
open/reopen/synchronize events run the separate advisory Fast workflow. The Product quality gate no longer
starts automatically for each development push.

When `ready-for-full-ci` is applied to the final SHA, a trusted `pull_request_target` requester that never checks
out PR code dispatches the Product gate against the exact same-repository head. Product still selects the same
API, browser, production-browser, backend/core, client, PostgreSQL and operator lanes; its internal
`Full Product evidence aggregate` remains the evidence summary. The trusted requester independently proves
Product's readiness authentication and aggregate success before completing `Report what this run validated`.
With the merge queue active, that success also causes the dedicated Merge Authority App to publish the
required `Trusted merge-queue binding` check on the exact pull-request head. There is no always-green
placeholder and Fast is not authoritative. That head-bound readiness survives unrelated `main` advancement;
the queue is responsible for validating the eventual composed candidate.

A synchronize event removes stale readiness, so a later SHA must request Full again. Fast and Full use
different concurrency groups; development feedback cannot cancel final Full evidence. The rolling metrics
collector remains the source for post-switch full-gates-per-merge, cancellation waste, queue/final-push-to-merge
timing and regression data; re-measure the new cadence rather than assuming savings.

## One native owner for operator execution

The required Windows `script-contracts` job owns the operator and recovery script family. The planner
suite checks that this owner still executes the complete family unconditionally; it no longer launches
the same family a second time from the Domain job. The native PowerShell steps and their assertions
remain intact, including backup preview, launcher, process ownership, recovery and fault cases.

The operator job captures the original `product/.local` fingerprint before execution and verifies it
in an `always()` step afterwards. Missing evidence, a changed path/content/mtime, a foreign snapshot,
or failure to capture/verify is a job failure. The capture refuses to overwrite its baseline or write
the snapshot into the store being protected. The layout contracts also execute inside this boundary,
while the always-running classifier retains its documentation/layout check.

This removes duplicate execution while retaining native Windows qualification. The local Full planner
still runs its operator family under the existing evidence and process-ownership boundary; its
Smtp4dev contract remains local-Full-only, while launcher/bootstrap and layout proofs remain owned by
the native Windows job. Measure hosted job and critical-path results before attributing any merge-time
saving to this change.

## API-independent client feedback

The advisory Fast client job runs the explicit logic and rendered-fixture files in
`product/client/fast-client-tests.json` after static checks. Logic uses no browser or server;
rendered fixtures use one Chromium worker and Vite, with no API or showcase setup. Neither tier retries.
The fixture boundary rejects its `request` fixture, `page.request`, and browser-context API methods, and
records and refuses browser API/external requests, including swallowed failures. The Full workflow still
discovers and executes all of these tests.

From `product/client`, run `npm run test:fast:routes`, `npm run test:fast:logic`, then
`npx playwright install chromium`, `npm run test:fast:isolation`, and `npm run test:fast:rendered`. The
isolation command runs a deliberately offending child test outside ordinary discovery and requires a
nonzero exit even when the child swallows the API-context error; its control child must pass. The routing
check compares actual Playwright discovery by file and title, rejects missing/duplicate/substituted
identities, and records every remaining file as Full-only. Newly added files therefore retain integrated
Full coverage until their dependencies and assertions are reviewed for early execution. Mixed
integrated/fixture files must not be added to the isolated manifest merely because they mock one response.

Fast retains its routing report, JSON results and available traces under a per-run artifact, including
failed runs. This addition is intended to expose defects earlier; it does not establish a reduction in
Full wall time. Measure hosted command time, workflow latency and first-pass results before expanding
the subset or moving any identities out of Full. The existing local changed-area planner remains a
broader local plan; these commands provide the same isolated client checks used by advisory CI.

## Merge-queue cutover, 2026-09-04

Issue #549 moved the repository to the `AeroLinkDEV` organization and PR #911 supplied the trusted repository
support for a squash merge queue. The repository-scoped App is installed only on this repository, its private
key is held by the main-only `merge-authority` environment, and the active `main` ruleset has no bypass actors.
A pull-request head must first pass the existing trusted Full requester; only then does the dedicated AeroLink
Merge Authority App publish `Trusted merge-queue binding` so the pull request can enter the queue. GitHub
builds a `gh-readonly-queue/main/...` candidate from current `main`, that pull request, and entries ahead of
it, and runs the complete Product gate on the composed SHA.

The Product workflow is candidate-composed, so its own green result is evidence rather than final authority.
A separate `workflow_run` verifier loaded from protected `main` reads the exact run and latest-attempt jobs,
requires the full gate topology, and refuses candidates that differ from `main` under `.github/`,
`product/test-planner/`, or `product/ci-metrics/`. It publishes the required check with the repository-scoped
App identity only after those checks pass. The App key is restricted to the main-only `merge-authority`
environment, and the ruleset pins the required context to that App's integration id. Every initial Product run
or rerun first replaces earlier App success with an in-progress check, so prior authority cannot bridge a
newer attempt.
The ruleset separately requires GitHub Actions' `Full Product evidence aggregate`; its native check suite
leaves success as soon as a rerun is queued, covering the earlier interval before `workflow_run` reaches
`in_progress`. The native Product check and App-bound evidence check are deliberately co-required.

Live activation is complete. This documentation-only delivery establishes the first single-entry acceptance
by proving both required identities on the pull-request head and the composed queue candidate after the
authority-maintenance fix. Ordinary behind branches no longer need a manual rebase; real conflicts and
deliberately protected CI-authority changes still require explicit disposition. Multi-entry, stale-base,
deliberate-failure and non-cancellation qualification stay tracked on issue #549 until their evidence is recorded.

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

`validate` also ran the client lint, type-check and build **after** the backend tests, in the same job, in
series, along with the PowerShell operator-script contracts. It has since been split — see below.

## What this rules out

**Adding browser shards — ruled out twice, and then correct.** The July increment moved three shards to two
and reasoned that "two shards are ~123s each against a ~200s+ backend job". The ratio still held when both
numbers had grown roughly 5×: 11–16 minutes of journeys against a 21–27 minute `validate`. A third or fourth
shard would have shortened a job nobody was waiting on. Proposed on 2026-08-13, measured, and dropped.

Then sharding `AeroLink.Api.Tests` removed the job the journeys were being compared against, the journeys
became the critical path at 997s, and the same test pointed the other way. They went to four.

**This is the useful shape of the thing.** The answer was never "shards are good" or "shards are bad" — it was
always a comparison against whatever else was in the run, and it flipped when that changed. Any conclusion in
this file is a claim about a ratio at a moment. When one side of a ratio moves, re-measure rather than
re-reading.

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

### `validate` split into four parallel jobs

`backend-api`, `backend-core`, `client` and `script-contracts`, where there was one serial `validate`.

Nothing in that job consumed a previous step's output, so the ordering bought nothing and the gate waited for
the **sum**: ~12 minutes of API tests, then ~3 of domain and infrastructure, then ~3 of client checks, then the
script contracts. Split, it waits for the slowest instead. `AeroLink.Api.Tests` is ~12 minutes against ~3 for
the other two assemblies combined, so it gets a runner to itself and everything else finishes underneath it.

Two details that are easy to get wrong, and were deliberately handled:

- **The scoped backend jobs build their own complete project graphs.** Each API shard restores and builds
  `AeroLink.Api.Tests.csproj`, and the Infrastructure job restores and builds
  `AeroLink.Infrastructure.Tests.csproj`, including transitive project references, before discovery and
  execution use `--no-build`. They do not consume binaries from another runner or use `--no-dependencies`.
- **Whole-solution coverage remains separately owned.** The required Domain job restores and builds the
  complete solution on Windows, including tools and projects outside the two test graphs. The PostgreSQL
  qualification keeps its independent whole-solution Linux restore/build. A selected backend job requires
  the Domain owner to complete; fewer projects compiled on a scoped runner is not evidence of a shorter
  Full gate by itself.
- **Naming assemblies individually introduces a new failure mode**: a test project that no job runs. It would
  still build, so nothing would fail — the suite would simply become invisible. `backend-core` carries a guard
  that enumerates `product/tests/*` and fails on any project not claimed by a job.

The "refuse a pass that validated nothing" guard moved from inside `validate` to the `gate` job, because what
it protects against is now spread across four jobs and no single one can see it. The required check name is
unchanged, so branch protection needed no edit.

**Measured outcome: no wall-clock saving.** The critical path went from 21m37s / 25m53s / 26m53s to
**24m47s** — inside the old range. Two assumptions in the original plan were simply wrong:

- The client checks were estimated at ~3 minutes. They take **28 seconds** — `npm ci`, lint, type-check and
  build combined. The script contracts take 24. Together they were ~50 seconds of genuinely serial work, not
  three minutes.
- `dotnet test <solution>` already runs the test projects **concurrently**, so putting the assemblies on
  separate runners moved work that was already overlapping.

It was kept rather than reverted, for two reasons that are not wall clock: a client lint failure now costs
**47 seconds** to re-verify instead of re-running a 25-minute job, and it is the prerequisite for the shard
below. But it should be recorded as what it was — a change that did not do the thing it was predicted to do.

### The API suite sharded across two runners

What the split *did* deliver was an exact measurement of where the time is:

| Step of `backend-api` | Duration |
|---|---|
| Setup, checkout, .NET | 49s |
| Restore and build the solution | 103s |
| **Run the API test suite** | **1328s (22m8s)** |

**89% of the critical path is one `dotnet test` invocation.** Nothing else is worth touching until it moves.

The reason it is 22 minutes on CI and ~12 on a developer machine is not that CI is mysteriously slow: xunit
parallelises to the core count, and `windows-latest` has **four cores** against a workstation's many more.
**Every local timing in this document understates CI by roughly 1.85×** — measure on the runner, not the
laptop.

That is also why a second runner helps where making the tests individually cheaper did not: it buys four more
cores. The suite splits across two shards, and since xunit has no sharding of its own, the partition is
computed:

1. Discover the test list with `--list-tests`.
2. Reduce to classes with their test counts, sorted heaviest first, **ties broken by class name**.
3. Assign each class to whichever shard is lighter, and run the shard's own classes by filter.

Both runners execute the identical deterministic computation over the same list, so the union is every class
and the intersection is empty *by construction* — there is no list to maintain and no way for a new test class
to land outside both halves. The classes are very uneven (42 tests in the largest, 1 in the smallest), so
heaviest-first matters: it balances 243 against 242, where round-robin or alphabetical would not.

Two guards, because both failure modes look like success:

- **An empty filter runs the whole assembly**, on both shards — which passes, and doubles the work it was
  meant to halve. A shard that selects no classes fails instead.
- **`~` is a substring match.** A class whose name prefixes another would pull in tests from both, and a
  malformed filter would quietly run fewer. Each shard compares the test count it actually ran against the
  count it claimed, so either becomes a failure rather than a smaller green suite.

### Measured result of the shards, and the second round

Two API shards took the critical path from **24m47s to 16m37s**, and both self-verified: 243 and 242 of the
485 discovered tests, the whole suite between them.

That moved the bottleneck rather than removing it. With `backend-api` at 656s and 771s, the longest thing in
the gate became a browser shard at 997s — so the next round was decided by measurement rather than by
symmetry:

| Job | Setup | Actual testing |
|---|---|---|
| Browser 1/2 | 167s | 603s |
| Browser 2/2 | 190s | 807s |
| API shard 1 | ~150s | 506s |
| API shard 2 | ~150s | 621s |

1410 seconds of journeys and 1127 of API tests, against ~150–190s of setup per runner. The journeys went to
**four** shards and the API suite to **three**, which is where setup becomes a large enough fraction of each
shard to stop.

Note the second column: Playwright shards by test *file*, not by duration, so its two halves were **25%
apart** — 603s against 807s. The computed API partition balances by test count and still came out 506 against
621. **Neither balances by time**, because neither has time data. More shards makes each imbalance smaller in
absolute terms, which is the cheap mitigation; carrying per-test durations between runs is the real fix and is
not done.

Every shard count in the workflow now derives from `strategy.job-total` rather than being written beside the
matrix. A matrix saying four next to a divisor saying two would run half the tests and pass.

### The ceiling on sharding, and why it is not "add another runner"

Four browser shards and three API shards took the critical path from 16m37s to **13m57s** — less than the
arithmetic promised, and the shortfall is entirely imbalance:

| Browser shard | Journey time |
|---|---|
| 4/4 | **662s** |
| 1/4 | 360s |
| 2/4 | 328s |
| 3/4 | 233s |

Perfectly balanced, those four would be ~396s each and the gate would be ~9m30s. **The imbalance costs more
than another runner would buy.**

It is not caused by a dominant spec file. Playwright shards by test count and does it well — 58/57/54/56 of
225 tests across 104 files, largest file 10 tests. What varies is **duration per test**. The computed API
partition has the same shape for the same reason: balanced at 162/162/161 tests, and 499s/347s/274s of
running.

Two consequences:

1. **Neither splitter can improve without duration data**, because neither has any. Test count is the only
   input either of them takes.
2. **More shards is capped, and the cap is not money.** At 14 jobs per run, two concurrent pull requests
   already exceed the account's concurrent-job limit — and this repository routinely has several agents
   working at once. Past that point extra shards make every agent's gate slower, which is the opposite of the
   goal. The remaining gain has to come from packing the runners that already exist.

### Packing the journeys by duration

The browser shards emit a Playwright JSON report, and the durations from it are checked in as
`product/client/journey-durations.json`. `scripts/plan-journey-shard.mjs` packs the spec files heaviest-first
into whichever shard is lightest, and each shard runs its own file list instead of `--shard`.

What the recorded numbers show is why no count-based split could have worked:

| Spec file | Duration |
|---|---|
| `zzz-post-414-picker-integrity.spec.ts` | **220s** — 19% of the suite alone |
| `zzz-searchable-authoring-pickers.spec.ts` | 100s |
| every other file | ≤ 39s |

Packed by duration, four shards come out at **296s each** against a measured worst of 662s. The test counts
become deliberately uneven — 46 / 55 / 50 / 74 — which is the point.

The durations are an **optimisation, never a correctness input**. An unknown or stale entry is weighted at the
median, and a missing file degenerates the whole thing to a count-based split; both were tested by deleting
the file, and still cover all 225 tests across all 104 spec files. Discovery returning nothing fails the shard
rather than running everything. Each shard then compares the report's own stats against what it planned,
because Playwright matches a positional argument as a path substring and a spec name contained in another
would otherwise quietly pull in a file that shard was never given.

Refresh the file from the artifacts these jobs upload when the numbers drift. Nothing breaks if it is stale —
it just packs less well.

**Measured result.** The shards went from a 2.0× spread to 1.1×:

| Split by | Shard wall clock | Spread |
|---|---|---|
| Test count (`--shard`) | 852s / 550s / 461s / 424s | 2.0× |
| Recorded duration | **614s / 603s / 593s / 555s** | **1.1×** |

Critical path **14m12s → 10m14s**. Each shard reported running exactly the tests it planned — 46, 55, 50 and
74, totalling all 225.

Note that the packed shards sum to more wall clock than the recorded durations predict (296s each). The JSON
report times test *execution*; a shard also pays worker startup, the web server, and fixtures. Recorded
durations are good enough to rank files, which is all the packing needs — they are not a wall-clock model, and
should not be read as one.

## Where it stops

`backend-api` is now 526s against the journeys' 614s, so the two are within a shard's setup cost of each
other and neither is worth splitting further. What remains is the ~175s of setup every shard pays and the
per-test overhead inside the assemblies, not the way work is divided across runners.

Sharing one API build across the browser shards was considered and rejected on the numbers: the 86s build
currently overlaps inside each parallel job, so centralising it would add a serial wait plus artifact
transfer and make the critical path *worse*.

## Not implemented here, and why

Tracked separately rather than done in this increment, because each is a real piece of work rather than a
setting:

1. **`AeroLink.Api.Tests` is still the critical path**, and it is now the *whole* critical path rather than
   half of a job. The obvious fix has been tried and **disproved**: generalizing the `ShowcaseApiFixture`
   template-copy so every factory copies a pre-built schema made the assembly **22% worse** in summed CPU
   (median 13.4s → 19.4s, p75 20.4s → 26.9s). The template is 2.45 MB and roughly nine tests run concurrently,
   so it trades cheap page-cached DDL for contended disk I/O. p10 improved, which is the tell — that is where
   concurrency is lowest.

   What the measurement did establish is the shape of the cost: p10 **5.8s** against a **13.4s** median, with
   no class above 7% of the total. That is a fixed per-test floor of roughly 39%, not a few slow tests. Any
   further attempt has to remove that floor **without adding per-test file I/O**. The unexplored direction with
   the best ratio: many tests boot a full API to assert a rule the domain assembly answers directly — 400
   domain tests run in **0.2 seconds**.
2. **Journey parallelism (`workers: 1`).** Every journey shares one API and one database and mutates it, so
   workers cannot simply be raised. Per-test isolation — each test owning its Program and Project, as the
   newer specs already do via `seedWorkspace` — is the prerequisite.

## For anyone changing CI after this

Measure before optimizing, and measure the *job*, not the suite. Both times someone has reasoned about shard
counts here, the reasoning was about the browser suite in isolation and the answer depended entirely on what
`validate` was doing at the time. The numbers in this file will go stale the same way; re-read the run
summaries before trusting them.

And be willing to throw the change away. The schema-template optimization above looked obviously correct,
was implemented, measured **22% worse**, and was reverted. Measuring first is only worth anything if a
disappointing result actually changes the decision — so record the negative results too. They are the ones
that stop the same idea being reimplemented in three months.

### The browser job timeout was inside the noise

`timeout-minutes` on `browser-pr` was 20. A shard is roughly four minutes of setup plus its half of the
journeys, and that half has grown: 14m48s, 15m21s, 17m04s, 17m46s, and then a run that printed
`105 passed (16.3m)` and was cancelled at 20m08s while uploading its artifacts. Every test in it had passed.

A timeout that fires after a green suite is the worst signal available: it reports failure, names no test, and
costs a full gate to re-run something that already worked. Raised to 30, which still stops a genuinely hung
browser.

This is the same growth that makes the shard-count reasoning above worth re-measuring rather than inheriting.

### The Infrastructure job outgrew its original timeout

Protected Product run 33432756016 measured about three minutes of setup and then more than 16 minutes 53
seconds in `AeroLink.Infrastructure.Tests` before the 20-minute outer job limit cancelled the runner. After
the deterministic failures from that run were corrected, run 33438875792 measured about three minutes of
setup and exactly 27 minutes 1 second in the suite before the 30-minute outer limit cancelled it. The same
exact head completed all 751 tests locally in 27 minutes 6 seconds, with 705 passed and 46 expected
PostgreSQL-only skips.

Protected run 33442155318 then showed that the hosted Windows runner needs more headroom than either the
local result or the preceding hosted sample implied. After roughly three minutes of setup, the suite ran for
37 minutes 1 second before the 40-minute outer job limit cancelled it. No test had failed: expensive
deterministic showcase cases continued to complete throughout the run, including 6-10 minute seed/upgrade
cases, and the latest passing test completed only 2 minutes 39 seconds before cancellation.

The Infrastructure job limit is therefore 60 minutes. Its six-minute per-test blame-hang budget remains
unchanged, so a genuinely hung test still fails early enough to finalize and upload diagnostics. This adds
outer-job headroom for the measured hosted suite plus setup and diagnostic finalization; it does not weaken
test selection or hang detection.

## API startup floor, measured (563A, 2026-08-14)

Per-shard telemetry (schema `aerolink-api-telemetry/v2`) splits each hosted API test's wall time into
factory startup (construction-to-host-start + host build + disposal, attributed from the construction
call site) and test body (wall minus startup). The three intervals are non-overlapping: `constructionMs`
is captured **before** `base.CreateHost`, so it never contains the host build. `connectionOpen` records
every SQLite connection open over the factory lifetime and is informational only (never added to
startup). Structured per-shard artifacts: `api-telemetry-<shard>-<attempt>`.

### Timing-correction note (round 2 review, 2026-08-14)

Round-1/round-2 heads captured `constructionMs` in the `finally` **after** `base.CreateHost` completed,
so the value already contained the whole host build and the aggregator's
`constructionMs + hostMs + disposeMs` added the host time twice (about 438s across the three shards in
run 31852062285). All startup percentages and summed-startup numbers in the tables below are therefore
inflated and are retained **only as the defect evidence**. The corrected baseline is measured by the
round-3 run (schema v2, non-overlapping intervals) and published in its per-shard artifacts, the PR
body, and the table below.

### Corrected baseline: run 31855848873, PR #581 head `019950c` (schema v2)

Each shard reconciles exactly: TRX = attributed + ambiguous theory rows + no-factory-telemetry.

| Shard | TRX | Attributed | Ambiguous theory rows | Unmatched methods | No factory telemetry | Factories | Summed wall | Summed startup | Startup % | Wall p10/median/p75/p95 | Startup p10/median/p75/p95 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 | 163 | 132 | 28 | 0 | 3 | 160 | 663.7s | 130.1s | 20% | 2.9 / 4.7 / 6.0 / 9.1s | 0.3 / 0.7 / 1.0 / 3.3s |
| 2 | 163 | 138 | 15 | 1 | 10 | 162 | 899.8s | 115.7s | 13% | 4.1 / 6.0 / 7.5 / 11.9s | 0.4 / 0.6 / 0.8 / 2.2s |
| 3 | 163 | 126 | 20 | 1 | 17 | 149 | 716.0s | 104.0s | 15% | 3.5 / 5.2 / 6.6 / 10.5s | 0.4 / 0.6 / 0.8 / 2.1s |
| Total | 489 | 396 | 63 | 2 | 30 | 471 | 2279.6s | 349.8s | 15% | — | — |

The raw host records confirm the non-overlapping boundary: pre-host construction summed to 176/131/170 ms
across the three shards of an earlier run (159/186/144 ms on this run) while the host builds themselves
summed to 144.1/135.8/111.4 s. The corrected startup floor (13–20% of summed wall by shard) is roughly a
third of the inflated round-2 fractions (40/25/31%), and the earlier per-class startup rankings were
materially distorted by the double count.

### Inflated exact-head baseline (defect evidence): run 31844562806, PR #581 head `e0d4770`

Each shard's TRX reported **162 tests** (486 across the three shards); the round-1 artifacts attributed
**419** of them because parameterized theory invocations shared one call-site method name. All startup
values below double-count the host build.

| Shard | Tests | Factories | Summed wall | Summed startup | Startup % | Wall p10/median/p75/p95 | Startup p10/median/p75/p95 |
|---|---:|---:|---:|---:|---:|---:|---:|
| 1 | 133 | 159 | 821.0s | 360.4s | 44% | 4.1 / 5.7 / 7.3 / 13.0s | 0.8 / 1.2 / 1.8 / 10.3s |
| 2 | 152 | 163 | 838.5s | 224.7s | 27% | 3.8 / 5.5 / 7.0 / 9.8s | 0.8 / 1.1 / 1.5 / 2.4s |
| 3 | 134 | 148 | 774.4s | 297.8s | 38% | 3.6 / 5.3 / 6.2 / 12.8s | 1.0 / 1.4 / 1.8 / 9.8s |
| Total | 419 | 470 | 2433.9s | 882.9s | 36% | — | — |

TRX totals are the authoritative test count: **162 tests per shard, 486 total**. The 67-test gap per run
(486 TRX − 419 attributed) is the parameterized-theory and fixture/helper attribution gap measured above,
not a suite reduction.

### Inflated earlier baseline for comparison (defect evidence): run 31843343040, PR #581 head `c002890`

This is the pre-rebase measurement, retained only as a comparison point. It uses the same aggregator and
the same attribution gap; it is **not** the exact-head baseline. Its startup values also double-count the
host build.

| Shard | Tests | Factories | Summed wall | Summed startup | Startup % |
|---|---:|---:|---:|---:|---:|
| 1 | 133 | 159 | 778.4s | 229.9s | 30% |
| 2 | 152 | 163 | 970.9s | 350.8s | 36% |
| 3 | 134 | 148 | 833.8s | 194.0s | 23% |
| Total | 419 | 470 | 2583.1s | 774.7s | 30% |

Notes:

- The corrected floor counts construction-to-host-start, host build, and disposal as three
  non-overlapping intervals; the TRX wall is the whole test method. `connectionOpen` (every SQLite
  connection open over the factory lifetime, not only host startup) is recorded separately and never
  added to the startup total.
- Startup percentages above 100% for classes whose factories are created in collection/class fixtures
  (e.g., `ShowcaseApiFixture`, `CancelReviewAuthorityTests`) are expected: fixture startup occurs outside
  any single test's TRX wall.
- 470 factories were created for 419 attributed tests at the exact-head baseline; multiple-factory tests
  are listed explicitly in the artifact, and parameterized-theory rows are reported separately (not merged
  into a fabricated multi-factory test) from the round-2 head onward. Every TRX row reconciles into
  exactly one bucket (attributed, ambiguous theory, or no-factory-telemetry) from the round-2 head onward.
- The schema-template copy experiment remains the recorded negative result: median rose 13.4s to 19.4s,
  p75 20.4s to 26.9s, summed CPU +22%; do not repeat a per-test database-copy strategy.
- Phase 2 (fresh-host/reusable-host/non-hosted inventory) and phase 3 (host-reuse pilot with >=10
  full-concurrency runs) are the next increments; this phase changes no isolation architecture.
- This foundation does not yet break host build down into database/schema, bootstrap, evidence-root, and
  first-client components; that attribution is a phase-2 requirement before a host-reuse design is chosen.

## Host reuse: final disposition (#563)

Measured 2026-08-15 across **eight** quality-gate runs, after the phase-1 instrumentation (#581) and the
phase-2 pilot tranches (#584, #585). The single-run figures earlier in this document are superseded for
this question: run-to-run variance on this runner topology is large enough that one observation cannot
support or refute the rollout gate.

### Startup floor, measured across eight runs

| Phase | Builds | Median | p95 | Summed across 3 shards |
|---|---:|---:|---:|---:|
| `host` | **433** | **665 ms** | **3,581 ms** | **503.8 s** |
| `dispose` | 843 | 1.7 ms | 78.0 ms | 19.1 s |
| `connectionOpen` | 451 | 0.4 ms | 2.0 ms | 0.4 s |

The eight-run telemetry recorded **433 host constructions**. Independently, the current source-exact inventory
forecast has **447 total test methods: 435 direct-host, 4 host-unknown, and 8 explicitly non-hosted**, across
**497 known xUnit cases**. These are distinct measures; runtime telemetry is authoritative for factory counts.
Disposal and connection opening are negligible in that telemetry. The p95 at more than five times the
median means averages understate the tail.

### Per-shard host cost and wall clock, eight runs

| Run | Shard 1 | Shard 2 | Shard 3 | max/min |
|---|---:|---:|---:|---:|
| 31903979582 | 220.4 s | 191.0 s | 116.1 s | 1.90x |
| 31904021300 | 113.9 s | 298.3 s | 91.7 s | 3.25x |
| 31905962385 | 117.6 s | 108.4 s | 108.0 s | 1.09x |
| 31906166911 | 120.8 s | 142.9 s | 159.1 s | 1.32x |
| 31906483905 | 122.9 s | 131.5 s | 121.8 s | 1.08x |
| 31907409523 | 161.7 s | 129.9 s | 118.7 s | 1.36x |
| 31908242269 | 128.7 s | 128.9 s | 129.9 s | 1.01x |
| 31908290378 | 175.2 s | 127.3 s | 118.0 s | 1.49x |
| **median** | **125.8 s** | **130.7 s** | **118.3 s** | **1.10x** |

Shard wall clock over the same period spans **375-602 s**, roughly **+/-23%** around the median.

### Class inventory (criterion 2)

Generated by `product/test-contracts/tools/generate-host-classification.mjs`, derived from a fresh source
inventory plus current source. The CI contract test compares both generated artifacts with that same
current tree. Host-classification schema v3 records test methods, known xUnit invocation cases, and
unknown-case methods separately. The classifier is conservative about unknown method-level host use and
custom service/fault/interceptor/factory/template-copy configuration. A reviewed override file records the
pre-#593 #563 holds that static host evidence cannot safely clear:
`product/test-contracts/api-host-classification-overrides.json`.

| Classification | Classes | Methods | Known invocations | Unknown-case methods | Share of methods |
|---|---:|---:|---:|---:|---:|
| reusable-host | 30 | 186 | 211 | 0 | 41.6% |
| converted (pilot) | 10 | 52 | 52 | 0 | 11.6% |
| fresh-host | 40 | 208 | 233 | 0 | 46.5% |
| migration-candidate | 1 | 1 | 1 | 0 | 0.2% |

`reusable-host` identifies a static conversion candidate, not an implementation-ready safety approval.
Every conversion still requires fresh per-test clients, unique per-test tagged logical data, and
assertions scoped to that data. Fixed seed identities and controlled values must be tagged or replaced
during conversion so one test cannot contaminate another test's setup or assertions.

Concrete service-replacement, custom-factory, showcase-template-copy, unscoped-count, and
shared-evidence classes are fresh-host until their isolation is explicitly reviewed. In addition, the
reviewed bootstrap-dependent, zero-saving, identifier-allocation, effectivity-count, saved-view-count,
and evidence-isolation holds are explicitly held fresh-host by review. The generated artifact records
the reason for every class and applies those reviewed holds; it is not a claim that static evidence alone
proves shared-host safety.

### What the current telemetry does and does not establish

An earlier revision put the full-conversion figure at 12.9% and concluded the gate was unreachable. That
calculation was wrong because it multiplied a removable-build estimate by the median host cost. The
historical accounting below is numerically reproducible, but it predates the conservative safety
reclassification above: its 62-class candidate pool is not identical to the current 30 reusable classes
and 23 reviewed holds.
It must not be read as current class-safety evidence:

| | |
|---|---:|
| All host builds, median run | 433 |
| All host time, median run | 415.4 s |
| Implied mean per build | 959 ms |
| Historical candidate classes with telemetry | 58 of 62 |
| Their host builds | 372 |
| Their host time | 352.4 s |
| Historical ideal-collapse builds removed (not current headroom) | 314 of 372 (84%) |
| Aggregate host time removed, all shards | 297.5 s |

`352.4 / 372 * 314 = 297.5 s` is an aggregate host-construction accounting result. Dividing it by
three would produce **99.2 s as an illustrative average**, but it is not a measurement of the critical
shard and is not an upper bound on wall-clock improvement. The current class-to-shard placement, xUnit
parallelism, and synchronization costs remain unresolved.

At the reviewed current tree, the 30 reusable classes contain **186 test methods and 211 known xUnit
invocations**. If every one were safely converted to exactly one class-scoped host, the method-level
conversion arithmetic is **156 method-to-class units (186 - 30)**. The invocation-level theoretical
factory-construction reduction is **181 (211 - 30)**. Both are planning arithmetic, exclude the 23
reviewed holds, and are not measured wall-clock savings. Any future unknown-case method must be excluded
from invocation arithmetic until its case count is supplied; runtime telemetry and randomized
full-concurrency wall-clock runs remain authoritative.

The measured pilot result remains valid: factories fell 471 -> 433 and wall clock did not visibly follow
inside the recorded +/-23% run-to-run spread. The full conversion may still be material, but only a
converted full-concurrency run can establish whether the 15% wall-clock gate is met.

### Disposition

**#563 and #566 were both closed `not_planned` on 2026-08-17.** The host-reuse rollout and the rule-matrix
migration are not planned work, and the incremental-conversion instruction this section used to carry is
withdrawn. Do not convert the reusable classes, publish class-by-shard attribution for that rollout, or
commission randomized full-concurrency runs to qualify it. #677 subsequently retired
`measure-api-host-reuse.ps1`, the tool that produced the evidence such a rollout would need, so restarting
this would mean rebuilding measurement that was removed on purpose.

[#942](https://github.com/AeroLinkDEV/requirements-management-tool/issues/942)'s F11 discussion revisited the
rule-matrix migration data recorded above and concluded that closing #566 was right: the migratable share of
the hosted API suite is small enough that relocating it is not the API lever. Do not re-propose either issue
on intuition; read that discussion first.

The measurements above stand as a dated historical record. The schema-template copy experiment remains a
negative result and should not be repeated, the aggregate host-time accounting is still not a wall-clock gate
result, and the migration ceiling is still not established.

## Baseline provenance (#567 acceptance criterion 14)

The `14m12s -> 10m14s` critical-path figure recorded above is the measured result of this sequence of merged
changes. They are listed so the baseline can be traced to the work that produced it rather than taken on trust:

| PR | Change | Effect on the critical path |
|---|---|---|
| #553 | Wait for the slowest gate rather than the sum of them | Made the critical path the real one; the previous figure summed independent jobs |
| #554 | Unify verification change control and procedure navigation | Product change; included because it moved journey counts |
| #555 | Buy the API suite four more cores | Reduced API shard wall clock |
| #556 | Split the journeys four ways and the API suite three | The largest single reduction; sharding replaced serial execution |
| #557 | Record which journeys are slow, since nothing does | No direct effect; produced the duration data #558 packs by |
| #558 | Pack the journeys by how long they take | Balanced the browser lanes using #557's recorded durations |
| #559 | Record what the packing actually did, and where it stops | No direct effect; documented the result and the remaining ceiling |

Three of these (#557, #559, and the measurement half of #558) changed no product behaviour and bought no
time directly. They are in the list because the reductions that followed were only possible once the
durations were recorded, and because the negative results they captured stopped the same approaches
being re-proposed.

Subsequent measurement changes are recorded separately: #571 (metrics foundation), #573 (rolling
collector), #574 (tested-tree provenance), #586 (read run metrics by name), #589 (full-gate scope label),
and #590 (evidence expiry and gate self-modification).

## Throughput review closeout, September 2026 (#942)

Issue #942 reviewed where the gate's time and runner cost go and recorded findings F1–F11. Under
**OWNER-942-SCOPE-01**, its closeout scope is a **throughput-review and disposition record** — not
certification that every proposed optimization was implemented, and not a demonstration of a hosted
whole-gate improvement.

The scope decision is adopted. Issue closure requires accepted documentation review, subsequently authorized
protected integration with the landed text verified, and recording the decision and accepted evidence
references on #942. **Only Sean may close the issue.**

Owner decision by Sean McCarthy, adopted on ChatGPT's recommendation under explicit delegation — not an
independent human technical review, a GitHub approval, or a merge approval. Independent review of the
underlying study: CG942-A-1, CG942-B-1, CG942-B2-1 and CG942-D-1, with a final method addendum.

### Where the underlying evidence lives

The delivered changes below carry their own accepted acceptance records on their pull requests and issues.
The investigation that produced the observations in this section, its independent reviews, and the adopted
disposition record are recorded on
[#942](https://github.com/AeroLinkDEV/requirements-management-tool/issues/942).

The raw run captures, observation ledger, failed-run classification extract and reviewer method addendum
behind the figures in this section were prepared as a local evidence packet and have **no public reference**.
Where a statement below depends on them, the necessary qualification is stated inline rather than delegated
to a link.

### What was delivered under the review

Each entry carries its own accepted record on its pull request or issue. **No performance claim is made here
for any of them**; where a delivery recorded targeted local work reductions, those remain reported at their
own measurement boundary and are not hosted whole-gate results.

| Delivery | What it did |
|---|---|
| #946 | Evidence and count reconciliation; scheduled browser completion required in the scheduled gate |
| #947 | Refreshed browser scheduling weights from recent queue evidence, with a provenance record |
| #949 | Isolated client behaviour checks in the advisory Fast lane |
| #951, #953 | Native operator-contract deduplication; backend test-project graph builds |
| #952, #1013 | Targeted persistence and lifecycle-seed scan reductions |
| #962, #1009 | Advisory API-packing report, authenticated observation collector and controlled benchmark — advisory, not wired into ordinary CI |
| #1010 | Standalone queue-to-main evidence reuse evaluator — shadow only, no skip authority |
| #1012 | Retained browser API logging — **diagnostics, not a demonstrated reliability fix** |

The earlier sections of this document remain the detailed record for the sharding, packing and cadence work
that preceded them.

### The September 2026 descriptive observation study

Derived from authenticated GitHub Actions job metadata over an inventory of the 300 most recent Product runs
(of 1,131 filtered), covering **2026-09-01T15:55:46Z to 2026-09-10T19:04:40Z**. Two sub-samples were drawn,
with **different bounds that must not be merged**:

- **Successful-run groups** — 85 successful *first workflow attempt* runs, primarily **2026-09-08 to 2026-09-10**.
- **Failure sample** — 33 failure-conclusion runs, **2026-09-01T23:56:58Z to 2026-09-10T17:54:32Z**.

These are **descriptive observations grouped by event role and executed-job count** — not controlled or
homogeneous performance cohorts. Equal executed-job counts may hide different selected test inventories,
workflow definitions, runner images or toolchains; that comparability was not comprehensively established.
The runs span **multiple source revisions** and are not pinned to one commit. The sample is not a complete
repository-wide population.

| Role group (executed jobs) | Observations | Runner-minutes median | Wall-span proxy median |
|---|---:|---:|---:|
| PR-readiness dispatch (17) | 9 | 151.23 | 23.05m |
| Merge-queue candidate (17) | 41 | 152.48 | 22.17m |
| Push on `main` (13) | 35 | 80.73 | 22.35m |

**Definitions — these are different clocks.**

- **Runner-minutes** — sum of (`completed_at` − `started_at`) over non-skipped job records. Not a billed
  financial cost. Included job intervals **contain configured test retries performed inside those jobs**;
  their separate contribution was not measured. Retry status is *unavailable*, not zero.
- **Wall-span proxy** — (`run.updated_at` − `run.run_started_at`). `updated_at` is **not** established as a
  terminal execution timestamp. This is **not** comparable with the instrumented critical path recorded
  earlier in this document, and it is not a replacement for the absent same-method remeasurement.
- **Latest-finishing included job under the analysis exclusion filter** — the job with the latest
  `completed_at` among non-skipped jobs, excluding two named aggregate jobs. The analysis did **not** traverse
  the workflow dependency graph or verify required-job membership. It is therefore **not** a
  required-dependency result, **not** a validated historical critical path, and **not** proof of what actually
  delayed the gate.

Later workflow attempts and other excluded observations are separate from the selected groups. Retained job
copies in later attempts are counted once at their originating execution and must never be counted again as
new execution.

The sum of the three role-group medians is approximately **384.4 Product runner-minutes**. That is an
**illustration** of one merge's three gate runs — a sum of marginal medians, not the median of matched
lifecycle totals. It excludes separately scoped Fast, requester and binding work. It is **not** a measured
per-pull-request lifecycle total, **not** a billed cost, and **not** evidence of improvement against the
review's original approximately 400-minute framing.

Under that filter, `AeroLink.Infrastructure.Tests` was the latest-finishing included job in **31 of 41**
merge-queue, **32 of 35** main-push and **7 of 9** readiness observations. This supports **investigation
priority within this sample only**.

### What the study did and did not establish

**No attributable hosted whole-gate improvement was established by this descriptive observation study.** That
statement is about the evidence available to the study. It does **not** deny the separately recorded targeted
local work reductions delivered against individual changes, and it does **not** demonstrate that no effect
exists.

Negative, inconclusive and unresolved results, retained:

- **API duration-based shard packing** measured **+1.7853%** on the slowest shard in a single predeclared
  local pair. **Inconclusive at one observation per configuration and unadopted — not disproven.** The
  advisory tooling and the original experiment are preserved.
- **Narrowing the journey subset on backend-only changes** (F9) is **measurement-incomplete**. An early proxy
  that inferred change content from which jobs executed was **invalid**, because job selection does not
  uniquely identify change content: broad effective events and broad-path rules can select all areas, while
  authenticated PR-readiness dispatches can instead pass an effective `pull_request` context to the
  classifier. Whether a client job ran was therefore not a reliable substitute for the original immutable
  change set. A later reconstruction used a pinned classifier and bases reconstructed against the then-current `origin/main`
  rather than each run's original immutable base input; it produced **exploratory reconstructions, not
  confirmed backend-only positive examples**. No verified counterexample, and no population traffic rate, was
  established. Broad browser coverage is retained because no accepted evidence authorizes reducing it.
- **Queue-to-main evidence reuse** is a **standalone, manually invoked shadow evaluator** with no automated
  observation hook and no operational skip authority. Its fallback predicate requires main diagnostic evidence
  containing the landed candidate, so its demonstrated cases do **not** establish eligibility at the original
  main-push decision. **No operational reuse saving was demonstrated**, and the operational opportunity is not
  quantified.
- **Execution cost associated with browser-only-failure runs.** Across the 33 inspected failure-conclusion
  runs in the wider 2026-09-01 to 2026-09-10 window, total failed-run execution was 4,132.6 runner-minutes, of
  which **runs whose only recorded failure-conclusion job lanes were browser-family lanes** accounted for
  2,639.8. The subtotal was independently reproduced from the supplied extract; **underlying causes and
  complete execution qualification were not**. This does not claim that every other selected required check
  passed, and it is **not** measured flakiness, waste, recoverable cost, or an optimization ranking. Browser
  reliability is tracked separately under #939 and #986.

### Suite-size snapshot at `d2ca7fa0202580b8cf4313606d4f56f8e072ec69` (2026-09-10)

A **dated partial snapshot**, not completion of the growth monitoring F8 proposes.

| Measure | Recorded earlier in this document | At this commit |
|---|---:|---:|
| Browser journey spec files | 104 | 151 |
| Journey spec files with recorded scheduling weights | 104 | 145 |
| Stale weight entries | — | 0 |
| `ShowcaseCollection` bound classes | 5 (#942, 2026-09-07) | 11 |
| `DisableParallelization` collection definitions | 13 (#942, 2026-09-07) | 16 |
| Tests per assembly | — | **not collected** |
| Runtime-discovered journey test identities | — | **not collected** |

Counts are **static counts of source declarations**, not runtime-expanded test identities. The two unavailable
rows were deliberately not collected; obtaining them requires test discovery, which was outside the read-only
scope. Six spec files carry no recorded scheduling weight: `requirement-redline-rendered`,
`team-work-rendered`, `upstream-change-request-authoring`, `webhook-delivery-operations`,
`workspace-contract`, `workspace-navigation`. `team-work-rendered.spec.ts` arrived with #1017; whether the
others are additions, renames or prior omissions is **not established**, and no rate of decay is claimed.
Missing weights are an optimisation input only — `plan-journey-shard.mjs` weights an unknown file at the
median and correctness never depends on it.

The existing rolling regression detector compares adjacent windows. **It can miss sustained cumulative growth
when individual adjacent-window comparisons remain below its thresholds.** That design limitation is the
motivation for F8; F8 proposes improving visibility and actionable reporting, and would not guarantee that a
performance regression can never recur.

### Adopted dispositions (OWNER-942-SCOPE-01)

A **condensed summary** of the adopted decisions, not a verbatim quotation; the full text is the owner
decision record on #942. Labels are deliberate: **AMENDED FOR CLOSEOUT**, **DEFERRED**, **RETAINED**,
**NOT PURSUED** and **UNEXERCISED** are not "passed". The decision identifiers D1–D7c are mapped to their
findings so every adopted decision is accounted for.

| Item | Decision | Disposition |
|---|---|---|
| **F1** post-merge binding | D1 | **DEFERRED.** Implementation and activation deferred; reuse remains disabled. Operational opportunity and net benefit remain unquantified. A future assignment must establish the complete decision-time evidence policy and distinguish it from the standalone shadow evaluator. "Tree equality plus weekly schedule" is not a complete authorization contract. |
| **F3** Infrastructure | D3 | **RETAINED** as separately scoped investigation/design work after the reporting follow-on. Additional shards, `ShowcaseCollection` splitting and greater concurrency are **not** preselected as the solution. |
| **F4(b)** scheduling-weight coverage | D7a | **RETAINED** with the F8 reporting follow-on, with its own acceptance row. Missing or stale weights must not silently reduce coverage. |
| **F5** API duration packing | D4 | **NOT PURSUED** in the present programme. Inconclusive and unadopted, **not** disproven. Tooling and the original experiment preserved. |
| **F6** planner/contracts platform move | D7b | **NOT PURSUED** in the present programme. A prioritization decision, **not** proof that a platform-neutral subset could never reduce cost. Existing Windows qualification preserved. |
| **F8** recurrence control | D2 | **RETAINED** as the next planned engineering follow-on, in all three parts: an absolute critical-path budget, a long-window comparison, and automated suite-size counters. |
| **F9** browser selection | D5 | **RETAIN BROAD COVERAGE.** No selection change. An owner decision under uncertainty, not experimental disproof of every narrower policy. |
| **F10** changed-area-weighted backend Fast | D7c | **RETAINED** as lower-priority, separately scoped design/qualification work. Full remains merge authority; the Fast budget evidence remains unresolved. |

| **This reconciliation** | D6 | Local documentation preparation authorized; **publication and integration require their separately authorized stage**. |

Retained obligations are recorded under their existing identifiers. **Creating a child issue does not complete
a parent requirement**, and no follow-on issue is a prerequisite of this closeout.

**Sean remains the custodian for commissioning and assigning retained future work. No agent is assigned that
work by implication.** Retained follow-ons require separate assignments; the scope decision does not
authorize their implementation.

### Original acceptance criteria under the amended scope

| # | Criterion | Status under OWNER-942-SCOPE-01 |
|---|---|---|
| 1 | Before/after **job** timings per item | **DOCUMENTED LIMITATION.** Per-delivery hosted-timing gaps accepted as limitations of a finite review. Missing comparisons are not marked supplied and no associated hosted saving is claimed. Future performance-changing work defines and satisfies its own measurement acceptance. |
| 2 | Eight or more comparable runs for a claimed wall-clock saving | **PRESERVED.** This closeout makes no such claim and required no sample-quota campaign. The standard is unchanged for future claims. |
| 3 | Nothing from the rejected list re-implemented | **MET.** No resumption of the rejected host-reuse rollout, per-test schema-template copy experiment, or shared-build-artifact proposal was established in the reviewed delivery scope. Shard counts unchanged. |
| 4 | F3, F4 and F5 measured together | **AMENDED FOR CLOSEOUT.** Not met as originally written. Replaced by a requirement to document individual dispositions, available integrated observations and the absence of a demonstrated combined improvement. Future performance-changing work arising from these proposals must assess the concurrently selected Infrastructure, browser and API lanes and the whole-gate outcome using an explicit comparison protocol; **a faster individual lane is not sufficient evidence of a faster gate**. Unadopted proposals need not be implemented to reproduce the originally projected configuration. This does not mark the original requirement passed and does not weaken the standard for future claims. |
| 5 | No required check, protection rule or gate-failure list weakened | **MET** for the current workflow definition and the review's own actions. Not a historical audit of every merged branch. |
| 6 | F1 preserves its refusals; a negative test proves a mismatched tree refuses | **UNEXERCISED, NOT PASSED.** Transfers to any future F1 implementation or activation, including protected-definition refusals, tree-SHA equality, the 30-day evidence-age rule, authenticated run/attempt and trusted-evidence binding, and negative cases proving ineligible evidence cannot suppress required testing. Deferring F1 removes its qualification from this closeout; it does not waive it, and shadow tests are not operational F1 acceptance. |
| 7 | F3 preserves `DisableParallelization`; self-verifies shard counts | **UNEXERCISED, NOT PASSED.** Transfers to any future F3 partition or concurrency change, including isolation, complete non-duplicated coverage, count reconciliation against actual results, empty/malformed-partition refusals, diagnostic retention and process/resource safety. Deferring F3 does not waive these or establish that sharding is safe or beneficial. |
| 8 | F4/F5 duration data stays an optimisation input only | **MET.** An unknown file is weighted at the median; correctness never depends on it. |
| 9 | No test deleted or moved | **MET WITHIN A BOUNDED AUDIT.** No removals matching the inspected declaration patterns were observed across the eleven audited delivery merges. Not a runtime-identity or assertion-semantics audit. |
| 10 | Persistent PostgreSQL (54329) and `product/.local` untouched | **MET** for the review's own actions; historical branches rest on their accepted delivery records. |
| 11 | This document updated with the new baseline, growth table and negative results | **AMENDED FOR CLOSEOUT.** Discharged only when this reconciliation has been accepted through review and subsequently integrated through the authorized protected path with the landed text verified. A **new same-method baseline measurement is not required**; its absence remains documented, and the descriptive wall-span proxy is not its replacement. |

**Scheduled-path qualification** is preserved as a gap recorded at the investigation snapshot: the weekly
Product schedule had not completed successfully since 2026-08-10 at that time, and no scheduled run had
occurred since the #946 changes to the scheduled-validation path. The cause of the intervening cancellations
was not demonstrated. It is not treated as a demonstrated defect or a completed proof, and no scheduled run
was commissioned or awaited for this closeout. **Applicable periodic-validation evidence must be established
before any future F1 activation.**

Other documented limitations of the study, which are **not** new research assignments and do not silently
become verified facts: historical per-run dependency topology, immutable pull-request base provenance, failure
causes, configured retry contributions, and the authenticity and completeness of the underlying captures.

### For anyone proposing throughput work after this

The review's durable lesson is not a number. A faster individual lane alone does not establish a faster gate;
job selection alone does not establish what a change touched; and a single observation does not establish a
repeatable performance improvement.

Read [AGENTS.md](../../AGENTS.md) on measurement-driven CI change, satisfy amended criterion 4's comparison
requirement, and inherit criteria 6 and 7 if the work touches post-merge evidence reuse or test partitioning.
