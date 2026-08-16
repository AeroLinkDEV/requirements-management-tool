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
- `-SinceOriginMain` is shorthand for the local `origin/main` ref; it never fetches or rebases and warns
  when that ref may be stale.
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
