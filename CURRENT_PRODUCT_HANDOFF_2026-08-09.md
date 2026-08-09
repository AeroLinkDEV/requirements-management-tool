# AeroLink current product handoff — 9 August 2026 (updated after PR #416)

This is the current restart point after PR #414 and PR #416 merged. It supersedes the 8 August handoff as the
document to read before starting new work. Older dated handoffs remain historical records and must not be used
as current backlog authority.

## Authoritative repository state

- Repository: `seanmccarthyns/requirements-management-tool`
- Authoritative branch: `main`
- Audited starting `main` SHA for this handoff: `6e1c2443b964304df1359d4dde39fe3dc4f04004`
- Latest merged PR: **#416 — Harden bounded authoring pickers after #414 (#402, #415)** (squash `6e1c244`)
- Prior merged PR in the same sequence: **#414 — Make TCR authoring pickers bounded, searchable and fully
  reachable (#402)** (squash `bbcccc740d9b5384936e09d185a280312207e617`)
- Post-merge `main` Product Quality Gate on `6e1c244`: run `31329405976`, **successful on the exact merged tree**
- There were **no open pull requests** at the start of the Aug. 9 overnight PRO-audit pass
- Persistent PostgreSQL remains the sole engineering-data store; no reset or replacement was part of any merge

Start every task by fetching `origin`, confirming `main` at the exact current GitHub head, and creating a
focused branch. GitHub is the source of truth over every local checkout.

### PR-head qualification vs post-merge check status

PR #416's final pre-merge head `c750ad626f67d60d0f4e486081a3719889bd13b3` passed its PR Product Quality Gate
(run `31327972264`). The merged `main` SHA `6e1c244` then passed the push-triggered gate (run `31329405976`).
The exact merged-tree run is the current checkpoint; a PR-head run alone is not evidence about the merged
commit.

## What PR #414 and PR #416 delivered

- Bounded, server-searched, paged TCR authoring pickers with totals and exact-ID hydration (all four
  authoring surfaces); >500 Modify/Retire reachability; unsaved `procedureChoice` hydration; truthful search
  totals and retained-selection wording.
- Stale build-context baseline race guard; bounded multi-select retention via client identity maps; visible
  picker failures (HTTP and rejected fetch) with coherent retry; bounded Modify/Retire target hydration (no
  `baseNumbers`/`ids` in the request line); 650-decision TCR regression.
- #402 (bounded reachable pickers and its post-merge request-line regression) and #415 (stale build-context
  baseline race) are **closed** by #416.

## Current open product work

The Aug. 9 PRO cross-discipline audit queue (marker `PRO-AUDIT: READY-DEEPSEEK`) is open:

- #417 — residual post-#416 picker integrity (unsaved-target retention, obsolete-response error clearing,
  stale driving-details, documentation truth)
- #418 — released baselines must refuse test-procedure TCR selection/removal/materialization
- #419 — test-procedure controlled documents must be immutable snapshots bound to the exact manifest
- #420 — test-procedure documents must show TCR approval authority and exact source provenance
- #421 — procedure titles must be revision-scoped
- #422 — test executions must target the exact procedure revision carried by the build
- #423 — evidence links to released executions must be refused by server authority
- #424 — Test Procedure History must match Trace provenance (exact TCR revision, folded sources)

Also open and tracked separately: #364 (legacy procedure-manifest bootstrap), #365 (superseded-TCR browser
presentation), #367 (evidence-only recheck after #364 and the #402 regression), and #332 (imported-baseline
materialization). Work is tracked by issue and branch; no separate "active agent" ownership claims are made
that are not reverified.

## Remediation branches (unmerged until PRO approval)

Remediation for the PRO-audit queue proceeds one issue per PR (or a documented same-root combination), each as
a DRAFT PR awaiting `PRO-AUDIT: PRO-APPROVED-FOR-MERGE`. The #417 branch is
`deepseek/pro-audit-417-picker-integrity`; nothing in this handoff claims unmerged branch work is already on
`main`.

## Standing safety and governance rules

- Never commit directly to `main`.
- Branch per task, pull request, quality gate, explicit owner authorization, squash merge.
- Never weaken branch protection, rulesets, required checks, strict/up-to-date requirements, enforce-admin
  settings, repository settings, merge policy, or GitHub Actions protection to make a merge possible.
- There is one persistent PostgreSQL database, normally on port `54329`. Automated tests use disposable
  databases only. Do not reset, restore, seed, migrate, or directly edit persistent engineering data without
  explicit owner authorization.
- Released Build 1.5 remains immutable and read-only. Build 1.6 is the in-work development workspace.
- No certification, compliance, or tool-qualification claim.
- No AI capability ships in the product under the current program boundary.

## Dated SHA statements are checkpoints

Every dated handoff records the exact `main` SHA it was written against. That SHA is a checkpoint, not a
standing guarantee: `main` advances as focused PRs merge. Always `git fetch origin --prune`, confirm the exact
current GitHub `main`, and reconcile the difference before acting.
