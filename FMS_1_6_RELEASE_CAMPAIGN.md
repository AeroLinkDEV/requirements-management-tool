# FMS 1.6 Release Campaign

> **Implemented lifecycle contract; partially exposed planning surface.** Reconciled 2026-08-10. The
> release model and thirteen gates remain implemented. Candidate Baselines is now a supported Configuration
> Management destination at `/baselines`, including legacy procedure-manifest bootstrap and candidate work; the
> broader release-campaign workbench remains only partially exposed and `/release-planning` remains retired.
> Seed-state counts are initial conditions only; live engineering work persists independently. See
> [the current handoff](CURRENT_PRODUCT_HANDOFF_2026-08-10.md).

## Repeatable user-controlled release progression

The seeded 1.5 baseline is the immutable starting product configuration. Version 1.6 is an editable target workspace, not a preordained release. Users must approve the intended change request revisions, select those exact revisions into a candidate inheriting the materialized 1.5 baseline, freeze and materialize it, close release evidence and impact gates, collect unanimous electronic release approval, and explicitly issue the release.

After 1.6 is released, the same product workflow can plan 1.7 from the released predecessor, then 1.8 and later versions. Creating a release creates only an empty in-work planning context; it never creates, approves, or releases a baseline automatically.

## Purpose

The live FMS workspace is no longer only a static 1.5 data demonstration. It includes a governed 1.6 release campaign that exposes the work required to transform selected approved change requests into a trustworthy released software configuration.

The campaign is deliberately incomplete at seed time. Managers and engineers can see real blockers, disposition impacted items, choose the verification build, inspect evidence, compare releases, and progress the release through ordered approval. The product does not manufacture a green status.

> **Current-surface qualification — 2026-08-10.** The lifecycle and server-side campaign model below
> remains implemented. Candidate Baselines is exposed at `/baselines` for supported Configuration Management
> work, but the full detailed Release Campaign workbench below is not the primary current navigation path and
> `/release-planning` remains retired. Lifecycle Decision Room remains visible. Release-campaign impact
> dispositions below are consuming-engineer/release decisions and are not the author impact selectors
> superseded by DEC-071.

## Campaign Lifecycle

1. **Planning** — approved change requests are selected, impacts are reviewed, and the candidate baseline is assembled.
2. **Verification** — the exact build is selected and requirement coverage, test outcomes, evidence, traceability, and controlled outputs are assessed.
3. **In Review** — release approval starts only after every non-approval readiness gate is complete. Approvers act in the author-defined order.
4. **Released** — unanimous approval permits one atomic release operation that marks the campaign, candidate baseline, software release, and build released with one deterministic manifest hash.

## Computed Readiness Gates

The campaign calculates rather than manually declares thirteen gates:

| Gate | Completion evidence |
| --- | --- |
| Change control integrated | Selected approved change requests are represented in the release baseline |
| Impact disposition | Every identified requirement, traceability, verification, and document impact is Addressed or explicitly Not Applicable with rationale |
| Baseline frozen | The candidate baseline has been materialized and frozen with exact contents and hashes |
| Verification impact decided | Every new, modified, or orphaned requirement has an approved procedure decision or an accepted no-test rationale |
| Test change requests approved | Every controlled System, HLR, and LLR Test Change Request for the build has completed test-lead approval |
| Derived trace completeness | Required system-to-HLR and HLR-to-LLR revision-aware traces exist |
| Requirement coverage | Every effective requirement has at least one applicable test-procedure revision |
| Code traceability | Every required exact LLR revision has immutable GitLab merge evidence or an attributable no-code decision |
| Verification passed | The latest applicable execution for every required procedure is Pass for the selected build |
| Evidence uploaded | Verification executions have checksum-protected uploaded evidence, not only free-text references |
| Problem-report blockers resolved | Every release-blocking Problem Report is resolved, formally dispositioned, or covered by an attributable waiver |
| Controlled outputs | SYSRD, HLR SWRD, LLR SWRD, and all three test-procedure document sets exist for the exact baseline |
| Release approval | Every ordered release approver has approved the frozen review snapshot |

Readiness percentage is the average completion percentage across the thirteen gates, so partial disposition and verification progress is visible without declaring a gate complete. Each incomplete gate reports a concrete blocker and supporting counts.

## Controlled Outputs

The application generates downloadable DOCX and PDF outputs directly from authoritative persisted records. The output carries the document revision, release, baseline, content hash, record count, generation time, controlled header/footer, and exact artifact revision identifiers. Requirement headings, statements, and provenance remain together across page boundaries.

The database records remain authoritative. Downloaded files are controlled snapshots and do not become editable sources of truth.

## Release Execution Workbench

The Release Campaign page provides one governed workbench for progressing the campaign rather than merely reporting blockers:

- all target change request inputs show their exact state, inclusion status, and unresolved impact count;
- pending impacts may be dispositioned together for one exact change request with a required shared rationale;
- the exact candidate baseline can be frozen and materialized only after change-control integration is complete;
- trace and test-coverage links may be carried forward from the predecessor only when both requirement artifacts remain effective in the target baseline;
- a release-candidate software build can be recorded and selected against the materialized baseline;
- the six controlled outputs can be generated from the same exact configuration;
- coverage gaps link directly to the Verification workspace for explicit procedure creation; and
- a completed external verification manifest and its signed evidence package can be imported against the selected build.

Carry-forward does not invent coverage for a new requirement. In the FMS 1.6 scenario, the new round-robin system requirement remains visibly uncovered until an engineer creates and approves an applicable procedure.

## External Verification Manifest

After a build is selected, the platform exports a JSON template containing every exact test-procedure revision required by the target baseline. Import is atomic and is rejected unless the completed manifest contains exactly one result for every required procedure, with no duplicates or unrelated procedures.

Each row requires Pass, Fail, or Blocked, execution time, executor, configuration, and a human determination. One uploaded campaign evidence package is checksum-protected and linked to every immutable execution created by that import. Re-importing the same procedure/build combination creates explicit retest chains rather than overwriting history. Failed database imports remove the newly stored evidence file so an unreferenced payload is not silently retained.

## Evidence Integrity

Uploaded evidence is stored outside the database payload with file metadata and a SHA-256 checksum persisted in the database. Evidence is linked to an exact immutable test execution, and both records must belong to the same project. File names are sanitized, storage paths are controlled by the server, and file size is bounded.

## Initial Seeded Demonstration State

The initial FMS 1.6 campaign starts in Verification with an intentionally mixed impact backlog. Eight impacts are already addressed and twenty-four remain pending. This creates an honest manager-facing readiness story without implying that those counts remain fixed after persistent live testing.

## Release Integrity Rule

Formal release is refused until every gate is complete. The final operation binds the baseline content hash, requirements hash, selected software build, and ordered controlled-document hashes into one SHA-256 release manifest. The associated records transition together in a database transaction so a partially released configuration cannot be presented as complete.
