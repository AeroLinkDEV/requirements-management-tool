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

Production adapters now cover every existing first-class controlled artifact model:

- `SystemChangeRequestControlledEditingAdapter` preserves SCR/SWCR identity allocation and aggregate validation through `SystemChangeRequest.UpdateDraft`.
- `RequirementProposalControlledEditingAdapter` updates a proposal through its owning SCR aggregate without changing the proposal identity.
- `SpecificationStructureControlledEditingAdapter` controls specification metadata plus reordering/retitling existing structure nodes, validates parent ownership and cycles, and rejects identity-changing node edits.
- `TestProcedureControlledEditingAdapter` updates only a Draft procedure revision through its owning procedure root; approved revisions remain immutable.
- `TraceLinkProposalControlledEditingAdapter` preserves source/target identity while validating a controlled classification/rationale update.
- `ReleasePlanningControlledEditingAdapter` controls Draft baseline naming and exact SCR membership through `CandidateBaseline.Select` and `Remove`.

Each of the four non-SCR roots has a persisted optimistic-concurrency version. The legacy `audit_events` table is
foreign-keyed to SCRs, so universal immutable evidence is the authoritative audit trail for every other family;
SCR and proposal evidence additionally attaches to the existing SCR audit stream. The live SCR workspace uses this
universal pipeline, and the retired direct-update route returns HTTP 410.

Stale authoritative content deterministically returns HTTP 409 with `stale_artifact_version` and does
not mutate the artifact, close the session, or release its lease. Rejected attempts remain attributable
in `controlled_artifact_check_in_evidence`.

## Deferred lifecycle models

A lease never changes controlled content by itself. Artifact-specific mutation remains behind adapters because each aggregate owns its validation and immutable-history rules. `DocumentTemplate`, `ProblemReport`, and `ConfigurationChangeSet` remain policy placeholders only: their first-class lifecycle models do not yet exist and must be added by their owning roadmap issues, not fabricated inside Issue #31.

Issue #31 remains open until each connected family passes two-user contention, recovery, stale-version, forced-unlock, expiry, and immutable-history acceptance journeys, plus the modeled policy set is reconciled with those future lifecycle-model issues.
