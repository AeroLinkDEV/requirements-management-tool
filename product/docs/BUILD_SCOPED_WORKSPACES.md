# Build-scoped workspaces

## Decision

The existing `SoftwareRelease` record is the durable software-build workspace identity. AeroLink does not
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
| Candidate and frozen baselines | `CandidateBaseline.ReleaseId` |
| Effective requirement snapshot | exact `BaselineRequirementSelection` rows for the resolved build baseline |
| Generated controlled documents | `ControlledDocument.ReleaseId` and `BaselineId` |
| Immutable software builds | `SoftwareBuild.ReleaseId` and `BaselineId` |
| Release campaigns and approvals | `ReleaseCampaign.ReleaseId` and `BaselineId` |
| Verification impact | `VerificationImpact.ReleaseId` |
| Test executions | `TestExecution.ReleaseId`; an optional immutable `SoftwareBuildId` adds exact configuration provenance |
| Problem reports | explicit `ProblemReportLink` to the owning `SoftwareRelease`; failure-origin reports derive it from their execution build |
| Requirement history | revision plus source SCR and effective baseline; historical rows retain their origin |

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

## Schema impact

`TestExecution.ReleaseId` is the one schema addition. A verification result can be recorded while a release is
in work, before an immutable `SoftwareBuild` provenance record exists, so deriving workspace ownership only
through optional `SoftwareBuildId` left those results without an unambiguous build boundary. The migration
backfills existing execution rows from their linked software build and leaves genuinely legacy unlinked rows
nullable. New browser-workspace results take the validated route release directly; release-campaign execution
imports take the campaign release.

The existing predecessor-release, baseline membership, source-SCR, controlled-document, software-build,
campaign, verification-impact, and typed problem-report link relationships provide the remaining build
identity and lineage. New manual problem reports are linked to the route build at creation; reports created
from failed verification derive that same explicit link from the execution's release/build context.

Records that are genuinely project governance, rather than build content, are intentionally not duplicated per
release. This increment never infers build ownership from affected-configuration free text.
