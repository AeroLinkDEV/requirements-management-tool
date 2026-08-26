# Working on AeroLink

This is the canonical operating contract for coding agents and automated contributors working in this repository.

AeroLink is a controlled aerospace requirements-management and development-assurance product. Its value depends on a defensible record: exact revision identity, attributable change, immutable historical evidence, build/release effectivity, and truthful user-facing state. A screen or API that confidently asserts the wrong controlled fact is worse than a missing feature.

## Read these first

Before changing the repository:

1. [PROJECT_STATE.md](PROJECT_STATE.md) — current product truth and present architecture.
2. [DECISIONS_AND_OPEN_QUESTIONS.md](DECISIONS_AND_OPEN_QUESTIONS.md) — authoritative product decisions; accepted entries are append-only.
3. This file — repository working and safety rules.
4. The relevant technical documentation under [product/docs](product/docs/README.md) where available, especially [ARCHITECTURE.md](product/docs/ARCHITECTURE.md), [OPERATIONS.md](product/docs/OPERATIONS.md), and [MERGING.md](product/docs/MERGING.md).
5. The GitHub issue being implemented and the current `main` branch. GitHub Issues are the live backlog authority; dated handoffs are historical checkpoints, not a substitute for refreshing GitHub.

## Authority hierarchy

When sources disagree, resolve them in this order:

1. Current code, migrations, tests, and behavior on protected `main`.
2. Accepted decisions in `DECISIONS_AND_OPEN_QUESTIONS.md`.
3. `PROJECT_STATE.md` for the current product-level summary.
4. The current scoped GitHub issue/PR contract.
5. Durable technical/product documentation.
6. Historical handoffs, audit reports, and archived snapshots.

Do not silently preserve a historical statement when current code and accepted decisions have superseded it. If the conflict is material and cannot be reconciled confidently, document it and stop rather than inventing a new rule.

## Multiple agents and worktrees

More than one agent may work on AeroLink at the same time.

- Prefer an isolated `git worktree` for any non-trivial or long-running task.
- Do not move another agent's branch, worktree, checkout, or uncommitted files.
- Never use broad staging such as `git add -A` in a shared checkout. Stage explicit paths.
- Capture `git rev-parse HEAD` and dirty status before and after long test runs. A green run that spans an unexpected checkout/branch/SHA change is invalid evidence.
- A `--no-build` test proves only the binaries already present. Use it only when those outputs are known to have been built from the exact SHA under test.
- Re-check current GitHub PR/branch state immediately before merge/rebase decisions; do not rely on a handoff's old branch counts.

See [docs/ENGINEERING_LESSONS.md](docs/ENGINEERING_LESSONS.md) for the durable lessons behind these rules.

## Persistent developer/demo state is not disposable

The normal AeroLink PostgreSQL developer/demo database uses port **54329** and is persistent state.

- Do not reset, reseed, drop, truncate, or repurpose the persistent `aerolink` database merely to qualify a change.
- Do not delete or rewrite persistent evidence under `product/.local` to make tests pass.
- Use SQLite fixtures, test containers, disposable PostgreSQL databases, or other owned disposable test state for destructive qualification.
- Migration tooling must fail closed. `AEROLINK_MIGRATIONS_CONNECTION` must point to a throwaway database; never allow EF tooling to fall back to the persistent database.
- Backup/restore testing must remain isolated and non-destructive unless an explicit operator task says otherwise.

## Controlled-history invariants

These rules are expensive to rediscover and should be treated as non-negotiable unless an accepted decision explicitly changes them:

- Approved/released historical controlled records are not destructively edited or physically deleted through normal product workflows.
- Approval and inclusion in a release/build are separate facts.
- Exact requirement/test artifact revision identity matters; do not replace historical exact references with whatever revision is current today.
- Build/release effectivity must come from the existing authoritative manifest/effectivity machinery, not a browser approximation.
- Controlled identifiers are governed identities, not cosmetic labels. Renaming or renumbering one can be a historical-evidence migration.
- Historical signatures, hashes, manifests, and evidence are not presentation data and must not be rewritten to make a new UI simpler.
- Software verification may be profile-dependent. Current full software verification is Requirement → Test Case → Test Procedure → Execution/Result/Evidence; Case-only profiles remain valid where explicitly configured.
- System verification remains a one-tier Procedure model unless an accepted decision changes it.
- AeroLink records/imports externally executed test results and evidence; it does not become a bench/test executor by accident.

## Migrations

- Never rewrite a migration that has already merged or may have been applied elsewhere.
- When two branches add migrations from the same base, compare ordering and overlapping tables after rebasing. Regenerate the unmerged migration when necessary rather than trusting a textual snapshot merge.
- API tests commonly use SQLite/`EnsureCreated`; they do not prove PostgreSQL migration SQL.
- Provider-specific PostgreSQL SQL and type conversions require explicit PostgreSQL qualification.
- Normalize generated migration/model-snapshot line endings according to repository conventions before commit.

## Tests, generated contracts, and CI

Use the repository's test-planning contract instead of guessing what to run.

- The changed-area planner is the shared authority for local/CI test selection.
- A passing `dotnet test` command is not proof that every `.csproj` in the repository compiles; ensure the required solution/projects/tools are covered by the planned build.
- Adding/removing API tests may change generated test-intent, route-manifest, and host-classification artifacts. Regenerate them from source; never hand-merge generated JSON.
- Run generators a second time and require a clean diff to prove checked-in generated artifacts are stable.
- New API routes must remain covered by the route-contract baseline.
- Playwright failures are evidence until dispositioned. Do not call a failure “flaky” merely because it is inconvenient; reproduce it on the exact SHA and retain diagnostics.
- CI optimization must be measurement-driven. See [BROWSER_AND_BACKEND_FEEDBACK_TIME.md](product/docs/BROWSER_AND_BACKEND_FEEDBACK_TIME.md) before changing shard counts or the quality gate.
- The protected Product quality gate must succeed at the exact head that is being merged. Do not weaken branch protection or required checks to land work.

## Pull requests and merging

- One focused branch/worktree per task.
- Keep unrelated cleanup out of feature PRs.
- Rebase/update only when the repository's actual merge state requires it; do not churn a green PR from habit.
- Follow [product/docs/MERGING.md](product/docs/MERGING.md).
- Do not force-merge or bypass required checks.
- Record exact validation commands/results in the PR when the change is material.
- For visual changes, add focused browser proof and screenshots where they materially improve review.

## Windows launchers and operator compatibility

Root `.bat`/`.cmd` files are deliberate Windows operator entry points, even when the real implementation lives under `product/scripts`.

Do not move or rename them merely to make the repository root look cleaner. External dependencies such as Task Scheduler entries, desktop shortcuts, remote-demo recovery tasks, or another machine may refer to their exact paths and are invisible to GitHub code search.

Any launcher-path change requires an explicit dependency audit and appropriate compatibility/transition behavior.

## Documentation discipline

Use one home for each kind of knowledge:

- **Current product truth:** `PROJECT_STATE.md`.
- **Accepted product decisions:** `DECISIONS_AND_OPEN_QUESTIONS.md`.
- **Live backlog/findings:** GitHub Issues.
- **Agent/repository safety:** `AGENTS.md`.
- **Durable product/reference/showcase/provenance docs:** `docs/`.
- **Implementation/architecture/operations/testing docs:** `product/docs/`.
- **Durable lessons learned:** `docs/ENGINEERING_LESSONS.md`.
- **Milestone history:** `docs/PROJECT_HISTORY.md`.
- **Historical handoffs/audits/status snapshots:** [`docs/archive/`](docs/archive/README.md).

Do not create a new root-level dated handoff or audit report as a parallel source of truth. Turn actionable findings into GitHub issues; capture lasting lessons in the lessons document; retain historical reports in the archive.

Update `PROJECT_STATE.md` in the same PR when a change materially alters product architecture, supported lifecycle, important user-visible authority, or a major product boundary. Do not update it for every small bug fix.

## Things agents must never do for convenience

- Reset the persistent AeroLink database or evidence store to make qualification easier.
- Rewrite controlled historical records, signatures, hashes, manifests, or identifiers for presentation convenience.
- Bypass project/build/revision authorization or effectivity rules in the browser.
- Concatenate separately paged server results and present them as one correctly paged set.
- Invent product policy where the issue/decision/code is ambiguous.
- Weaken tests, branch protection, or required checks merely to get a PR merged.
- Treat a historical handoff as the live backlog without refreshing GitHub.
- Move stable Windows launcher paths without an external-dependency audit.
- Copy a large Case/Procedure or discipline-specific UI when the existing architecture is intended to be parameterized/shared.

When in doubt, preserve controlled truth first, then optimize convenience.
