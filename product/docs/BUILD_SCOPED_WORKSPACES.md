# Build-scoped workspaces

## Decision

The existing `SoftwareRelease` record is the durable software-build workspace identity. Its official display
name is derived deterministically (`1.6` becomes `SW-01.60`); “Build 1.6” remains acceptable informal wording.
A baseline and software build are the same product concept. AeroLink does not
introduce a second mutable "active build" table or a browser-session selection record.

An entered workspace is identified by the existing route tuple:

`programId / projectId / releaseId`

The route is the client source of truth and survives refresh, deep links, back/forward navigation, and session
restoration. The client sends the route release as `X-AeroLink-Build-Context` on API requests made from an
entered workspace. The API validates that context against project authority and rejects conflicting project,
release, or release-owned resource addresses.

The context-free selection route is:

`/projects/fms-product-development/builds`

Only selecting an accessible build establishes a release context and enters the existing controlled workspace.
Leaving the workspace returns to the selection route. There is no release selector inside the workspace.

## Data ownership

The build boundary follows relationships that already exist:

| Content | Build ownership |
| --- | --- |
| Change requests and reviews | `SystemChangeRequest.TargetReleaseId` |
| Software build configuration (persisted as candidate/frozen baseline) | `CandidateBaseline.ReleaseId` |
| Effective requirement snapshot | exact `BaselineRequirementSelection` rows for the resolved build baseline |
| Generated controlled documents | `ControlledDocument.ReleaseId` and `BaselineId` |
| Word-authored managed documents | exact `ManagedDocumentBuildSelection` plus any successor `ManagedDocumentRevision.TargetReleaseId` |
| Immutable software builds | `SoftwareBuild.ReleaseId` and `BaselineId` |
| Release campaigns and approvals | `ReleaseCampaign.ReleaseId` and `BaselineId` |
| Test change reviews and procedure-impact decisions | `TestChangeReview.ReleaseId` and `VerificationImpact.ReleaseId` |
| Downstream change assessments | target build plus exact upstream/downstream requirement revisions |
| Prospective upward allocations | target build plus exact child and proposed parent revisions |
| Test executions | `TestExecution.ReleaseId`; an optional immutable `SoftwareBuildId` adds exact configuration provenance |
| Problem reports | explicit `ProblemReportLink` to the owning `SoftwareRelease`; failure-origin reports derive it from their execution build |
| Code traceability | `CodeTraceabilityRecord.ReleaseId` plus exact LLR artifact and immutable revision IDs; GitLab remains the code authority |
| Requirement history | revision plus source SRCR and effective baseline; historical rows retain their origin |

Project configuration such as schemas, document structure, directory membership, integrations, and review
workflow definitions remains project-scoped. It governs how every build is worked; it is not primary build
content.

## Effective requirements and inheritance

A released build resolves to its latest materialized frozen baseline. That exact baseline membership is the
released requirement set.

An in-work build resolves to its latest materialized baseline when one exists. Until its candidate baseline is
materialized, it inherits the exact materialized baseline of its predecessor release. Pending and approved
changes targeted to the in-work release remain visible as that build's change layer; they do not rewrite the
released predecessor rows.

This preserves explicit lineage without cloning 1,250 requirements or silently sharing mutable revisions.
Historical requirement detail may include earlier revisions, but each row is labelled with its originating
release/baseline and remains read-only evidence. Opening that evidence does not change the route release.

## Read and mutation enforcement

- Primary lists accept or derive the selected `releaseId`.
- Search is release-scoped and only returns release-owned records or requirements in the effective baseline.
- The browser sends the route release context on API calls.
- The API rejects a context whose release does not belong to the addressed project.
- The API rejects release-owned resource IDs from a different release.
- Every unsafe browser request made while a released build is active is rejected server-side. The UI also
  presents the workspace as read-only, but that is explanatory rather than the security boundary.
- Existing domain rules that prevent new work against released releases remain in force.

API clients that do not send a browser workspace context retain their existing explicit request contracts. This
keeps integrations and administrative automation deterministic; their project/release authorization continues
to be enforced by endpoint and domain rules.

## Verification alignment and release evidence

Approval of a change request automatically creates one controlled Test Change Review for each affected
discipline: System, Software HLR, or Software LLR. A mixed-level software request therefore creates two
independent reviews. Verification engineers decide whether to create, link, modify, retire, or omit a test
procedure; the review cannot be submitted until every item is decided, and an approver closes it.

Approved requirement changes also create consuming-discipline assessments where an exact upstream change can
affect downstream requirements. The consuming engineer owns the rationale and independent approval. When a
software engineer proposes a new upward allocation, the product records the prospective exact child/parent
relationship for independent approval and materializes the trace only after approval. Neither workflow mutates
released Build 1.5 evidence or creates an unreviewed trace.

Procedure alignment is always a release gate. Execution evidence is a release gate for the build's **test
set** — the procedures somebody decided this build has to run (DEC-076). The older per-decision mark,
**Evidence required before software-build release**, no longer has a control that sets it and survives only as
one of the inputs that seeds a new set. Execution work outside the set may continue after release. A failed post-release test remains evidence
against that released software build. If software caused the failure, correction is made through a change
request in a later software build; the released build is never rewritten.

Code traceability is a separate release gate. Every Project, including the FMS demonstration, evaluates only
the LLR revisions introduced or modified in that build; a demonstration boundary limits what is seeded, never
which changes owe evidence. Every
required revision must have either immutable GitLab merge evidence or an attributable no-code decision. A real
build with no changed LLRs therefore passes this gate at 0/0. The same projection drives the Code workspace,
release readiness, and the signed review manifest.

## Schema impact

`TestExecution.ReleaseId`, `TestChangeReview`, and the review/action/pre-release-evidence fields on
`VerificationImpactItem` are the schema additions. A verification result can be recorded while a release is
in work, before an immutable `SoftwareBuild` provenance record exists, so deriving workspace ownership only
through optional `SoftwareBuildId` left those results without an unambiguous build boundary. The migration
backfills existing execution rows from their linked software build and leaves genuinely legacy unlinked rows
nullable. New browser-workspace results take the validated route release directly; release-campaign execution
imports take the campaign release.

The existing predecessor-release, baseline membership, source-SRCR, controlled-document, software-build,
campaign, verification-impact, and typed problem-report link relationships provide the remaining build
identity and lineage. New manual problem reports are linked to the route build at creation; reports created
from failed verification derive that same explicit link from the execution's release/build context.

Records that are genuinely project governance, rather than build content, are intentionally not duplicated per
release. This increment never infers build ownership from affected-configuration free text.

The Software Builds lineup may show **Plan next build** after the active release. It is a non-record entry point:
it has no `SoftwareRelease`, route, baseline, or version and cannot be opened. A future planning workflow must
create the next release explicitly and validate its predecessor; the placeholder never fabricates a future
build record.
