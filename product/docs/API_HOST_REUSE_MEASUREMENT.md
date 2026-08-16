# #563 API host-reuse measurement harness

`product/tools/measure-api-host-reuse.ps1` is a Windows-only, read-only-to-source benchmark harness for the
#563 host-reuse decision. It never checks out, fetches, merges, or changes either condition worktree. It writes
only to the selected output directory, apart from the normal `dotnet restore/build` outputs required by `Run` mode.
The output directory must be outside both worktrees.

## Plan first

Plan mode is the default. It discovers the API test list when the Release test assembly is already built, or it
can consume a test-list file for a contract smoke. It creates no API hosts, databases, evidence roots, or test
processes. Plan output must be a new or empty directory and must be outside both condition worktrees.

```powershell
$root = 'C:\work\aerolink'
$plan = Join-Path $env:TEMP 'aerolink-563-plan'

pwsh -NoProfile -File "$root\product\tools\measure-api-host-reuse.ps1" `
  -Mode Plan `
  -BaselinePath 'C:\work\aerolink-baseline' `
  -TreatmentPath 'C:\work\aerolink-treatment' `
  -OutputRoot $plan `
  -Runs 10 `
  -Seeds '563001,563002,563003,563004,563005,563006,563007,563008,563009,563010'
```

For every seed, the harness uses a deterministic Fisher–Yates shuffle followed by seeded assignment to the
lightest of three shards. The same saved partition is used for baseline and treatment, so a result cannot be
explained by silently changing the class packing. `plan.json` records the exact filters, expected case count,
class union, and alternating order. Plan mode writes only `plan.json` and `plan.md`.

## Run the paired observations

Prepare two clean worktrees at the exact baseline and treatment commits. The test lists must describe the same
classes, exact test-case names, and case count; a difference is rejected. Run mode requires distinct clean worktrees
at distinct SHAs, rejects `-TestListPath`, rejects `-SkipBuild`, and refuses a non-empty output directory. It restores
and builds each exact clean SHA before live test discovery and partition generation, then saves `plan.json` and
`plan.md` before any measured shard starts; those files describe the freshly built execution that will follow.

Every benchmark-owned `dotnet restore`, `dotnet build`, and `dotnet test` invocation includes
`--disable-build-servers`. Persistent MSBuild or compiler servers must not outlive the Job Object root, create
false cleanup failures, or leak warm build-server state from one paired condition into the other.

```powershell
pwsh -NoProfile -File "$root\product\tools\measure-api-host-reuse.ps1" `
  -Mode Run `
  -BaselinePath 'C:\work\aerolink-baseline' `
  -TreatmentPath 'C:\work\aerolink-treatment' `
  -OutputRoot 'C:\work\aerolink-563-results' `
  -Runs 10 `
  -TimeoutMinutes 30 `
  -ProcessTimeoutMinutes 60 `
  -MaxProcessTreeCount 256 `
  -Seeds '563001,563002,563003,563004,563005,563006,563007,563008,563009,563010' `
  -Warmup
```

The three shards of one observation run concurrently. Baseline and treatment never run concurrently. Odd-numbered
observations run baseline then treatment; even-numbered observations reverse that order. Each shard receives its own
`AEROLINK_API_TELEMETRY_JSONL`, TRX directory, standard-output log, standard-error log, and process metrics.

The harness reuses `product/ci-metrics/bin/aggregate-api-telemetry.mjs`; it does not reimplement the telemetry
attribution rules. It records per-shard and per-observation:

- exact Git SHA, branch, clean-status snapshot, SDK/OS/CPU metadata, seed, order, start/end, and exit code;
- canonical SHA-256 case-manifest facts, condition identity metadata, saved partitions, final clean worktree state,
  and comparable environment fingerprints;
- test count, passed/failed/skipped/other outcomes and class-to-shard assignment;
- worst-shard and summed-shard wall time;
- process-tree CPU milliseconds and Windows process-counter disk read/write bytes when available;
- factory count and `summedFactoryStartupMs` from the existing telemetry report;
- malformed/truncated telemetry and lock/cleanup signals.

## Output and invalid observations

Each observation is under `baseline\run-NN-seed-S` or `treatment\run-NN-seed-S`, with an `observation.json` file
and one `shard-N` directory per shard. Each shard directory contains TRX, raw JSONL, aggregated telemetry, and
logs. The root contains:

- `plan.json`/`plan.md`;
- `baseline-summary.json` and `treatment-summary.json`;
- `summary.json`, including quantiles and the decision.

An observation is invalid when a shard exits non-zero, its TRX count or exact test-case names differ from the manifest,
telemetry is missing or empty, telemetry is malformed/truncated, aggregation reports zero records or factories, a test
is failed/skipped/other, or lock/cleanup evidence is found. Missing disk performance counters or process-tree CPU
samples make the measurement incomplete; the test result remains visible, but the rollout decision is `inconclusive`
rather than a claimed pass. A completed shard that produced no successful active process sample is invalid.

Each shard is bounded by `-TimeoutMinutes` (30 by default). Restore, build, discovery, and aggregation processes are
bounded by `-ProcessTimeoutMinutes` (60 by default). Every harness-launched process uses the native Windows Job Object
boundary: the job is configured with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, the process is created suspended, assigned to
the job, and resumed only after assignment. Job drain, process exit, termination (when needed), and handle closure are
all queried and proven. A create/assign/resume/query/terminate/close failure is a cleanup failure and invalidates the
observation; no PID ancestry walk is treated as process-ownership authority. Nested-job assignment failures abort before
the process can run, and a job that cannot prove zero active processes is never reported as clean. Process-tree snapshots
remain diagnostic-only for CPU and disk attribution. Any cleanup uncertainty invalidates the observation and is never
silently converted into a pass.

No failure may be removed from the ten-run denominator. Re-run a failed seed only as a separately identified
diagnostic; it does not replace the failed observation.

## Decision rule

The decision requires ten valid and fully instrumented observations in each condition. Treatment passes only when:

```text
median(treatment worst-shard wall) <= 0.85 * median(baseline worst-shard wall)
```

and the median paired-seed improvement is also at least 15%. Factory-count reduction, summed startup, CPU, or disk
I/O are supporting evidence, not substitutes for the critical-path wall-clock gate. The existing `p10`, `median`,
`p75`, and `p95` quantiles are emitted for worst-shard wall, summed wall, CPU, disk, factories, and startup.

Evaluate recomputes validity, metrics completeness, required counts, unique/equal seed sets, and paired-seed joins from
the observations. It does not trust caller-provided `allValid`, `validObservationCount`, `metricsComplete`, or array
ordering; forged or inconsistent summaries are inconclusive and cannot pass. Each summary persists the complete sorted
case-name manifest and class facts. Evaluate then reopens both recorded condition paths, requires each to remain clean at
its recorded distinct SHA, rediscovers the live test manifest and environment, and compares those facts to both the
summary and every condition metadata record. Matching hashes alone are not authentication. Missing or invalid paths
fail before any decision file is written. It also canonicalizes every filesystem component, including absolute or
relative NTFS junction/symlink targets and bounded loop detection, before applying output containment against the live
condition worktrees.

To evaluate already-produced condition summaries without running tests:

```powershell
pwsh -NoProfile -File "$root\product\tools\measure-api-host-reuse.ps1" `
  -Mode Evaluate `
  -BaselineSummaryPath 'C:\work\aerolink-563-results\baseline-summary.json' `
  -TreatmentSummaryPath 'C:\work\aerolink-563-results\treatment-summary.json' `
  -OutputRoot 'C:\work\aerolink-563-results\decision'
```

The harness is intentionally a measurement tool, not a conversion tool. It does not touch PostgreSQL or persistent
demo evidence. Component-level attribution for schema/bootstrap/evidence/first-client work remains outside the
current phase-v2 telemetry contract and must not be inferred from the host total.
