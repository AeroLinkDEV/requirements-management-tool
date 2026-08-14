# AeroLink CI metrics (phase A: foundation)

This directory is CI-only tooling. It is never product telemetry and adds no runtime behavior to AeroLink:
everything here runs inside GitHub Actions or local test tooling.

## What one fragment contains

This increment provides the tooling and tests; the workflow instrumentation that makes every quality-gate
job emit fragments is the second part of the same phase and lands as a separate, independently reviewable
PR after this foundation is merged.

Each fragment (`aerolink-ci-fragment/v1`, schema in
[`schema/v1-fragment.json`](schema/v1-fragment.json)) with:

- run identity: run id/attempt, event, repository, workflow and workflow revision, commit and **exact Git
  tree** SHA, PR number and base/head SHAs when the event provides them;
- job identity: stable job **group** (`backend-api`, `browser-pr`), unique job **instance**
  (`backend-api-1`, `browser-pr-4`), matrix coordinates, dependency list, and result (`success`, `failure`,
  `cancelled`, `skipped`, or `unavailable`);
- timings: job start, setup end, test end, and job end in milliseconds, plus the derived setup/test/
  upload-and-cleanup durations;
- counts: expected/executed/passed/failed/skipped/flaky totals read from TRX or the Playwright JSON report,
  never from console text. Playwright semantics are explicit: `expected` is the planned unique-test count,
  `executed` excludes skipped tests, `passed` is the final-pass total (clean plus retry-passes), and
  `flaky` is the retry-pass count. Row-derived flaky titles must agree with `stats.flaky`. All counters must
  be non-negative and internally consistent (`expected === executed + skipped` and
  `executed === passed + failed` for Playwright; `executed + skipped <= expected` and
  `passed + failed <= executed` for TRX); a missing per-test duration makes the class/spec duration unknown
  rather than zero. A Playwright flaky count without title evidence is never silent: the writer records an
  explicit `counts.missing` reason when the report has no suites hierarchy or no titles could be derived,
  the 20-title bound is surfaced as `flakyTitlesTruncated` with a truncation reason, and read-time
  validation rejects a `flaky > 0` fragment that has neither titles nor an explicit unavailable/truncated
  reason;
- slowest classes or specs (bounded to 50) and flaky test titles (bounded to 20);
- cache hit/miss for NuGet, npm, and Chromium where a job has those steps;
- the path classifier outputs when the job can see them.

Every missing value carries a `missing` reason. Nothing is ever reinterpreted as zero.

## How a fragment is produced

1. `bin/mark.mjs` appends named timestamps (`job-start`, `setup-end`, `test-end`) to `METRICS_TIMING_FILE`.
2. The primary test command runs as before; TRX/Playwright JSON paths are supplied via
   `METRICS_TRX_PATH` / `METRICS_PLAYWRIGHT_JSON_PATH`.
3. `bin/write-fragment.mjs` runs with `if: always()`: it reads the timing file, parses the structured test
   output, resolves the tree SHA, and writes a bounded fragment to `METRICS_FRAGMENT_PATH`.
4. The fragment is uploaded with `if: always()` and a 30-day retention.

Marker and writer failures warn and exit zero: metrics are never merge authority, and a metrics bug must not
turn a correct product gate red.

## Aggregation

`bin/aggregate.mjs <fragments-dir> <output-dir> [run-meta.json]` validates every fragment, checks that every
fragment belongs to the same run identity, resolves group-level dependencies to every matrix instance,
computes the longest-path critical path, and writes:

- `run-metrics.json` (`aerolink-ci-run/v1`) — the merged, bounded record;
- `run-metrics.md` — a concise human-readable summary naming the critical path and separating setup/build/
  test/upload time.

The optional `run-meta.json` may carry `{"queueDelayMs": <integer|null>, "expectedJobs": [...],
"expectedRun": {...}}` from a trusted default-branch source. `expectedJobs` names every job group/instance
that should have produced a fragment and supplies the **trusted dependency topology**: when present, the
dependency graph comes exclusively from that metadata (a fragment whose `needs` disagree is reported as a
topology disagreement and the trusted graph wins). `expectedRun` is required for an **authoritative**
merged record: fragments that disagree with it are excluded from every derived aggregate and recorded in
`missing`. Without `expectedRun`, conflicting fragment identities never resolve by artifact order — the
aggregate and critical path are unavailable — and a fully consistent set is aggregated but explicitly
labelled untrusted. A job whose duration is unknown, absent, or whose dependency group does not resolve
makes the critical path **unavailable with a reason** rather than numerically smaller. Phase B (rolling
collection) is where GitHub API queue and cancellation accounting lands.

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

## Missing-data contract

| Situation | Representation |
|---|---|
| No fragment uploaded (job cancelled before cleanup) | listed in `missing` with reason |
| Fragment file malformed | listed in `missing` with parse error |
| Fragment over size bound | listed in `missing` with reason |
| Unknown schema version | rejected; listed in `missing` |
| No TRX/Playwright report | `counts.source = null` + `counts.missing` reason |
| No timing markers | `timings.* = null` + `timings.missing` reasons |
| Expected job instance uploaded no fragment | listed in `missing` with reason (requires `expectedJobs`) |
| Any job duration unknown or absent | critical path `job = null` + explicit `unavailableReason` |
| Fragments disagree on run identity | excluded from jobs/counts/cache/flaky/classifications; recorded in `missing` with reason |
| Conflicting identities without `expectedRun` | aggregate unavailable; no identity chosen by artifact order |
| Duplicate job instance identity | no derived jobs/counts/cache/flaky/classifications published; recorded in `missing` |
| Reversed/inconsistent timing markers | rejected on read; the writer emits null durations + missing reasons |
| No fragments at all | critical path `job = null`, `durationMs = null` |

## Performance budget

The per-job instrumentation adds two small Node invocations plus one artifact upload per job (each under a
second), and the aggregation job runs only after the required gate. The measured overhead is documented in
`product/docs/BROWSER_AND_BACKEND_FEEDBACK_TIME.md`.

## Tests

```powershell
node --test product/ci-metrics/tests/trx.test.mjs product/ci-metrics/tests/playwright.test.mjs product/ci-metrics/tests/fragment.test.mjs product/ci-metrics/tests/aggregate.test.mjs
```

The suite (62 tests) covers schema-driven nested validation, real-format Playwright suite traversal,
representative TRX success/failure fixtures, valid/missing/malformed/oversized fragments, unknown schema
versions, failed/cancelled/skipped jobs, missing test reports, count mismatches, retried Playwright tests,
empty test sets, comparable-run grouping, matrix topology with distinct instances, run-identity
consistency with exclusion from derived aggregates and order-invariant conflict handling, trusted
expected-jobs topology with disagreement reporting, matrix property/key/value bounds, read-time timing
validation, duplicate-instance aggregate exclusion, closed run schemas with read-time credential guards,
inconsistent structured counters, planned/executed/passed semantics, explicit flaky-title
unavailable/truncation handling, null-duration propagation, credential-value refusal with legitimate
security-vocabulary retention, bounded output, Markdown escaping, and critical-path computation (including
cycles, absent lanes, unknown durations, and missing dependency groups).

CI runs this suite in the `metrics-tooling` job from a clean checkout and reports its result in the
authoritative gate summary; the job is deliberately not part of merge authority.
