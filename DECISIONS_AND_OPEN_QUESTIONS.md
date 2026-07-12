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

## Working Assumptions

Assumptions are not decisions. They remain valid only until confirmed or replaced.

- **ASM-001:** Product- and behavior-level definitions precede technical architecture, data schema, and UI design.
- **ASM-002:** Artifact numbers are globally unique across programs; exact prefixes, digit lengths, and revision display syntax are configurable or decided later.
- **ASM-003:** The first slice supports multiple programs even if initial validation uses one reference program.
- **ASM-004:** Requirements may include controlled images/figures as part of revisioned content.
- **ASM-005:** Exact review roles, quorum, and independence vary by organization/program and require controlled configuration.
- **ASM-006:** PR references may point to an external system until full PR management exists.
- **ASM-007:** The initial platform records at least Pass, Fail, and Not Applicable; additional operational states require precise definitions.
- **ASM-008:** Source Word files remain unmodified in the repository root during the initial consolidation.
- **ASM-009:** GitHub will eventually become the shared remote source of truth, but no repository details are assumed.

## Open Questions Required Before Phase 1 Technical Planning

| ID | Question | Why It Matters | Decision Owner / Timing |
| --- | --- | --- | --- |
| OQ-001 | What is the exact program/project/product/system/configuration hierarchy? | Determines ownership, access, identifiers, applicability, and baseline scope | Product owner before domain/data design |
| OQ-002 | What global identifier and revision display conventions are required for SCRs, requirements, procedures, executions, and documents? | Affects migration, usability, external references, and never-reuse rules | Product/configuration stakeholders before data design |
| OQ-003 | Which requirement fields are mandatory in the first slice, and which are program-configurable? | Controls validation, import, review, and SYSRD content | Requirements stakeholders before Phase 1 |
| OQ-004 | Which workflow states, reviewer roles, quorum rules, and independence constraints are mandatory versus configurable? | Controls the review/approval engine and authorization model | Quality/configuration/product stakeholders before Phase 2 design |
| OQ-005 | What constitutes an electronic approval, and is password re-entry or another signature ceremony required? | Affects identity, audit evidence, usability, and policy | Security/quality stakeholders before Phase 2 |
| OQ-006 | What are the allowed verification methods, and can one requirement require multiple methods? | Affects completeness logic and document output | Verification stakeholders before Phase 1 completion |
| OQ-007 | Are test case and test suite separate first-slice artifacts, or is procedure plus execution configuration sufficient? | Avoids redundant objects and unclear trace semantics | Verification stakeholders before Phase 3 design |
| OQ-008 | What exact meanings and approval effects apply to Pass, Fail, Not Applicable, Blocked, and Not Run? | Prevents misleading traceability and release status | Verification/quality stakeholders before Phase 3 |
| OQ-009 | When must a failed execution have a PR, anomaly, or formal disposition? | Controls completeness and release gates | Quality/program stakeholders before Phase 3 |
| OQ-010 | How are requirement retirements presented in a successor SYSRD: omitted, listed in a change section, or marked retired? | Affects controlled document semantics and historical clarity | Product/configuration stakeholders before Phase 2 |
| OQ-011 | How are conflicting approved SCRs affecting the same requirement ordered or resolved? | Required for deterministic candidate-baseline construction | Configuration/product stakeholders before Phase 2 |
| OQ-012 | Does reproducible document generation require byte-identical PDFs or content-equivalent outputs with explained metadata differences? | Drives generator, archive, validation, and platform constraints | Product/quality stakeholders before Phase 2 |
| OQ-013 | What legacy SYSRD structure and import quality should the first migration workflow support? | Import was requested but depends heavily on source format and validation needs | Product owner after sample documents are available |
| OQ-014 | What production data volumes, response-time targets, availability, RPO, and RTO are required? | Converts quality ambitions into testable architecture constraints | Operations/product owner before production architecture |
| OQ-015 | What GitHub organization/repository name, visibility, branch policy, and contributor workflow should be used? | Required before publishing the local repository | Repository owner before remote setup |

## Open Questions for Later Phases

- What program-defined feedback workflow applies to derived HLRs and LLRs?
- Which architecture, source, Git, build, and release references provide useful traceability without expanding into code management?
- What complete PR classification, lifecycle, field set, and closure rules are required?
- Which external identity, test, document, or issue systems need integration?
- Whether standards-plan management or compliance-objective mapping should ever enter product scope.
- Whether local AI assistance provides sufficient value after the controlled domain model is proven.
