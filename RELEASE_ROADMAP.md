# Release Roadmap

This roadmap is capability-driven, not calendar-driven. Each phase ends with evidence that its behavior works before the next phase broadens scope.

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

The next increment should extend the proven edit-session contract to requirement proposals, specification structures, test procedures, trace links, and release-planning drafts; complete email outbox delivery; broaden universal search facets and pagination; and add browser journeys for the remaining controlled document, verification/retest, and release-approval paths.

The market benchmark in [ENTERPRISE_REQUIREMENTS_MANAGEMENT_BENCHMARK.md](ENTERPRISE_REQUIREMENTS_MANAGEMENT_BENCHMARK.md) establishes a maturity program that cuts across the original domain phases:

1. **Enterprise Requirements Workspace:** configurable schemas, specification/module authoring, rich content, collaboration, universal search, saved views, bulk operations, redlines, and governed CSV/Excel onboarding.
2. **Open Digital Thread:** ReqIF 1.2, supported REST APIs, webhooks, OSLC RM, service identities, and monitored integrations.
3. **Product-Line Configuration and Reuse:** components, streams, change sets, controlled merge, governed libraries, synchronized reuse, variants, and composite configurations.
4. **Enterprise Operations and Identity Federation:** OIDC/SAML, SCIM, backup/restore, observability, retention, audit export, secure upgrades, and performance qualification.
5. **Risk, Compliance, and Portfolio Intelligence:** configurable risk/hazard/compliance artifacts and cross-program intelligence after subject-matter validation.

The Enterprise Requirements Workspace is the next recommended massive implementation. It turns the existing assurance-control core into a high-throughput daily engineering environment and establishes the model needed for later interchange, reuse, configuration, and reporting.

## Phase 5: PR Management and Integrations

**Goal:** Add the full PR lifecycle, PR-driven impact analysis, broader configuration/release functions, enterprise identity, and selected external references or integrations.

PR scope will include classification, effects, investigation, resolution, verification, closure, alternative dispositions, and links across requirements, tests, changes, builds, and releases.

## Post-MVP Identity and Account Hardening Backlog

These controls remain committed follow-up work, but they do not displace the current showcase and usability priority:

- [ ] require temporary-password rotation at first sign-in and support configurable password-expiration policy;
- [ ] add secure self-service account recovery with short-lived, single-use tokens, rate limiting, revocation, and complete audit history;
- [ ] add multi-factor authentication, recovery codes, and step-up authentication for privileged actions; and
- [ ] add enterprise identity federation and provisioning through OIDC/SAML and SCIM while retaining a tightly controlled break-glass administrator path.

## Future: Optional AI Assistance

AI may be considered only after the artifact, revision, workflow, traceability, and authorization models are mature. Any AI feature must remain suggestion-only, locally governable, provenance-recorded, and subject to explicit qualified human acceptance.

## Roadmap Governance

- Passing a phase is an evidence decision, not simply completion of a feature checklist.
- Later-phase needs may inform extensibility but cannot inflate earlier scope without a recorded decision.
- Dates, staffing, and technology choices will be planned after the Phase 0 baseline.
- Production readiness also requires the quality gates in [QUALITY_ATTRIBUTES.md](QUALITY_ATTRIBUTES.md), scaled appropriately by phase.
