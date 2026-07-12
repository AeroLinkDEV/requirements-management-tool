# FMS 1.6 Release Campaign

## Purpose

The live FMS workspace is no longer only a static 1.5 data demonstration. It includes a governed 1.6 release campaign that exposes the work required to transform selected approved change requests into a trustworthy released software configuration.

The campaign is deliberately incomplete at seed time. Managers and engineers can see real blockers, disposition impacted items, choose the verification build, inspect evidence, compare releases, and progress the release through ordered approval. The product does not manufacture a green status.

## Campaign Lifecycle

1. **Planning** — approved SCRs and SWCRs are selected, impacts are reviewed, and the candidate baseline is assembled.
2. **Verification** — the exact build is selected and requirement coverage, test outcomes, evidence, traceability, and controlled outputs are assessed.
3. **In Review** — release approval starts only after every non-approval readiness gate is complete. Approvers act in the author-defined order.
4. **Released** — unanimous approval permits one atomic release operation that marks the campaign, candidate baseline, software release, and build released with one deterministic manifest hash.

## Computed Readiness Gates

The campaign calculates rather than manually declares nine gates:

| Gate | Completion evidence |
| --- | --- |
| Change control integrated | Selected approved change requests are represented in the release baseline |
| Impact disposition | Every identified requirement, traceability, verification, and document impact is Addressed or explicitly Not Applicable with rationale |
| Baseline frozen | The candidate baseline has been materialized and frozen with exact contents and hashes |
| Derived trace completeness | Required system-to-HLR and HLR-to-LLR revision-aware traces exist |
| Requirement coverage | Every effective requirement has at least one applicable test-procedure revision |
| Verification passed | The latest applicable execution for every required procedure is Pass for the selected build |
| Evidence uploaded | Verification executions have checksum-protected uploaded evidence, not only free-text references |
| Controlled outputs | SYSRD, HLR SWRD, LLR SWRD, and all three test-procedure document sets exist for the exact baseline |
| Release approval | Every ordered release approver has approved the frozen review snapshot |

Readiness percentage is the average completion percentage across the nine gates, so partial disposition and verification progress is visible without declaring a gate complete. Each incomplete gate reports a concrete blocker and supporting counts.

## Controlled Outputs

The application generates downloadable DOCX and PDF outputs directly from authoritative persisted records. The output carries the document revision, release, baseline, content hash, record count, generation time, controlled header/footer, and exact artifact revision identifiers. Requirement headings, statements, and provenance remain together across page boundaries.

The database records remain authoritative. Downloaded files are controlled snapshots and do not become editable sources of truth.

## Evidence Integrity

Uploaded evidence is stored outside the database payload with file metadata and a SHA-256 checksum persisted in the database. Evidence is linked to an exact immutable test execution, and both records must belong to the same project. File names are sanitized, storage paths are controlled by the server, and file size is bounded.

## Seeded Demonstration State

The FMS 1.6 campaign starts in Verification with an intentionally mixed impact backlog. Eight impacts are already addressed and twenty-four remain pending. This creates an honest manager-facing readiness story and leaves meaningful work for the interactive demonstration rather than presenting a pre-completed release.

## Release Integrity Rule

Formal release is refused until every gate is complete. The final operation binds the baseline content hash, requirements hash, selected software build, and ordered controlled-document hashes into one SHA-256 release manifest. The associated records transition together in a database transaction so a partially released configuration cannot be presented as complete.
