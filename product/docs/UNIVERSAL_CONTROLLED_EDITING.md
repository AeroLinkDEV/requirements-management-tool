# Universal Controlled Editing

Issue #31 extends AeroLink's renewable exclusive edit-session contract beyond change request drafts.

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
- requirement proposals inside Draft change request records;
- requirement specification structures;
- test procedures and exact procedure revisions;
- requirement trace links; and
- candidate-baseline release planning;
- document templates;
- problem reports; and
- configuration change sets.

## Universal check-in engine

`POST /api/controlled-editing/sessions/{sessionId}/check-in` executes one fail-closed pipeline:

1. active session, owner, project authority, lifecycle, lease, and session-version validation;
2. canonical artifact re-resolution and snapshot-hash comparison;
3. deterministic parsing of the latest immutable autosave snapshot;
4. aggregate mutation exclusively through the registered family adapter; and
5. atomic aggregate persistence, immutable check-in evidence, audit evidence, session closure, and lease release.

Production adapters now cover every existing first-class controlled artifact model:

- `SystemChangeRequestControlledEditingAdapter` preserves change request identity allocation and aggregate validation through `SystemChangeRequest.UpdateDraft`.
- `RequirementProposalControlledEditingAdapter` updates a proposal through its owning SRCR aggregate without changing the proposal identity.
- `SpecificationStructureControlledEditingAdapter` controls specification metadata plus reordering/retitling existing structure nodes, validates parent ownership and cycles, and rejects identity-changing node edits.
- `TestProcedureControlledEditingAdapter` updates only a Draft procedure revision through its owning procedure root; approved revisions remain immutable.
- `TraceLinkProposalControlledEditingAdapter` preserves source/target identity while validating a controlled classification/rationale update.
- `ReleasePlanningControlledEditingAdapter` controls Draft baseline naming and exact SRCR membership through `CandidateBaseline.Select` and `Remove`.
- `DocumentTemplateControlledEditingAdapter` preserves the assigned template number while validating title, body, and ownership changes.
- `ProblemReportControlledEditingAdapter` preserves the report identity and reporter while validating the controlled problem and analysis record.
- `ConfigurationChangeSetControlledEditingAdapter` preserves the change-set identity while validating its scoped configuration content.

Each of the four non-SRCR roots has a persisted optimistic-concurrency version. The legacy `audit_events` table is
foreign-keyed to SRCRs, so universal immutable evidence is the authoritative audit trail for every other family;
SRCR and proposal evidence additionally attaches to the existing SRCR audit stream. The live SRCR workspace uses this
universal pipeline, and the retired direct-update route returns HTTP 410.

Verification Center now exposes the same controlled authoring contract for Draft test procedures: an author chooses
the exact Draft revision, acquires its exclusive lease, edits a server-recoverable snapshot, and checks it in before
an independent approver can make the revision immutable.

Stale authoritative content deterministically returns HTTP 409 with `stale_artifact_version` and does
not mutate the artifact, close the session, or release its lease. Rejected attempts remain attributable
in `controlled_artifact_check_in_evidence`.

## Complete Issue #31 model set

The policy registry, public creation route, persistence mappings, adapters, and universal check-in evidence now cover all nine Issue #31 families. A lease never changes controlled content by itself: artifact-specific mutation remains behind the owning adapter and aggregate validation. The API acceptance suite proves creation, checkout, server autosave, check-in, evidence creation, and lease release for the three newly modeled families; existing shared-contract tests prove contention, read-only observation, recovery, stale-version rejection, expiry, forced unlock, and non-mutation on failure.
