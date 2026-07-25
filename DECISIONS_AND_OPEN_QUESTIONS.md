# Decisions, Assumptions, and Open Questions

This is the authoritative product-decision log. Accepted entries are append-only: if a decision changes, add a superseding decision and retain the original.

## Decision Record Format

Future entries use:

- **ID / Date / Status**
- **Decision**
- **Rationale**
- **Consequences**
- **Supersedes / Superseded by**, when applicable

## Accepted Decisions

### D-029 — Successor releases and baselines are explicitly user-controlled

- **Decision:** Authorized users may create repeated in-work product versions from a released predecessor. They—not seed logic or background automation—select and approve changes, assemble the exact successor candidate, complete release gates, and explicitly release it.
- **Rationale:** Product evolution from 1.5 to 1.6, 1.7, and beyond must be useful as a real configuration-management workflow and must preserve accountable human release authority.
- **Constraint:** A successor candidate must identify an exact materialized predecessor baseline whenever prior product data exists. Released baselines remain immutable.

### DEC-001 - Markdown in Git Is Authoritative

- **Date:** 2026-07-11
- **Status:** Accepted
- **Decision:** Project-definition documents use Markdown under Git version control. Generated Word/PDF snapshots are secondary outputs.
- **Rationale:** Plain-text review, history, cross-linking, and future Codex continuity are stronger than maintaining parallel Word masters.
- **Consequences:** Product decisions must be updated in Markdown first. Original Word inputs remain provenance sources.

### DEC-002 - Production Platform Ambition

- **Date:** 2026-07-11
- **Status:** Accepted
- **Decision:** Optimize the long-term definition for a trustworthy, multi-user production platform delivered incrementally.
- **Rationale:** The intended organizational value and 150-user target exceed a personal demonstration, while a phased build limits risk.
- **Consequences:** Security, audit, recovery, maintainability, and on-premises operations are foundational quality concerns.

### DEC-003 - Standards-Informed, No Initial Compliance Claim

- **Date:** 2026-07-11
- **Status:** Accepted
- **Decision:** Use ARP4754 and DO-178 concepts and terminology without claiming compliance, certification suitability, or tool qualification.
- **Rationale:** The product direction benefits from domain rigor, but compliance depends on program-specific processes and evidence beyond this initial scope.
- **Consequences:** Objective-by-objective mappings are deferred.

### DEC-004 - System-Level First Slice

- **Date:** 2026-07-11
- **Status:** Accepted
- **Decision:** First prove the complete system-level chain from SCR through requirement, baseline, SYSRD, test procedure, external result/evidence capture, and traceability.
- **Rationale:** A coherent vertical slice validates the hardest lifecycle concepts better than a shallow full-V prototype.
- **Consequences:** HLR, LLR, SWCR, and software-test functions are later phases.

### DEC-005 - Artifact Platform, Not Document Master

- **Date:** 2026-07-11
- **Status:** Accepted
- **Decision:** Structured controlled artifact records and baselines are authoritative; documents are generated views.
- **Rationale:** Version-aware traceability, baseline integrity, impact analysis, and reproducibility cannot depend on independently edited document masters.
- **Consequences:** Legacy document import will require an explicit ingestion and validation process when scoped.

### DEC-006 - SYSRD and SWRD Naming

- **Date:** 2026-07-11
- **Status:** Accepted
- **Decision:** Use `SYSRD` for System Requirements Document and `SWRD` for Software Requirements Document; do not use ambiguous `SRD` alone.
- **Rationale:** Both source materials used SRD in potentially conflicting ways.
- **Consequences:** All product documentation and future UI vocabulary follow these terms.

### DEC-007 - Controlled Retirement, Not Destructive Deletion

- **Date:** 2026-07-11
- **Status:** Accepted
- **Decision:** A requirement may be retired from future effective baselines through an approved SCR, while all historical data remains intact.
- **Rationale:** This preserves the user's desired “deletion” outcome and the complete certification/history story.
- **Consequences:** Normal product interfaces never physically delete approved or historical controlled records.

### DEC-008 - Test Execution Is Initially External

- **Date:** 2026-07-11
- **Status:** Accepted
- **Decision:** The first slice manages test procedures and records/imports executions, results, configurations, evidence, and reviews; it does not run tests.
- **Rationale:** External benches and tools already perform execution, while controlled evidence and traceability provide the immediate value.
- **Consequences:** Execution provenance and import validation are required; bench control is excluded.

### DEC-009 - Procedure-First Verification Model

- **Date:** 2026-07-11
- **Status:** Accepted for first slice
- **Decision:** Begin with test procedures and steps. Add a separate test-case layer only after its distinct semantics and value are defined.
- **Rationale:** The source notes explicitly favored procedure-first and were uncertain about test-case ordering.
- **Consequences:** The domain model must remain extensible without requiring a test case for every initial procedure.

### DEC-010 - Approval and Baseline Inclusion Are Separate

- **Date:** 2026-07-11
- **Status:** Accepted
- **Decision:** Approval makes a revision eligible for controlled use; explicit baseline selection determines release/document applicability.
- **Rationale:** Approved changes may skip a release, and approved artifacts need not belong to every baseline.
- **Consequences:** Workflow, reporting, and UI must never collapse these states.

### DEC-011 - Git Repository Initialized Locally Before GitHub Connection

- **Date:** 2026-07-11
- **Status:** Accepted
- **Decision:** Initialize Git locally as part of the documentation foundation. Configure a GitHub remote and publish only under a later explicit repository decision.
- **Rationale:** Local history can begin immediately, while repository ownership/name/visibility are not yet specified.
- **Consequences:** No remote, commit, or push is implied by this documentation implementation.

### DEC-012 - AeroLink Mockups Are the North-Star Experience

- **Date:** 2026-07-11
- **Status:** Accepted
- **Decision:** Preserve the AeroLink dashboard, SCR review, traceability, and test-evidence mockups as guiding visual and interaction inspiration, subject to validation and refinement rather than pixel-for-pixel implementation.
- **Rationale:** The concepts make the intended controlled lifecycle experience tangible and establish a coherent, modern product direction for managers and engineers.
- **Consequences:** Future UX work should retain the calm mission-control character, visible state/provenance, actionable drill-downs, and role-aware information priorities defined in [DESIGN_VISION_AND_DASHBOARDS.md](DESIGN_VISION_AND_DASHBOARDS.md).

### DEC-013 - Dashboards Are a Core Capability

- **Date:** 2026-07-11
- **Status:** Accepted
- **Decision:** Role-aware manager, engineer, configuration/quality, and administrator dashboards are first-class product capabilities, not optional reporting added after core workflows.
- **Rationale:** Users need immediate understanding of progress, readiness, gaps, risk, assignments, and required action across controlled lifecycle data.
- **Consequences:** Dashboard needs influence the underlying domain events, metric definitions, authorization, audit, performance, and drill-down behavior from the beginning. Every important metric requires a trusted metric contract.

### DEC-014 - Interactive Showcase Before Production Implementation

- **Date:** 2026-07-11
- **Status:** Accepted; fulfilled and superseded by DEC-046 on 2026-07-24
- **Decision:** After the documentation baseline, create a static-data interactive web showcase of the end-to-end experience before production application architecture and implementation.
- **Rationale:** A realistic show-and-tell experience will validate desirability, terminology, information architecture, dashboard priorities, and workflow comprehension at far lower cost than production code.
- **Consequences:** The showcase must be labeled as a concept and must not imply production authentication, workflow enforcement, audit integrity, compliance, data persistence, integration, or deployment readiness.
- **Superseded by:** DEC-046. The showcase was built, served its validation purpose, and was retired
  once the product surpassed it. The sequencing this decision required was followed.

### DEC-015 - System Engineer and Manager Are the Initial Primary Users

- **Date:** 2026-07-11
- **Status:** Accepted
- **Decision:** Prioritize the System Engineer and Manager experiences in the initial product slice and interactive showcase.
- **Rationale:** These roles have the strongest immediate need to understand controlled change, progress, review demand, verification status, and release readiness.
- **Consequences:** Dashboard and workflow validation begins with these two perspectives; supporting quality, configuration, and administration needs remain in scope but do not dilute the first showcase.

### DEC-016 - Initial Hierarchy Is Software-Oriented

- **Date:** 2026-07-11
- **Status:** Accepted as default
- **Decision:** Use `Program -> Project -> Software Product -> Software Release` as the initial default hierarchy, with optional additional system/product/configuration levels where a program requires them.
- **Rationale:** Current target programs are primarily software-oriented and move from program/project directly into software lifecycle management.
- **Consequences:** The first model must not require artificial hierarchy levels, while remaining extensible for programs with richer system structures.

### DEC-017 - Approval Requires Every Required Reviewer

- **Date:** 2026-07-11
- **Status:** Superseded by DEC-024, DEC-025, and DEC-026
- **Decision:** An artifact revision is approved only when every reviewer assigned as required approves that exact revision.
- **Rationale:** Approval represents agreement of the complete required review group, not a majority or partial quorum.
- **Original consequence (superseded):** Rejection or requested changes block approval; the earlier model assumed rework created a new submitted revision. DEC-024 replaces that assumption with same-revision review cycles before first approval.
- **Superseded by:** DEC-024 clarifies same-revision pre-approval rework, DEC-025 governs post-approval revision changes, and DEC-026 establishes author-selected approvers.

### DEC-018 - Retired Requirements Are Omitted from the Effective Document

- **Date:** 2026-07-11
- **Status:** Accepted
- **Decision:** A requirement retired by an approved change is not present in the next effective SYSRD.
- **Rationale:** The effective document should contain the requirements applicable to that baseline.
- **Consequences:** The retired requirement remains retrievable through prior baselines, SCR history, comparison reports, traceability, and audit records, but is omitted from the successor SYSRD body.

### DEC-019 - Verification Outcomes Require Human Judgment

- **Date:** 2026-07-11
- **Status:** Accepted direction
- **Decision:** Pass and Fail are controlled human conclusions about whether an execution successfully verified applicable requirements; Blocked means a valid verification conclusion could not be reached.
- **Rationale:** Test execution data alone does not determine whether the requirement was adequately verified.
- **Consequences:** Outcome records require reviewer attribution and evidence. Blocked is neither Pass nor Fail and requires a reason and disposition.

### DEC-020 - FMS Version 3.3 Is the Showcase Scenario

- **Date:** 2026-07-11
- **Status:** Accepted
- **Decision:** Use an FMS Version 3.2 to 3.3 release story driven by two SCRs: one introduces a Round Robin function and one incorporates fixes for four linked PRs.
- **Rationale:** This reflects a realistic software-oriented program and exercises change, requirements, verification coverage, problem reports, baselines, dashboards, and traceability in one story.
- **Consequences:** [SHOWCASE_STORY_FMS_3_3.md](SHOWCASE_STORY_FMS_3_3.md) is the canonical fictional dataset and walkthrough. The second-change interpretation remains an assumption until confirmed.

### DEC-021 - Requirements Are Reviewed Through the SCR

- **Date:** 2026-07-11
- **Status:** Accepted
- **Decision:** Requirements do not enter an independent review/approval workflow. Reviewers evaluate and unanimously approve the exact SCR revision containing Problem, Analysis, Solution, and all proposed requirement introductions, modifications, and retirements.
- **Rationale:** The SCR is the controlled change package and provides the context required to judge its requirement changes together.
- **Consequences:** The SCR author decides when the package is ready to submit; submission validation checks completeness. Requirement revisions authorized by an approved SCR do not become effective until explicitly selected into a baseline.

### DEC-022 - Revision Is Appended to the Stable Requirement ID

- **Date:** 2026-07-11
- **Status:** Accepted
- **Decision:** Use a stable requirement identifier such as `SYSR-00002375` and display revision 4 as `SYSR-00002375.04`.
- **Rationale:** The combined display is familiar and concise while the system can still maintain identity and revision separately.
- **Consequences:** Revision suffixes use a minimum of two digits and expand beyond two digits when necessary. Interfaces and integrations must distinguish base identity from the revision-qualified value.

### DEC-023 - Showcase Demonstrates System Requirements and HLRs

- **Date:** 2026-07-11
- **Status:** Accepted
- **Decision:** The FMS Version 3.3 Round Robin showcase includes system requirements allocated to software HLRs.
- **Rationale:** Current programs are software-oriented, but the full change story must demonstrate the relationship between externally meaningful system behavior and software requirements.
- **Consequences:** Both system and HLR revisions appear inside the SCR package and trace through verification and the Version 3.3 baseline. This showcase breadth does not by itself redefine the production implementation sequence.

### DEC-024 - Pre-Approval Rework Keeps the SCR Revision

- **Date:** 2026-07-11
- **Status:** Accepted
- **Decision:** When an SCR has never been approved and an approver requests a change, it returns to Draft at the same revision number. Resubmission creates a new review cycle, not a new SCR revision.
- **Rationale:** The revision has not yet achieved an approved controlled state, so ordinary review rework belongs to the original revision.
- **Consequences:** Every review-cycle submission, comment, decision, and snapshot remains historical. Earlier approvals do not carry into the resubmitted cycle.

### DEC-025 - Post-Approval SCR Change Creates the Next Revision

- **Date:** 2026-07-11
- **Status:** Accepted
- **Decision:** Any change to an approved SCR creates the next SCR revision, even when the associated SYSRD/SWRD or release baseline has not yet been approved or released.
- **Rationale:** The approved SCR revision is a completed controlled record and cannot be edited in place.
- **Consequences:** The new revision begins in Draft, receives an author-selected ordered approval sequence, and requires unanimous fresh approval. The earlier approved revision remains visible and may be superseded for release selection.

### DEC-026 - SCR Author Selects the Approval Group

- **Date:** 2026-07-11
- **Status:** Accepted
- **Decision:** The SCR author has authority to select the people whose approval is required for that SCR review cycle.
- **Rationale:** The author determines the appropriate approval participants for the content and affected disciplines.
- **Consequences:** Approval requires every selected approver in the author-defined order. DEC-027 defines how that sequence may change after submission.

### DEC-027 - SCR Review Is Sequential with Controlled Approver Replacement

- **Date:** 2026-07-11
- **Status:** Accepted
- **Decision:** SCR review proceeds through the author-selected approvers in order. Before a future approver’s turn is reached, the author may replace that approver without restarting completed stages. Active and completed stages are locked. If a completed approval used the wrong person, the review cycle is cancelled and restarted from the first approver.
- **Rationale:** Sequential review reflects the actual process, permits practical correction of future assignments, and prevents an invalid completed approval from contributing to final approval.
- **Consequences:** Every substitution and cancellation is audited. Cancelled decisions remain historical but do not count. Restart uses the same submitted snapshot when content is unchanged; content changes follow the applicable Draft/revision rules.

### DEC-028 - Material Actions Use Authenticated Server Identity

- **Date:** 2026-07-12
- **Status:** Accepted
- **Decision:** The API derives the actor for material lifecycle actions from a revocable authenticated session. Client-supplied actor identifiers are not authoritative.
- **Rationale:** Authorship, review, baseline assembly, verification, and release evidence cannot be trustworthy if the browser can impersonate another username.

### DEC-029 - Approvals Require Credential Confirmation and Immutable Signature Evidence

- **Date:** 2026-07-12
- **Status:** Accepted
- **Decision:** SCR and release approvals require password re-entry, assigned-stage identity, Program Approver authority, an explicit signature meaning, and an immutable signature record tied to the controlled snapshot hash.
- **Rationale:** A visible approval button is insufficient evidence of intent, identity, and exact approved content.

### DEC-030 - Disabled Identities Remain Historically Resolvable

- **Date:** 2026-07-12
- **Status:** Accepted
- **Decision:** Accounts are disabled or locked rather than deleted. Existing artifact attribution, review decisions, and signatures retain username and display-name snapshots.
- **Rationale:** Personnel changes must not damage the audit trail.

### DEC-031 - Enterprise Parity Is Required for Core Requirements Engineering

- **Date:** 2026-07-12
- **Status:** Accepted
- **Decision:** AeroLink will meet enterprise expectations for configurable requirements authoring, collaboration, search, bulk operations, interchange, traceability, configuration/reuse, reporting, security, and operations while retaining its aerospace assurance controls.
- **Rationale:** Controlled approvals and baselines alone are not enough; engineers must be able to perform high-volume daily requirements work as efficiently as they can in established platforms.
- **Consequences:** The capability benchmark and stable features in [ENTERPRISE_REQUIREMENTS_MANAGEMENT_BENCHMARK.md](ENTERPRISE_REQUIREMENTS_MANAGEMENT_BENCHMARK.md) and [FEATURE_CATALOG.md](FEATURE_CATALOG.md) govern enterprise-parity planning.

### DEC-032 - Enterprise Requirements Workspace Is the Next Major Slice

- **Date:** 2026-07-12
- **Status:** Accepted
- **Decision:** The next massive implementation will focus on configurable artifact schemas, specification/module hierarchy, rich authoring, collaboration, universal search, saved views, governed bulk operations, visual redlines, and previewed CSV/Excel onboarding.
- **Rationale:** This is the largest current competitive gap and provides the data/configuration model required by ReqIF, reporting, product-line reuse, and integrations.
- **Consequences:** ReqIF/OSLC, streams/variants, enterprise federation, and risk/compliance follow as explicit waves rather than being mixed into the first authoring slice.

### DEC-033 - Requirement Authoring Remains Subordinate to SCR/SWCR Authority

- **Date:** 2026-07-12
- **Status:** Accepted
- **Decision:** An engineer may discover an approved requirement, analyze its lifecycle impact, and author a proposed next revision in the Enterprise Requirements Workspace, but the proposal must belong to a Draft SCR/SWCR. Only the complete change package is reviewed and approved; approved requirement revisions remain immutable and new effective revisions arise only through baseline materialization.
- **Rationale:** Enterprise-speed authoring must not create a second approval path or weaken the accepted change-authority model.
- **Consequences:** Rich proposal content, Program fields, relationship impact, assignments, and dispositions are included in the exact SCR/SWCR review snapshot and audit story.

### DEC-034 - Enterprise Control Records Surround but Do Not Replace Lifecycle Authority

- **Date:** 2026-07-12
- **Status:** Accepted
- **Decision:** Controlled files, saved queries, background jobs, editing sessions, merge conflicts, and integrity checkpoints are attributable Project-scoped control records around authoritative artifacts. They do not independently approve, baseline, or release lifecycle content.
- **Rationale:** Enterprise usability and operability require durable supporting state, while change authority must remain unambiguous and approved history immutable.
- **Consequences:** Attachments retain every superseded version and digest; jobs retain idempotency and outcomes; concurrent work retains base/local/remote content; saved links reapply current permissions; and integrity checkpoints describe an observed repository state rather than certifying the product.

### DEC-035 - Persistent Role-Oriented Workspaces

- **Date:** 2026-07-13
- **Status:** Accepted
- **Decision:** Authenticated users retain the main navigation at all times. AeroLink presents distinct Systems, Software, System Test, and Software Test workspaces over the same authoritative lifecycle repository.
- **Rationale:** Most engineers spend nearly all their time within one discipline and should not repeatedly return to a generic dashboard to reach daily work.
- **Consequences:** Navigation preserves Program and release context, highlights the active workspace, and filters change requests, requirements, specifications, tests, and documents by discipline without duplicating records.

### DEC-036 - Server-Assigned Identity, Authorship, and Revisions

- **Date:** 2026-07-13
- **Status:** Accepted
- **Decision:** The server assigns the next never-reused identifier for every new SCR, SWCR, and requirement; derives the author from the authenticated session; and assigns the next requirement revision when an existing requirement is modified or retired.
- **Rationale:** User-entered identifiers, authors, and revision counters invite collision, impersonation, and inconsistent history.
- **Consequences:** Interfaces may preview the reserved format but cannot authoritatively choose these values. Requirement modification begins by searching and selecting an existing controlled requirement. Sequences are installation-wide and independent per artifact prefix.

### DEC-037 - Separate System and Software Change Creation

- **Date:** 2026-07-13
- **Status:** Accepted
- **Decision:** System SCR creation accepts only System requirement changes. Software SWCR creation is a separate route and accepts only HLR and LLR changes, including an explicit derived-requirement classification.
- **Rationale:** Mixing both disciplines in one form obscures ownership, validation, review context, and document consequences.

### DEC-038 - Sequential or Parallel Review Activation

- **Date:** 2026-07-13
- **Status:** Accepted
- **Decision:** The author explicitly chooses sequential or parallel review for each submitted snapshot. Sequential review activates and notifies only the next reviewer; parallel review activates and notifies all selected reviewers together. Unanimous approval remains required.
- **Rationale:** Notification and My Work queues must represent actual decision authority, not merely membership in a future review stage.
- **Consequences:** Every activated reviewer receives an in-product deep link; external email is delivered through a later organization integration. Review pages expose an immediate controlled PDF of the exact in-review artifact.

### DEC-039 - Controlled Check-Out with Recoverable Auto-Save

- **Date:** 2026-07-13
- **Status:** Accepted
- **Decision:** Editable controlled artifacts use an attributable check-out/edit session with renewable ownership, server-side draft auto-save, explicit check-in, administrative recovery, and read-only visibility to everyone else.
- **Rationale:** Silent concurrent overwrite is unacceptable, while abandoned locks must not stop Program work.
- **Consequences:** Browser-local recovery may supplement but never replace server-side draft state. The existing edit-session and merge-conflict foundation will be generalized from requirements to SCR/SWCR and document authoring.

### DEC-040 - Trace-Rich Publications and Release Lineage

- **Date:** 2026-07-13
- **Status:** Accepted
- **Decision:** Requirement publications end with readable upward-trace annexes; change-request publications separate introductions, modifications with old/new redlines, and retirements; and release history includes an interactive clickable predecessor/branch tree.
- **Rationale:** Reviewers and managers must understand both content and lifecycle context without manually reconstructing relationships.

### DEC-041 - Operational Backup Is a Product Requirement

- **Date:** 2026-07-13
- **Status:** Accepted
- **Decision:** AeroLink supports complete, integrity-checked backup and tested restore of the database, controlled file store, configuration, and required provenance. The production target is at least one IT-managed backup every 24 hours.
- **Rationale:** A lifecycle system is trustworthy only if its authoritative records and evidence can be recovered together.

### DEC-042 - FMS Demonstration Narrative and Fictional Directory

- **Date:** 2026-07-13
- **Status:** Accepted
- **Decision:** The demonstration repository presents FMS 1.5 as fully released with approved historical change and document evidence, and FMS 1.6 as its active successor. It includes 200 deterministic fictional personnel with realistic engineering titles and searchable selection controls.
- **Rationale:** A populated, internally consistent working Program communicates the product more credibly than disconnected sample screens.

### DEC-043 - Durable URLs Are the Artifact Navigation Contract

- **Date:** 2026-07-13
- **Status:** Accepted
- **Decision:** Authenticated page and artifact identity is represented in the browser URL. Navigation, search results, breadcrumbs, copy-link actions, refresh, and browser back/forward all use that same contract while restoring Program, Project, and release context.
- **Rationale:** Controlled records must be referenceable in review, audit, notification, and multi-tab work without relying on transient component state.
- **Consequences:** Unsupported or missing identities render an explicit not-found view; inaccessible Program data returns a forbidden response and is not disclosed by search.

### DEC-044 - Exclusive Editing Is a Renewable Server Lease

- **Date:** 2026-07-13
- **Status:** Accepted
- **Decision:** An exclusive checkout is a server-enforced, uniquely indexed lease with a fifteen-minute inactivity expiry, heartbeat renewal, version-checked autosave snapshots, explicit check-in/discard, and privileged forced unlock with a mandatory reason.
- **Rationale:** A durable lease prevents silent concurrent overwrite while allowing abandoned work to recover without hiding the artifact from readers.
- **Consequences:** Review submission is rejected while an incompatible lease is active. Approved/frozen content is never made editable through autosave. The first complete vertical implementation applies to SCR and SWCR drafts; other controlled draft types will adopt the same domain contract incrementally.

### DEC-045 - Recovery Is Proved Outside the Authoritative Database First

- **Date:** 2026-07-13
- **Status:** Accepted
- **Decision:** Backup verification checks the archive sidecar, safe entry paths, manifest paths, sizes, and SHA-256 values. Automated restore exercises target only explicitly named validation databases and isolated evidence storage. Production restore requires a separate elevated switch and exact confirmation phrase and creates a pre-restore backup.
- **Rationale:** Recovery tooling is valuable only when testable without risking the authoritative PostgreSQL database or evidence store.
- **Consequences:** Production recovery is an attended operator action. A successful archive check alone does not replace periodic isolated restore drills and post-restore health validation.

### DEC-046 - The Concept Showcase Is Retired; One Product Remains

- **Date:** 2026-07-24
- **Status:** Accepted
- **Decision:** Delete the `showcase/` Phase 0.5 static-data prototype. The product application is the
  single software artifact and the only thing demonstrated to stakeholders.
- **Rationale:** The prototype had served its purpose. It was a single-file click-through over
  hardcoded arrays, and the product had surpassed it in both capability and visual maturity — the July
  2026 usability refresh had already carried its design direction into the real application. Keeping a
  second, better-looking but entirely fictional artifact created a standing risk of demonstrating the
  wrong one, and split effort across two front ends.
- **Consequences:** Phase 0.5 is closed. Demonstrations use the application with the `FMSLIVE` dataset,
  which exercises real domain and persistence rules rather than mock data. The prototype's design
  intent survives in `design/mockups` and
  [DESIGN_VISION_AND_DASHBOARDS.md](DESIGN_VISION_AND_DASHBOARDS.md); its narrative survives in
  [SHOWCASE_STORY_FMS_3_3.md](SHOWCASE_STORY_FMS_3_3.md), retained as a historical record. Feature
  SHOW-001 is closed as delivered and retired. Any future design exploration extends the product or
  the mockups rather than reviving a parallel application.
- **Supersedes:** DEC-014, which required an interactive showcase before production implementation.
  That sequencing was followed and is complete.

### DEC-047 - The Client Has No External Runtime Dependency

- **Date:** 2026-07-24
- **Status:** Accepted
- **Decision:** The web client must not request any resource from outside its own origin at runtime.
  Fonts, styles, scripts, and assets are served by the AeroLink installation. The two typefaces are
  self-hosted through versioned packages rather than fetched from a public font CDN.
- **Rationale:** AeroLink is on-premises software that must run on restricted and disconnected
  networks. The previous CDN font import was render-blocking: measured on 2026-07-24, first paint took
  12,994 ms when the request hung — the normal behaviour of a firewall that drops packets rather than
  rejecting them — against 147 ms when it failed immediately. A dependency that is invisible on a good
  network and disabling on a customer's network is not acceptable in this product. An outbound call to
  a third party from a controlled engineering tool is also an egress that a security review would
  reasonably challenge.
- **Consequences:** The client starts and renders correctly with all external requests blocked, which
  is verified behaviour and should be retained as a check. Typography is unchanged; DM Sans and Manrope
  are SIL Open Font License 1.1 and redistributable, with licences shipped in the packages. Adding any
  CDN-hosted asset in future requires a superseding decision.

### DEC-048 - Verification Impact Gates Release Approval, Not the Baseline Freeze

- **Date:** 2026-07-25
- **Status:** Accepted
- **Decision:** Unresolved verification impact items block **release approval**, enforced as the
  `verification_impact` gate in `ReleaseReadinessService`. They do not block freezing a candidate
  baseline. An item is resolved when a test engineer either names an approved procedure or records that
  no test is required; a release that changed no requirements raises no items and the gate is complete
  by having nothing to decide.
- **Rationale:** The gate was first placed on the freeze endpoint. That deadlocks the workflow:
  requirement revisions do not exist until a baseline is frozen and then materialized, and a revision is
  what a test procedure is written against. Blocking the freeze therefore withheld the verification
  team's own inputs and made the item permanently unresolvable. Release approval is also what was
  actually asked for, and expressing it as a named readiness gate makes the outstanding items visible in
  the release workbench instead of surfacing as a refusal at the final step.
- **Consequences:** "Decided" means the procedures are authored and approved. It carries no claim about
  execution or results — those remain the `coverage`, `verification` and `evidence` gates. The freeze
  endpoint no longer takes a `VerificationImpactService` dependency.

### DEC-049 - Information Density Is Spacing, and WCAG 2.2 AA Is a Commitment

- **Date:** 2026-07-25
- **Status:** Accepted
- **Decision:** Information density is expressed as spacing and line-height tokens with two settings,
  applied through the workspace shell (`.workspaceView > main`) rather than as a per-page selector list.
  Compact compresses the block axis only. AeroLink targets **WCAG 2.2 AA**: 4.5:1 contrast for body
  text, 3:1 for large text, and 24x24 CSS pixel minimum target sizes. Both are verified on rendered
  pixels, in both densities, by `design-system.spec.ts` and `accessibility-contrast.spec.ts`.
- **Rationale:** The previous density implementation was fifteen selectors carrying hard-coded compact
  pixel values; it reached six of the product's twenty-eight row and card families, so most surfaces
  looked identical in both settings and every new panel silently opted out. Page padding was declared in
  eight places across seven files, several using `!important` purely to win against each other. On
  accessibility, the readability floor was a claim in a report until it was measured; the first real
  measurement in compact found text below the floor, and the first contrast measurement found 96
  distinct failing colour pairs. A commitment that is not measured is not a commitment.
- **Consequences:** Compact is measurably denser on record-heavy surfaces, and the spec fails if it
  stops being so. Only the block axis is compressed, because several record rows carry 3px of inline
  padding inside an already-padded container and a shared horizontal value would break their alignment.
  Roughly 110 `color:` declarations were darkened, and two shared colours had to be split because one
  value cannot satisfy AA on both a light and a dark surface. Elements sitting on a gradient are counted
  and reported rather than assumed conformant, since their effective background cannot be resolved from
  computed style; 94 such elements remain unverified by machine and would need visual review.

## Working Assumptions

Assumptions are not decisions. They remain valid only until confirmed or replaced.

- **ASM-001:** Product- and behavior-level definitions precede technical architecture, data schema, and UI design.
- **ASM-002:** Artifact numbers are globally unique across programs; exact prefixes, digit lengths, and revision display syntax are configurable or decided later.
- **ASM-003:** The first slice supports multiple programs even if initial validation uses one reference program.
- **ASM-004:** Requirements may include controlled images/figures as part of revisioned content.
- **ASM-005:** Exact review roles and independence rules vary by organization/program; the SCR author selects the ordered approval sequence and unanimous sequential approval is fixed initial behavior.
- **ASM-006:** PR references may point to an external system until full PR management exists.
- **ASM-007:** The initial platform records Pass, Fail, Blocked, Not Run, and Not Applicable using the meanings in [SYSTEM_LEVEL_WORKFLOW.md](SYSTEM_LEVEL_WORKFLOW.md); detailed step/result transition rules still require validation.
- **ASM-008:** Source Word files remain unmodified in the repository root during the initial consolidation.
- **ASM-009:** GitHub will eventually become the shared remote source of truth, but no repository details are assumed.
- **ASM-010:** Dashboard values are computed only from records the current user is authorized to know exist.
- **ASM-011:** The first interactive showcase uses deterministic fictional data and simulated state changes rather than a production backend.
- **ASM-012:** Confirmed: the second Version 3.3 change package is an SCR linked to four PRs.

## Open Questions Required Before Phase 1 Technical Planning

| ID | Question | Why It Matters | Decision Owner / Timing |
| --- | --- | --- | --- |
| OQ-001 | Which optional product/system/configuration layers are needed beyond the accepted `Program -> Project -> Software Product -> Software Release` default? | Determines applicability and baseline scope for programs that do not fit the software-oriented default | Product owner before domain/data design |
| OQ-002 | Should numeric sequences be global across all artifact types or unique within each prefix? The accepted base/suffix format is documented in [IDENTIFIERS_AND_REQUIREMENT_FIELDS_PROPOSAL.md](IDENTIFIERS_AND_REQUIREMENT_FIELDS_PROPOSAL.md). | Affects identifier generation and external references | Product/configuration stakeholders before data design |
| OQ-003 | Which proposed requirement-change fields must be complete before an SCR can be submitted, and which existing FMS fields must be preserved or program-configurable? | Controls SCR package validation, import, review, and generated documents | Requirements stakeholders before Phase 1 |
| OQ-004 | Which independence or organizational policy constraints, if any, may prevent the author from selecting a particular approver? | Controls review integrity while preserving accepted author authority and sequential replacement behavior | Quality/configuration/product stakeholders before Phase 2 design |
| OQ-006 | What are the allowed verification methods, and can one requirement require multiple methods? | Affects completeness logic and document output | Verification stakeholders before Phase 1 completion |
| OQ-007 | Are test case and test suite separate first-slice artifacts, or is procedure plus execution configuration sufficient? | Avoids redundant objects and unclear trace semantics | Verification stakeholders before Phase 3 design |
| OQ-008 | What detailed transition, amendment, and release-gate rules apply to the accepted Pass, Fail, Not Applicable, Blocked, and Not Run meanings? | Prevents misleading traceability and release status | Verification/quality stakeholders before Phase 3 |
| OQ-009 | When must a failed execution have a PR, anomaly, or formal disposition? | Controls completeness and release gates | Quality/program stakeholders before Phase 3 |
| OQ-010 | Should a successor SYSRD include a separate change summary listing retired requirements even though they are omitted from its effective requirement body? | Affects document change communication without weakening the effective-content rule | Product/configuration stakeholders before Phase 2 |
| OQ-011 | How are conflicting approved SCRs affecting the same requirement ordered or resolved? | Required for deterministic candidate-baseline construction | Configuration/product stakeholders before Phase 2 |
| OQ-012 | Does reproducible document generation require byte-identical PDFs or content-equivalent outputs with explained metadata differences? | Drives generator, archive, validation, and platform constraints | Product/quality stakeholders before Phase 2 |
| OQ-013 | What legacy SYSRD structure and import quality should the first migration workflow support? | Import was requested but depends heavily on source format and validation needs | Product owner after sample documents are available |
| OQ-014 | What production data volumes, response-time targets, availability, RPO, and RTO are required? | Converts quality ambitions into testable architecture constraints | Operations/product owner before production architecture |
| OQ-015 | What GitHub organization/repository name, visibility, branch policy, and contributor workflow should be used? | Required before publishing the local repository | Repository owner before remote setup |
| OQ-016 | Which specific decisions and recurring tasks must the accepted Manager and System Engineer dashboards support first? | Determines dashboard information priority and avoids generic views | Product owner and representative users before showcase build |
| OQ-017 | What exact definitions, thresholds, applicability rules, and owners govern the initial dashboard measures? | Prevents misleading readiness and completeness indicators | Product/process owners before showcase validation |
| OQ-018 | Which details in the accepted FMS Version 3.3 fictional story need correction or richer realism before the showcase build? | Keeps the reusable prototype data credible without using sensitive real program data | Product owner before showcase build |

## Open Questions for Later Phases

- What program-defined feedback workflow applies to derived HLRs and LLRs?
- Which architecture, source, Git, build, and release references provide useful traceability without expanding into code management?
- What complete PR classification, lifecycle, field set, and closure rules are required?
- Which external identity, test, document, or issue systems need integration?
- Whether standards-plan management or compliance-objective mapping should ever enter product scope.
- Whether local AI assistance provides sufficient value after the controlled domain model is proven.
