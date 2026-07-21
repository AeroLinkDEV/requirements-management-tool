# Universal Controlled Editing

Issue #31 extends AeroLink's renewable exclusive edit-session contract beyond SCR/SWCR drafts.

## Shared lease API

The `/api/controlled-editing` endpoints provide one policy-aware implementation for:

- supported-family discovery;
- authoritative project and lifecycle-state resolution;
- read-only lock status;
- exclusive checkout with database-enforced lock keys;
- resumable server snapshots;
- version-checked autosave;
- heartbeat and expiry;
- generic, version-checked check-in from the latest server autosave;
- discard without controlled-content mutation; and
- administrator/configuration-manager forced unlock with audit evidence.

## Connected adapters

This increment resolves and snapshots the controlled families that already have authoritative records:

- change requests;
- requirement proposals inside Draft SCR/SWCR records;
- requirement specification structures;
- test procedures and exact procedure revisions;
- requirement trace links; and
- candidate-baseline release planning.

## Universal check-in engine

`POST /api/controlled-editing/sessions/{sessionId}/check-in` executes one fail-closed pipeline:

1. active session, owner, project authority, lifecycle, lease, and session-version validation;
2. canonical artifact re-resolution and snapshot-hash comparison;
3. deterministic parsing of the latest immutable autosave snapshot;
4. aggregate mutation exclusively through the registered family adapter; and
5. atomic aggregate persistence, immutable check-in evidence, audit evidence, session closure, and lease release.

The production adapters now include `SystemChangeRequestControlledEditingAdapter` and
`RequirementProposalControlledEditingAdapter`. Both preserve SCR/SWCR identity allocation and aggregate
validation through `SystemChangeRequest.UpdateDraft`; proposal evidence remains keyed to the proposal while
aggregate audit events attach to its owning SCR. The live SCR workspace uses this universal pipeline, and the
retired direct-update route returns HTTP 410.

Stale authoritative content deterministically returns HTTP 409 with `stale_artifact_version` and does
not mutate the artifact, close the session, or release its lease. Rejected attempts remain attributable
in `controlled_artifact_check_in_evidence`.

## Remaining family adapters

A lease never changes controlled content by itself. Artifact-specific mutation remains behind adapters because each aggregate owns its validation and immutable-history rules. The next increments connect adapters for the other existing authoritative families, then add problem-report, configuration-change-set, and controlled-template adapters when those first-class lifecycle models are introduced by their owning roadmap issues.

Issue #31 must remain open until every family passes two-user contention, recovery, stale-version, forced-unlock, expiry, and immutable-history acceptance journeys.
