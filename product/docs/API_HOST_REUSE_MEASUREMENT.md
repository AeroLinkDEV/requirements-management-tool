# #563 API host-reuse measurement harness

`product/tools/measure-api-host-reuse.ps1` is a Windows-only, read-only-to-source benchmark harness for the
#563 host-reuse decision. It never checks out, fetches, merges, or changes either condition worktree. It writes
only to the selected output directory, apart from the normal `dotnet restore/build` outputs when `Run` mode is
used without `-SkipBuild`. The output directory must be outside both worktrees.

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
at distinct SHAs, rejects `-TestListPath`, and refuses a non-empty output directory. Build is performed once per
condition before measurement unless `-SkipBuild` is explicitly supplied. Run mode saves `plan.json` and `plan.md`
before any restore/build; those files describe the execution that will follow.

```powershell
pwsh -NoProfile -File "$root\product\tools\measure-api-host-reuse.ps1" `
  -Mode Run `
  -BaselinePath 'C:\work\aerolink-baseline' `
  -TreatmentPath 'C:\work\aerolink-treatment' `
  -OutputRoot 'C:\work\aerolink-563-results' `
  -Runs 10 `
  -TimeoutMinutes 30 `
  -ProcessTimeoutMinutes 60 `
  -Seeds '563001,563002,563003,563004,563005,563006,563007,563008,563009,563010' `
  -Warmup
```

The three shards of one observation run concurrently. Baseline and treatment never run concurrently. Odd-numbered
observations run baseline then treatment; even-numbered observations reverse that order. Each shard receives its own
`AEROLINK_API_TELEMETRY_JSONL`, TRX directory, standard-output log, standard-error log, and process metrics.

The harness reuses `product/ci-metrics/bin/aggregate-api-telemetry.mjs`; it does not reimplement the telemetry
attribution rules. It records per-shard and per-observation:

- exact Git SHA, branch, clean-status snapshot, SDK/OS/CPU metadata, seed, order, start/end, and exit code;
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
bounded by `-ProcessTimeoutMinutes` (60 by default). A timeout stops only the exact process tree launched by the
harness, verifies owned descendants exit, and invalidates that observation; it is never silently converted into a pass.

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
ordering; forged or inconsistent summaries are inconclusive and cannot pass.

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
