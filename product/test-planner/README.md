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
- `-Mode Fast` runs the selected low-cost local commands after the wrapper's safety prompt; `-Mode Full`
  runs a broader local disposable SQLite/browser subset. Full is not full CI parity. `-DryRun` always stops
  after printing the plan.
- `-Explain` prints each changed path and its selected areas. `-Json` emits a machine-readable,
  plan-only result with symbolic refs, resolved base/head commit SHAs, provenance, CI selections, and safety
  fields. `node product/test-planner/tools/plan.mjs --help` prints usage without consulting Git.

The wrapper does not fetch, rebase, touch persistent PostgreSQL, write the product evidence roots, or
claim that local output is merge evidence. PostgreSQL-sensitive checks and the complete gate remain
authoritative in GitHub Actions. Full mode uses the repository's temporary SQLite/browser test subset;
it is not full CI parity, and you should review the commands before allowing it to run.

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

## Pull-request overlap advisory

`tools/check-overlap.mjs` is an API-only advisory checker for issue #569. The
`pull_request_target` workflow runs the checker from the trusted default branch;
it never checks out or executes pull-request-head code. The signal is not a
merge gate.

Before applying `ready-for-full-ci`, inspect the latest overlap comment or JSON
artifact and confirm that its `Current head SHA` matches the pull request you
are reviewing. The report also names the affected planner/CI lanes and bounded
reasons; use those lanes to decide what to coordinate, not as proof that a
test ran.

The status vocabulary and action are:

- `Critical overlap`: an exact changed-file overlap. Coordinate merge order,
  integrate/rebase the peer, or record an explicit reviewed disposition before
  requesting full CI. This remains advisory and never blocks by itself.
- `Coordinate`: different files share a reviewed hotspot/surface. Inspect the
  peer intent and affected lanes before full CI; it is a non-blocking warning.
- `Clear`: no active exact-file or reviewed-surface overlap was found for this
  SHA. Continue with the normal planner and full-gate process.
- `Unknown`: the API response, labels, files, comments, or bounds were
  incomplete. Do not treat it as Clear; repair or re-run the analysis first.

The exact PR label `overlap-reviewed` is an acknowledgement that a human or
agent reviewed the coordination decision. It is trusted metadata only: it is
shown in the report, never suppresses an overlap, and does not create a block.
An incomplete label list is `Unknown`, not an implicit absence of the label.

The checker compares eligible open pull requests by canonical changed paths
and by the repository hotspots in `lib/overlap.mjs`. Rename records include
both `filename` and `previous_filename`. Every PR identity and every returned
file record must be complete, including valid head/base SHAs, branch fields,
file status, and rename source where applicable. An absent, malformed, or
incomplete API record is `Unknown`, never `Clear`.

Analysis is deliberately bounded: at most 100 open pull requests, 30 eligible
pull requests, 1,000 files per pull request, 4,096 characters per path, 100
labels per pull request with 256 characters per label name, 1,000 comments per
target, 100,000 characters per comment, 435 pair comparisons,
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
