# Feedback Time: Where a Pull Request's Wall Clock Actually Goes

Date: 2026-08-13

> **Live measurement record.** This supersedes the shard-count reasoning in
> [CI_COST_AND_READINESS_REVIEW.md](CI_COST_AND_READINESS_REVIEW.md), whose numbers describe a suite roughly a
> quarter of its present size. The workflows themselves remain the authority on what runs.

## Why this exists

A pull request that is green is not a pull request that is merged. Branch protection is **strict** (a branch
must be up to date with `main`), so every unrelated merge to `main` invalidates a passing run and starts the
whole gate again. On a day when `main` moved six times, one parity pull request was re-tested end to end six
times and found nothing new on any of them.

**Auto-merge is now enabled** (it was not when this was first measured), which removes the half of that cost
that came from a green run sitting unnoticed. It does not remove the other half: strict protection still
blocks a pull request that has fallen behind, and an armed pull request in that state waits rather than
merging. A merge queue would remove the rest and cannot be enabled here — see
[Merging into main](MERGING.md).

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

- **Both backend jobs still build the whole solution.** `dotnet test <project>` builds only that project's
  dependency graph, which would quietly stop proving that the tools and product projects outside it still
  compile. The duplicated build is the price of keeping "everything compiles" true once the assemblies run
  apart.
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

Tracked as a GitHub issue rather than done in this increment, because each is a real piece of work rather than
a setting:

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
3. **The strict-branch-protection treadmill.** A merge queue would let GitHub perform the rebase-retest-merge
   cycle without a human holding the window open. It **cannot be enabled on this repository**: merge queues
   require an organization-owned repository and this one is owned by a personal account. Auto-merge was
   enabled instead and takes back the part of the cost that came from waiting on a human. See
   [Merging into main](MERGING.md).

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

## API startup floor, measured (563A, 2026-08-14)

Per-shard telemetry from run 31843343040 (PR #581 head c002890) splits each hosted API test's wall time
into factory startup (host build + disposal, attributed from the construction call site) and test body
(wall minus startup). Structured per-shard artifacts: `api-telemetry-<shard>-<attempt>`.

| Shard | Tests | Factories | Summed wall | Summed startup | Startup % | Wall p10/median/p75/p95 | Startup p10/median/p75/p95 |
|---|---:|---:|---:|---:|---:|---:|---:|
| 1 | 133 | 159 | 778.4s | 229.9s | 30% | 3.7 / 5.6 / 7.7 / 11.5s | 0.7 / 1.0 / 1.5 / 8.7s |
| 2 | 152 | 163 | 970.9s | 350.8s | 36% | 4.0 / 5.9 / 7.5 / 13.5s | 1.0 / 1.5 / 1.9 / 10.0s |
| 3 | 134 | 148 | 833.8s | 194.0s | 23% | 3.4 / 6.6 / 7.7 / 11.1s | 0.7 / 1.0 / 1.2 / 3.9s |
| Total | 419 | 470 | 2583.1s | 774.7s | 30% | — | — |

Notes:

- The measured floor (~30% of summed wall) is lower than the historical ~39% because it counts only host
  build and disposal; factory construction, database open, and first-client latency are inside the host
  build, and the TRX wall is the whole test method. Startup percentages above 100% for classes whose
  factories are created in collection/class fixtures (e.g., `ShowcaseApiFixture`, `CancelReviewAuthorityTests`)
  are expected: fixture startup occurs outside any single test's TRX wall.
- 470 factories were created for 419 attributed tests; multiple-factory tests are listed explicitly.
- The schema-template copy experiment remains the recorded negative result: median rose 13.4s to 19.4s,
  p75 20.4s to 26.9s, summed CPU +22%; do not repeat a per-test database-copy strategy.
- Phase 2 (fresh-host/reusable-host/non-hosted inventory) and phase 3 (host-reuse pilot with >=10
  full-concurrency runs) are the next increments; this phase changes no isolation architecture.
