# Release Roadmap

This roadmap is capability-driven, not calendar-driven. Each phase ends with evidence that its behavior works before the next phase broadens scope.

## Phase status at a glance

| Phase | Status |
| --- | --- |
| 0 — Documentation and domain validation | Complete |
| 0.5 — Interactive concept showcase | Complete; the prototype was retired on 2026-07-24 (DEC-046) |
| 1 — Platform skeleton and system-requirement control | Complete |
| 2 — SCRs, review, baselines, and SYSRD | Complete |
| 3 — System verification and traceability | Complete |
| 4 — Software-level lifecycle | Delivered: HLRs, LLRs, SWCRs, SWRDs, and software verification are implemented |
| 5 — PR management and integrations | Integration foundation delivered; broad Problem Report surface intentionally dormant |
| Enterprise maturity program | MVP program closed 2026-08-01; provider/deployment-specific boundaries remain conditional |

The phase goals and exit evidence below are retained as the definition of what each phase had to
prove. Live status is in [AEROLINK_3_IMPLEMENTATION_STATUS.md](AEROLINK_3_IMPLEMENTATION_STATUS.md)
and [PROJECT_STATE.md](PROJECT_STATE.md); this roadmap is not the status record.

## Phase 0: Documentation and Domain Validation

**Goal:** Establish an agreed product-definition baseline.

Deliverables:

- the authoritative Markdown document set;
- stable terminology, feature identifiers, scope boundaries, decisions, and open questions;
- source-material disposition;
- successful walkthrough of all eight system-level paper scenarios; and
- a reviewed go/no-go decision for technical discovery.

Exit criteria:

- no unresolved contradiction among authoritative documents;
- high-impact open questions for the first slice are resolved;
- the first-slice workflow has unambiguous actors, inputs, transitions, outputs, and retained history; and
- stakeholders approve the documentation baseline.

## Phase 0.5: Interactive Concept Showcase

**Complete.** The static-data prototype validated the information architecture and experience, and its
findings were absorbed into the product — including the July 2026 usability refresh that carried its
visual direction into the real application. The prototype was retired on 2026-07-24 (DEC-046) once the
product exceeded it. Demonstrations now use the application with the `FMSLIVE` dataset.

**Goal:** Validate the information architecture, terminology, dashboard measures, and end-to-end experience before production technical design.

Capabilities:

- a polished static-data web experience based on [DESIGN_VISION_AND_DASHBOARDS.md](DESIGN_VISION_AND_DASHBOARDS.md);
- manager and engineer dashboard modes;
- a coherent dashboard -> SCR -> requirement revision -> impact -> baseline -> test evidence -> traceability story;
- simulated filters, drill-downs, review states, evidence, and provenance; and
- explicit concept labeling with no production security, integrity, compliance, or architecture claim.

Exit evidence:

- representative managers and engineers can understand current status and next actions;
- every displayed metric has an agreed meaning and drill-down expectation;
- the showcase reveals and records terminology, workflow, navigation, and information-priority issues; and
- stakeholders decide whether to proceed to Phase 1 technical discovery and implementation planning.

## Phase 1: Platform Skeleton and System-Requirement Control

**Goal:** Prove secure controlled artifacts and revision history without attempting the full workflow.

Capabilities:

- programs, users, authentication, roles, administration, and sessions;
- globally unique stable identifiers;
- system requirement creation, formatted content/images, revisions, comparison, search, and audit;
- controlled attachments; and
- backup/restore foundations and operational logging.

Exit evidence:

- authorized users can create and revise requirements while unauthorized actions are blocked;
- approved-history rules and audit attribution survive concurrency and recovery tests; and
- no generated document or test feature is treated as complete yet.

## Phase 2: SCRs, Review, Baselines, and SYSRD

**Goal:** Prove the controlled change-to-document chain.

Capabilities:

- SCR introductions, modifications, retirements, target releases, and deferrals;
- author-selected ordered approval sequences, review-cycle snapshots, controlled future-approver substitution, cancellation/restart, comments, dispositions, pre-approval rework, rejection, unanimous approval, and independence;
- impact analysis and suspect links;
- candidate/approved baselines and comparison; and
- draft-watermarked and approved SYSRD generation with exact provenance and hashes.

Exit evidence:

- paper scenarios 1-5 pass as end-to-end product tests;
- a prior baseline remains unchanged after successor approval;
- a requirement revision authorized through an approved SCR is not confused with baseline inclusion; and
- document output can be explained and reproduced from exact controlled inputs.

## Phase 3: System Verification and Traceability

**Goal:** Complete the first usable system-level vertical slice.

Capabilities:

- reviewed, versioned system test procedures and steps;
- reusable many-to-many requirement verification links;
- external execution/result/evidence entry or import;
- failure, amendment, PR reference, and retest chains;
- completeness checks, interactive trace navigation, and impact analysis; and
- controlled system test, result, and traceability outputs.

Exit evidence:

- paper scenarios 6-8 pass end to end;
- users can identify missing, suspect, failed, incomplete, and unpassed chains;
- prior failures remain visible after successful retest; and
- a released requirement has a complete attributable change, baseline, document, verification, and audit story.

## Phase 4: Software-Level Lifecycle

**Goal:** Reuse proven platform patterns for HLRs, LLRs, SWCRs, SWRDs, and software verification.

Before implementation, refine derived-requirement workflows, allocation/refinement semantics, verification independence, and software-specific document structures.

## Enterprise Maturity Program

### 2026-07-13 enterprise control increment delivered

The current application now includes a complete first vertical increment for durable routing and SCR/SWCR exclusive editing: context-preserving artifact URLs, browser navigation, breadcrumbs, quick navigation, bounded Program-aware search, renewable checkout, server autosave snapshots, read-only observers, check-in/discard, forced unlock auditing, and review/checkout incompatibility enforcement. The same increment adds authentication rate limiting, broad Program-scope request enforcement, isolated browser-test topology, verified backup integrity, isolated PostgreSQL restore tooling, diagnostics, and controlled stop/start operations.

That recommendation is historical. Subsequent increments delivered universal controlled-editing foundations,
email outbox delivery, broad search/filtering, verification/retest/release journeys, build-scoped verification,
controlled Test Change Requests, downstream assessments, identity lifecycle administration, and exact software
upward allocation. Current continuation starts from a reproduced need and the
[current handoff](CURRENT_PRODUCT_HANDOFF_2026-08-01.md), not this old next-increment sentence.

### 2026-07-18 Requirements Explorer boundary delivered

The Requirements surface is now intentionally read-only: users browse specifications, inspect authoritative revisions, understand trace and verification context, review history and discussion, and see active controlled changes without editing requirement content. “Propose controlled change” creates a durable handoff to the dedicated SCR/SWCR editor with the selected requirement pre-populated. Governed CSV/XLSX onboarding also begins in Changes and creates a Draft package; direct bulk mutation and import entry points have been removed from Explorer. No AI-facing score, suggestion, assistant, or generative control is presented.

The market benchmark in [ENTERPRISE_REQUIREMENTS_MANAGEMENT_BENCHMARK.md](ENTERPRISE_REQUIREMENTS_MANAGEMENT_BENCHMARK.md) establishes a maturity program that cuts across the original domain phases:

1. **Enterprise Requirements Workspace:** configurable schemas, specification/module authoring, rich content, collaboration, universal search, saved views, bulk operations, redlines, and governed CSV/Excel onboarding.
2. **Open Digital Thread:** ReqIF 1.2, supported REST APIs, webhooks, OSLC RM, service identities, and monitored integrations.
3. **Product-Line Configuration and Reuse:** components, streams, change sets, controlled merge, governed libraries, synchronized reuse, variants, and composite configurations.
4. **Enterprise Operations and Identity Federation:** OIDC/SAML, SCIM, backup/restore, observability, retention, audit export, secure upgrades, and performance qualification.
5. **Risk, Compliance, and Portfolio Intelligence:** configurable risk/hazard/compliance artifacts and cross-program intelligence after subject-matter validation.

The Enterprise Requirements Workspace was recommended as the next implementation at the time of that
benchmark, and has since been delivered along with the Open Digital Thread and much of the product-line
configuration work. The current program and its per-workstream status are in
[AEROLINK_3_IMPLEMENTATION_STATUS.md](AEROLINK_3_IMPLEMENTATION_STATUS.md).

## Phase 5: PR Management and Integrations

**Goal:** Add the full PR lifecycle, PR-driven impact analysis, broader configuration/release functions, enterprise identity, and selected external references or integrations.

Retained Problem Report relationships support corrective routing, but the broad first-class Problem Reports
surface remains dormant by decision. A future PR increment requires a fresh product decision and validated
classification/lifecycle contract; retained code or this roadmap sentence is not authority to restore it.

## Post-MVP Identity and Account Hardening Backlog

Status as of 2026-07-24. The deferred items are deferred **by decision**, not merely unscheduled — see
the Workstream 4 decision record in
[AEROLINK_3_ENTERPRISE_LIFECYCLE_COMPLETION.md](AEROLINK_3_ENTERPRISE_LIFECYCLE_COMPLETION.md) for the
reason, the trigger to resume, and the order to resume in.

- [x] require temporary-password rotation at first sign-in — **delivered**; rotation is enforced before
      workspace access. Configurable password expiration is **deferred**;
- [ ] secure self-service account recovery with short-lived, single-use tokens, rate limiting,
      revocation, and complete audit history — **deferred**; also blocked on an email transport the
      product does not yet have;
- [x] multi-factor authentication and recovery codes — **delivered**, with encrypted secrets and
      downgrade protection. Step-up authentication for privileged actions is **deferred**; and
- [x] current role, session, and delegation administration — **delivered**; individual Program role revocation,
      current/other-session controls, and retained active/expired/revoked delegation history are qualified;
- [ ] enterprise identity federation and provisioning through OIDC/SAML and SCIM with a break-glass
      administrator path — **deferred**. The trusted group-to-Program-role mapping that federation will
      consume is delivered, persisted and audited.

## Future: Optional AI Assistance

AI may be considered only after the artifact, revision, workflow, traceability, and authorization models are mature. Any AI feature must remain suggestion-only, locally governable, provenance-recorded, and subject to explicit qualified human acceptance.

## Roadmap Governance

- Passing a phase is an evidence decision, not simply completion of a feature checklist.
- Later-phase needs may inform extensibility but cannot inflate earlier scope without a recorded decision.
- Dates, staffing, and technology choices will be planned after the Phase 0 baseline.
- Production readiness also requires the quality gates in [QUALITY_ATTRIBUTES.md](QUALITY_ATTRIBUTES.md), scaled appropriately by phase.
