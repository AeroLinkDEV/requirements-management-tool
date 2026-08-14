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
  never from console text. All counters must be non-negative and internally consistent; a missing per-test
  duration makes the class/spec duration unknown rather than zero;
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
that should have produced a fragment, so an absent fragment is reported as missing instead of invisible; a
job whose duration is unknown, absent, or whose dependency group does not resolve makes the critical path
**unavailable with a reason** rather than numerically smaller. Phase B (rolling collection) is where GitHub
API queue and cancellation accounting lands.

## Security and trust

- Fragments contain no environment values, cookies, headers, passwords, connection strings, request/response
  bodies, or file contents. The builder refuses any field that matches a credential-*value* pattern
  (`Password=...`, `Bearer <long token>`, private-key blocks, connection-string assignments); legitimate
  test/class names that merely contain security vocabulary ("Password visibility test", "token refresh")
  are retained. The same credential guard is re-applied when fragments are read back from disk, the `run`
  object is a closed schema, and `job.matrix` is a bounded scalar-coordinate shape, so a crafted artifact
  cannot smuggle arbitrary content into the merged report.
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
| No fragments at all | critical path `job = null`, `durationMs = null` |

## Performance budget

The per-job instrumentation adds two small Node invocations plus one artifact upload per job (each under a
second), and the aggregation job runs only after the required gate. The measured overhead is documented in
`product/docs/BROWSER_AND_BACKEND_FEEDBACK_TIME.md`.

## Tests

```powershell
node --test product/ci-metrics/tests/trx.test.mjs product/ci-metrics/tests/playwright.test.mjs product/ci-metrics/tests/fragment.test.mjs product/ci-metrics/tests/aggregate.test.mjs
```

The suite (49 tests) covers schema-driven nested validation, real-format Playwright suite traversal,
representative TRX success/failure fixtures, valid/missing/malformed/oversized fragments, unknown schema
versions, failed/cancelled/skipped jobs, missing test reports, count mismatches, retried Playwright tests,
empty test sets, comparable-run grouping, matrix topology with distinct instances, run-identity
consistency with exclusion from derived aggregates, closed run/matrix schemas with read-time credential
guards, inconsistent structured counters, null-duration propagation, credential-value refusal with
legitimate security-vocabulary retention, bounded output, Markdown escaping, and critical-path computation
(including cycles, absent lanes, unknown durations, and missing dependency groups).

CI runs this suite in the `metrics-tooling` job from a clean checkout and reports its result in the
authoritative gate summary; the job is deliberately not part of merge authority.
