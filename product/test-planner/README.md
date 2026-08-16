# Changed-test planner

The changed-test planner answers two separate questions from one classifier:

1. Which local checks are worth running before a push?
2. Which GitHub Actions jobs will the current `ci.yml` conditions select?

The planner is plan-only. It derives the CI forecast from the workflow text, reports its version and
content hash, and refuses to guess when a workflow expression is outside its supported subset. A
planner/workflow/shared-contract change selects every area. Renames and copies contribute both paths,
including the old path, and Windows separators and case are normalized before classification.

## Windows entry point

From the repository root, double-click `TEST_AEROLINK_CHANGED.bat` or run the PowerShell entry point:

```powershell
.\product\scripts\Get-AeroLinkTestPlan.ps1 -SinceOriginMain -Explain -DryRun
.\product\scripts\Get-AeroLinkTestPlan.ps1 -Paths product\client\src\App.tsx -Mode Fast -DryRun
.\product\scripts\Get-AeroLinkTestPlan.ps1 -Base origin/main -Head HEAD -Mode Full
.\product\scripts\Get-AeroLinkTestPlan.ps1 -Paths product\test-planner\lib\classify.mjs -Json -DryRun
```

The switches are deliberately explicit:

- `-Base` and `-Head` compare Git refs. The planner uses `merge-base` and a three-dot diff.
- `-SinceOriginMain` requires an existing local `origin/main` ref; it never creates, fetches, or rebases
  that ref. If it is absent, the planner fails clearly and asks for an explicit `-Base` or `-Paths`; when
  present, it warns that the remote-tracking ref may be stale.
- `-Paths` supplies paths directly and accepts Windows separators. It cannot be combined with `-Base`,
  `-Head`, or `-SinceOriginMain`.
- `-Mode Fast` runs the selected low-cost local commands without claiming merge authority; `-Mode Full`
  runs the selected local suites, the non-documentation operator/recovery script-contract family, and the
  broader disposable SQLite/browser subset. When the CI forecast selects PostgreSQL, Full requires Docker
  and runs the migration/bootstrap proof only in a uniquely named container, loopback port, database and
  labeled temporary volume. Missing Docker fails as `not-proven`; it never falls back to the persistent
  PostgreSQL service. `-DryRun` always stops after printing the plan.
- `-Explain` prints each changed path and its selected areas. `-Json` emits a machine-readable,
  plan-only result with symbolic refs, resolved base/head commit SHAs, provenance, CI selections, and safety
  fields. `node product/test-planner/tools/plan.mjs --help` prints usage without consulting Git.

The wrapper does not fetch, rebase, touch persistent PostgreSQL, write the product evidence roots, or
claim that local output is merge evidence. The complete gate and merge authority remain in GitHub Actions;
this tool preserves #561's Fast/full-gate hold. Full reports every selected CI job as `executedCiJobs` or
`ciOnlyJobs`, and its compact `AEROLINK_TEST_PLAN_RESULT` includes monotonic `execution.timing.totalMs`
plus one elapsed duration per executed step. Plan-only JSON reports `execution.status=not-run` and zero
elapsed time. These measurements are evidence for feedback only; they do not fabricate or imply the
3–4 minute #561 target until a representative disposable-checkout measurement is collected.

The disposable PostgreSQL lane owns and removes its container, loopback port, database and labeled volume
in a `finally` block. It also stops its API process and restores the parent process environment. It never
calls the persistent `Start-Postgres` launcher, port 54329, or writes `product/.local` evidence.

## Node planner

The shared implementation is `tools/plan.mjs` and the classifier is `lib/classify.mjs`:

```powershell
node product/test-planner/tools/plan.mjs --since-origin-main --dry-run
node product/test-planner/tools/plan.mjs --files product/src/AeroLink.Domain/Rules/Rule.cs --json --dry-run

# Backend/domain only
node product/test-planner/tools/plan.mjs --files product/src/AeroLink.Domain/Rules/Rule.cs --dry-run
# Client only
node product/test-planner/tools/plan.mjs --files product/client/src/App.tsx --dry-run
# Migration/persistence
node product/test-planner/tools/plan.mjs --files product/src/AeroLink.Infrastructure/Persistence/Migrations/0001_init.cs --dry-run
# Operator script (conservative broad fallback)
node product/test-planner/tools/plan.mjs --files START_AEROLINK_PRODUCTION.bat --dry-run
# Mixed client and migration change
node product/test-planner/tools/plan.mjs --files product/client/src/App.tsx product/src/AeroLink.Infrastructure/Persistence/Migrations/0001_init.cs --dry-run
```

The workflow calls `tools/classify-ci.mjs` and publishes planner version/hash, the fallback reason and unknown
paths, selected and skipped jobs, and each job's condition/reason in the gate summary. The authoritative backend-core
contract invocation is directory-driven, so every `product/test-contracts/tests/*.test.mjs` file runs.

## Safe timing protocol

For a bounded feedback measurement, use a disposable clean worktree at the exact SHA under review. Start
with `-Json -DryRun` for representative docs, backend, client, browser and mixed paths; then run one Fast
and one Full case at a time, capture the compact result and wall-clock output, and verify `git status`, the
local `origin/main` SHA, `product/.local`, and persistent PostgreSQL are unchanged. A PostgreSQL case is
valid only when Docker is available and the result says the disposable gate passed; a missing daemon or a
failed cleanup is `not-proven`, never a green Full result. Remove the disposable worktree and any temporary
logs after each case. This protocol measures the target; it does not claim the target was met.

## Pull-request overlap advisory

`tools/check-overlap.mjs` is an API-only advisory checker for issue #569. The
`pull_request_target` workflow runs the checker from the trusted default branch;
it never checks out or executes pull-request-head code. The signal is not a
merge gate.

The checker compares eligible open pull requests by canonical changed paths
and by the repository hotspots in `lib/overlap.mjs`. Rename records include
both `filename` and `previous_filename`. Every PR identity and every returned
file record must be complete, including valid head/base SHAs, branch fields,
file status, and rename source where applicable. An absent, malformed, or
incomplete API record is `Unknown`, never `Clear`.

Analysis is deliberately bounded: at most 100 open pull requests, 30 eligible
pull requests, 1,000 files per pull request, 4,096 characters per path, 1,000
comments per target, 100,000 characters per comment, 435 pair comparisons,
and 30,000 analyzed file paths. The checker fails closed to `Unknown` when a
bound is exceeded; it does not truncate the input and claim that the remaining
evidence is clean. The JSON artifact reports `analysisComplete` and the limits
used for that run.

Only marker comments authored by the exact `github-actions[bot]` account with
GitHub's `Bot` type are managed. A human comment containing the marker text is
left untouched. PR-controlled titles, branches, authors, paths, reasons, and
timestamps are bounded and escaped before entering Markdown comments.

The workflow has a trusted-base presence guard. If the checker is absent from
the checked-out default branch, the guard writes a bounded schema-compatible
`Unknown` JSON artifact and a warning instead of silently skipping the
analysis. The `#569` rollout remains open until the integrated trusted-base
lifecycle behavior, normal runtime, lane guidance, and reviewed disposition are proven.
