# AeroLink CI metrics (phase A)

This directory is CI-only tooling. It is never product telemetry and adds no runtime behavior to AeroLink:
everything here runs inside GitHub Actions or local test tooling.

## What one fragment contains

Every quality-gate job in `.github/workflows/ci.yml` emits a fragment (timing markers, TRX/Playwright/JUnit
counts, cache hits, classifier outputs) that is uploaded with `if: always()` and
`continue-on-error: true`. The non-authoritative `metrics-report` job builds run metadata with
`bin/build-run-meta.mjs` (exact tree SHA, expected job topology derived from the event and classifier
outputs, deliberate skips, provenance), downloads only the current-attempt fragments, aggregates them, and
publishes bounded JSON + Markdown artifacts after the required gate.

Each fragment (`aerolink-ci-fragment/v2`, schema in
[`schema/v2-fragment.json`](schema/v2-fragment.json); the v1 file remains on disk only for validating older
artifacts) with:

- run identity: run id/attempt, event, repository, workflow and workflow revision, commit and **exact Git
  tree** SHA, PR number and base/head SHAs when the event provides them;
- job identity: stable job **group** (`backend-api`, `browser-pr`), unique job **instance**
  (`backend-api-1`, `browser-pr-4`), matrix coordinates, dependency list, and result (`success`, `failure`,
  `cancelled`, `skipped`, or `unavailable`);
- timings: job start, setup end, test end, and job end in milliseconds, plus the derived setup/test/
  `postTestMs` durations. `postTestMs` is exactly the interval between the test-end marker and the fragment
  writer; the later artifact upload and runner cleanup are **not** observable from inside the job and are
  never labelled as upload time. Real completion/upload timing comes from Actions metadata (phase B);
- counts: expected/executed/passed/failed/skipped/flaky totals read from TRX (one or more comma-separated
  files), the Playwright JSON report, or Node's JUnit XML report, never from console text. Playwright
  semantics are explicit: `expected` is the planned unique-test count, `executed` excludes skipped tests,
  `passed` is the final-pass total (clean plus retry-passes), and `flaky` is the retry-pass count.
  Row-derived flaky titles must agree with `stats.flaky`. All counters must be non-negative and internally
  consistent (`expected === executed + skipped` and `executed === passed + failed` for Playwright and
  node-junit; `executed + skipped <= expected` and `passed + failed <= executed` for TRX); a missing
  per-test duration makes the class/spec duration unknown rather than zero. A Playwright flaky count
  without title evidence is never silent: the writer records an explicit `counts.missing` reason when the
  report has no suites hierarchy or no titles could be derived, and the structured flags
  `flakyTitlesUnavailable` / `flakyTitlesTruncated` carry the state. Read-time validation is a strict
  structural state machine: available detail requires exactly `flaky` titles; unavailable requires
  `flaky > 0`, zero titles, and a bounded reason; truncated requires `flaky > 20`, exactly 20 retained
  titles, a bounded reason, and unavailable=false; zero flakes requires both flags false and no titles.
  Non-Playwright fragments must not carry flaky titles, title-state flags, or a flaky count. Free text is
  never trusted as evidence;
- slowest classes or specs (bounded to 50; JUnit report paths are reduced to bounded basenames before
  fragment construction, so absolute workspace/profile paths never reach an artifact) and flaky test titles
  (bounded to 20);
- cache hit/miss for NuGet, npm, and Chromium where a job has those steps;
- the path classifier outputs when the job can see them.

Every missing value carries a `missing` reason. Nothing is ever reinterpreted as zero.

## How a fragment is produced

1. `bin/mark.mjs` appends named timestamps (`job-start`, `setup-end`, `test-end`) to `METRICS_TIMING_FILE`.
2. The primary test command runs as before; TRX/Playwright JSON/JUnit paths are supplied via
   `METRICS_TRX_PATH` (comma-separated files are summed) / `METRICS_PLAYWRIGHT_JSON_PATH` /
   `METRICS_JUNIT_PATH`.
3. `bin/write-fragment.mjs` runs with `if: always()`: it reads the timing file, parses the structured test
   output, resolves the tree SHA, and writes a bounded fragment to `METRICS_FRAGMENT_PATH`.
4. The fragment is uploaded with `if: always()`, a 30-day retention, and an attempt-scoped artifact name
   (`ci-metrics-fragment-<job>-<instance>-<run-attempt>`).

Marker, writer, and upload failures warn and exit zero, and every metrics-only step additionally runs with
`continue-on-error: true`: metrics are never merge authority, and a metrics bug must not turn a correct
product job or the required gate red. `tests/ci-workflow-contract.test.mjs` statically enforces that every
metrics-only step in `.github/workflows/ci.yml` is isolated and that fragment artifacts are attempt-scoped.

## Aggregation

`bin/aggregate.mjs <fragments-dir> <output-dir> [run-meta.json]` validates every fragment, checks that every
fragment belongs to the same run identity, resolves group-level dependencies to every matrix instance,
computes the longest-path critical path, distinguishes deliberately skipped jobs from missing fragments,
models each test family separately from the sourced totals, and writes:

- `run-metrics.json` (`aerolink-ci-run/v2`) — the merged, bounded record;
- `run-metrics.md` — a concise human-readable summary naming the critical path and separating setup/build/
  test/post-test time, plus deliberate skips and families without structured counts. `merged.jobs[].slowest`
  carries the bounded class/spec duration detail so one merged record can drive rebalancing.

The optional `run-meta.json` may carry `{"queueDelayMs": <integer|null>, "expectedJobs": [...],
"expectedRun": {...}, "skippedJobs": [...], "provenance": {"mode": "...", "reason": "..."}}` from the
workflow. `expectedJobs` names every job group/instance that should have produced a fragment and supplies
the dependency topology: when present, the dependency graph comes exclusively from that metadata (a
fragment whose `needs` disagree is reported as a topology disagreement and the expected graph wins).
`skippedJobs` lists deliberately skipped jobs with reasons so an absent fragment is never confused with a
job that never existed. `expectedRun` is required for an **authoritative** merged record: run identity is
(id, sha, tree, workflow revision, repository) **without attempt** — a partial rerun is a continuation of
the same run, and jobs that were not rerun keep their earlier-attempt fragment. The aggregator selects the
latest fragment per instance, records earlier attempts in `superseded`, and excludes fragments that
disagree with the expected run identity from every derived aggregate (`missing`). Same-attempt duplicates
remain a contradiction and make the derived aggregates unavailable. Without `expectedRun`, conflicting
fragment identities never resolve by artifact order — the aggregate and critical path are unavailable —
and a fully consistent set is aggregated but explicitly labelled untrusted. A job whose duration is
unknown, absent, or whose dependency group does not resolve makes the critical path **unavailable with a
reason** rather than numerically smaller. Phase B (rolling collection) is where GitHub API queue and
cancellation accounting lands.

`bin/build-run-meta.mjs` emits `expectedRun` (exact commit and tree SHA), `expectedJobs`, and `skippedJobs`
from the workflow's own event and classifier predicates (mirrored in `tests/build-run-meta.test.mjs` for
docs-only, backend-only, client-only, full PR, merge-group, push, schedule, and dispatch). Fragments are
untrusted data; they never define the topology. **Provenance:** on `pull_request` and `merge_group` runs
the same-workflow checkout is PR-controlled, so the metadata is labelled `shadow` and the merged record can
never claim trusted identity; trusted validation is phase B. Only default-branch push/schedule/dispatch
runs label the record trusted. A PR modification cannot promote its own record to trusted.

## Security and trust

- Fragments contain no environment values, cookies, headers, passwords, connection strings, request/response
  bodies, or file contents. The builder refuses any field that matches a credential-*value* pattern
  (`Password=...`, `Bearer <long token>`, private-key blocks, connection-string assignments); legitimate
  test/class names that merely contain security vocabulary ("Password visibility test", "token refresh")
  are retained. The same credential guard is re-applied when fragments are read back from disk, the `run`
  object is a closed schema, and `job.matrix` is a bounded scalar-coordinate shape (max 8 properties, key
  and value lengths bounded, scalar types only), so a crafted artifact cannot smuggle arbitrary content into
  the merged report. Timing relationships are validated on read: reversed markers or derived values that do
  not match their raw markers are rejected rather than published as zero.
- Fragment and report sizes are bounded; oversized or malformed fragments are reported as missing with a
  reason.
- The aggregator treats fragment values as data, never as commands, paths, expressions, or scripts.
- The reporting job is not required and never influences merge authority; the required check remains
  `Report what this run validated`.
- Every metrics-only step in product jobs and the gate runs with `continue-on-error: true`; injected marker,
  writer, or upload failures cannot change an otherwise successful product job/gate result (enforced by
  `tests/ci-workflow-contract.test.mjs`).
- Fragment artifacts are attempt-scoped and the report downloads `ci-metrics-fragment-*` for the current
  run into per-artifact subdirectories (never a previous run's `ci-metrics-run-*` merged report);
  per-instance the latest attempt wins and earlier attempts are recorded as `superseded`.
- The run-level totals are explicitly a **sourced-families subtotal**: `countsModel.sourcedFamilies`
  counts distinct families with structured counts, `sourcedJobInstances` counts the job instances behind
  them, `missingFamilies` lists every selected test family (group + instance) without structured counts,
  and `totalIsPartial` is true whenever one exists. The taxonomy includes backend-api, backend-core,
  browser-pr, browser-production, browser-full, metrics-tooling, script-contracts, and postgresql-smoke;
  script-contracts and postgresql-smoke have no structured test runner and are listed as missing rather
  than silently excluded.
- JUnit `file` attributes are never published: `sanitizeFilePath` reduces them to bounded basenames before
  fragment construction (POSIX and Windows absolute paths covered by tests).

## Missing-data contract

| Situation | Representation |
|---|---|
| No fragment uploaded (job cancelled before cleanup) | listed in `missing` with reason |
| Fragment file malformed | listed in `missing` with parse error |
| Fragment over size bound | listed in `missing` with reason |
| Unknown schema version | rejected; listed in `missing` |
| No TRX/Playwright/JUnit report | `counts.source = null` + `counts.missing` reason |
| No timing markers | `timings.* = null` + `timings.missing` reasons |
| Expected job instance uploaded no fragment | listed in `missing` with reason (requires `expectedJobs`) |
| Any job duration unknown or absent | critical path `job = null` + explicit `unavailableReason` |
| Fragments disagree on run identity | excluded from jobs/counts/cache/flaky/classifications; recorded in `missing` with reason |
| Conflicting identities without `expectedRun` | aggregate unavailable; no identity chosen by artifact order |
| Duplicate job instance identity | no derived jobs/counts/cache/flaky/classifications published; recorded in `missing` |
| Same instance from an earlier attempt (partial rerun) | latest attempt wins; earlier fragment listed in `superseded` with reason |
| Deliberately skipped job (event/classification) | listed in `skipped` with reason; never treated as missing |
| Selected test family without structured counts | listed in `countsModel.missingFamilies`; totals remain a labelled sourced subtotal |
| Reversed/inconsistent timing markers | rejected on read; the writer emits null durations + missing reasons |
| Flaky titles unavailable or truncated | carried into the merged record (`flakyTitleEvidence`) and Markdown summary per job |
| No fragments at all | critical path `job = null`, `durationMs = null` |

## Performance budget

Overhead is measured from Actions job/step timestamps, not from the fragment writer's own clock, and
critical-path overhead is reported separately from post-gate report time. On run 31790715303:

- The required gate performs product enforcement before any telemetry prerequisite; its metrics-only
  checkout plus marker/upload steps added roughly 4s to the gate (Actions step timestamps), within the
  30s budget.
- The non-authoritative `metrics-report` job ran after the gate and took about 10s of Actions wall time; it
  is not a required check and never extends the merge gate.
- Actions-reported step wall time for individual marker steps varied between jobs (up to 13s for
  `changes` and 8s for `script-contracts` on that run); every such step is isolated with
  `continue-on-error: true`, so a slow or failing telemetry step cannot change a product result. Phase B
  records exact queue/completion timing from Actions metadata in the merged artifact.

The per-job instrumentation adds two small Node invocations plus one artifact upload per job, all isolated
with `continue-on-error: true`, and the aggregation job runs only after the required gate (and after every
independently selected producer, so push/schedule reports are complete).

## Tests

Run the full suite exactly as CI does:

```powershell
node --test product/ci-metrics/tests/trx.test.mjs product/ci-metrics/tests/playwright.test.mjs product/ci-metrics/tests/fragment.test.mjs product/ci-metrics/tests/aggregate.test.mjs product/ci-metrics/tests/build-run-meta.test.mjs product/ci-metrics/tests/junit.test.mjs product/ci-metrics/tests/ci-workflow-contract.test.mjs
```

The suite (90 tests) covers schema-driven nested validation, real-format Playwright suite traversal,
representative TRX success/failure fixtures, Node JUnit parsing, valid/missing/malformed/oversized
fragments, unknown schema versions, failed/cancelled/skipped jobs, missing test reports, count mismatches,
retried Playwright tests, empty test sets, comparable-run grouping, matrix topology with distinct
instances, exact event/classification topology (docs-only, backend-only, client-only, full PR,
merge-group, push, schedule, dispatch), deliberate skips, provenance shadow/trusted semantics,
run-identity consistency with exclusion from derived aggregates and order-invariant conflict handling,
expected-jobs topology with disagreement reporting, matrix property/key/value bounds, read-time timing
validation, duplicate-instance aggregate exclusion, closed run schemas with read-time credential guards,
inconsistent structured counters, planned/executed/passed semantics, explicit flaky-title
unavailable/truncation handling with structural read-time rules and run-level propagation, null-duration
propagation, credential-value refusal with legitimate security-vocabulary retention, bounded output,
Markdown escaping, critical-path computation (including cycles, absent lanes, unknown durations, and
missing dependency groups), and the static workflow contract (failure isolation, attempt-scoped
artifacts, report ordering, structured-count wiring, derived gate needs).

CI runs this suite in the `metrics-tooling` job from a clean checkout and reports its result in the
authoritative gate summary; the job is deliberately not part of merge authority.
