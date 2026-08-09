# AeroLink current product handoff — 9 August 2026

This is the current restart point after PR #414 merged and two post-merge picker-integrity findings were
reproduced and filed. It supersedes the 8 August handoff as the document to read before starting new work.
Older dated handoffs remain historical records and must not be used as current backlog authority.

## Authoritative repository state

- Repository: `seanmccarthyns/requirements-management-tool`
- Authoritative branch: `main`
- Audited starting `main` SHA for this handoff: `bbcccc740d9b5384936e09d185a280312207e617`
- Latest merged PR: **#414 — Make TCR authoring pickers bounded, searchable and fully reachable (#402)**
- Exact final PR #414 head (pre-merge): `1db655c582a20b31309bd3dda8b1714649271ba3`
- Post-merge `main` Product Quality Gate on `bbcccc7`: run `31318103042`, **successful on the exact merged tree**
- There were **no open pull requests** at the assignment start after #414 merged
- Persistent PostgreSQL remains the sole engineering-data store; no reset or replacement was part of any merge

Start every task by fetching `origin`, confirming `main` at the exact current GitHub head, and creating a
focused branch. GitHub is the source of truth over every local checkout.

### PR-head qualification vs post-merge check status

PR #414's final pre-merge head (`1db655c`) passed its PR Product Quality Gate (run `31316935386`). The merged
`main` SHA `bbcccc7` then passed the push-triggered gate (run `31318103042`). The exact merged-tree run is the
current checkpoint; a PR-head run alone is not evidence about the merged commit.

## What PR #414 delivered

PR #414 replaced the fixed first-page TCR authoring pickers with bounded, server-searched, paged pickers with
totals and exact-ID hydration across all four authoring surfaces:

- quick procedure-authoring requirement selection;
- `ProcedureCoverageConfirmed` approved-procedure selection;
- TCR Modify/Retire controlled-procedure target selection;
- TCR driving-requirement selection.

It delivered deterministic >500 Modify/Retire evidence (API and real-browser picker over a 520-procedure
build), unsaved `procedureChoice` hydration, truthful search totals and retained-selection wording, and kept
#412/#413 invariants and mutation-side authority intact. It is not a rollback target.

## Open post-merge picker-integrity findings (open until the corrective PR merges)

Two late inline review comments were posted against the exact merged #414 head approximately four minutes
after the merge. Both were reproduced deterministically against disposable SQLite on `bbcccc7`:

### #402 — reopened with post-merge evidence

Issue: [#402](https://github.com/seanmccarthyns/requirements-management-tool/issues/402) (open).

The multi-select requirement pickers serialized the complete selected revision-ID set into the `ids` query
parameter. At roughly 200+ UUID selections the GET request line exceeds the server's default 8192-byte limit;
the failed response was silently ignored, so paging stopped and candidates beyond the loaded page became
unreachable. The same-class defect exists in the TCR driving-requirement picker. Non-OK picker responses on all
four surfaces produced no visible error.

### #415 — stale release build-context can overwrite the active build's effective baseline

Issue: [#415](https://github.com/seanmccarthyns/requirements-management-tool/issues/415) (open).

`TestingCoverageWorkspace.load` writes `effectiveBaseline` before the load-ticket check. When the workspace
stays mounted across a release switch (browser Back/Forward between two same-view URLs, applied through the
app's popstate handler without remounting), a delayed `build-context` response from the previous release can
land last and make the requirement picker query the wrong baseline.

## Current open product work

- #402 — reopened post-merge: bounded multi-selection hydration and visible picker failures.
- #415 — stale release build-context baseline race.
- #364 — explicit, attributable, idempotent legacy procedure-manifest bootstrap.
- #365 — remaining superseded-TCR browser/history/deep-link presentation (its domain/API supersession is done).
- #367 — evidence-only recheck/closure after #364 and the #402 regression are resolved.
- #332 — real imported-baseline materialization (separate major campaign).

Work is tracked by issue and branch. The handoff makes no separate "active agent" ownership claims that are not
reverified; every agent fetches current `origin/main` before acting.

## Corrective branch (unmerged, in review)

The corrective work for #402 and #415 lives on branch `deepseek/post-414-picker-integrity` as a draft pull
request titled "Harden bounded authoring pickers after #414". It is **not on `main`**; nothing in this handoff
should be read as claiming unmerged branch work is already merged. The PR is expected to:

- move the effective-baseline write behind the load-ticket guard and reset picker-owned state on
  project/release/discipline transitions;
- retain multi-select requirements in a client-side identity map instead of serializing the selection into
  request lines;
- surface non-OK picker responses as visible errors with coherent retry;
- update this documentation set (the 8 August handoff stays historical).

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
