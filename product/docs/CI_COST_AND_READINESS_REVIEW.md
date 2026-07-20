# CI Cost and Requirements Readiness Review

Date: 2026-07-19

## Purpose

This record explains the July 2026 changes made after reviewing AeroLink's browser-test stability and GitHub Actions usage. It is intended to let a future Codex session or human reviewer understand what changed, why it changed, what tradeoffs were accepted, and what should be verified before merge.

## Problems observed

1. The Requirements Explorer journey had accumulated cold-run timing fixes. The newest change waited up to 30 seconds for the text `150 requirements`. That asserted the expected data, but it coupled readiness to a particular seeded count and masked the distinction between "the page shell is visible" and "the controlled requirement index is ready".
2. A normal workflow could start seven jobs: backend, client, three browser shards, a result-only aggregate job, and PostgreSQL smoke.
3. Three browser shards improved elapsed time but increased total runner setup and duplicated .NET, npm, Playwright, and API build work.
4. Full validation ran on every PR update and then again on the merge push to `main`.
5. PostgreSQL smoke ran even for changes unrelated to persistence, migrations, identity, or bootstrap.
6. Browser timing artifacts were uploaded from every shard on every successful PR.
7. GitHub-maintained actions were referenced by mutable major-version tags.
8. Documentation-only changes could invoke the entire product gate.

## Implemented decisions

### 1. Semantic requirement-index readiness

The enterprise Requirements Explorer journey no longer extends the assertion on `150 requirements` to 30 seconds. It first waits for the existing accessible `Loading controlled requirements` status to disappear, then checks the exact controlled count.

Why:

- The loading status is a product state, whereas `150 requirements` is showcase data.
- A failure now distinguishes "index never became ready" from "index became ready with incorrect data."
- The test retains the exact 150-record functional assertion.

Follow-up for Codex:

- Consider promoting readiness to an explicit stable attribute such as `data-testid="requirements-ready"` or an `aria-live` status containing the loaded count.
- Consider disabling search, filter, paging, and row-selection controls while `loading` is true, especially during initial hydration. The current change improves synchronization without expanding the product UI diff.
- Confirm that request cancellation or stale-response protection exists when filters change rapidly. The current component debounces requests but may still permit an older request to resolve after a newer one.

### 2. Path-aware classification

A lightweight `changes` job classifies the diff into documentation-only, backend, client, browser, and PostgreSQL scopes.

Why:

- Documentation and design changes should not consume multiple Windows browser runners.
- Backend-only or client-only changes should not automatically invoke unrelated lanes.
- PostgreSQL verification should be preserved where it provides evidence, not treated as a universal tax.

Tradeoff:

- Path rules are policy and must be maintained when directories or responsibilities change.
- The workflow file itself is classified as requiring browser and PostgreSQL validation so CI changes prove their own behavior.

### 3. Combined static validation

Backend build/tests and client lint/type-check/build now share one Ubuntu validation job. The job retains the branch-protection-facing name `Build, test, and exercise product journeys`.

Why:

- The previous aggregate job started a separate Ubuntu runner only to compare three result strings.
- Combining static lanes removes a runner and repeated checkout/setup while retaining a stable required-check name.
- Ubuntu is sufficient for these cross-platform .NET and Node checks; Windows remains where the browser harness currently depends on PowerShell and Windows paths.

Codex review:

- Confirm no backend test has a hidden Windows dependency.
- Confirm repository branch protection requires this stable validation name and separately requires the browser lane if desired.

### 4. Two browser shards for PRs

Routine pull requests use two fail-fast browser shards. Pushes to `main`, nightly schedules, and manual dispatches use the complete three-shard suite with `fail-fast: false`.

Why:

- Two shards preserve parallel browser coverage while reducing duplicate setup by one third.
- Fail-fast saves runner time on clearly broken PRs.
- Full diagnostics remain available after merge, nightly, and on demand.

Tradeoff:

- PR elapsed time may increase slightly compared with three shards.
- A failing PR may not report every independent shard failure until rerun; nightly/full runs retain complete diagnostics.

### 5. Conditional PostgreSQL smoke

PostgreSQL migrations and secure bootstrap run when persistence, migration, API infrastructure, identity/auth/bootstrap, tests, or CI workflow paths indicate relevance. They always run on pushes to `main`, nightly schedules, and manual runs.

Why:

- This preserves production-database evidence for integration points that can affect it.
- Routine UI or documentation work no longer pays for a PostgreSQL service container and full API build.

Codex review:

- Review the regular expression whenever persistence code moves.
- Add any new migration, database-provider, security-bootstrap, or identity directories to the classifier.

### 6. Reduced artifact churn

PR browser jobs upload diagnostics only on failure. Timing artifacts are retained for full main/nightly/manual runs.

Why:

- Successful PRs do not need three retained timing artifacts.
- Trend and diagnostic data remains available from the complete recurring gate.

### 7. Immutable action references

Workflow actions are pinned to immutable commit revisions and annotated with the corresponding release. Dependabot is configured to review GitHub Actions updates weekly.

Why:

- Mutable tags create a supply-chain change outside the reviewed repository diff.
- Dependabot restores maintainability by proposing explicit reviewed updates.

Codex review:

- Verify each pinned revision resolves successfully.
- In particular, verify the setup-dotnet v5.2.0 revision and hosted-runner compatibility with Node 24.
- Keep action-update PRs small and inspect release notes before merging.

### 8. Full-suite operating model

The full three-shard suite is retained on:

- push to `main`;
- nightly schedule at 06:17 UTC;
- manual workflow dispatch.

This is intentionally not "less testing." It separates quick change feedback from complete recurring assurance.

## Expected runner impact

A product-affecting PR previously started approximately seven jobs. A typical product PR now starts:

- one lightweight path classifier;
- one combined validation job where relevant;
- two browser jobs where relevant;
- PostgreSQL only where relevant.

A documentation-only PR starts only the classifier. A normal client/browser PR should therefore use roughly four jobs instead of seven, and a UI change that does not affect PostgreSQL should avoid that service entirely.

The full main/nightly/manual gate remains intentionally larger.

## Required review before merge

1. Parse `.github/workflows/ci.yml` as YAML and inspect GitHub Actions expression syntax.
2. Confirm the diff classifier receives a valid base SHA for pull requests and handles the first push edge case acceptably.
3. Run client lint, type-check, production build, and the focused Requirements Explorer journey.
4. Run backend tests on Ubuntu or confirm existing tests are cross-platform.
5. Trigger the PR workflow and inspect which jobs are skipped or run for:
   - a Markdown-only change;
   - a client source change;
   - a backend domain change;
   - a migration or identity/bootstrap change;
   - a CI workflow change.
6. Confirm branch-protection required checks still match the intended job names.
7. Confirm scheduled and manual events execute the complete three-shard and PostgreSQL gates.

## Known limitations and deliberate non-claims

- This change does not prove the optimal shard count; it establishes a lower-cost default while retaining full recurring coverage.
- The path classifier is conservative but cannot infer runtime coupling beyond its rules.
- The Requirements Explorer test now uses semantic loading state, but the product does not yet expose a dedicated immutable readiness contract.
- No claim is made that GitHub Actions billing will fall by an exact percentage until several weeks of run data are compared.
