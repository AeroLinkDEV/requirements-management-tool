# Current product and repository handoff - 2026-08-02

This is the current restart point for AeroLink. It supersedes the 1 August handoff, which remains a historical
delivery record. [PROJECT_STATE.md](PROJECT_STATE.md) is the canonical product description,
[FEATURE_CATALOG.md](FEATURE_CATALOG.md) is the stable capability inventory, and
[DECISIONS_AND_OPEN_QUESTIONS.md](DECISIONS_AND_OPEN_QUESTIONS.md) remains the append-only decision record.

## Repository checkpoint

- Repository: `seanmccarthyns/requirements-management-tool`
- August observation reconciliation: issues #277, #278, #279 and #280; pull requests #281, #282, #283
  and #284
- GitHub backlog after this delivery: **zero open issues**
- Earlier 2 August delivery: issues #258, #259, #260, #261, #262 and #269; pull requests #263, #264,
  #265, #266, #267, #268 and #270
- Delivery rule remains: focused `codex/*` branch, pull request, required Product Quality Gate, squash merge,
  exact-merge requalification; never push implementation directly to `main`
- The persistent PostgreSQL demonstration database remains the one real-life database. Its engineering records
  were preserved. The verified daily backup task produces integrity-manifested archives; it does not create a
  second live database.

## What changed on 2 August

### Draft software change requests

- Saving an empty form creates no record and consumes no identifier.
- **Save SWCR Draft** persists incomplete, attributable work. **Save and check in** applies a controlled working
  copy and closes the edit session.
- Reopened Draft SWCRs expose every proposal action at their correct level: introduce, modify and retire HLR or
  LLR work.
- Dirty/no-op state and asynchronous save behavior are explicit and regression tested.

### HLR and LLR isolation

- Software Change Requests have separate HLR and LLR views whose selection survives URL copy, refresh and back.
- Server filtering, mixed-level badges, creation actions, downstream queues and Draft pickers honor the selected
  level.
- Build 1.5 stays read-only; Build 1.6 remains the active development workspace.

### Searchable controlled references

- Problem Report selection searches server-side by controlled number and content, retains selected/locked PRs,
  and supports multiple driving PRs.
- Requirement modification hydrates its exact current upstream trace. Already-selected historical parent
  revisions remain intelligible and navigable even if no longer active in the current baseline; new candidates
  remain scoped to the current build.
- Source SCRs, driving PRs, requirements, procedures, executions/evidence, documents and releases use durable,
  permission-checked links where supported. Unavailable targets say so rather than acting clickable.

### Actionable downstream assessments

- Storage states are presented as HLR/LLR engineering decisions: impact pending, in progress, in review,
  complete with no impact, complete with controlled impact, change required with SWCR pending, and superseded.
- A refresh-safe assessment drawer shows the source SCR, full Problem/Analysis/Solution case, approved requirement
  changes and current downward trace, including a genuine no-trace state.
- An engineer may record no impact, link an existing level-compatible Draft, or create the correct HLR/LLR Draft
  directly. A new Draft links automatically; a simulated link outage proves the saved Draft remains visible and
  the link can be retried.
- `ChangeRequired` cannot be submitted as a conclusion. Independent approval/return, role separation,
  supersession and released-build immutability remain enforced.

### Primary navigation

- **Requirements** contains Change Requests, Requirements Explorer, requirements Documents and Digital Thread.
- **Verification** contains direct System or HLR/LLR Coverage, Results and verification Documents.
- **Code** is a standalone destination between Verification and Problem Reports. It maps exact LLR revisions to
  GitLab merge evidence while GitLab remains source of truth.
- **Problem Reports** is a standalone top-level destination with no submenu.
- The redundant generic Verification sidebar entry is gone; `/system-verification` and
  `/software-verification` remain backward-compatible chooser routes.
- Assurance reviewer role names and legitimate production-assurance language were not renamed.

### Live-sweep punctuation remediation

- The persistent Build 1.6 Problem Reports center exposed malformed separator, arrow and ellipsis literals in
  controlled-reference cards and related loading/action text.
- The observation was reproduced on PR-00002.00, recorded as durable engineering artifact PR-00003.00, and
  corrected through GitHub issue #269 / PR #270 with a focused PR-to-SCR regression.
- The persistent relationship card was refreshed after the fix and rendered `Software Build (SW) · SW-01.60`
  and `Open →` correctly; PR-00003.00 remains open as realistic unfinished Build 1.6 work.

## Problem Reports and mandatory verification

Problem Reports lead to corrective work; requirement changes do not automatically create PRs. Every SCR,
SWCR and discipline-specific TCR may select one or more driving PRs. Approved changes are projected to the PR
as corrective actions. Applicable executions/results are projected as test evidence. Connected controlled
records are searchable and refresh-safe.

The PR lifecycle is Draft → Ready for SCCB → Open → Implementing → Verifying → Awaiting SQA Closure → Closed.
Title and rich Problem Description are the only Draft requirements. Raised-by/date are immutable; owner and one
target build are auditable but reassignable. Rich supporting fields and Unknown/No/Yes impact decisions are
progressive, filters combine with AND, History is an internal tab, and SQA closure is independent.

When a build introduces or modifies a requirement, procedures covering the impacted exact revisions enter the
build test set as mandatory changed-requirement scope. They cannot be removed, and release readiness requires
passing results with evidence. This is not automated test execution: AeroLink governs the procedures, scope,
decisions and imported/recorded evidence while tests execute externally.

## Qualification evidence

Each implementation PR passed the GitHub Product Quality Gate: backend/domain tests, client lint/type-check/build,
production-build journeys, browser shards, and PostgreSQL migration/bootstrap when relevant. Exact squash merges
were requalified locally before the next branch.

Focused evidence from the final increments includes:

- 199 full domain tests passed after the downstream-domain change.
- Eight focused controlled-reference API regressions and four reference/search browser journeys passed on the
  exact #265 merge.
- Five downstream domain tests, the focused API authority/read-only regression, and two durable downstream
  browser workflows passed on the exact #266 merge.
- Build 1.5/1.6 navigation, direct-link, refresh, showcase and verification-chooser journeys passed on #267;
  both full CI browser shards then passed.
- Client lint, type-check and production build passed for every affected client increment.

## Runtime and data

- Development website: `http://127.0.0.1:5173`
- API readiness: `http://127.0.0.1:5080/health/ready`
- Production-shaped single-origin launcher: `START_AEROLINK_PRODUCTION.bat`
- Demonstration password: `AeroLink!2026` (non-production only)
- Database: persistent PostgreSQL; isolated SQLite databases are test infrastructure only
- Daily backup: `SCHEDULE_AEROLINK_BACKUP.bat`, default 02:00 current-user task, configurable and non-overlapping

Never reset the persistent database to simplify a test. Stop the demo API before backend builds if assemblies
are locked, then restart through `product/scripts/Start-AeroLink.ps1` so readiness is checked rather than assumed.

## Code traceability and future-build planning

- GitLab is authoritative for source, MRs, review, and commit content. AeroLink records immutable pointers from
  exact approved LLR revisions to GitLab MRs and merge SHAs, or a justified `No code change required` decision.
- Build 1.5 code evidence is historical/read-only; Build 1.6 is active. The small FMS mapping set is explicitly
  labelled demonstration data and does not pretend that every seeded LLR has a real GitLab MR.
- The Software Builds lineage ends with **Plan next build**, a non-record placeholder. It creates no future
  build identity or version.
- Digital Thread presents SYSR → HLR → LLR → procedure → execution/result → evidence → build on one screen and
  retains focused traversal and evidence views.

## Deliberate boundaries and next work

- AeroLink is not a Git host and does not manage source code. The delivered
  [LLR-to-code traceability MVP](LLR_TO_CODE_TRACEABILITY_PROPOSAL.md) records exact approved LLR-to-GitLab merge
  pointers without importing code or claiming a merge satisfies the requirement.
- Fine-grained permission expansion and rule-based requirement-quality checks remain unimplemented product
  choices, not hidden features.
- Optional Problem Report classification, attachments, and configurable closure policy should continue only
  when later product decisions justify them.
- Build 1.6 should continue through realistic, incomplete engineering work. Do not force it to release merely
  to demonstrate a completed state; use Build 1.5 for immutable historical behavior.

## Safe continuation sequence

1. Verify repository identity, `origin`, `main`, and a clean tree; pull with `--ff-only`.
2. Confirm GitHub has no newer issue/PR state than this handoff.
3. Start AeroLink and require both the website and `/health/ready` to answer.
4. Exercise an untested Build 1.6 workflow with durable artifacts and reopen them through direct links/refresh.
5. Recheck the corresponding Build 1.5 page for read-only behavior.
6. For a confirmed defect: reproduce, deduplicate on GitHub, raise an implementation-ready issue, deliver the
   smallest coherent fix on `codex/*`, add regression coverage, merge only after CI, and requalify exact `main`.
7. Keep this handoff, Project State, Feature Catalog, glossary and operator documentation aligned in the same
   documentation delivery whenever the current product truth changes.
