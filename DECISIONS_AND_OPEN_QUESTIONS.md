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

### DEC-050 - Suspect Coverage Is Not Coverage, and Materialization Owns Carry-Forward

- **Date:** 2026-07-25
- **Status:** Accepted
- **Decision:** Coverage is carried forward onto a new requirement revision at **materialization**, marked
  suspect whenever the requirement changed under the procedure. Suspect links do not count toward the
  `coverage` release-readiness gate. Release reconciliation no longer creates coverage links; it reports how
  many revisions carry suspect coverage and how many still need confirmed coverage.
- **Rationale:** Reconciliation had been carrying coverage forward since it was written, leaving the new
  links unmarked. That silently asserted the thing nobody had said — that a procedure written against the
  previous wording still verifies the new one — and the readiness gate then counted it as verified. Two
  carry-forward mechanisms would have been worse than one, so the unmarked one was removed rather than
  duplicated. Materialization is the correct owner because it is the only place that knows both the prior
  and the new revision of a changed requirement.
- **Consequences:** A modified requirement whose procedure has not been reconfirmed now holds both the
  `coverage` and `verification_impact` gates, which is the intended behaviour and a stricter release bar
  than before. `ReleaseReconciliationResult.CoverageLinksCreated` is replaced by `SuspectCoverage`; the
  release workbench reports the new wording. Existing baselines materialized before this change keep
  whatever links they already had — nothing retroactively marks historical coverage suspect.

### DEC-051 - Identity Federation Is Deferred Until an Organization Commits to Deploying It

- **Date:** 2026-07-24 (recorded here 2026-07-26)
- **Status:** Accepted
- **Decision:** The remainder of Workstream 4 — OIDC and SAML sign-in, SCIM, break-glass, step-up, account
  recovery, administrator session inventory, provider health and the identity administration UI — is
  deferred, not in progress and not scheduled. Pull request #53 stays a draft. The full record, with the
  reasoning, the resume trigger and the order to resume in, is the *Deferred scope — decision record* in
  [AEROLINK_3_ENTERPRISE_LIFECYCLE_COMPLETION.md](AEROLINK_3_ENTERPRISE_LIFECYCLE_COMPLETION.md); this entry
  does not restate it.
- **Rationale:** No organization yet signs in to AeroLink with a corporate directory, so federation has no
  user, and most of the deferred list exists only to serve it. Security-critical code maintained against no
  real usage is how such code silently rots.
- **Consequences:** Issue #34 stays open as the tracking record and must not be closed as though the gate
  were met. The trigger to resume is the first commitment to deploy AeroLink for an organization
  authenticating against its own directory; that commitment has not been made.
- **Why this entry exists at all:** the deferral was recorded only inside the workstream contract, so it had
  no `DEC-nnn` to cite — and `DEMO_BRIEF.md` cited DEC-046, which is the retirement of the concept showcase
  and says nothing about identity. The demonstration brief is the script for answering the software quality
  group on exactly this question, and it pointed them at the wrong record. This project's own convention is
  that decisions carry stable identifiers; the worked example of a deferral had not followed it.

> **2026-08-01 disposition:** PR #53 was closed unmerged and issue #34 was closed as not planned for the MVP.
> Local identity administration has since delivered explicit role revocation, session inventory/revocation,
> and delegation lifecycle controls. The external-directory resume trigger above remains unchanged; start a new
> implementation from current `main` only after a real provider/customer contract exists.

### DEC-052 - The API Serves the Built Client

- **Date:** 2026-07-26
- **Status:** Accepted
- **Decision:** When given a built client through `Client:StaticFiles` or a published `wwwroot`, the API
  serves it: static files, immutable caching for content-hashed assets, and a fallback to the entry document
  so a deep link reloads. One process, one port, one origin. Where no client is supplied the API behaves
  exactly as before, so a deployment serving the client through its own reverse proxy is unaffected. The
  repository's `product/client/dist` is deliberately never discovered automatically.
- **Rationale:** Nothing in the repository had ever served the built client. Both launchers and every gate
  ran the Vite development server, so the production bundle was compiled on every pull request and never
  rendered in a browser on any platform — while `DEMO_BRIEF.md` named a dry run from a production build as
  the one preparation that could not be skipped. Serving from the API is also the correct on-premises shape:
  no CORS policy joining two servers, one place to terminate TLS, one service to supervise.
- **Consequences:** A document and an API need opposite content security policies, and the API's
  `default-src 'none'; sandbox` applied to a document serves a blank page. Both policies are now explicit in
  `ClientHosting`, and the document policy's `'self'` throughout makes [DEC-047](#dec-047---the-client-has-no-external-runtime-dependency)
  something the browser enforces rather than something people remember. `START_AEROLINK_PRODUCTION.bat` is
  the demonstration path; `START_AEROLINK.bat` remains the development one. Auto-discovering `client/dist`
  was rejected because an ordinary `dotnet run` would then serve whatever build was left in the working
  tree, and a stale bundle served silently is worse than none.

### DEC-053 - Sharing on the Local Network Is Opt-In, and Moves Two Settings Together

- **Date:** 2026-07-27
- **Status:** Accepted
- **Decision:** `START_AEROLINK_SHARED.bat` runs the production launcher with `-Shared`, which binds Kestrel
  to `0.0.0.0:5080` **and** sets `AllowedHosts` to `*` for that run, then prints the machine's own network
  address to hand out. Without the switch the launcher binds loopback and leaves `AllowedHosts` at
  `localhost;127.0.0.1`, exactly as before. Nothing in `appsettings.json` changes, so a deployment is
  unaffected either way.
- **Rationale:** The obvious change is the binding, and on its own it does not work. ASP.NET Core's host
  filtering compares the `Host` header against `AllowedHosts`, so a colleague typing this machine's address
  reaches a socket that accepted their connection and receives a bare HTTP 400 with no body — a symptom that
  reads as a binding fault and is not one. Coupling the two in one switch means the failure cannot be
  reproduced by getting half of it right. Opt-in rather than default because the same run prints a known
  administrator password, seeds demonstration accounts and data, and carries sessions over plain HTTP; a
  launcher somebody double-clicks by habit should not put that on an office network unasked.
- **Consequences:** Windows Firewall is a third gate and deliberately not automated — the launcher checks for
  an enabled inbound allow rule and prints the `New-NetFirewallRule` command when there is none, because
  editing a firewall rule is the machine owner's decision. The development launcher is not shareable and will
  not be: two ports, a CORS policy joining them, and a bundle that rebuilds mid-demonstration. Verified in
  both modes by sending a foreign `Host` header over loopback — 200 shared, 400 default — rather than by
  checking only that the port was open, which is the check that would have passed while the feature was
  broken. Sharing over plain HTTP remains a demonstration convenience and is not the TLS story owed by
  [DEC-052](#dec-052---the-api-serves-the-built-client) and `SECURITY_AND_IDENTITY_MODEL.md`.

### DEC-054 - Approved and Allocated Are One Fact, and a Released Build Freezes It

- **Date:** 2026-07-27
- **Status:** Accepted
- **Decision:** A change request can be revised from `Approved` **or** `SelectedForBaseline`, and from neither
  once its target build has been released. `StartNextRevision` takes the target release's released state as an
  argument so the rule stays inside the aggregate; the endpoint reads that fact and passes it in.
- **Rationale:** Gating the action on exactly `Approved` reads correctly in the enum and was unreachable in the
  product. Allocating an approved change request to a candidate baseline moves it to `SelectedForBaseline`, and
  there it stays — across the demonstration programme's 113 change requests, **not one** was in `Approved` and
  107 were `SelectedForBaseline`. The Revise action was therefore correct code that could never appear on any
  record anybody opened, reported twice as missing. To the person asking, both states say the same thing: the
  engineering is signed for. What must not happen is revising a change request already incorporated in a
  released build, because a `.02` of it would claim the release said something it never said.
- **Consequences:** The state a gate admits must be a state the product rests in, which is a question about
  transitions and not about the enum — so the tests are written against `MarkSelectedForBaseline` rather than
  against `ScrState`. The refusal is enforced in the domain and asserted at the endpoint, not only in the
  browser, because a UI-only gate would have let the released case through to an unexplained 400. The wording
  of a change request's state now comes from `changeRequestStateLabel` on the record's own page as well as in
  the lists, closing the half of [DEC-052](#dec-052---the-api-serves-the-built-client)-era work that reached
  the list and missed the detail rail.

### DEC-055 - A Released Build Takes No New Change Requests

- **Date:** 2026-07-27
- **Status:** Accepted
- **Decision:** Both change request creation endpoints refuse a target build that has been released, naming the
  build and where to raise the change instead. The client shows that explanation in place of the editor, with a
  one-press switch to the in-work build. The navigation action stays visible rather than disappearing.
- **Rationale:** `retarget` has always refused to *move* a change request onto a released build. Nothing stopped
  one being *created* there, so the product offered an action whose result was a record with no future: it could
  not reach a baseline, be incorporated, or be revised, because none of those are things a build that has
  shipped will do again. It is also the most likely mechanism behind a report of a draft that saved and then
  could not be found — created while the released build was selected, it was allocated to that build while the
  list being searched was filtered to the in-work one, so the record existed, was correct, and was nowhere the
  author looked.
- **Consequences:** Enforced server-side on both endpoints, which are separate code paths, and asserted there
  rather than only in the browser — the panel is a courtesy, not the rule. The action is not hidden when the
  build is closed, because somebody looking for how to raise a change needs to be told where to raise it, and a
  menu item that vanishes when you switch build teaches nothing. The refusal names the version, since "bad
  request" would leave an author auditing a dozen fields none of which were wrong.

### DEC-056 - Allocation and State Are Two Questions, Answered Separately

- **Date:** 2026-07-27
- **Status:** Accepted
- **Decision:** A change request reports an **allocation** — the build it is going into, or `Deferred` — and a
  **state** — Draft, In review, Approved, Incorporated, or Superseded. Deferring records the state the work had
  reached in a new `DeferredFromState` column, and `Reinstate` puts it back there; a change request deferred
  mid-review returns as a Draft because deferral cancels the cycle. `Incorporated` and `Superseded` stay derived:
  the first from whether the target build has been released, the second from a later revision existing. Listings
  collapse to each change request's newest revision, with the count of superseded revisions on the row and
  `baseNumber` to expand them.
- **Rationale:** `ScrState` was carrying both facts and two of its five values were really allocations. A reader
  asking "which build is this going into" or "how far has it got" got a single word that half answered the other,
  and "Selected for baseline" answered neither. Storing only `Deferred` also lost the difference between a
  signed-off change put away and an unwritten one — a shelf that cannot tell those apart is a shelf nobody can
  plan from. The two derived values are derived on purpose: each is read from the thing that makes it true, so
  neither can drift, and neither needs a transition somebody has to remember to perform.
- **Consequences:** `Defer` had existed in the domain since the states were reworked and **nothing exposed it** —
  the dashboard counted deferred change requests and the history explorer filtered for them while the only way
  one could exist was the demonstration seeder. The endpoints and the buttons are part of this change; a shelf
  that is visible and unreachable is the same defect class as
  [DEC-054](#dec-054---approved-and-allocated-are-one-fact-and-a-released-build-freezes-it)'s Revise gate.
  Collapsing revisions without a way to expand them would be hiding the record, so nothing is hidden: the row
  states what is behind it. Superseded is shown ahead of the stored state because reading "Approved" against a
  revision a later one has replaced invites somebody to work from stale content.
### DEC-057 - Where a Requirement Goes Is Part of What Is Proposed

- **Date:** 2026-07-27
- **Status:** Accepted
- **Decision:** A requirement change carries an optional `TargetSectionId`. Materialization places an introduced
  requirement in that section, and moves a modified one if it has changed; null changes nothing, so the existing
  placement rule still decides for anything that does not name a section. The picker offers only the sections
  belonging to the specification for that requirement's level, and a requirement chosen for modification arrives
  with the section it is already in.
- **Rationale:** Section membership existed only as a `SpecificationNode` row created after the fact, by a
  backfill that derives a section from a hash of the requirement's number. So a change request could say what a
  requirement means and not where it goes — half of what an author is deciding — and the requirements explorer
  had section filtering that no authoring path could ever aim. Applied at materialization because that is the
  first moment the requirement exists to be placed.
- **Consequences:** Null-means-unchanged keeps this additive: no proposal has to name a section to be valid, which
  matters because a proposal is worth saving before every field is settled. A section belonging to another
  project's specification is ignored rather than acted on, since a stale identifier from a copied draft would
  otherwise file a requirement into an unrelated document and look deliberate afterwards. Check-in rewrites every
  proposal from its draft, so the section had to be carried through `ProposalDraft` explicitly — without that,
  saving an unrelated edit would have silently erased the chosen section of every proposal in the change request.

### DEC-058 - A Test Procedure Has No Shelf of Its Own

- **Date:** 2026-07-27
- **Status:** Accepted
- **Decision:** Deferral applies to change requests only. Test procedures get no deferred state. A requirement
  that is new or modified in the build being worked on is assumed to need verification coverage, so the
  procedures covering it cannot be put away while it ships.
- **Rationale:** Systems, software and test were asked for as three separate shelves when
  [DEC-056](#dec-056---allocation-and-state-are-two-questions-answered-separately) was scoped, and the third one
  is a different thing from the first two. A change request is a proposal, and a programme may decide not to
  make it — that is what a shelf is for. A test procedure is not a proposal; it is how a requirement that *is*
  shipping gets verified. Deferring one does not postpone work, it removes coverage from a requirement that is
  still in the build, and the tool would be recording that as an ordinary planning act. The deferral that
  matters happens one level up: shelve the change request and its verification work goes with it, because
  verification already follows its change request through retargeting and materialization.
- **Consequences:** Nothing is built, and the absence is deliberate rather than pending. If a programme ever
  needs to ship a requirement while explicitly accepting reduced coverage, that is a coverage exception with an
  accountable approver — a different record with a different meaning — and not a procedure quietly moved to a
  shelf. Revisit only against that need, not as leftover scope.

### DEC-059 - Computed Impact Informs the Declared Disposition, and Never Replaces It

- **Date:** 2026-07-28
- **Status:** Accepted
- **Decision:** A requirement proposal shows what the traceability graph records for the requirement it changes
  — the requirements derived from it, and the procedures that verify it — beside the five impact dispositions.
  The panel is read-only, writes no disposition, and closes no readiness gate. An introduced requirement has
  nothing downstream, so nothing is shown for one, and an unknown identifier answers "nothing recorded" rather
  than failing.
- **Rationale:** The product asked authors to decide whether trace relationships and verification coverage were
  affected, and asked it from memory: the links that answer both were recorded, and reachable only from the
  requirements explorer, a page away from the person deciding. Showing them where the decision is made means the
  author dispositions something concrete instead of an empty category. This closes the impact-disposition
  question raised in `PRODUCT_REVIEW_2026_07_26.md`, which had been parked because the two ideas behind it —
  computed impact and declared disposition — needed separating before either could be built.
- **Consequences:** The distinction is the whole point and is enforced by test: reading the traces must leave
  every disposition Pending. "The tool found no links" and "an engineer confirmed there is no impact" are
  different claims, and only the second is signed for and frozen into the review snapshot. A change request with
  nothing downstream therefore still requires its author to say so. Keyed by base number rather than artifact
  identifier, because that is the identity a proposal carries before materialization gives it anything else.

### DEC-060 - The Repository Is Public

- **Date:** 2026-07-28
- **Status:** Accepted
- **Supersedes the open question in:** [DEC-011](#dec-011---git-repository-initialized-locally-before-github-connection),
  which deferred publication to "a later explicit repository decision". This is that decision.
- **Decision:** `seanmccarthyns/requirements-management-tool` is public.
- **Rationale:** GitHub Actions is unmetered on public repositories, and the private-repository bill had reached
  94% of its cap with a hard stop configured — CI would have stopped mid-week. Every other saving available was
  a trade of coverage for money; this one was not a trade.
- **Consequences:** The full commit history, the demonstration dataset, this decision log and its record of
  mistakes are all readable. Checked before publishing rather than after: no tokens, keys or private keys appear
  in any commit, `node_modules` was never committed, and the only credentials in the tree are the demonstration
  password the launcher prints on screen and two throwaway CI values. Publication is one-way in practice — clones
  and caches survive a later switch back — so anything genuinely sensitive must never be committed rather than
  removed afterwards. Pull requests from forks require workflow approval, which does not affect branches pushed
  to this repository directly.

### DEC-061 - Browser Mutations Are Session-Bound and Qualified Against the Built Client

- **Date:** 2026-07-28
- **Status:** Accepted
- **Decision:** The browser resolves relative and absolute API request addresses against a canonical origin
  before applying mutation protection. A successful sign-in establishes the CSRF token for that new session
  before protected controls become actionable; logout, session expiry and credential rotation discard cached
  mutation state. Only the server's explicit antiforgery rejection may cause one automatic retry, because that
  rejection proves the controlled operation did not run. Arbitrary failures are never retried.
- **Rationale:** The compiled single-origin client used relative API addresses, while its fetch wrapper tried to
  use each relative address as a URL base. Every protected production mutation therefore threw before reaching
  the server, and the production gate stayed green because it performed reads only. A token cached before
  sign-in could also survive into the new session and deterministically fail the first protected action.
- **Consequences:** The compiled-production gate creates a real System SCR and records an immutable verification
  result, then queries the API to prove both durable outcomes. Mutation surfaces use one error-envelope contract:
  non-JSON, empty, authorization, conflict and network failures remain visible, release busy state, preserve safe
  input, and never display success until the server confirms it. Client failure diagnostics record only an
  operation identifier and transport status/code, never credentials, request bodies or controlled content.

### DEC-062 - Requirement Proposal Metadata Is a Server-Enforced Lifecycle Contract

- **Date:** 2026-07-28
- **Status:** Accepted
- **Decision:** Requirement proposal attributes are validated against the active schema for their exact level.
  Authored fields survive unchanged; `derived` is server-owned and is recomputed rather than trusted. The five
  canonical impact categories are mandatory and must contain one of `Affected`, `Not Affected`, or
  `Follow-up Assigned` before review. A chosen specification section is part of the proposal and must survive
  create, detail, checkout, autosave, check-in, review snapshot and materialization.
- **Rationale:** Initial creation replaced every authored attribute with `{ derived }`, reopening a Draft omitted
  `TargetSectionId`, and the domain treated `{}` impact JSON as a completed decision. Each defect made the
  browser appear to capture controlled intent while the durable record said something else; direct API calls
  could then advance that incomplete record.
- **Consequences:** Malformed, unknown and schema-invalid attributes fail with an actionable error. Incomplete,
  malformed and unknown impact dispositions fail at submission, baseline selection, freeze and materialization.
  Stale section identifiers fail closed while naming the required author action. Operators can list historical
  attribute gaps without rewriting evidence, and integrity checkpoints report legacy impact-disposition
  violations. Approved history is never auto-filled: repair occurs through a Draft checkout or a controlled
  successor revision.

### DEC-063 - Change-Request Type and Review Principal Are Frozen Context

- **Date:** 2026-07-28
- **Status:** Accepted
- **Decision:** Every change-request detail URL encodes `systems` or `software`; generic legacy links are
  accepted only long enough to load the authorized record and are then replaced with its canonical typed URL.
  Review steps retain the selected account name and resolved Program authority as frozen fields. Presentation
  uses those fields directly and records the actual signing principal separately in electronic-signature
  evidence.
- **Rationale:** A generic route made an SWCR appear inside System navigation, and the showcase people registry
  replaced `systems.reviewer` with the System author even though authorization and the signature used the
  reviewer account. Both defects let surrounding presentation contradict the controlled record.
- **Consequences:** Refresh, new-tab, history, search, My Work, notification and Jira entry paths keep the
  discipline revealed by the record. A caller-supplied type mismatch is canonicalized before actions are
  offered. Reviewer name, authority, active assignment, audit actor and signature are now traceable to stable
  principals; missing legacy authority is displayed as unresolved rather than inferred from another person.

### DEC-064 - Mutation Attribution Comes Only from Authenticated Context

- **Date:** 2026-07-28
- **Status:** Accepted
- **Decision:** Authenticated browser mutation contracts do not accept actor, author, owner, recorder, or
  executor identity. Human attribution is resolved from the authenticated session; service attribution is
  resolved from the authenticated service principal. Legacy JSON identity properties are ignored during the
  compatibility interval and can never alter stored provenance. Standard operations diagnostics are anonymous
  and session-free; an optional authenticated probe uses a separately issued `integrations:read` service key.
- **Rationale:** The handlers already used the server session, but public DTOs and clients continued to send
  values such as `server-derived`, role-like usernames, and the visible user name. Those misleading fields made
  spoofable attribution look supported and created a future risk that a handler would accidentally trust one.
  Diagnostics also logged in using a committed demonstration password, coupling health evidence to a human
  account and leaving a new administrator session behind after every run.
- **Consequences:** OpenAPI-visible request shapes describe the actual trust boundary, client forms contain no
  hidden identity placeholders, and compatibility tests prove submitted legacy identities cannot change
  authorship. Historical provenance is untouched. Diagnostics distinguish liveness, readiness, authentication
  capability, migration posture, backup recency, and storage without changing session state or exposing secrets.

### DEC-065 - Administrator Recovery Authority Does Not Transfer Authorship

- **Date:** 2026-07-28
- **Status:** Accepted
- **Decision:** An authenticated administrator with access to the governed Project may perform the same
  author-owned recovery actions as the original change-request author: controlled checkout/check-in,
  supporting-file attachment while Draft, proposal completion, review submission or restart, defer/reinstate,
  retargeting, and successor-revision creation. The server derives that authority from the authenticated
  principal; request-body identity values cannot grant it. Every resulting audit event, attachment and
  check-in record identifies the actual administrator, while the immutable `AuthorId` continues to identify
  the original author.
- **Rationale:** The browser offered administrator recovery controls, but several domain guards and adapters
  still required the administrator's account to equal `AuthorId`. That made the controls fail and encouraged
  callers to impersonate the author. Transferring authorship would repair the UI at the cost of false controlled
  history.
- **Consequences:** Author, administrator and unrelated-engineer behavior uses one server-owned rule for System
  and Software changes. Project access is checked before authority, non-administrators cannot gain author powers
  by submitting identity fields, and administrators do not bypass Draft, review, approval, release or optimistic
  concurrency rules. Recovery remains operationally possible and forensically attributable without rewriting
  ownership.

### DEC-066 - Test Procedures Bind Only to Materialized Requirement Revisions

- **Date:** 2026-07-28
- **Status:** Accepted
- **Decision:** A test procedure may be authored for a release only after its candidate requirement baseline is
  frozen and materialized. That is the first point at which the exact immutable requirement-revision IDs exist.
  AeroLink will not silently bind a procedure to a predecessor revision, provisionally bind proposed content,
  or later repoint either form as though it had always covered the materialized revision.
- **Rationale:** Predecessor and provisional authoring can be useful future workflows, but each needs an explicit
  applicability state and an attributable carry-forward decision. Treating either as confirmed coverage would
  let evidence written for different content satisfy a release gate. The existing exact-revision architecture
  already avoids that ambiguity; its UI merely presented the prerequisite as an empty, broken form.
- **Consequences:** Before materialization, procedure creation is disabled with the exact reason and next
  governed step. Existing inherited procedures remain visible against the predecessor revisions they actually
  cover, and verification-impact items retain planned work without counting it as confirmed coverage. After
  materialization, the new exact revisions become selectable. Traceability, coverage, verification, and evidence
  gates report `WaitingForPrerequisite` rather than misleading `0/0` failures until that point. A materialized
  baseline with no effective requirements is an evaluated invalid release population, not a waiting state, and
  remains on HOLD with a repair instruction. System, Software, and product-line configurations use the same rule.

### DEC-067 - Settled Coverage Has One Definition, and Every Surface Reads It

- **Date:** 2026-07-28
- **Status:** Accepted
- **Extends:** [DEC-050](#dec-050---suspect-coverage-is-not-coverage-and-materialization-owns-carry-forward),
  which established that suspect coverage is not coverage. This says where that judgement lives.
- **Decision:** A requirement revision is **Covered** only when a coverage link is not suspect, names a
  procedure revision that is Approved, and that procedure has no other revision still in draft or review. It
  is **Suspect** when a coverage link exists and does not meet that test, and **Uncovered** when no coverage
  link exists at all. The three states are mutually exclusive and exhaustive. The predicate lives in
  `VerificationCoverageProjection` and the release readiness gate, the requirements-workspace filter and the
  trace panel all read it from there.
- **Rationale:** The three-part test already existed inside the release readiness gate, computed in memory
  over a baseline's members. The requirements workspace had no coverage filter at all, so the only way to
  ask "which requirements need verification attention?" was to read the readiness counts, which appear far
  too late to act on. Adding a filter that tested only the suspect flag would have been the cheap
  implementation and would have given the product two answers to one question — the workspace calling a
  requirement covered while the gate refused to count it. This project has paid for that shape repeatedly.
  The trace panel was already the third answer: it counted "confirmed tests" from the raw flag, so a
  requirement could show one confirmed test beside a row labelled Suspect, both describing the same link.
- **Consequences:** `IsSuspect` keeps its meaning as the stored carry-forward fact; `CoverageState` is the
  computed judgement and is no longer derived from the flag alone. A procedure being rewritten therefore
  stops settling coverage everywhere at once, which is a behaviour change on the trace panel and in the
  verification workspace, not only in the new filter. The workspace filter is a composable database query,
  not an in-memory pass, because it runs against fifty thousand requirements. A test asserts the workspace
  and the gate return the same set, so a future second implementation fails rather than diverging quietly.

### DEC-068 - The Showcase Demonstrates a Suspect Gap, and Deliberately Not an Uncovered One

- **Date:** 2026-07-28
- **Status:** Accepted
- **Decision:** Fresh showcase data contains exactly one verification gap: an approved System procedure
  (`SYSTP-000040`) put back into revision as FMS 1.6 work, which makes the two requirements it covers
  **Suspect**. No **Uncovered** requirement is seeded.
- **Rationale:** Every one of the showcase's 1,250 requirements was covered, so the product could never be
  shown discovering the thing it exists to discover. Reaching an Uncovered requirement, however, needs one
  of two untruths. Removing coverage from a released FMS 1.5 requirement produces a released baseline that
  failed its own coverage gate, which is worse than a missing demonstration state. Materializing the FMS 1.6
  baseline discards the `WaitingForPrerequisite` lifecycle position that
  [DEC-066](#dec-066---test-procedures-bind-only-to-materialized-requirement-revisions) exists to show. The
  in-work requirements that will need coverage are already visible as verification-impact items, so the
  workflow is represented; only the workspace state is not.
- **Consequences:** Uncovered is reachable in a demonstration the moment somebody materializes FMS 1.6,
  which is a governed action the product already offers, and that is the honest way to show it — the tool
  creating the gap rather than the fixture asserting one. The seeded procedure is deliberately not
  `SYSTP-000001`: procedures are dealt requirements round-robin, so `SYSTP-000001` covers `SYSR-000001` and
  is the first approved procedure any test searching for one finds. Seeding the gap there removed it from
  the covering-procedure list and broke an unrelated journey. A fixture that changes what other journeys
  discover is not an isolated fixture. Seeding stays idempotent and applies to databases seeded before this
  existed, so an upgrade reconciles rather than duplicating.

### DEC-069 - Controlled Numbers Come From a Sequence, and Gaps Are the Accepted Cost

- **Date:** 2026-07-29
- **Status:** Accepted
- **Decision:** Every controlled identifier (`SCR`, `SWCR`, `SYSR`, `HLR`, `LLR`, `SYSTP`, `HLRTP`, `LLRTP`,
  `PR`) and every controlled attachment version is claimed by a single atomic increment against a row in
  `identifier_sequences`, keyed by prefix — repository-wide, not per Program or Project. Attachment versions
  use one sequence per logical file. A number is spent when it is handed out, so an abandoned or rolled-back
  create leaves a permanent gap.
- **Rationale:** Numbers were derived by loading every identifier sharing a prefix, taking the maximum in
  application memory and adding one, with a unique index as the only backstop. Two overlapping creates read
  the same maximum and chose the same number, so one of them failed and a person had to resubmit work the
  product had already accepted; the same applied to two uploads of one logical file, where the losing upload
  failed after its bytes were already stored. The cost also grew with the identifier set on every single
  create. Scope is the prefix because that is what the existing unique indexes on the base numbers have
  always enforced — a per-Project scope would need those indexes relaxed and would change what an identifier
  means.
- **Consequences:** Contiguity is not a property of the numbering and nothing in the product infers meaning
  from it. Reusing a number that a failed attempt may already have displayed, exported or been referenced by
  is the worse failure, so gaps are correct rather than tolerated. A sequence row is created on first use
  from the highest identifier already recorded, which lets an existing database adopt this without a data
  migration that has to know every prefix in use; if two writers seed the same prefix at once, the unique
  index on `Scope` settles it and both then claim from the surviving row. The claim commits on its own
  connection statement rather than as part of the caller's save, which is what makes the number spent at the
  moment it is issued rather than at commit.

### DEC-070 - Project and Build Selection Establish the Workspace Context

- **Date:** 2026-07-29
- **Status:** Accepted
- **Decision:** Authentication opens a context-free Projects page. Selecting **FMS Product Development**
  opens its Software Builds lineage, and selecting an accessible build establishes the project and build in
  the canonical route. Build 1.5 is a released, read-only historical workspace; Build 1.6 is the in-work
  development workspace. Build 0.5 and 1.0 remain visible lineage but are inaccessible. There is no build
  switcher inside a workspace: changing build requires **Back to Software Builds** and an explicit new
  selection.
- **Rationale:** Entering the FMS workspace immediately after login made the project and release implicit and
  allowed one shared screen to appear as though it represented every build. A route-owned context survives
  refresh and deep links, lets every query and mutation validate the same build, and prevents a casual selector
  from silently replacing the data beneath an engineer.
- **Consequences:** Primary requirements, changes, traceability, verification, search, reports, exports and
  navigation counts are scoped to the selected build. Released Build 1.5 exposes exploration but rejects
  mutation at both UI and server/data boundaries; because it is already released, its Command Center reports
  that fact and never displays a completion percentage. Build 1.6 may show clearly labelled read-only evidence
  from Build 1.5 without changing the active context. Logout and workspace exit clear or replace the context.
  Canonical routes and the complete contract are summarized in
  [CURRENT_PRODUCT_HANDOFF_2026-07-29.md](CURRENT_PRODUCT_HANDOFF_2026-07-29.md).

### DEC-071 - Change Authors Describe the Change; Consuming Engineers Decide Downstream Impact

- **Date:** 2026-07-29
- **Status:** Accepted
- **Supersedes:** [DEC-059](#dec-059---computed-impact-informs-the-declared-disposition-and-never-replaces-it)
  and only the impact-disposition requirements and consequences in
  [DEC-062](#dec-062---requirement-proposal-metadata-is-a-server-enforced-lifecycle-contract).
- **Decision:** A change-request author records the change and may inspect a read-only live trace of known
  downstream relationships. The author does not disposition trace relationships, verification coverage,
  controlled documents, baselines/builds, collaboration, or lifecycle impact. The engineers who consume and
  triage the change determine the actual downstream response in their governed workspaces.
- **Rationale:** Asking the author to decide every downstream consequence confuses proposal intent with the
  specialist assessment that follows it. The trace graph is useful context, but a link does not tell an author
  whether a procedure, document or baseline must change. Requiring those answers also made browser fields into
  lifecycle gates that blocked otherwise useful Drafts and encouraged unsupported guesses.
- **Consequences:** Author impact selectors and the lifecycle-impact summary are absent. Review submission,
  baseline selection/freeze/materialization and integrity checkpoints do not require the former five
  dispositions. Existing stored disposition data remains historical and is not rewritten. The read-only trace
  does not change active build context or close a readiness gate. A Draft may be incomplete; a title is the only
  prerequisite to save because the title prevents an empty form from consuming a controlled number. System
  Change Requests open directly from the System area; Software authoring first asks whether the change targets
  HLR or LLR.

### DEC-072 - The Active Product Surface Favors the Current Engineering Story

- **Date:** 2026-07-29
- **Status:** Accepted
- **Supersession:** DEC-085 reactivates Problem Reports only. Product Versions, Candidate Baselines, and the
  Change Request Software Builds view remain hidden.
- **Decision:** Keep System and Software as distinct areas, with Verification as the third Command Center
  concern. Hide Problem Reports, Product Versions, Candidate Baselines, and the Change Request page's Software
  Builds view from navigation, search and direct UI routing for now. Preserve their domain/API/client
  implementation as dormant code unless a later decision removes or reuses it.
- **Rationale:** These surfaces added competing concepts to the demonstration before their product roles were
  settled. Hiding them creates a coherent path—project, build, System/Software change, requirements and
  verification—without paying the irreversible cost of deleting working lifecycle primitives that may inform
  a later design.
- **Consequences:** The Command Center is a three-way System/Software/Verification summary. It omits the old
  requirement-count banner, release-attention rail and change-request-flow visualization. A requirement
  Overview omits the Controlled Revision, Digital Thread and empty-discussion banners; Trace & Impact, History
  and Discussion remain the focused places for that information. New Change Request actions live on the
  applicable Change Requests page rather than as a permanent sidebar button. Lifecycle Decision Room remains
  available. The broader Verification redesign is intentionally deferred; current verification behavior is
  simplified only where required by DEC-071 and the three-way Command Center.

### DEC-073 - One Official Software-Build Identity and Governed Test Change Reviews

- **Date:** 2026-07-29
- **Status:** Accepted; the pre-release evidence flag is superseded by DEC-076 and the workspace shape by DEC-077
- **Decision:** A baseline and software build are one product concept. The official identifier derives from the
  release version (`1.6` becomes `SW-01.60`); “Build 1.6” is informal wording. SCR/SWCR identifiers use five
  digits for existing and future records. Every approved change request creates one controlled Test Change
  Review per affected System, Software HLR, or Software LLR discipline.
- **Rationale:** Separate baseline/build names implied two configurations where the product owner intends one.
  Verification procedure maintenance is specialist downstream work, not author impact disposition. Treating
  each approved change as a governed discipline-specific review creates a clear handoff and release gate.
- **Consequences:** Existing SCR/SWCR and FMS software-build identifiers are destructively normalized by
  migration. Each Test Change Review records create/link/modify/retire/no-test decisions and requires complete
  decisions plus independent approval. Procedure alignment is required for release. Only procedures explicitly
  marked during that review require passing evidence before release; other evidence may be captured after
  release. A failure remains attached to the tested released build, and software-caused correction occurs in a
  later build rather than mutating the released one. The verification workspace exposes the active official
  software-build identity without a build selector.

### DEC-074 - Controlled Test Change Requests, Raised Automatically and by Hand

- **Date:** 2026-07-30
- **Status:** Accepted
- **Decision:** A Test Change Review is a controlled record — a Test Change Request — with its own number and
  revisions. One may cover more than one requirement change request, and an engineer may raise one deliberately
  rather than waiting for a change approval to raise it. Claiming a package claims the whole of it.
- **Rationale:** Two change requests whose requirement changes are best tested together should be one piece of
  test work, and a package half-assigned has no owner anybody can name. Some test work is worth doing before a
  change approval exists.
- **Consequences:** Numbers are claimed atomically from a per-prefix sequence, like every other controlled
  identifier. The queue on Testing Coverage is where a package is picked up or started.

### DEC-075 - Sixteen Named Program Roles

- **Date:** 2026-07-30
- **Status:** Accepted
- **Decision:** The Program role vocabulary names system, software and test engineering leads, project
  engineering lead, program manager, engineering manager, system engineer, software engineer, test engineer,
  software quality analyst and airworthiness, alongside the existing control roles. A precise job title never
  removes capability: a role that implies another satisfies it (`ProgramRoleAuthority.Satisfying`).
- **Rationale:** The product owner wanted the roles identified before their authority is tuned. Naming a
  person's actual job is what makes an assignment readable; making the specific role a superset of the general
  one is what stops naming it taking capability away.
- **Consequences:** Authority checks accept any satisfying role. Which role may do what remains open and is
  expected to change.

### DEC-076 - A Build's Test Scope Is a Set, and the Gates Measure It

- **Date:** 2026-07-30
- **Status:** Accepted
- **Decision:** Each build carries one **Build Test Set** per discipline — a working list, with history, of the
  procedures that build has to run, recording who added each and why (changed requirement, coverage area,
  corrective action, chosen). Release gates measure results against that set. It replaces the per-decision
  "evidence required before release" flag as the thing the gates read.
- **Rationale:** A build is rarely worth its whole test suite, and testing decisions are made from two
  directions at once: what changed, and which areas the change makes worth re-exercising. A flag on an
  individual decision could express neither.
- **Consequences:** An empty set is only an answer when there is no test work at all — with test change reviews
  present, an empty set holds the gate rather than passing it. Choosing the set is a lead's decision (Test Lead
  or Program manager); recording determinations against it is a Test Engineer's.

### DEC-077 - Verification Is Two Pages Per Discipline, Not Four Tabs

- **Date:** 2026-07-31
- **Status:** Accepted
- **Decision:** The tabbed verification workspace is replaced by **Testing Coverage** and **Test Results**, one
  pair per discipline — System, Software HLR, Software LLR. `/system-verification` and `/software-verification`
  become a chooser between them. The software level rides on the existing `artifactKind` rather than on a new
  discipline value.
- **Rationale:** The four tabs answered two questions, and which tab held an answer was something a reader had
  to know before they could ask. HLR and LLR test work is planned, done and approved separately, so it is two
  destinations rather than one page with a switch. Adding discipline values would have meant auditing every
  comparison that silently treats an unrecognised value as System.
- **Consequences:** Everything that lived only in the tabs moved: procedure authoring, independent approval,
  the coverage inventory, procedure filters and paging with their addressable worklist, decision reopening and
  history, run history and retest, and the corrective action a problem report routes to — now on the Test
  Results page, at `…/results/{problemReportId}`. The "evidence required before release" checkbox is gone; the
  server field remains only as one of the inputs that seeds a new test set.

### DEC-078 - Downstream Requirement Impact Is Assessed Before an SWCR Is Created

- **Date:** 2026-07-31
- **Status:** Accepted
- **Decision:** Final approval of a System change raises an HLR downstream change assessment; final approval
  of an HLR change raises an LLR assessment. The consuming engineer may conclude that no downstream change is
  required or link one or more Draft SWCRs. A Draft SWCR may answer multiple assessments, so both one-to-one
  and consolidated delivery remain possible without allocating empty controlled change-request numbers.
- **Rationale:** The author of an upstream change cannot responsibly decide the consuming discipline's impact.
  Creating an SWCR before that engineering conclusion falsely asserts that a downstream requirement change is
  required and wastes a controlled identifier when the correct answer is no change.
- **Consequences:** Assessments are build-scoped, assigned, independently approved by an explicitly selected
  approver, and server-governed. Revising an approved source change creates fresh assessment work and marks the
  earlier assessment and its verification work **Superseded â€” out of date, update required**. Historical rows
  remain readable and attributable but do not satisfy current readiness or appear in active counts.

### DEC-079 - Verification Approvals Name the Reviewer Up Front

- **Date:** 2026-07-31
- **Status:** Accepted
- **Decision:** A test-procedure revision receives an explicitly selected independent approver when it is
  created. A test change request receives one when its completed decisions are submitted. Only that active
  Program approver can approve the exact revision or package.
- **Rationale:** Role eligibility alone does not establish who owns a pending review and produced inconsistent
  behavior between controlled artifacts. Named assignment makes the queue accountable while preserving the
  server-side authority and independence checks.
- **Consequences:** Existing historical records may have no selected approver; all newly authored procedures
  and newly submitted test change requests require one. The server, not merely the button visibility, rejects
  approval by another user or by the author/submitting engineer.

### DEC-080 - Verification Inventory Is Exact by Build and Discipline

- **Date:** 2026-08-01
- **Status:** Accepted
- **Decision:** System, Software HLR, and Software LLR Testing Coverage, procedures, history, search, Test Change
  Requests, and results resolve only against the selected build and discipline. Controlled procedure revision
  numbers are valid deep-link/search identities and closing history restores the scoped inventory.
- **Rationale:** Mixing project-latest or adjacent software-level data made a correct-looking page answer the
  wrong configuration question.
- **Consequences:** API projections, queries, routes, refresh behavior, tests, and reviewer authority preserve
  Project/build/discipline/exact-revision scope end to end.

### DEC-081 - Production Surfaces Show Authoritative Controls, Not Qualification Simulations

- **Date:** 2026-08-01
- **Status:** Accepted
- **Decision:** The production Concurrency simulation and count-only IntegrityScan job are retired. Real
  controlled checkout/conflict records are the concurrency workflow. The supported integrity operation
  recomputes controlled attachment hashes and reports missing, altered, and unreadable content.
- **Rationale:** A production control must change or verify authoritative state. A simulation beside the real
  workflow invites false operational conclusions.
- **Consequences:** Historical simulation/count snapshots remain labelled evidence, but no new production action
  can create them. Unknown background job types fail rather than silently mapping to another operation.

### DEC-082 - Roles, Sessions, and Delegations Have Explicit Lifecycles

- **Date:** 2026-08-01
- **Status:** Accepted
- **Decision:** Program role grants are individually visible and revocable; global system administration is
  distinct from Program Administrator authority; users can identify the current session and revoke other
  sessions; delegations retain active, future, expired, and revoked history with full attribution.
- **Rationale:** Disabling an account is not role administration, and an authority grant that disappears when it
  stops authorizing cannot support audit or incident review.
- **Consequences:** Duplicate grants conflict, revocation does not erase history, expired/revoked delegations do
  not authorize, and every lifecycle mutation is audited.

### DEC-083 - Software Proposals Carry Prospective Exact Upward Allocation

- **Date:** 2026-08-01
- **Status:** Accepted
- **Decision:** An HLR proposal selects one or more current System revisions from the target build; an LLR
  proposal selects current HLR revisions. The only alternative is explicit derived classification with a
  meaningful rationale.
- **Rationale:** Review must see what the proposed software requirement refines. Creating traces only after
  approval left reviewers unable to judge completeness and allowed cross-build or obsolete parents.
- **Consequences:** Exact proposed parent IDs are validated server-side, included in the immutable review hash,
  preserved through controlled editing and CR revisioning, and materialized as revision-to-revision
  `AllocatedFrom` links. Approved/superseded history remains immutable.

### DEC-084 - Current State Comes from the Qualified Repository, Not a Dated Backlog

- **Date:** 2026-08-01
- **Status:** Accepted
- **Decision:** `PROJECT_STATE.md`, the newest dated handoff, current GitHub state, and the qualified merge commit
  are the current-state authorities. Older handoffs, reviews, acceptance notes, Word inputs, and update reports
  are historical evidence.
- **Rationale:** Dated issue inventories and next-step recommendations remained accurate for their day but
  contradicted later merged work when read as live instructions.
- **Consequences:** Historical records receive clear supersession notices rather than rewritten history. New
  work begins from live reproduction and GitHub refresh. Root Word files remain unmodified source inputs.

### DEC-085 - Problem Reports Drive Controlled Change and Changed Requirements Drive Mandatory Tests

- **Date:** 2026-08-01
- **Status:** Accepted
- **Decision:** Reactivate the build-scoped Problem Reports center. A PR may drive an SCR, SWCR, or System/HLR/LLR
  TCR. Requirement changes do not create PRs. Final engineering-change approval is automatically presented as
  an approved corrective action on every linked PR. Containment and preventive-action authoring are outside
  this increment. Every approved procedure covering a requirement introduced or modified in a build is
  mandatory pre-release scope and cannot be removed from that build's test set.
- **Rationale:** The causal thread begins with an observed problem and proceeds through approved correction and
  verification. Reversing it would manufacture problem records for ordinary planned change. Similarly, an
  impacted test that can be unchecked cannot protect release readiness.
- **Consequences:** PR selection is build scoped and validated server-side; automatically raised TCRs inherit
  their source CR's PR links; final approval adds attributable corrective-action evidence; exact procedure
  revisions require a passing build execution with evidence before release. Released builds remain read-only.
  Broader PR classification, lifecycle, and closure policy will be added only as product decisions settle.

### DEC-086 - Primary Navigation Follows Engineering Work and Verification Evidence

- **Date:** 2026-08-02
- **Status:** Accepted
- **Decision:** The primary sidebar groups requirement change, controlled documents, and the Digital Thread
  under **Engineering**. Coverage, results, and verification documents sit under **Verification**. Problem
  Reports remain a standalone operational center. Existing discipline chooser routes remain compatible entry
  points even though they are not duplicate sidebar destinations.
- **Rationale:** Engineers follow a change and its trace consequences, while verification users work from
  coverage and evidence. A standalone PR center preserves the problem-to-correction thread without treating it
  as either a requirement level or a verification subtype.
- **Consequences:** Navigation labels describe user work rather than internal modules. Direct links and browser
  refreshes remain supported, and historical URLs continue to resolve while the visible information
  architecture stays compact.

## Working Assumptions

Assumptions are not decisions. They remain valid only until confirmed or replaced.

- **ASM-001:** Product- and behavior-level definitions precede technical architecture, data schema, and UI design.
- **ASM-002:** Artifact numbers are globally unique across programs; exact prefixes, digit lengths, and revision display syntax are configurable or decided later.
- **ASM-003:** The first slice supports multiple programs even if initial validation uses one reference program.
- **ASM-004:** Requirements may include controlled images/figures as part of revisioned content.
- **ASM-005:** Exact review roles and independence rules vary by organization/program; the SCR author selects the ordered approval sequence and unanimous sequential approval is fixed initial behavior.
- **ASM-006:** Superseded by DEC-085. PRs are controlled first-class records in the product; external issue
  references may be added later without replacing them.
- **ASM-007:** The initial platform records Pass, Fail, Blocked, Not Run, and Not Applicable using the meanings in [SYSTEM_LEVEL_WORKFLOW.md](SYSTEM_LEVEL_WORKFLOW.md); detailed step/result transition rules still require validation.
- **ASM-008:** Source Word files remain unmodified in the repository root during the initial consolidation.
- **ASM-009:** Superseded by DEC-060 and DEC-084. GitHub repository
  `seanmccarthyns/requirements-management-tool` is the shared remote source of truth.
- **ASM-010:** Dashboard values are computed only from records the current user is authorized to know exist.
- **ASM-011:** Fulfilled and superseded by DEC-046. Live demonstrations use the real application and persistent
  `FMSLIVE` data; the static showcase is retired.
- **ASM-012:** Confirmed: the second Version 3.3 change package is an SCR linked to four PRs.

## Historical Open Questions Required Before Phase 1 Technical Planning

The table below is retained as the Phase 0 questionnaire. Phase 1 has been delivered, and several questions
were answered by later decisions and implementation. It is not an active GitHub backlog; unresolved future
choices are created as focused issues only when their trigger and acceptance boundary exist.

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
