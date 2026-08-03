# Current product and repository handoff - 2026-08-03

This is the current restart point for AeroLink. It supersedes the 2 August handoff, which remains a historical
delivery record. [PROJECT_STATE.md](PROJECT_STATE.md) is the canonical product description and
[FEATURE_CATALOG.md](FEATURE_CATALOG.md) is the stable capability inventory.

## Repository checkpoint

- Repository: `seanmccarthyns/requirements-management-tool`
- Aug 3 observation reconciliation: GitHub issues #298 through #304
- Delivery rule remains: focused `codex/*` branch, pull request, required Product Quality Gate, squash merge,
  and exact-merge requalification; never push implementation directly to `main`.
- The persistent PostgreSQL database remains the sole real-life database. Its records were preserved and a
  verified backup was captured before this delivery at `product/.local/backups/aerolink-20260803-102734.zip`.

## Aug 3 corrections

- An empty Software Draft now stores whether it belongs to HLR or LLR. The level survives save, refresh,
  history filters, direct links, and later revisions; legacy non-empty requests are backfilled when their
  requirement changes make the level unambiguous.
- The Problem Report queue is Build-scoped, numeric oldest-first, and intentionally limited to ten visible
  records while reporting the full matching total. Existing active unscoped FMS records are moved to the one
  active build with an immutable `TargetBuildReconciled` history event.
- Every test change request has a discipline-specific controlled identity: SYSTCR, HLRTCR, or LLRTCR. The
  incorrectly classified legacy HLR package remains available as superseded history and points to the correctly
  classified software successor; its verification items no longer appear as current work.
- HLR and LLR verification links no longer appear active at the same time. Code and Problem Reports use the
  same top-level visual hierarchy as Requirements, Verification, Release, and Administration.
- Downstream assessment drawers present the source case, approved changes, and current downward trace before
  asking for an engineering conclusion. Required-change and linked-Draft states use action-oriented wording.
- Controlled dialogs remain fixed to the browser viewport even when opened deep in a long workspace. The
  workspace entrance animation no longer leaves a transformed containing block behind.
- Release Readiness now opens a searchable list of every controlled change instead of silently opening the
  first record. Any listed change can be selected for its exact impact review.

## Persistent FMS 1.6 evidence

- PR-00001.00 through PR-00004.00 are visible in the Build 1.6 Problem Report queue.
- The legacy HLR verification package has a controlled HLRTCR identity and is Superseded; the correct
  HLRTCR-000002.00 remains current.
- Existing approved and in-work SCRs, SWCRs, assessments, procedures, results, evidence, documents, and code
  mappings were retained. Build 1.5 remains historical and read-only.

## Runtime and operations

- Development website: `http://127.0.0.1:5173`
- API readiness: `http://127.0.0.1:5080/health/ready`
- Demonstration password: `AeroLink!2026`
- Production-shaped launcher: `START_AEROLINK_PRODUCTION.bat`
- Daily backup scheduler: `SCHEDULE_AEROLINK_BACKUP.bat`

Never reset the persistent database to simplify testing. Stop the local API before backend builds if assemblies
are locked, then restart through `product/scripts/Start-AeroLink.ps1` so migrations, seeding, and readiness are
checked rather than assumed.
