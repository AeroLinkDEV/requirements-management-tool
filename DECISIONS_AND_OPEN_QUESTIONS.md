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
- **Decision:** First prove the complete system-level chain from SRCR through requirement, baseline, SYSRD, test procedure, external result/evidence capture, and traceability.
- **Rationale:** A coherent vertical slice validates the hardest lifecycle concepts better than a shallow full-V prototype.
- **Consequences:** HLR, LLR, software change request, and software-test functions are later phases.

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
- **Decision:** A requirement may be retired from future effective baselines through an approved SRCR, while all historical data remains intact.
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
- **Decision:** Preserve the AeroLink dashboard, SRCR review, traceability, and test-evidence mockups as guiding visual and interaction inspiration, subject to validation and refinement rather than pixel-for-pixel implementation.
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
- **Consequences:** The retired requirement remains retrievable through prior baselines, SRCR history, comparison reports, traceability, and audit records, but is omitted from the successor SYSRD body.

### DEC-019 - Verification Outcomes Require Human Judgment

- **Date:** 2026-07-11
- **Status:** Accepted direction
- **Decision:** Pass and Fail are controlled human conclusions about whether an execution successfully verified applicable requirements; Blocked means a valid verification conclusion could not be reached.
- **Rationale:** Test execution data alone does not determine whether the requirement was adequately verified.
- **Consequences:** Outcome records require reviewer attribution and evidence. Blocked is neither Pass nor Fail and requires a reason and disposition.

### DEC-020 - FMS Version 3.3 Is the Showcase Scenario

- **Date:** 2026-07-11
- **Status:** Accepted
- **Decision:** Use an FMS Version 3.2 to 3.3 release story driven by two SRCRs: one introduces a Round Robin function and one incorporates fixes for four linked PRs.
- **Rationale:** This reflects a realistic software-oriented program and exercises change, requirements, verification coverage, problem reports, baselines, dashboards, and traceability in one story.
- **Consequences:** [SHOWCASE_STORY_FMS_3_3.md](SHOWCASE_STORY_FMS_3_3.md) is the canonical fictional dataset and walkthrough. The second-change interpretation remains an assumption until confirmed.

### DEC-021 - Requirements Are Reviewed Through the SRCR

- **Date:** 2026-07-11
- **Status:** Accepted
- **Decision:** Requirements do not enter an independent review/approval workflow. Reviewers evaluate and unanimously approve the exact SRCR revision containing Problem, Analysis, Solution, and all proposed requirement introductions, modifications, and retirements.
- **Rationale:** The SRCR is the controlled change package and provides the context required to judge its requirement changes together.
- **Consequences:** The SRCR author decides when the package is ready to submit; submission validation checks completeness. Requirement revisions authorized by an approved SRCR do not become effective until explicitly selected into a baseline.

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
- **Consequences:** Both system and HLR revisions appear inside the SRCR package and trace through verification and the Version 3.3 baseline. This showcase breadth does not by itself redefine the production implementation sequence.

### DEC-024 - Pre-Approval Rework Keeps the SRCR Revision

- **Date:** 2026-07-11
- **Status:** Accepted
- **Decision:** When an SRCR has never been approved and an approver requests a change, it returns to Draft at the same revision number. Resubmission creates a new review cycle, not a new SRCR revision.
- **Rationale:** The revision has not yet achieved an approved controlled state, so ordinary review rework belongs to the original revision.
- **Consequences:** Every review-cycle submission, comment, decision, and snapshot remains historical. Earlier approvals do not carry into the resubmitted cycle.

### DEC-025 - Post-Approval SRCR Change Creates the Next Revision

- **Date:** 2026-07-11
- **Status:** Accepted
- **Decision:** Any change to an approved SRCR creates the next SRCR revision, even when the associated SYSRD/SWRD or release baseline has not yet been approved or released.
- **Rationale:** The approved SRCR revision is a completed controlled record and cannot be edited in place.
- **Consequences:** The new revision begins in Draft, receives an author-selected ordered approval sequence, and requires unanimous fresh approval. The earlier approved revision remains visible and may be superseded for release selection.

### DEC-026 - SRCR Author Selects the Approval Group

- **Date:** 2026-07-11
- **Status:** Accepted
- **Decision:** The SRCR author has authority to select the people whose approval is required for that SRCR review cycle.
- **Rationale:** The author determines the appropriate approval participants for the content and affected disciplines.
- **Consequences:** Approval requires every selected approver in the author-defined order. DEC-027 defines how that sequence may change after submission.

### DEC-027 - SRCR Review Is Sequential with Controlled Approver Replacement

- **Date:** 2026-07-11
- **Status:** Accepted
- **Decision:** SRCR review proceeds through the author-selected approvers in order. Before a future approver’s turn is reached, the author may replace that approver without restarting completed stages. Active and completed stages are locked. If a completed approval used the wrong person, the review cycle is cancelled and restarted from the first approver.
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
- **Decision:** SRCR and release approvals require password re-entry, assigned-stage identity, Program Approver authority, an explicit signature meaning, and an immutable signature record tied to the controlled snapshot hash.
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

### DEC-033 - Requirement Authoring Remains Subordinate to change request Authority

- **Date:** 2026-07-12
- **Status:** Accepted
- **Decision:** An engineer may discover an approved requirement, analyze its lifecycle impact, and author a proposed next revision in the Enterprise Requirements Workspace, but the proposal must belong to a Draft change request. Only the complete change package is reviewed and approved; approved requirement revisions remain immutable and new effective revisions arise only through baseline materialization.
- **Rationale:** Enterprise-speed authoring must not create a second approval path or weaken the accepted change-authority model.
- **Consequences:** Rich proposal content, Program fields, relationship impact, assignments, and dispositions are included in the exact change request review snapshot and audit story.

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
- **Decision:** The server assigns the next never-reused identifier for every new SRCR, HLRCR, LLRCR, and requirement; derives the author from the authenticated session; and assigns the next requirement revision when an existing requirement is modified or retired.
- **Rationale:** User-entered identifiers, authors, and revision counters invite collision, impersonation, and inconsistent history.
- **Consequences:** Interfaces may preview the reserved format but cannot authoritatively choose these values. Requirement modification begins by searching and selecting an existing controlled requirement. Sequences are installation-wide and independent per artifact prefix.

### DEC-037 - Separate System and Software Change Creation

- **Date:** 2026-07-13
- **Status:** Accepted
- **Decision:** System SRCR creation accepts only System requirement changes. Software change request creation is a separate route and accepts only HLR and LLR changes, including an explicit derived-requirement classification.
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
- **Consequences:** Browser-local recovery may supplement but never replace server-side draft state. The existing edit-session and merge-conflict foundation will be generalized from requirements to change request and document authoring.

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
- **Consequences:** Review submission is rejected while an incompatible lease is active. Approved/frozen content is never made editable through autosave. The first complete vertical implementation applies to change request drafts; other controlled draft types will adopt the same domain contract incrementally.

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
- **Consequences:** The compiled-production gate creates a real System SRCR and records an immutable verification
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
- **Rationale:** A generic route made a software change request appear inside System navigation, and the showcase people registry
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
- **Decision:** Every controlled identifier (`SRCR`, `HLRCR`, `LLRCR`, `SYSR`, `HLR`, `LLR`, `SYSTP`, `HLRTP`, `LLRTP`,
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
  release version (`1.6` becomes `SW-01.60`); “Build 1.6” is informal wording. change request identifiers use five
  digits for existing and future records. Every approved change request creates one controlled Test Change
  Review per affected System, Software HLR, or Software LLR discipline.
- **Rationale:** Separate baseline/build names implied two configurations where the product owner intends one.
  Verification procedure maintenance is specialist downstream work, not author impact disposition. Treating
  each approved change as a governed discipline-specific review creates a clear handoff and release gate.
- **Consequences:** Existing change request and FMS software-build identifiers are destructively normalized by
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

### DEC-078 - Downstream Requirement Impact Is Assessed Before a software change request Is Created

- **Date:** 2026-07-31
- **Status:** Accepted
- **Decision:** Final approval of a System change raises an HLR downstream change assessment; final approval
  of an HLR change raises an LLR assessment. The consuming engineer may conclude that no downstream change is
  required or link one or more Draft software change requests. A Draft software change request may answer multiple assessments, so both one-to-one
  and consolidated delivery remain possible without allocating empty controlled change-request numbers.
- **Rationale:** The author of an upstream change cannot responsibly decide the consuming discipline's impact.
  Creating a software change request before that engineering conclusion falsely asserts that a downstream requirement change is
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
- **Decision:** Reactivate the build-scoped Problem Reports center. A PR may drive an SRCR, software change request, or System/HLR/LLR
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

### DEC-086 - Primary Navigation Follows Requirements Work and Verification Evidence

- **Date:** 2026-08-02
- **Status:** Accepted
- **Decision:** The primary sidebar groups change requests, requirements, requirements documents, and the
  Digital Thread under **Requirements**. Coverage, results, and verification documents sit under
  **Verification**. **Code** is a standalone destination between Verification and the standalone Problem
  Reports center. Existing discipline chooser routes remain compatible entry points even though they are not
  duplicate sidebar destinations.
- **Rationale:** Engineers follow a change and its trace consequences, while verification users work from
  coverage and evidence. A standalone PR center preserves the problem-to-correction thread without treating it
  as either a requirement level or a verification subtype.
- **Consequences:** Navigation labels describe user work rather than internal modules. Direct links and browser
  refreshes remain supported, and historical URLs continue to resolve while the visible information
  architecture stays compact.

### DEC-087 - Problem Reports Use a Progressive, Independently Closed Lifecycle

- **Date:** 2026-08-02
- **Status:** Accepted
- **Decision:** A PR progresses through Draft, Ready for SCCB, Open, Implementing, Verifying, Awaiting SQA
  Closure, and Closed. Title and rich Problem Description are the only Draft requirements. Raised-by and date
  are automatic and immutable; owner and one target build are auditable but reassignable. Additional
  Information, Proposed Corrective Action, Root Cause, combined System/Aircraft Impact, and Unknown/No/Yes
  impact decisions for requirements, code, tests, documents, and safety are disclosed progressively. History
  is an internal tab. SCCB opening is a light approver action and SQA closure is independent.
- **Rationale:** Draft reporting must be fast enough to capture a real observation while implementation and
  closure need enough structure and role separation to be trusted. Containment, preventive action, saved
  filter views, attachments, and configurable classifications would add process the current users have not
  requested.
- **Consequences:** Approved linked CRs appear automatically as read-only corrective-action cards. Only test
  results deliberately selected to support closure appear as PR test evidence. Build 1.5 remains readable and
  immutable; Build 1.6 remains active.

### DEC-088 - GitLab Owns Code and AeroLink Owns Exact LLR-to-Merge Evidence

- **Date:** 2026-08-02
- **Status:** Accepted
- **Decision:** GitLab is the source of truth for source, branches, merge requests, review, and commit content.
  AeroLink records an immutable pointer from an exact approved LLR revision in one build to the GitLab MR and
  merge commit SHA, or an attributable `No code change required` disposition with rationale. Code is not stored
  or reviewed in AeroLink. A compact demonstration set is conspicuously labelled as demonstration data.
- **Rationale:** Reviewers need to answer which merged code implements the exact LLR wording in this build
  without creating a second code authority or confusing GitLab merge requests with AeroLink Problem Reports.
- **Consequences:** Code traceability is a release gate for changed LLR scope. Build 1.5 exposes historical
  mappings read-only; Build 1.6 permits active mappings. The Software Builds lineage includes a non-record
  **Plan next build** placeholder, but no future build identity is created until an authorized user performs a
  later governed planning workflow.

### DEC-089 - The Problem Report Database Is Project-Scoped, Not Build-Scoped

- **Date:** 2026-08-03
- **Status:** Accepted; reverses the build-scoping half of DEC-085 as implemented under issue #298
- **Decision:** There is one Problem Report database per Project. The list of open and in-work reports, the
  dashboard counts over it, and the ability to open and modify a report are identical whichever build the
  reader is standing in. A report names one target build and may be closed during a particular build; that
  target is an attribute of the record and an explicit filter a user may choose, never an implicit filter
  applied by the workspace.
- **Rationale:** Requirements, change requests, baselines and verification are owned by a build — a requirement
  revision only exists inside the configuration that carries it. A Problem Report is not: it is a report about
  the product, raised by whoever found the problem, and it outlives and crosses the builds that respond to it.
  Filtering the database by the active workspace did not present a different view of one database; it presented
  what looked like a different database, with ten reports visible in Build 1.6 and none in Build 1.5.
- **Consequences:** Problem Report queues and dashboards are Project-scoped. Problem Reports are exempt from
  cross-build resource refusal, so a report opens from any build. Genuinely build-owned records keep their
  scoping unchanged. `targetReleaseId` remains available as a deliberate filter.

### DEC-090 - A Downstream Assessment's Surface Follows Its State, and a Wrong Conclusion Is Withdrawn Explicitly

- **Date:** 2026-08-04
- **Status:** Accepted; implemented under issue #313
- **Decision:** The downstream assessment queue offers one entry control, worded "Open assessment", on every
  row in every state. What may be done about an assessment is decided inside the drawer from the assessment's
  state: unclaimed offers "Take it on"; claimed and undecided is the only state offering both conclusions;
  concluded offers the software change request work and a withdrawal; in review offers approve and return to the approver alone;
  approved offers no conclusion control at all. Wherever a conclusion exists it is stated outright with its
  author, its rationale and, once approved, its approver. Changing a recorded conclusion is not a second press
  of a conclusion button: it is "Reopen assessment", which requires a stated reason, returns the assessment to
  undecided, detaches any linked Draft software change request without altering the software change requests themselves, and writes an immutable
  `downstream_assessment_reopenings` row holding everything the withdrawn conclusion carried.
- **Rationale:** The entry control used to read "Review assessment" or "View assessment" depending on approval
  state, putting state in the one place a reader looks for an action and saying it twice, in near-synonyms,
  next to a status column that already said it. Underneath, the drawer rendered the same conclusion controls
  regardless of state, so an assessment already answered — even an approved one — showed both conclusions live
  and indistinguishable from a first-time answer. Pressing one silently overwrote a controlled engineering
  judgement with no record that it had ever been made. A controlled record must be able to say that a
  conclusion was reached, by whom, and that it was later withdrawn and why.
- **Consequences:** Withdrawing an unapproved conclusion is the assigned engineer's act; withdrawing an
  approved one requires Approver authority, because the conclusion has left the engineer's hands. An assessment
  in review is returned, never withdrawn behind its approver. A superseded assessment cannot be reopened. A
  released build refuses the withdrawal like every other change, and the drawer now names the released build
  as the reason rather than blaming the reader's authority. Existing conclusions were backfilled with their
  deciding engineer, which the aggregate has always constrained to be the assignee; the deciding instant was
  never recorded and is deliberately left empty rather than invented.
- **Narrowed by [DEC-094](#dec-094---an-assessment-says-whether-it-was-done-and-what-it-found):** only a
  no-change conclusion now reaches the in-review state at all, so the in-review and approved surfaces
  described above apply to that conclusion alone. Everything else here stands.

### DEC-091 - A Problem Report Is Edited Under the Universal Controlled-Editing Lease

- **Date:** 2026-08-04
- **Status:** Accepted; implemented under issue #314
- **Decision:** A Problem Report is edited exactly as every other controlled record is: an exclusive
  server-leased checkout, a recovery snapshot saved while the author types, an explicit check-in, and a
  discard that restores the last checked-in content. Any state except Closed and the terminal dispositions
  can be checked out; reopening is the route back from those. The lease is governed by the responsible
  engineer, so a checkout is refused up front to anybody whose check-in the aggregate would refuse anyway.
  A check-in writes a `DetailsCheckedIn` entry into the report's own `ProblemReportRevision` history with its
  actor and time. The report's own `POST /details` write path is retired.
- **Rationale:** The Problem Report MVP delivered the lifecycle and the field set but wired editing to a
  form of its own, which posted the whole record with an expected version and hoped nobody else was doing
  the same. Its controlled-editing policy still named `Investigating` and `ResolutionProposed` — states the
  MVP lifecycle no longer produces — so in practice only a Draft could be checked out at all. Two write
  paths to the same fields is the defect, not the fix for it: a Problem Report is the record most likely to
  need correcting while the work it describes is in flight, and it is exactly the record that should not be
  correctable by two people at once.
- **Consequences:** The report number, its project, who raised it and who is responsible for it are checked
  on check-in and never applied — they are facts about the record, not fields on the form. The working copy
  is carried whole through check-in, so a field the editor does not show is preserved rather than reverted.
  Reassigning the owner and retargeting the build stay separate audited lifecycle actions, unchanged. Its
  audit lives in `ProblemReportRevision` rather than `AuditEvent`, whose aggregate key is a foreign key to a
  change request.

### DEC-092 - Change Requests Are SRCR, HLRCR and LLRCR, and the Prefix Names the Level

- **Date:** 2026-08-04
- **Status:** Accepted; implemented under issue #327
- **Decision:** A System change request is an **SRCR**. A software change request is an **HLRCR** or an
  **LLRCR** according to the requirement level it carries. `SCR` and `SWCR` are retired and rejected outright.
  The prefix is derived from type and level by one authority, `ChangeRequestNumbering.Prefix`, which the
  allocator and the data migration both ask. HLRCR and LLRCR are numbered independently, matching
  SYSTCR/HLRTCR/LLRTCR and the procedures they govern. Every existing record was renamed in place with its
  numeric part preserved exactly; no record was renumbered, and no record of the former identifiers is kept.
- **Rationale:** One software prefix could not say which discipline a change request belonged to, while the
  work, the reviewers and the approvals are separate for HLR and LLR. A reader who sees the identifier should
  already know which of the three they are holding. Making the prefix depend on the level also turns a
  previously optional field into an invariant: a software change request that cannot say whether it is HLR or
  LLR is a controlled record that cannot be named, so it can no longer exist.
- **Consequences:** A software change request must declare its level before it exists, and the identifier and
  the declared scope can never disagree — an LLRCR holding HLR work is refused. Mixed-level software change
  requests are impossible; they were already unreachable through the authoring endpoint, and are now
  unreachable through the domain. Each new prefix resumes numbering above the highest number already used at
  its own level, so the sequences begin with gaps, which is honest. The API routes moved from `/api/scrs` and
  `/api/scr-drafts` to `/api/change-requests` and `/api/change-request-drafts`; two middleware guards keyed on
  the old path — the cross-build resource check and the released-build write refusal — had to move with them.
- **Accepted consequence:** `SystemChangeRequest.ComputeSnapshotHash()` places the display number first in the
  hashed content, and `CandidateBaseline.Freeze()` hashes every selection's display number. Renaming therefore
  means 126 frozen review-cycle hashes, 17 electronic signatures and 1 frozen baseline hash no longer recompute
  from the records they attest to, and nothing in the database explains why. Nothing re-verifies them at
  runtime, so no behaviour breaks; the cost is that a review snapshot hash printed on a generated document is
  no longer reproducible. **The frozen hashes were not recomputed** — doing so would make a signature attest to
  content its signer never approved.

### DEC-093 - An Imported Baseline Is an Assertion, Not a Change

- **Date:** 2026-08-04
- **Status:** Accepted; to be implemented under issue #332
- **Decision:** A program that already exists in another requirements tool is brought in as an **externally
  sourced baseline**, created directly with its provenance. It never becomes a change request. The import
  runs through five gates — Source, Analyse, Map, Reconcile, Accept — the last of which is signed by a named
  person. The resulting baseline is permanently marked externally sourced wherever it appears, and carries a
  provenance record holding the extract's SHA-256, the source system and version, the source baseline name
  and date, the mapping used, and the reconciliation report.
- **Rationale:** Both existing import paths commit into a Draft change request. That is right for proposing
  requirements into a controlled program and wrong for porting one in: nobody at the customer approved those
  requirements *in this tool*, so routing them through review and approval would produce a real signature
  attesting to a fiction. An imported baseline must never be indistinguishable from one built through this
  product's own controlled chain — the whole value of the chain is that it can be told apart from a claim.
- **Consequences:**
  - **Identifiers.** New controlled numbers are issued, and the source identifier is kept forever as a
    searchable **external identity record** joined by a typed trace reading *`SYSR-000148.00` originates from
    `SYS-0147`* — the controlled requirement is the subject, the source object is what it came from.
    Discarding the source identifier was rejected because every drawing, CDRL and test procedure outside this
    tool still names it. Preserving source identifiers verbatim was rejected because they fail
    `ArtifactNumber.ValidateBase` and would leave two schemes coexisting forever. This is deliberately the
    opposite of [DEC-092](#), where no record of the retired names was kept: there the old names were our own
    naming mistake, here they are another organisation's system of record.
  - **Traces gain a second recognised origin.** A trace created by an import records the import as its
    origin, never a change request, so nothing suggests a build carried work it did not.
  - **Source-system authorship is never imported as ours.** DOORS `Created By`, `Created On`,
    `Last Modified By` and `Status` describe activity in DOORS. Writing them as AeroLink authorship would
    attribute work here to people who never touched this tool — the same class of error as fabricating an
    approval. They stay in the provenance record.
  - **Nothing is dropped silently.** Every source object is accounted for at Reconcile. Duplicate source
    identities block the import; dangling links are either accepted as recorded gaps or block it; every
    attribute is mapped or explicitly excluded with a reason before the Map gate will close.
  - **Mapping covers values, not only names.** An attribute mapping without a value mapping is incomplete —
    DOORS `T/A/I/D` has to become Test, Analysis, Inspection, Demonstration.
  - **Link mappings state direction explicitly**, because a reversed mapping produces a complete, plausible
    and entirely wrong traceability tree.
  - **Re-import is a delta**, keyed on source system, module and absolute number. Programs re-extract, and a
    second import must not produce a duplicate set.
  - **One import per Program for now, but scoped by artifact kind in the model.** A Program is expected to be
    ported from a single source. It is foreseeable that requirements come from one system and test procedures
    from another, so an import declares which artifact kinds it carries and its provenance is recorded per
    import rather than per Program. Nothing is built for the second source now; the model simply does not
    preclude it, which is cheap today and expensive to retrofit.
  - **A single source baseline, not a chain.** An import brings in the source's current state as one baseline
    — the program's V1.0. Importing a sequence of historical source baselines would mean inventing revision
    records with authors and dates from a system that is not ours, which is the same error as fabricating an
    approval, one level down. (Resolves OQ-019.)
  - **An externally sourced baseline may be the predecessor of a normally built one.** Importing V1.0 and
    then building V1.1 in this product is the point of the feature, so the imported baseline takes its place
    in the release lineage like any other predecessor. (Resolves OQ-020.)
  - **An imported baseline arrives released, and therefore never runs readiness gates.** Gates evaluate a
    build before it is released; an imported V1.0 is already past that. The prior decisions — review,
    approval, verification — are credited to the source's own release rather than re-litigated here, and this
    product never claims to have made them.
  - **Inherited requirements count as settled coverage, and are always shown apart.** In a successor build
    the coverage gate still counts every effective requirement revision, and one inherited from an imported
    baseline is settled by the source's release. The gate's summary must always split the two — "4,900
    confirmed here, 280 inherited from the DOORS import" — so a claim made elsewhere can never be read as one
    this product verified. Where an import also carries verification data, that is recorded as the richer
    evidence it is. (Resolves OQ-021.)
  - **Modifying an inherited requirement expires its credit.** The source's assertion covers the wording it
    released. As soon as a later build modifies an inherited requirement, its inherited coverage goes suspect
    exactly as any other modified requirement's would, and must be answered here. Without this boundary,
    crediting prior decisions would quietly become never verifying anything again.
  - **Source history is imported as reported facts, and this product makes no claim about it.** Where an
    extract carries the source's own history — a requirement's wording at V0.8 and V0.9, who changed it and
    when, the source's own change reference — it is recorded verbatim as *what the source system reported*.
    It is never restated as this product's revisions, never signed for, and never participates in any gate,
    coverage figure or readiness computation. The Accept signature covers the V1.0 mapping and reconciliation
    only. This is what makes importing history safe: an incomplete or messy chain can be recorded honestly
    because nothing downstream reasons over it. (Resolves OQ-019, replacing the earlier answer.)
  - **History is not imported as revisions, because a revision means something here.** `RequirementRevision`
    binds a non-nullable `SourceChangeRequestId` and `EffectiveBaselineId` — a revision *is* what an approved
    change request put into a materialized baseline. Importing V0.8 and V0.9 as revisions would require
    fabricating a change request and a baseline for each, or making those fields nullable and weakening the
    invariant for every requirement in the product. Source history is held against the external identity
    record instead, where it costs neither.
  - **Only objects present in the imported baseline join the live graph.** A requirement in V1.0 gets its
    external identity and its trace. An object that existed at V0.9 and was retired before V1.0 appears in
    source history and nowhere else: no requirement, no identity, no trace. History is narrative, not nodes,
    so a retired ancestor never becomes a dangling reference in the traceability network.
  - **Source history is searchable.** Somebody holding a drawing that cites a source identifier retired two
    baselines ago should get an answer — "`SYS-01233` appears in the source history of `SYS-01234`, retired
    at V0.9" — rather than an empty result they read as the tool having lost it.
  - **Porting a program in takes Program authority, not engineering authority.** Every import gate is
    restricted to the configuration manager, program manager and administrator — the same set that
    establishes a Project — while reading an import and searching source identities is open to anyone with
    Project access. An engineer has every right to work inside a Program; declaring that a whole baseline
    arrived from elsewhere, already released, is Program setup rather than engineering work on a build.
  - **An import is not a mutation of the active build, and is exempted from the released-build write refusal
    by name.** That refusal exists to stop a released build being edited. An import creates a new build from
    a source that is already released, so the refusal would answer a question nobody asked. Exempting it by
    name rather than by leaving it off the prefix list is deliberate: the list entry `/api/baseline` is loose
    enough to catch `/api/baselines`, and so catches `/api/baseline-imports` with it. A regression test pins
    both halves — an import runs with a released build in the workspace, and `/api/baselines` is still
    refused there.
  - **Externally sourced is derived from the provenance, never stored as a flag on the build.** A build is
    externally sourced exactly when an accepted import points at it. A duplicated boolean could drift away
    from the record that justifies it, and the whole value of the marking is that it cannot be wrong.
  - **An import that accounts for no source objects cannot be reconciled.** "Every source object is accounted
    for" is vacuously true against nothing, and accepting it would produce an empty build asserting that a
    program was brought in from elsewhere — the one outcome no later gate would catch. The Reconcile gate
    therefore refuses until the import has been told what the extract held.
  - **What an import accounted for is held by the import, not counted from the identities it created.** A
    re-extract is a delta: an object already recorded by an earlier import is marked seen again and keeps the
    import that first recorded it. Counting rows would report a second import of the same program as holding
    nothing, and refuse to reconcile the exact case the delta rule exists for.
  - **Two objects claiming one source identity are refused at the point of recording, not reported as a gap.**
    Other reconciliation findings are outcomes of a mapping somebody can accept. This one is not: a later
    extract cannot tell the two apart, so the delta rule would silently merge them. There is no mapping
    decision that makes it safe, so it is refused outright.
  - **An import is a one-way move, not an ongoing synchronisation.** (Resolves OQ-022.) A program is
    extracted from its old tool once, at the point of leaving it, and DOORS is not kept in step afterwards.
    So an identifier renamed between two extracts — the case OQ-022 raised — cannot arise: there is no second
    extract of a program still being worked on elsewhere. A source identifier therefore needs no history of
    its own, and the recorded name is simply the name the source used when it was left behind, which is what
    every external drawing and CDRL cites. The delta keys stay as they are: they cost nothing, and they are
    what make the retry below safe.
  - **Getting one accepted import usually takes several attempts, and an abandoned one leaves nothing.**
    Import, find the mapping wrong, abandon, re-extract, try again — only the last attempt is accepted. An
    abandoned attempt committed nothing, so the source identities and history it recorded are discarded with
    it, and only rows that attempt owned are removed. Left in place they would make the retry find every
    object already taken: the accepted import would own no source records at all, while its page reported
    counts belonging to the attempt that was thrown away.
  - **An object retired before the imported baseline cannot have anything originate from it, and the identity
    itself enforces that.** The provenance link is created through the source identity rather than
    constructed directly, because the rule that keeps source history narrative rather than nodes can only be
    enforced where the identity is in hand. Constructing a link freely would let a real lineage claim hang
    off an object nobody imported.

### DEC-094 - An Assessment Says Whether It Was Done, and What It Found

- **Date:** 2026-08-05
- **Status:** Accepted; delivered by PR #343
- **Decision:** A downstream assessment answers exactly one question in exactly one sentence — has it been
  performed, and what did it conclude:

  | Situation | Label |
  | --- | --- |
  | Not decided, picked up or not | `HLR Assessment Required` |
  | Change required, no change request yet | `HLR Assessment Complete – Draft HLRCR Required` |
  | Change request linked, whatever its own state | `HLR Assessment Complete – HLRCR Created`, with the change request and its state beneath |
  | No change required, awaiting the discipline lead | `HLR Assessment Complete – No HLRCR Required Pending Approval` |
  | No change required, approved | `HLR Assessment Complete – No HLRCR Required` |
  | Superseded | `HLR Assessment Superseded – Refer to <the assessment that replaced it>` |

  Two rules follow. **A change request cannot be linked until the assessment has concluded that one is
  required.** And **only a no-change conclusion is approved**: a conclusion calling for a change is complete
  when the engineer records it.
- **Rationale:** The queue answered with nine phrasings that mixed how far along the workflow was with what
  the engineering conclusion had been, so the same stage read differently depending on which branch reached
  it. There is deliberately no wording for an assessment somebody has picked up but not answered — it has
  either been performed or it has not, and an interim state reports an intention rather than a fact. The
  approval asymmetry is the substantive half: a conclusion that calls for a change produces a change request
  which is reviewed on its own terms, so approving the assessment as well reviews one judgement twice and
  delays the work carrying it. A conclusion that nothing is needed produces nothing, and was the single
  answer in the chain that nobody would ever examine.
- **Consequences:**
  - Linking previously worked from an undecided assessment, so a Draft could hang off a conclusion that said
    nothing while the queue reported controlled downstream work. That is now refused.
  - Because the queue reads the same before and after a no-change conclusion is sent for approval — it is
    pending approval either way — the hand-off is visible only in the drawer, where the approver picker is
    replaced by the named approver. A returned assessment also reads as pending approval; the reviewer's
    reason for returning it is in the drawer.
  - The row offers one control. The number, title and level chip were a second button opening the same
    drawer, so a row carried two ways to do one thing and neither announced itself as the way.

### DEC-095 - A Test Change Request Is What a Test Assessment Raises, Not What an Approval Raises

- **Date:** 2026-08-05
- **Status:** Accepted; delivered by PR #345
- **Decision:** An approved change raises a **test assessment** for each affected verification discipline,
  unnumbered and carrying no controlled identity. The SYSTCR, HLRTCR or LLRTCR number is allocated only when
  that assessment concludes test-procedure work is required. Concluding that none is required raises nothing,
  states why, and goes to a test lead for approval. The wording is [DEC-094](#dec-094---an-assessment-says-whether-it-was-done-and-what-it-found)'s,
  with the discipline's own nouns — `System Test Assessment Complete – SYSTCR Created`, and so on.
- **Rationale:** `TestChangeReview` and the test change request are the same record, and it was numbered the
  moment a change request was approved. Every approved change therefore produced a controlled test change
  request before anybody had looked at whether it touched a single procedure, and the page described them
  accordingly — "raised when a change request is approved" — which is why it read nothing like the
  requirements queue beside it. The two pages were showing the same stage of the same workflow in two
  unrelated vocabularies, so what a reader learned on one told them nothing about the other.
- **Consequences:**
  - Raising a test change request **by hand** is itself the conclusion that work is required, so it is
    numbered immediately. An orphaned procedure is likewise its own finding.
  - An unassessed package is identified by the change it is assessing, because it has no number yet. A
    coverage refusal that named the holder by its number consequently trailed off into nothing, and now names
    what the package is currently called — distinguishing *covered by another package* from *already has an
    assessment of its own*, which otherwise read as "X is already covered by X".
  - The showcase answers the assessments that carry procedure decisions and leaves the rest open, so the
    demonstration shows both a queue with work waiting to be judged and the test change requests that judging
    it produced.
  - Convergence completed in DEC-099: the row, the single control and the drawer are now the requirements
    queue's, from its own stylesheet.

### DEC-099 - The Testing Queue Is the Requirements Queue, Not Something Like It

- **Date:** 2026-08-06
- **Status:** Accepted; delivered by PRs #357 and #359
- **Decision:** A test assessment row is the requirements card — change number, title, discipline chip, then the
  conclusion, then **one** `Open assessment` control in every state. Take it on, Link PRs, conclude, send for
  approval, approve and return all moved inside, along with the inline expanding workbench. (`Take it on` was
  removed outright by [DEC-102](#dec-102---answering-an-assessment-is-what-takes-it-on); moving it inside the
  drawer hid it without retiring it.) The amber
  "attention" row colour is gone. Both drawers import `DownstreamAssessmentQueue.css` rather than copying it.
- **Rationale:** The two pages showed the same stage of the same workflow in two unrelated shapes. A second
  stylesheet that merely resembled the first would drift the first time either was touched, so the testing
  surface uses the requirements one literally. The amber was carrying "nobody has picked this up", which the
  conclusion column already says in words.
- **Consequences:**
  - **Two drawers, not one.** `Open assessment` holds the assessment; the SYSTCR opens in its own workspace, as
    an HLRCR opens from the requirements drawer. A package is a record of its own, not a panel inside another.
  - **The per-requirement decisions live with the assessment**, and this is the one place the mirror has nothing
    to copy: a requirement change is *read* on the requirements side, while a test change must be *answered*
    requirement by requirement. They cannot live in the package, because they exist even when the conclusion is
    that no package is needed — and would then be unreachable for exactly those assessments.
  - Ten journeys reached the page through the old markup, not the nine first counted; the tenth was found by
    running the suite rather than by reading. Six needed changing.
  - The row deliberately no longer says who holds a package, matching the requirements row. Journeys that had
    filtered on the presence of "Take it on" to find an *unclaimed* one silently became "the first row" — see
    [LES-007](#les-007---a-selector-that-stops-selecting-still-passes).

### DEC-097 - A Test Procedure Is Built and Handled Exactly as a Requirement Is

- **Date:** 2026-08-06
- **Status:** Accepted; delivered by PRs #348, #350, #351, #353, #357, #358
- **Decision:** The test-procedure world mirrors the requirement world rather than paralleling it. A test change
  request carries `TestProcedureChange` records the way a change request carries `RequirementChange` records; it
  advances to a next revision the same way; and a baseline fixes exactly which procedure revisions it holds:

  | Requirements | Test procedures |
  | --- | --- |
  | `RequirementRevision.SourceChangeRequestId` | `TestProcedureRevision.SourceTestChangeRequestId` |
  | `RequirementRevision.EffectiveBaselineId` | `TestProcedureRevision.EffectiveBaselineId` |
  | `BaselineRequirementSelection` | `BaselineTestProcedureSelection` |
  | `BaselineChangeRequestSelection` | `BaselineTestChangeRequestSelection` |
  | `CandidateBaseline.RequirementsHash` | `CandidateBaseline.TestProceduresHash` |
  | `RequirementBaselineMaterializer` | `TestProcedureBaselineMaterializer` |

- **Rationale:** The two disciplines do the same job on different artifacts. A second mechanism that merely
  resembled the first would drift the moment either was touched, and an engineer moving between them would have
  to learn two vocabularies for one idea.
- **Consequences:**
  - **A test change request may be selected into a baseline after the freeze**, unlike a change request.
    Freezing fixes the requirements; the procedures that verify them are written against those requirements and
    so finish later. What closes the procedure manifest is materialization, not the freeze.
  - **`MarkReleased` does not require a procedure manifest.** Every build released so far has none, and gating
    on it would make those builds retrospectively invalid rather than simply unmaterialized. Whether a future
    release should require one is deliberately left as an open decision, not taken as a side effect.
  - Attribution columns are nullable. 518 procedure revisions predate controlled procedure change, and "nobody
    knows which package approved this" is the true record for them.
  - A proposal's driving requirement revisions become real `TestRequirementCoverage` only at materialization —
    the same point a change request's proposed upstream allocation becomes a trace link.
  - Settled by [DEC-101](#dec-101---a-revision-takes-its-folded-in-claims-with-it): when a test change request
    with other change requests folded into it revises, the claims move to the successor.

### DEC-098 - A Test Procedure Covers Requirements at Its Own Level, and Nothing Else

- **Date:** 2026-08-06
- **Status:** Accepted; delivered by PR #356
- **Decision:** A coverage link between a procedure and a requirement at a different level is refused. An HLR
  test procedure exists because it verifies one or more HLRs. A procedure's number and its level must also
  agree, because the allocator picks `SYSTP`/`HLRTP`/`LLRTP` *from* the level, so they are one fact.
- **Rationale:** This is the root cause of a System change request raising work in the HLR queue. Retiring a
  System requirement stranded an HLR procedure that should never have been linked to it, and the orphan was then
  routed by the **procedure's** level onto a change request of a different discipline. Forbidding the link
  removes the whole class of problem instead of routing around it: a retirement can now only strand procedures
  of its own level, so the discipline the orphan reaches always matches the change request that caused it, and
  the mis-disciplined record is no longer constructible.
- **Consequences:**
  - Enforced in `SaveChangesAsync`, the one place every write passes through, so code not yet written is covered.
  - `TestProcedure`'s constructor defaults `level` to `HighLevel`. A caller that omitted it produced a HighLevel
    procedure wearing a `SYSTP-` number — the same wrong fact one step earlier. Two existing tests were doing
    exactly that.
  - Verified against live data before changing anything: **0 of 1,251** coverage links crossed a level and
    **0 of 516** procedures disagreed with their prefix. Prevention with nothing to migrate.
  - Retargeting a stranded procedure survives, but is level-bounded: it may move to another requirement at its
    own level, never across one.

### DEC-100 - A Procedure Is Browsed Through the Requirements Inspector, Not a Copy of It

- **Date:** 2026-08-06
- **Status:** Accepted; the Test Procedure Explorer is delivered, the two pages behind it are not yet
- **Decision:** Verification gets a third page per discipline, **Test Procedure Explorer**, listing every
  controlled procedure in the build with a right-side inspector carrying the same four tabs a requirement's
  does — Overview, Trace & impact, History, Discussion — rendered from `RequirementsWorkspace.css` and, for the
  discussion, from the same `form` and `article` markup and the same `ArtifactComment` record.
- **Rationale:** [DEC-097](#dec-097---a-test-procedure-is-built-and-handled-exactly-as-a-requirement-is) made
  the procedure *model* mirror the requirement model. This is the same commitment one layer up: an engineer who
  can read a requirement can read a procedure without being taught a second screen.
- **Consequences:**
  - **Trace runs the other way, and that is the one real difference.** A requirement's trace shows what derives
    from it; a procedure's shows the requirements it exists to verify. A procedure verifying nothing says so,
    because that is the stranded case [DEC-098](#dec-098---a-test-procedure-covers-requirements-at-its-own-level-and-nothing-else) governs.
  - Resolving a procedure comment goes through the existing `/api/enterprise-requirements/comments/{id}/resolve`
    route, which reads `ArtifactComments` by identifier alone and never mentioned requirements except in its
    path. A second route would have been a second behaviour to keep in step.
  - Mentioning somebody on a procedure notifies them, as it does on a requirement. Procedures carry no watch
    list, so the audience is who was named plus whoever is being replied to.
  - The page is **additive**. Procedures appear both here and in the Testing Coverage library until the
    remaining stages rename that page to Change Requests and strip the library out of it. Visibly redundant for
    a while, but never half-migrated.
  - Discussion is read-only in a released build, matching the requirements pane exactly.
  - The list pages 25 at a time behind the same `.pager` control, and the discussion loads on selection rather
    than on opening the tab, because the tab wears the comment count.
### DEC-101 - A Revision Takes Its Folded-In Claims With It

- **Date:** 2026-08-06
- **Status:** Accepted; closes the open item left by
  [DEC-097](#dec-097---a-test-procedure-is-built-and-handled-exactly-as-a-requirement-is)
- **Decision:** When an approved test change request advances to its next revision, the change requests folded
  into it move to the successor. `StartNextRevision` no longer refuses.
- **Rationale:** A change request is claimed by at most one package, enforced by a unique index, so exactly one
  of the two revisions may hold it. The successor is the package that will actually be approved and
  materialised. Leaving the claim behind would mean a superseded package still answering for test work nobody
  is doing; dropping it would make the new revision cover less than the old one without saying so.
- **Consequences:**
  - Each claim is **moved, not recreated** — same row, same identifier, same claimant and time. Who took a
    change's test work on, and when, is not something a revision should rewrite.
  - The claim's foreign key is required and cascades on delete, which is exactly the shape EF Core reads as
    "this child was orphaned, delete it". It does not — it reparents with an UPDATE — but that was **verified
    against a real store**, not reasoned about, because a deletion here would violate no index and report
    nothing. The persistence test asserts the row, not the aggregate.
  - The revise endpoint has to `Include(x => x.AdditionalSources)`. An unloaded collection is an empty one, so
    a missing include would move nothing, strand the claim on the superseded revision, and return success.
    The API test drives the real route and was confirmed to fail without the include.
  - The endpoint now reports `coveredChangeRequests` on the successor, so the move is visible in the response
    rather than only in the table.
### DEC-103 - A Procedure Is Only Introduced, Modified or Retired by a Test Change Request

- **Date:** 2026-08-07
- **Status:** Accepted
- **Decision:** Nothing creates a test procedure outside a package. The `+ New test procedure` control and the
  `POST /api/test-procedures` route are removed. A procedure comes into existence, changes, or is retired only
  through a `TestProcedureChange` on a test change request, reviewed with that package and materialised into
  the build — exactly as a requirement is only changed by a change request.
- **Rationale:** There is no `+ New requirement` button, and there should never have been a procedure
  equivalent. The product already knew: the code carried a comment saying the control *"writes a procedure
  with no memory of why it exists"* and offered it anyway. Two ways in meant a procedure could exist with no
  package, no rationale and no trace to the change that required it.
- **Consequences:**
  - **`Author the procedure`**, on a decision that asked for one, now proposes an `Introduce` change on the
    package that asked — carrying the driving requirement revision — rather than writing a procedure. It picks
    no approver: the package carries the proposal to its own review, and a second approver for the procedure
    alone would be a second approval of the same work.
  - Probing the collection with `POST` answers **405, not 404** — the collection is still there to be read,
    and only the verb that wrote is gone. The test asserts that exact status, because a 404 would mean the
    route had been renamed rather than retired, and the door would still be open somewhere else.
  - **A rule was nearly lost and nearly mis-ported.** The removed route refused a procedure that named no
    requirement revision. Moving that onto the package proposal broke three existing tests, correctly: that
    route wrote a controlled procedure immediately and needed its coverage then, while a package only
    proposes and driving revisions become coverage at materialisation. The rule was not ported — it was
    reinstated deliberately, at a different moment, as the next two points record.
  - **A procedure being introduced must name at least one requirement revision it verifies.** A procedure that
    verifies nothing is not a controlled procedure, and an approver must never be asked to sign one.
  - **That rule is enforced when the package is submitted for review, not while it is being drafted.** A draft
    package is worked on incrementally, exactly as a change request is, so the gate belongs where an approver
    is about to sign rather than where an engineer starts typing. `TestChangeReview.SubmitForReview` refuses a
    package whose introduced procedures name nothing; the authoring endpoint deliberately does not, and says
    so in a comment where the temptation to add it will next appear.
  - **Procedure-level approval does not survive.** `POST /api/test-procedures/{revisionId}/approve`, the
    `Review & approve` control, the client call path, the `selectedApproverId` projection that routed it, and
    `TestProcedureRevision.Approve` are all removed. The last of those had exactly one caller — the deleted
    route — and a capability nothing calls is not a capability
    ([LES-006](#les-006---a-capability-with-no-caller-is-not-delivered)).
  - **Approving the test change request is the authorisation for materialising its procedure revisions.** The
    SYSTCR, HLRTCR or LLRTCR is what gets reviewed and signed. That signature covers the procedure work the
    package carries, because that work is what the package *is*.
  - **Materialised revisions are written directly as `Approved`**, on that authority, and there is no separate
    signature on the procedure revision for the same change. Signing twice would not make the work more
    controlled; it would only make it ambiguous which signature meant it.
  - No controlled path now produces a `Draft` procedure revision. Drafts that predate controlled test change
    still exist and are still shown as what they are; they are simply not approvable any more, because the
    step that approved them was a second approval of work no package had ever carried.
  - The rule that outlived the approval is that an unapproved revision cannot be executed. That is enforced at
    `POST /api/test-executions` and is the reason a Draft still means something.

### DEC-102 - Answering an Assessment Is What Takes It On

- **Date:** 2026-08-06
- **Status:** Accepted
- **Decision:** The `Take it on` control is removed from both the requirements HLR/LLR downstream assessment
  drawer and the test assessment drawer. An assessment nobody has answered is open to any engineer with the
  authority for it; recording an answer is what makes it theirs.
- **Rationale:** Claiming was a step that produced nothing. It did not record a judgement, it did not change
  what the assessment said, and it did not stop anybody else working — it only announced an intention, and it
  stood between the reader and the work. Requested repeatedly and not acted on, because
  [DEC-099](#dec-099---the-testing-queue-is-the-requirements-queue-not-something-like-it) moved the control
  inside the drawer rather than retiring it, which made it look gone from the queue while it still gated
  every decision.
- **Consequences:**
  - **It was never only a button.** `canEdit`, `canDecide` and `canSubmit` all required
    `AssignedEngineerId == actor` on both sides, so removing the control alone would have left an authorised
    engineer looking at a drawer offering nothing. The rule is now "unheld, or held by this reader".
  - **The holder is still recorded**, assigned implicitly by the answer. My Work and the submit/approve chain
    both key on it, and the next reader needs to see that somebody is on it.
  - Amending an answer before approval remains the holder's. Correcting a concluded assessment is already an
    act of its own under [DEC-094](#dec-094---an-assessment-says-whether-it-was-done-and-what-it-found).
  - Both sides changed together. They had the same control, the same server rule and the same drawer shape,
    and the point of that convergence is that neither moves without the other.

### DEC-096 - A Problem Report Names Its Kind, Its Workaround, and Who Holds It

- **Date:** 2026-08-05
- **Status:** Accepted; delivered by PR #344
- **Decision:** A Problem Report carries a **Type** (Documentation, Code, Test, Other) so a queue can be
  narrowed to the work one discipline owns, and a **Workaround** where empty means none has been found. The
  impact assessment's `Safety` area is renamed **Airworthiness**, which names what is actually being judged.
  `Responsible owner` is called **Assigned user** throughout. No separate state field was added: the report
  already has a lifecycle state and the queue already filters on it.
- **Rationale:** Type is stored by name so adding a kind later is a code change rather than a data migration.
  `Other` exists because every report raised before the field did was genuinely unclassified, and a report
  fitting none of the named kinds needs somewhere to go that is not the nearest wrong answer.
- **Consequences:**
  - Both new enum columns had to override EF's scaffolded `defaultValue: ""`. A value stored by name cannot
    be an empty string, and every existing record would have failed to materialize. See
    [LES-004](#les-004---an-enum-stored-by-name-cannot-default-to-an-empty-string).
  - Renaming the impact area moves the recorded answers with it: the migration rewrites the stored JSON key,
    and the domain still accepts `Safety` on input so a client that has not been reloaded is understood
    rather than rejected.
  - The queue's search box filters as it is typed into, a moment after typing stops, and the `Refresh` button
    is gone. The dropdowns keep `Apply filters`, because changing three of them should ask once.
  - The editable-state policy is unchanged. Six states still block editing — Closed, Rejected, Duplicate,
    Cannot Reproduce, No Fault Found and Accepted Risk — and whether the last four should is deliberately
    left open.

### DEC-104 - Legacy Procedure Effectivity Starts With an Explicit Attributable Bootstrap

- **Date:** 2026-08-10
- **Status:** Accepted; delivered by #364 / PR #440
- **Decision:** A released predecessor whose controlled procedures predate build-scoped procedure manifests is
  not treated as carrying zero procedures and is not silently reconstructed on read. A Configuration Manager
  explicitly previews and commits one exact migration/configuration snapshot. The commit is bound to the exact
  preview hash, records actor/time/source rule/count/hash, refuses drift without partial writes, and is
  idempotent for the same snapshot. Normal successor materialization requires that exact predecessor manifest,
  carries it forward, then applies selected approved Introduce/Modify/Retire TCR decisions.
- **Rationale:** Treating a missing legacy manifest as empty turns absence of historical metadata into a false
  configuration statement. Inferring an exact historical manifest from today's mutable inventory would make the
  opposite false claim. An explicit bootstrap states exactly what is known: the migration snapshot established
  now, under named authority and deterministic evidence.
- **Consequences:** Existing procedure revisions, authorship, approvals and coverage are preserved; coverage is
  never inferred. A genuinely empty legacy inventory can establish an exact empty snapshot. Candidate Baselines
  at `/baselines` is the supported Configuration Management surface for this operation; `/release-planning`
  remains retired. This supersedes only prior statements that Candidate Baselines itself was dormant/unexposed,
  not the broader decisions that retired redundant product-version/release-planning surfaces.

### DEC-105 - Procedure Search Uses the Same Exact Revision Title It Displays

- **Date:** 2026-08-10
- **Status:** Accepted; delivered by #442 / PR #444
- **Decision:** Any search whose result represents an exact procedure revision matches the authoritative
  `TestProcedureRevisionTitleProjection` for that revision, subject to the same project/release/effectivity/
  discipline scope as the result. This includes procedure list search, universal procedure search, universal
  execution search and Modify/Retire target search.
- **Rationale:** A Retire proposal can contain supplied title text that is deliberately discarded from
  controlled history while the retirement revision inherits the predecessor title. Searching raw proposal text
  made discarded text discoverable and the displayed controlled title undiscoverable. Search and display cannot
  use different definitions of the same controlled record.
- **Consequences:** Discarded/forged Retire title text does not match; the inherited predecessor title does.
  Legacy revisions remain searchable by the deterministic compatibility label actually displayed, without
  promoting today's mutable catalog title into historical evidence.

### DEC-106 - A Stale Controlled Procedure Target Requires Explicit Reselection

- **Date:** 2026-08-10
- **Status:** Accepted; delivered by #367 / PR #445
- **Decision:** Modify/Retire authoring binds to the exact controlled procedure identity/effectivity the engineer
  selected. If that build membership or current revision is stale when the mutation reaches the server, AeroLink
  returns a conflict and requires refresh/reselection. It never silently substitutes the procedure revision that
  happens to be current now.
- **Rationale:** Silent repair would turn a concurrency/effectivity conflict into authorization for different
  controlled work. Preserving the engineer's prose is useful; preserving a stale target as though it were still
  valid is not.
- **Consequences:** `procedure_not_carried_by_build`, `procedure_manifest_revision_missing`, and
  `procedure_revision_not_next_for_build` are explicit 409 conflicts. The client keeps the dialog and authored
  engineering content, clears stale target-dependent identity/coverage state, reloads the controlled picker and
  requires deliberate reselection. Unknown/cross-project/wrong-level targets remain validation failures.

### DEC-107 - Superseded TCRs Are History; Their Exact Successors Are Active Work

- **Date:** 2026-08-10
- **Status:** Accepted; completed by #365 / PR #438 on top of the atomic supersession delivered earlier
- **Decision:** Once approved test work is revised, the predecessor remains an immutable controlled historical
  record and the exact successor is the active package. Active engineering/baseline selection must not offer the
  superseded predecessor. Historical presentation must show the Superseded relationship and provide an exact
  successor route even when that successor belongs to another release or the historical item is a folded
  automatic assessment.
- **Rationale:** Atomic domain supersession without truthful browser/history presentation leaves two competing
  stories: the database knows which package survives while the engineer can still encounter the predecessor as
  if it were current. Controlled history and active work must agree at every route.
- **Consequences:** Superseded packages remain readable with their prior decisions/signatures, are excluded and
  authoritatively refused from new baseline selection, and route to the exact successor rather than a guessed
  same-release row.

### DEC-108 - A Duplicate Problem Report Points Directly to One Canonical Project Root

- **Date:** 2026-08-11
- **Status:** Accepted; implemented by #455
- **Decision:** A new Duplicate disposition names exactly one existing Problem Report in the same Project. The
  target is a non-Duplicate canonical root, not another Duplicate, and a report already representing an inbound
  duplicate cannot itself become Duplicate. Reopening retains the prior relationship as history; it does not
  permit a second current-looking target to be appended. Open, in-work, and non-Duplicate terminal reports may
  serve as the root because Duplicate means the anomaly is represented by that controlled record, not that the
  root has reached a particular lifecycle conclusion.
- **Rationale:** Arbitrary chains make the controlling record depend on traversal order and permit cycles,
  dangling targets, and cross-Project conclusions. A direct root is deterministic, independently auditable, and
  does not force an obsolete target-state restriction onto legitimate anomaly consolidation.
- **Consequences:** The authoritative disposition command validates the target and graph inside a serializable
  atomic unit with the state, link, and revision write. Legacy dangling, cross-Project, branching, chained, and
  cyclic relationships remain immutable and are exposed through a versioned diagnostic status for deliberate
  reconciliation; they are never silently normalized or rewritten.

### DEC-109 - Draft Corrective Actions Sustain Only Their Own Automatic Implementation Claim

- **Date:** 2026-08-11
- **Status:** Accepted; implemented by #458
- **Decision:** Linking an Open Problem Report to a Draft change request may automatically enter Implementing.
  Removing the last proposed corrective action returns it to Open only when immutable lifecycle evidence proves
  the current Implementing state began automatically and no investigation, corrective work, manual start, or
  approved corrective action has superseded that inference. A no-op link edit has no lifecycle side effect.
- **Rationale:** Implementing is a controlled statement that must not outlive its sole inferred cause, but
  removing one Draft relationship must not erase deliberate engineering work or approved evidence. Deriving
  provenance from append-only events avoids a mutable flag that could drift from the history it summarizes.
- **Consequences:** Automatic start and reconciliation events carry versioned narrative/evidence naming the
  exact Draft change request. The canonical Problem Report snapshot and historical hashes remain unchanged;
  pre-contract events without exact routing evidence are retained conservatively rather than retroactively
  reinterpreted.

### DEC-110 - A Project Staffs Itself, and a Lead Is a Position Rather Than a Label

- **Date:** 2026-08-12
- **Status:** Accepted
- **Decision:** A Project has its own Personnel page, reached beside Software Builds rather than inside a
  build. **Program Manager, Project Engineer, Project Engineering Lead and the Project-scoped Administrator**
  may add somebody, end a position, and name a standing backup on their own Project. Granting `Administrator`
  itself remains with the global account. Every other member reads the roster.
- **Rationale:** Membership already meant access, and the roles were already there, but every route was gated
  on `IsAdministrator` — which resolves to the single account named `admin` — and organised by user, so it
  answered "which projects is this person on" and never "who is on this project". A Program Manager could not
  see their own team.
- **Consequences:**
  - **A position is the job; leading it is a designation.** Somebody is a System Engineer, and one of them is
    the System Engineering Lead. `SingularProgramRoles` names the nine positions exactly one person holds, and
    a second grant is refused while the first is current.
  - **Verification splits by discipline.** `SystemTestEngineer`, `SoftwareTestEngineer`, `SystemTestLead` and
    `SoftwareTestLead` join the undivided `TestEngineer` and `TestLead`, which are retained because they are
    what every membership recorded before this says. `ProgramRoleAuthority.Satisfying` makes the precise titles
    answer requests for the general ones, so a more exact title still never removes capability
    ([LES-009](#les-009---a-rule-moved-is-not-the-same-rule-and-look-alike-call-sites-are-not-alike)).
  - **Leading carries review and approval authority.** A lead satisfies `Reviewer` and `Approver`; belonging
    to the discipline does not. Before this, naming somebody the lead meant separately remembering to grant
    both, and forgetting produced a lead unable to sign the stage that names their own position.
  - **A membership ends; it is not deleted.** `EndedAt` and `EndedBy` are stamped and the row is kept, because
    "who was the System Engineering Lead in March" is asked about a period that has already closed, long after
    the security event recording the revocation has scrolled away. The uniqueness index is filtered to current
    rows, so somebody who left and returned is not blocked by their own history.
  - **Every read of `ProgramMemberships` that confers authority now excludes ended rows.** All nineteen call
    sites were read individually rather than swept, including both copies of `HasRoleAsync` and the session
    projection in `MapAsync`; seeding deliberately still counts ended rows, so re-seeding cannot resurrect a
    membership somebody ended on purpose.

### DEC-111 - A Standing Backup Acts as the Holder, With No Interval

- **Date:** 2026-08-12
- **Status:** Accepted
- **Decision:** A Project may name one standing backup per position. The backup may review, approve and act in
  that position **at any time**, with no date range and no requirement that the holder be away. It stands until
  it is removed. `RoleDelegation` is retained unchanged for genuine dated handovers.
- **Rationale:** The owner's rule is that a named backup can do what the main person does. A delegation cannot
  express it: `RoleDelegation` refuses to exist without a start and end, and expires on its own. Requiring
  somebody to arrange cover before each absence puts a step between a signature and the person authorised to
  make it, which is how work stalls while its approver is unreachable.
- **Consequences:**
  - Backup is a property of the position, and `IdentityService.HasRoleAsync` gains a third source of authority
    beside membership and delegation.
  - **A backup must be a current member.** Naming somebody who later leaves must not keep letting them sign,
    so the check requires an unended membership and the last role ending stands their backups down.
  - The holder cannot be their own backup, which would report cover that does not exist.
  - **This is deliberately weaker separation of duties than a dated delegation**, and was chosen knowing that:
    two people can sign a position's stages permanently rather than during a stated absence. Signatures made on
    this authority are expected to record that they were made as backup rather than as holder — with no
    interval to explain the name, that attribution is the only thing that later says why.

### DEC-112 - A Test Change Request Answers Only for Its Own Level

- **Date:** 2026-08-12
- **Status:** Accepted
- **Decision:** An HLRTCR answers only for HLR requirement changes, an LLRTCR only for LLR, a SYSTCR only for
  system. A change at any other level is **refused**, not deprioritised — in the picker and again on the
  server. Confirmed by the owner as a hard refusal.
- **Rationale:** A procedure verifies the requirements one level above it, so a change at another level cannot
  drive it. The picker previously offered every approved change allocated to the build, so an engineer raising
  an HLRTCR was shown SRCRs and LLRCRs as valid choices; selecting one produced a package claiming to answer
  for work it could not verify.
- **Consequences:**
  - `TestChangeRequestSourceEligibility` gains the level rule in two forms — a predicate for validation and a
    query for the picker — so the browser list and the server refusal cannot disagree.
  - Enforced on `POST /api/releases/{id}/test-change-requests` as well as the source list, because a filtered
    list is a convenience and a request that never opened the picker must still meet the rule
    ([LES-006](#les-006---a-capability-with-no-caller-is-not-delivered) in reverse: a rule with only a client
    is not a rule).
  - The refusal names the level rather than saying "not selectable", so an engineer learns why.
  - **A consequence worth stating plainly:** where a build has no approved change at the package's own level,
    the package cannot currently be raised at all, because a test change request still requires an originating
    change request. Whether a Problem Report alone may originate one is recorded as an open question below.

### DEC-113 - A Problem Report May Originate a Test Change Request

- **Date:** 2026-08-12
- **Status:** Accepted
- **Decision:** A test change request is raised from **exactly one** originating driver, which is either an
  approved change request at the package's own level ([DEC-112](#dec-112---a-test-change-request-answers-only-for-its-own-level))
  or a **Problem Report**. Raised from nothing at all is refused. Approved change requests may still be folded
  in afterwards.
- **Rationale:** Test work is not only ever caused by a requirement change — an anomaly found in the field is
  a legitimate reason to write, correct or withdraw a procedure. With DEC-112 in force, a build carrying no
  approved change at the package's own level made that package impossible to raise at all.
- **Consequences:**
  - `ChangeRequestId` becomes nullable and `OriginatingProblemReportId` joins it; exactly one is set. The
    Problem Report takes the originating slot rather than sitting beside a fabricated change request, so the
    package still has one thing it was raised from — which its number, its covered-sources record and its
    case snapshot all depend on. `TestChangeReview.FromProblemReport` is a separate factory, so every existing
    construction site is untouched.
  - **The case snapshot is hashed, and that hash is what an electronic signature recorded.** The Problem
    Report origin is therefore written to the snapshot **only when present**, and the originating entry is
    omitted rather than restructured — a package raised from a change request serialises byte-identically to
    before, so every hash already recorded still verifies.
  - **`CaseContractVersion` was deliberately not bumped.** It gates rules through `CaseContractVersion >=
    CurrentCaseContractVersion`, so raising the current version would push every existing package below the
    threshold and silently stop enforcing case completeness and the procedure-decision requirement on all of
    them. A contract version that gates rules cannot be used as a serialisation marker.
  - Nine call sites assumed an originating change request always exists — the impact service, the showcase
    seeder, the provenance projection, the baseline materialiser and three API queries among them. Each was
    read and answered on its own terms rather than swept
    ([LES-009](#les-009---a-rule-moved-is-not-the-same-rule-and-look-alike-call-sites-are-not-alike)).
  - A materialised procedure records no change-request source for a Problem Report-originated package, rather
    than inventing one. `ProcedureSourceSnapshot` is a record of change requests; the package itself carries
    what it was raised from.

### DEC-114 - A Procedure Is Written Into a Document When It Becomes One, Not at the Next Restart

- **Date:** 2026-08-12
- **Status:** Accepted
- **Decision:** Every Project has three test procedure documents (SYSTD, HLRTD, LLRTD) from the moment it
  exists, and every procedure is written into one at the moment it becomes a controlled revision.
  `TestProcedureDocumentBootstrap` remains the backfill for what already existed; it is no longer the only
  thing that files anything.
- **Rationale:** The bootstrap ran once, at startup. A Project created afterwards had no documents, and a
  procedure materialised afterwards was in no document — present in the list, under no heading in the rail,
  and uncounted in every section total. A requirement is authored into SYSRD, HLRD or LLRD as part of becoming
  one; a procedure had no equivalent moment, so it was given the one it already has.
- **Consequences:**
  - Filed in `TestProcedureBaselineMaterializer` after its save and inside its transaction, because the
    placement reads the procedures back from the database — a procedure and its place in a document are
    committed together or not at all.
  - Ensured at `POST /api/workspaces` for a new Project, and after the seeders at `POST /api/showcase/seed`
    and `POST /api/showcase/upgrade`, which run long after startup. Each site was read on its own terms
    ([LES-009](#les-009---a-rule-moved-is-not-the-same-rule-and-look-alike-call-sites-are-not-alike)).
  - Document numbers run across the installation, not within a Project: two documents answering to
    SYSTD-000001 would make a reference ambiguous. A Project's HLR document is therefore **not** necessarily
    HLRTD-000001, and nothing may assume it is.

### DEC-115 - A Saved Procedure View Is a Separate Contract From a Saved Requirement View

- **Date:** 2026-08-12
- **Status:** Accepted
- **Decision:** Saved worklists over the test procedure library are their own record and their own validated
  contract (`SavedProcedureView`, `ProcedureSavedViewContract`), parallel to the requirements pair rather than
  shared with it.
- **Rationale:** The two lists are not the same list. A requirements view carries `verification`, `tag` and
  `specificationId`; a procedure view carries `outcome` and the document it is written into. A shared contract
  would accept either set on both sides, so a view could be saved against one list and silently do nothing on
  the other — which is the exact failure the requirements contract was written to prevent.
- **Consequences:**
  - Validated and normalized at the boundary on create **and** on update, because a view is a worklist
    somebody else opens; a field the Explorer cannot apply must be refused when written, not ignored when read.
  - Owner-only lifecycle, answering Not Found rather than Forbidden for somebody else's view: confirming that
    an id exists but is not yours is more than a reader of a shared list needs to know.
  - Carried on the `/api/test-procedures` list response rather than fetched separately, as the requirements
    workspace carries its own.

### DEC-116 - One Repository Knowledge Authority Model

- **Date:** 2026-08-26
- **Status:** Accepted
- **Decision:** AeroLink uses one explicit repository knowledge authority model: `PROJECT_STATE.md` is the
  current product truth; this append-only log is the authority for accepted long-lived decisions; GitHub Issues
  own the live backlog and findings, while Pull Requests own implementation, review, and merge state; `AGENTS.md`
  is the repository operating contract;
  `docs/` holds durable product definition, reference, showcase, provenance, lessons and history; `docs/archive/`
  holds historical handoffs, audits, reports and work logs; and `product/docs/` holds implementation and operator
  documentation. `CLAUDE.md` and other model-specific instruction files are thin adapters and must not duplicate
  mutable product architecture or current status. Root compatibility shims remain only where an established path
  requires them and are never current authority.
- **Rationale:** Valuable records had accumulated under several names and locations, making a dated handoff or
  compatibility redirect easy to mistake for the current product state. A machine-checked taxonomy and a single
  authority per kind of knowledge preserve discoverability without allowing historical evidence to become live
  guidance. A repository-layout guard enforces the root allow-list and maintained-document link boundary.
- **Consequences:** New active findings go to GitHub Issues, current product changes update `PROJECT_STATE.md` when
  material, accepted decisions are appended here, durable lessons go to `docs/ENGINEERING_LESSONS.md`, and
  historical records go to `docs/archive/`. New root narrative requires an explicit compatibility/current-authority
  reason and a guard update. This supersedes **DEC-084 only to the extent that DEC-084 treated the newest dated
  handoff as a current-state authority**; the qualified repository, current code/tests, `PROJECT_STATE.md`, and
  live GitHub state remain authoritative, while the dated handoff remains historical evidence.

## Lessons Learned

Findings that cost real time, recorded so they cost it once. These are about how the work is done rather than
what the product does.

### DEC-117 - The Digital Thread Canvas Is Measured for Legibility at Its Default Zoom, Not in CSS Pixels

- **Date:** 2026-09-03
- **Status:** Accepted
- **Decision:** The Digital Thread canvas is a narrow, recorded exception to the product's 12px readability
  floor. Text inside the scaled canvas subtree — identifiers, titles, meta lines and status pills on the cards
  — may be authored below 12px. Everything else on the Digital Thread page, and every other surface in the
  product, remains held to the floor exactly as before.
- **Rationale:** The product owner accepted the smaller type on 2026-08-31, on the reasoning that this surface
  can be zoomed. The canvas is drawn at a zoom the reader controls, so a CSS pixel is not what a reader sees:
  the same 11.5px identifier renders larger or smaller depending on where they have put the board. Its three
  density tiers deliberately *shed content* as the reader pulls back rather than shrinking the type, so the
  size a reader encounters is a function of their own zoom, not of the stylesheet. Measuring this surface with
  a flat CSS-pixel rule would therefore measure the wrong thing, while forcing 12px into a lane of 236px cards
  would cost the density the whole design exists to provide.
- **The rule that replaces it:** legibility at the default fit zoom on landing. Every identifier, title and
  state label must be legible when the page opens, before the reader touches anything. Text may fall below
  12px effective size only as a consequence of the reader deliberately zooming out, where less detail is the
  point and the compact and dense tiers have already dropped content. Status meaning — suspect above all —
  must remain non-colour-coded and readable at every tier, which is why the suspect indicator on a card is
  anchored outside every density-gated container and carries its word rather than only its amber.
- **Scope of the exception, deliberately narrow:** `design-system.spec.ts` exempts only elements inside
  `.dtCanvasScene`. The Digital Thread page's own toolbar, view switch, export control, evidence table, state
  messages and detail panel are all still audited at 12px, as is every other surface in the product. This is
  an exception for one scaled subtree, not a licence for small type anywhere. `AGENTS.md` forbids weakening a
  test to land work; this is an approved product exception with a stated replacement rule, and this record is
  what makes it one.
- **Consequences:**
  - A future change that moves canvas text out of the scaled subtree loses the exception automatically, which
    is the intended behaviour rather than an oversight.
  - The list alternative is not optional. `DESIGN_VISION_AND_DASHBOARDS.md` and WCAG 2.2 require a
    keyboard-accessible list or table beside any graph view, and the evidence table is that alternative; it is
    audited at the full floor and must stay.
  - **Fitting the whole board on landing and landing legibly cannot both hold, and legibility wins.** Pure
    width-fit landed a fully populated six-lane thread at roughly 0.61 zoom at 1280px, rendering 11.5px
    identifiers at about 7px — below what this decision calls legible, and reached without the reader having
    chosen anything. That is the condition this decision exists to forbid, so it is resolved rather than
    recorded: the board now lands at `LANDING_MIN_ZOOM` (0.86, the detailed-tier boundary), and card
    identifiers, titles and state labels are authored at 14px so they arrive at 12.04px effective — at or
    above the product floor, at every supported enterprise desktop width. A board wider than the viewport is
    panned on arrival, which is an ordinary canvas affordance and costs a reader far less than text they
    cannot read.
  - **Landing and Fit are different operations with different floors.** Every landing is floored: the initial
    fit, a re-fit after a resize, and the selection framing a deep link triggers — that last one matters most,
    because a focal deep link arrives selected and the framing path immediately replaces the landing
    transform, so flooring only the initial fit would have left every §4.4 landing unprotected. An explicit
    Fit the reader asks for — keyboard `0`, or double-clicking empty canvas — is *not* floored: §6.1 requires
    it actually to fit the whole board, and §10.1 permits sub-floor text precisely when the reader has chosen
    to pull back. `land()` and `fitAll()` are separate for this reason.
  - `MIN_ZOOM` (0.58) is unchanged and still governs zooming the reader performs themselves.
  - The detailed tier grew to `rowPitch 138 / cardHeight 108` and card top rows wrap. At 14px an identifier
    and a long state label such as `Selected for baseline` do not share a 236px row, and the answer to a
    spill is geometry — never type back below the floor, and never an ellipsis that would cost a controlled
    state label its words.
  - **A board that no longer fits changed three behaviours that assumed it did**, each corrected rather than
    left to be discovered: a card is drawn only while it is inside its lane's window **and** wholly inside the
    area the board has (so §6.6's "the panel never rests on a linked record" holds without zooming out past
    the floor); arrow navigation pans the camera as well as rolling the lane, since a lane can now be off to
    one side; and selection framing falls back from the whole traced web to the selection and one hop when
    the web cannot fit beside a docked panel — the wide framing is a landing convenience, non-occlusion is a
    guarantee.
  - Product ruling of 2026-09-03 on PR #907: the later, specific §10.1 landing rule supersedes **only** the
    older assumption that every lane must be horizontally visible on an automatic landing. Lane identity and
    order, rolling, pan and zoom, the density tiers, trace behaviour, panel non-occlusion, cross-lane sync and
    explicit Fit are all unchanged.
  - `digital-thread-page.spec.ts` asserts the replacement rule directly, measuring **effective** size —
    computed font size multiplied by the scene's own transform scale — for every identifier, title and state
    label at 1280, 1440 and 1920 wide, on three landings: the unselected change network, a deep-linked
    artifact thread, and a deep-linked change request. It also asserts that an explicit Fit goes below the
    floor and genuinely fits, and that no card's identifier or state label spills outside its card.
- **Supersedes:** nothing. It records the exception #880 section 10.1 required to be written down rather than
  quietly applied.


### LES-001 - A New API Route Inherits Middleware Guards by Prefix Match

`primaryMutationPrefixes` in `Program.cs` matches with `StartsWith`, and its entries are deliberately loose —
`/api/baseline` is written that way to catch `/api/baselines`. It therefore also caught `/api/baseline-imports`,
and every import gate was refused with "Build 1.5 is released and read-only", about a build the import does
not touch. Tightening the prefixes to a segment boundary would break the routes they were written for, so an
exemption is named explicitly instead. **Check every new `/api` route against three places in `Program.cs`:**
the mutation prefix list, the Project-scope resolution switch, and the build-owned resource switch.

### LES-002 - Test a Performance Hypothesis Before Shipping the Fix

Browser journeys intermittently time out after fifteen seconds waiting for sign-in, with the page still
showing "Authenticating…". A plausible cause was that Entity Framework builds its model on the first query,
so a readiness probe that only opens a connection reports ready while the cost still lands on the first real
request. The fix was written, then measured: **167 ms with the warm-up against 170 ms without.** Identity
seeding already issues queries at startup, so the model was warm long before the probe. The change was
deleted rather than shipped with a rationale that had just been disproven. The cause remains unknown;
password-hashing cost under parallel-shard CPU contention is the next candidate, and it should be measured
the same way.

### LES-003 - Green Tests Are Not a Look at the Page

Four defects in one day passed every assertion and were caught only by rendering the page and reading it:
dates shifted a day for anyone west of UTC because provenance facts were formatted in local time; a cascade
collision where `.importStart label` outranked `.importCheck` and dropped every checkbox onto its own line;
two project cards wearing the same icon; and a block of divider colour where a grid's last row wrapped short.
**Screenshot a changed page and look at it before calling the change done.**

### LES-004 - An Enum Stored by Name Cannot Default to an Empty String

EF Core scaffolds a new non-nullable string column with `defaultValue: ""`. Where that column holds an enum
converted with `HasConversion<string>()`, `""` is not a member name and **every existing row fails to
materialize** — a total outage of that entity, from a migration that looks routine. It happened twice in one
day, on `problem_reports.Type` and `test_change_reviews.Outcome`. Always replace the scaffolded default with
a real member, and backfill the rows that deserve a different one.

### LES-005 - Prefer the Editor Over Scripted Multi-File Edits

A `perl -pi -e` substitution intended to change one card's icon silently stripped the field from **two**,
leaving an unrelated card without one. This repeats an earlier finding that scripted edits misfire in this
repository — `${number}` shell-expanding to nothing, `===` breaking the parser, CRLF causing silent no-ops.
Use the editing tools for anything structural; reserve scripted substitution for changes whose every match is
verified afterwards.

A second failure mode, found on 6 August while proving a test discriminated: the script rewrote the file
within the same second as the previous build, so MSBuild's timestamp check skipped the rebuild and the run
used a **stale assembly**. The test then failed for a reason present in no source anybody was reading, which
led to an invented explanation and a workaround for a defect that did not exist. When a result contradicts the
code in front of you, `dotnet clean` and rebuild before theorising about the framework.

### LES-006 - A Capability With No Caller Is Not Delivered

`TestProcedureBaselineMaterializer` was written, tested, registered in dependency injection, and merged across
two pull requests — and **no endpoint ever called it**. The three tables it fills stayed empty, and the gap was
found only by grepping the API project for the type's name, which matched nothing but compiled binaries. Every
gate had been green throughout, because the tests exercised the class directly.

Domain and infrastructure tests prove a mechanism works. They cannot prove anyone can reach it. Before calling
a capability delivered, find the caller: a route, a UI control, a scheduled job. If the only callers are tests,
the feature exists in the codebase and not in the product.

Its counterpart from the same session: building the screen found three defects — two controls whose names read
almost identically, a dialog taller than the viewport whose submit button could never be reached, and a journey
that starved the shared fixture pool. None was reachable by a backend test.

### LES-007 - A Selector That Stops Selecting Still Passes

Several journeys found an *unclaimed* test change request by filtering rows for the presence of a "Take it on"
button. When that button moved into the drawer, the filter matched nothing and `.first()` quietly became "the
first row on the page" — which could be a package somebody else held, offering no decisions at all. The tests
did not fail at the filter. They failed several steps later, looking for a button that was never going to be
there, and the message pointed at the wrong thing.

A locator that narrows by the presence of something is a silent assertion. When that something moves, the
locator does not break — it widens. Where a filter is load-bearing, assert what it found before acting on it,
or select by a property the redesign cannot remove.

### LES-008 - A Test Timeout Names the Test, Not the Action That Hung

The production design-contract sweep timed out at its full 600 seconds, twice, with the failure snapshot showing
the Test Procedure Explorer rendered correctly. Both times the conclusion drawn was "the sweep has outgrown its
budget, and the new page is simply the route it happened to reach" — and both times that was wrong. The trace
told the truth immediately: one `page.locator('main').innerText()` call had consumed **562 of the 600 seconds**,
because the new page rendered a `<div>` where every other workspace renders its own `<main>`. Playwright waited
for a landmark that was never going to appear; `.catch(() => '')` could not fire, because the action had not
failed yet.

Two things follow. A missing landmark is a real accessibility defect, and it was found by a timeout rather than
by the accessibility journeys, because those walk a hardcoded surface list that the new page was not on. And a
timeout report names the *test*, never the action inside it — the unmatched `before` event in `trace.zip` names
the action. Read the trace before theorising about budget.

### LES-009 - A Rule Moved Is Not the Same Rule, and Look-Alike Call Sites Are Not Alike

Three defects in one day, all the same shape: several sites looked alike, one change was applied to all of
them, and each time the difference that mattered was invisible in the text.

A rule was carried from the deleted direct-create route onto the test change request's proposal — *a procedure
must name what it verifies*. Three tests failed and were right to. The old route created a controlled
procedure the instant it was called and needed its coverage then; a package only proposes, and driving
revisions become coverage at materialisation. Identical words, different moment, different rule. The rule was
later reinstated deliberately, at submission, by
[DEC-103](#dec-103---a-procedure-is-only-introduced-modified-or-retired-by-a-test-change-request) — which is
the point: it belonged somewhere, but not where it was first put.

A `replace_all` across two identical proposal payloads broke a passing test. The break was the useful part: it
revealed that the procedure-changes endpoint validates driving requirement revisions against real ones, which
no amount of reading the diff would have shown.

And scripted multi-line edits silently changed nothing three times, because the working tree is CRLF — see
[LES-005](#les-005---prefer-the-editor-over-scripted-multi-file-edits).

Treat "these all look the same" as a reason to open each one. Before moving a rule, ask what was true where it
came from that may not be true where it is going. When a batch edit breaks something that was passing, read
the breakage before undoing it — it is describing the system.

### LES-010 - The Exact Head Is the Merge Authority

A merge candidate is not qualified because an earlier commit was green or because its last edit looks
non-functional. The August procedure-control sequence repeatedly refreshed `main`, compared the exact branch,
reviewed that exact head and waited for the required aggregate gate before squash merge. A later correction,
even to a test, creates a new head and therefore a new qualification subject.

When a CI failure looks flaky, retry the **identical commit**. That can distinguish timing from code change
without moving the evidence target. It still does not make a red aggregate acceptable: preserve the failure,
rerun the same head, and merge only once the required final reporter is green.

### LES-011 - Provider-Safe Queries Project Data Before They Format It

The Candidate Baselines predecessor route was logically correct and still returned 500 in production-browser
qualification because SQLite could not translate display/order expressions embedded in the projection. The
correction was not provider-specific branching: select SQL-safe primitives, materialize them, then compute
human display values and ordering in memory.

A query that passes one provider or a unit-shaped test is not automatically a portable EF query. If formatting
is not part of the filtering/join semantics, keep it out of translation.

### LES-012 - A Native Select Option Exists Without Being Playwright-Visible

The stale-target browser regression correctly found the new `.01 · Approved` option after refresh, but
`toBeVisible()` failed because native `<option>` elements are not rendered as independently visible DOM boxes in
Playwright's visibility model. The locator found the right element repeatedly; the assertion was testing the
wrong browser contract.

For native select membership, assert existence/count or selectability. Reserve visibility assertions for the
control the user actually sees.

### LES-013 - A Replacement PR Does Not Clean Up the PR It Replaced

#444 was the qualified replacement for #443 and merged #442 correctly. #443 nevertheless remained open as a
draft until the final repository check found it. Product behavior was clean; repository state was not.

A closeout checklist must include open PRs as well as merged code. Close superseded drafts with an explicit
reason so the next engineer/model does not treat them as unfinished parallel work.

### LES-014 - Temporary Repair Machinery Must Disappear From the Reviewed Tree

Connector-only repairs sometimes need branch-local helper workflows or scripts. Those are delivery machinery,
not product content. The branch must be inspected and, when necessary, its history/tree cleaned so temporary
runners do not leak into `main` or become misleading long-lived automation.

Review the final filenames and exact tree, not merely the intended product diff. A self-deleting helper is only
successful when the reviewed merge candidate contains no trace of it.

### DEC-118 - #881 Ships as One Integrated Branch, Superseding Its Own Bounded-Slice Language

- **Date:** 2026-09-03
- **Status:** Accepted
- **Decision:** The remaining scope of #881 is delivered as **one implementation branch and one integrated
  pull request**, not as separate bounded child issues and slice PRs. This supersedes the issue body's "This
  is an operational-resilience umbrella, not one giant PR. Create bounded child issues before implementation"
  and the 2026-09-03 amendment's instruction to keep the source-isolation slice separate from the database
  upgrade coordinator, clone validation and conflict resolver.
- **Authority:** The product owner's implementation instruction for this work, verbatim: *"IMPORTANT OWNER
  DIRECTION — THIS SUPERSEDES OLD SLICING LANGUAGE… ONE implementation branch. ONE integrated PR… Do NOT
  create separate child issues merely to split implementation… This prompt is the owner's current
  implementation authority and supersedes older #881 statements only where they require separate delivery
  slices/PRs."*
- **Scope of the supersession, deliberately narrow:** it overrides the *delivery shape* and nothing else.
  Every other #881 requirement — the operating-mode contract, the safety rules, the qualification matrix and
  the acceptance criteria — stands exactly as written. It is not authority to widen scope, to skip
  qualification, or to merge without review.
- **Why this record exists:** the instruction lives in an implementation prompt, while the issue body still
  carries the superseded text. A reviewer reading #881 later would find the old rule and no trace of the
  decision that replaced it, and would be right to raise it as a contradiction — which is exactly what
  happened during independent review of PR #909. The durable record belongs here; the issue carries a pointer.
- **Consequences:**
  - The integrated PR is reviewed as one change, so the burden of keeping it reviewable moves onto the PR
    description and the commit history rather than onto the branch count.
  - Operator acceptance for #881 remains a separate, later activity on the real machines. One PR is not one
    acceptance.

### DEC-119 - The AeroLink Repository Uses an App-Bound GitHub Merge Queue

- **Date:** 2026-09-04
- **Status:** Accepted
- **Decision:** The canonical repository is `AeroLinkDEV/requirements-management-tool`, and `main` is governed
  by an active squash merge queue. Pull-request heads first earn readiness through the trusted Full Product
  requester. GitHub then validates the exact composed queue candidate, and the candidate may merge only when
  both GitHub Actions' `Full Product evidence aggregate` and the dedicated App's
  `Trusted merge-queue binding` succeed.
- **Trust boundary:** The AeroLink Merge Authority App is private, installed only on this repository, and has
  Checks read/write plus Contents read access. Its private key exists only in the `merge-authority`
  environment, whose deployment policy admits `main` only. The ruleset pins the trusted binding to that App's
  integration identity and has no bypass actors.
- **Freshness model:** The queue's current-`main` composition supersedes classic strict "require branches to
  be up to date" enforcement. A behind pull-request branch does not need a mechanical rebase; the exact tree
  that would land must pass the queue gate. Real conflicts still require resolution.
- **Protected authority surfaces:** A candidate that changes `.github/`, `product/test-planner/`, or
  `product/ci-metrics/` cannot automatically authorize itself. Those changes require an explicit, reviewed
  authority-maintenance cutover; the queue must never be weakened merely to make such a change convenient.
- **Acceptance:** A documentation-only delivery through the live queue is required to establish the
  single-entry path. Issue #549 remains the live authority for that proof and the remaining composition,
  deliberate-failure, stale-base, and non-cancellation evidence before final closeout.

### DEC-120 - Digital Thread Story Framing Separates Arrival from Deliberate Selection

- **Date:** 2026-09-06
- **Status:** Accepted
- **Decision:** Digital Thread arrival and deep-link framing retain DEC-117's readable detailed floor of `.86`.
  After the reader deliberately selects a record, the canvas may use the measured compact floor of `.81` (about
  11.34px for the authored 14px identifier) while preserving the complete directed, cycle-safe trace supplied by
  the server. An explicit **Fit selected story** action frames that selected trace, **Fit entire story** frames the
  complete directed trace down to the existing `.58` floor, and **Fit board** frames the whole projected board.
  The canvas does not replace a complete story with a one-hop fallback, and pan or lane roll remains available when
  the complete story cannot fit at a readable scale.
- **Intent boundary:** The `.81` measurement applies to ordinary deliberate selection only. A user returning to the
  focal record receives the selection treatment; the initial/deep-link landing treatment is consumed once by the
  view's arrival selection. This clarification does not rewrite DEC-117's landing floor.
- **Authority:** The directed `trace()` projection and the view-supplied node set remain authoritative. Framing is a
  presentation operation over that set and must not discover siblings, invent edges, or alter controlled identity.

## Working Assumptions

Assumptions are not decisions. They remain valid only until confirmed or replaced.

- **ASM-001:** Product- and behavior-level definitions precede technical architecture, data schema, and UI design.
- **ASM-002:** Artifact numbers are globally unique across programs; exact prefixes, digit lengths, and revision display syntax are configurable or decided later.
- **ASM-003:** The first slice supports multiple programs even if initial validation uses one reference program.
- **ASM-004:** Requirements may include controlled images/figures as part of revisioned content.
- **ASM-005:** Exact review roles and independence rules vary by organization/program; the SRCR author selects the ordered approval sequence and unanimous sequential approval is fixed initial behavior.
- **ASM-006:** Superseded by DEC-085. PRs are controlled first-class records in the product; external issue
  references may be added later without replacing them.
- **ASM-007:** The initial platform records Pass, Fail, Blocked, Not Run, and Not Applicable using the meanings in [SYSTEM_LEVEL_WORKFLOW.md](SYSTEM_LEVEL_WORKFLOW.md); detailed step/result transition rules still require validation.
- **ASM-008:** Source Word files remain unmodified in the repository root during the initial consolidation.
- **ASM-009:** Superseded by DEC-060 and DEC-084. GitHub repository
  `seanmccarthyns/requirements-management-tool` is the shared remote source of truth.
- **ASM-010:** Dashboard values are computed only from records the current user is authorized to know exist.
- **ASM-011:** Fulfilled and superseded by DEC-046. Live demonstrations use the real application and persistent
  `FMSLIVE` data; the static showcase is retired.
- **ASM-012:** Confirmed: the second Version 3.3 change package is an SRCR linked to four PRs.

## Historical Open Questions Required Before Phase 1 Technical Planning

The table below is retained as the Phase 0 questionnaire. Phase 1 has been delivered, and several questions
were answered by later decisions and implementation. It is not an active GitHub backlog; unresolved future
choices are created as focused issues only when their trigger and acceptance boundary exist.

| ID | Question | Why It Matters | Decision Owner / Timing |
| --- | --- | --- | --- |
| OQ-001 | Which optional product/system/configuration layers are needed beyond the accepted `Program -> Project -> Software Product -> Software Release` default? | Determines applicability and baseline scope for programs that do not fit the software-oriented default | Product owner before domain/data design |
| OQ-002 | Should numeric sequences be global across all artifact types or unique within each prefix? The accepted base/suffix format is documented in [IDENTIFIERS_AND_REQUIREMENT_FIELDS_PROPOSAL.md](IDENTIFIERS_AND_REQUIREMENT_FIELDS_PROPOSAL.md). | Affects identifier generation and external references | Product/configuration stakeholders before data design |
| OQ-003 | Which proposed requirement-change fields must be complete before an SRCR can be submitted, and which existing FMS fields must be preserved or program-configurable? | Controls SRCR package validation, import, review, and generated documents | Requirements stakeholders before Phase 1 |
| OQ-004 | Which independence or organizational policy constraints, if any, may prevent the author from selecting a particular approver? | Controls review integrity while preserving accepted author authority and sequential replacement behavior | Quality/configuration/product stakeholders before Phase 2 design |
| OQ-006 | What are the allowed verification methods, and can one requirement require multiple methods? | Affects completeness logic and document output | Verification stakeholders before Phase 1 completion |
| OQ-007 | Are test case and test suite separate first-slice artifacts, or is procedure plus execution configuration sufficient? | Avoids redundant objects and unclear trace semantics | Verification stakeholders before Phase 3 design |
| OQ-008 | What detailed transition, amendment, and release-gate rules apply to the accepted Pass, Fail, Not Applicable, Blocked, and Not Run meanings? | Prevents misleading traceability and release status | Verification/quality stakeholders before Phase 3 |
| OQ-009 | When must a failed execution have a PR, anomaly, or formal disposition? | Controls completeness and release gates | Quality/program stakeholders before Phase 3 |
| OQ-010 | Should a successor SYSRD include a separate change summary listing retired requirements even though they are omitted from its effective requirement body? | Affects document change communication without weakening the effective-content rule | Product/configuration stakeholders before Phase 2 |
| OQ-011 | How are conflicting approved SRCRs affecting the same requirement ordered or resolved? | Required for deterministic candidate-baseline construction | Configuration/product stakeholders before Phase 2 |
| OQ-012 | Does reproducible document generation require byte-identical PDFs or content-equivalent outputs with explained metadata differences? | Drives generator, archive, validation, and platform constraints | Product/quality stakeholders before Phase 2 |
| OQ-013 | What legacy SYSRD structure and import quality should the first migration workflow support? | Import was requested but depends heavily on source format and validation needs | Product owner after sample documents are available |
| OQ-014 | What production data volumes, response-time targets, availability, RPO, and RTO are required? | Converts quality ambitions into testable architecture constraints | Operations/product owner before production architecture |
| OQ-015 | What GitHub organization/repository name, visibility, branch policy, and contributor workflow should be used? | Required before publishing the local repository | Repository owner before remote setup |
| OQ-016 | Which specific decisions and recurring tasks must the accepted Manager and System Engineer dashboards support first? | Determines dashboard information priority and avoids generic views | Product owner and representative users before showcase build |
| OQ-017 | What exact definitions, thresholds, applicability rules, and owners govern the initial dashboard measures? | Prevents misleading readiness and completeness indicators | Product/process owners before showcase validation |
| OQ-018 | Which details in the accepted FMS Version 3.3 fictional story need correction or richer realism before the showcase build? | Keeps the reusable prototype data credible without using sensitive real program data | Product owner before showcase build |

## Open Questions for Later Phases

- What program-defined feedback workflow applies to derived HLRs and LLRs?
- Which additional GitLab metadata or automated synchronization is useful after the exact LLR-to-merge MVP is
  proven, without expanding AeroLink into code management?
- Which optional PR classifications, attachments, or configurable closure rules are useful after the agreed
  lifecycle is exercised on real work?
- Which external identity, test, document, or issue systems need integration?
- Whether standards-plan management or compliance-objective mapping should ever enter product scope.
- Whether local AI assistance provides sufficient value after the controlled domain model is proven.
