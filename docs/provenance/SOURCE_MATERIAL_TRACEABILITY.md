# Source Material Traceability

> **Source-disposition record.** The root `.docx` files are retained, unmodified historical inputs. This
> trace explains how their ideas entered the product definition; labels such as **First slice**, **Later**,
> and **Open** reflect the decision point when this trace was written and are not a current backlog. Use
> [Project State](PROJECT_STATE.md) for delivered behavior and current boundaries; the former
> [2026-08-10 handoff](docs/archive/CURRENT_PRODUCT_HANDOFF_2026-08-10.md) is retained as historical restart context.

This document records how substantive ideas from the supplied source files were dispositioned. It is a product-intent trace, not a line-by-line legal comparison.

Disposition values:

- **Accepted:** Included in the current product definition.
- **First slice:** Included in the Phase 1-3 system-level vertical slice.
- **Later:** Preserved as a planned later capability.
- **Excluded now:** Explicitly outside the near-term scope.
- **Open:** Preserved as a question requiring stakeholder input.
- **Corrected:** Retained with clarified terminology or behavior.

## `Project Vision.docx`

| Source Intent | Disposition | Authoritative Destination |
| --- | --- | --- |
| Web tool covering much of the system/software development-assurance V | Corrected | [PROJECT_VISION.md](PROJECT_VISION.md): lifecycle-data platform with explicit exclusions and no “complete lifecycle” claim |
| Capture and change system requirements; generate revised system requirements document | First slice | [SYSTEM_LEVEL_WORKFLOW.md](SYSTEM_LEVEL_WORKFLOW.md), Sections 3-5 |
| Human review and approval of requirements and documents | Corrected/First slice | Requirement changes are reviewed within the complete SRCR package; controlled documents have their own review/approval workflow |
| HLRs linked to system requirements; derived requirements; software requirements document | Delivered | Features SW-001/SW-002; exact upward allocation and controlled HLR documents are implemented |
| LLRs linked to HLRs or derived | Delivered | Feature SW-001; LLR scope and exact HLR allocation are implemented |
| System requirements linked to one or more system tests | First slice | Features ST-001/ST-002 and workflow Sections 6-8 |
| Capture results and generate system test result/traceability documents | First slice | Features ST-004-ST-006, TR-003/TR-004, DOC-004 |
| Equivalent software testing for HLRs and LLRs | Delivered | Feature SW-003; each level has isolated procedures, coverage, TCRs and results |
| PR management as a possible module with lifecycle and broad linkages | Delivered MVP | Features PR-001/PR-002 and DEC-087; the agreed lifecycle, fields, filters, corrective actions, test evidence, and independent closure are delivered |
| Full traceability document | Accepted incrementally | System traceability in Phase 3; complete system/software traceability after Phase 4 |
| All produced documents as PDF | Corrected/Open | Controlled PDF is the intended output direction; exact document formats and reproducibility definition remain OQ-012 |
| Multiple programs, 150+ concurrent users, on-premises, username/password, admin portal | Accepted | [PROJECT_VISION.md](PROJECT_VISION.md), [QUALITY_ATTRIBUTES.md](QUALITY_ATTRIBUTES.md), features PF-001-PF-004 |

## `System Level.docx`

| Source Intent | Disposition | Authoritative Destination |
| --- | --- | --- |
| SRCR introduces, modifies, or deletes requirements | Corrected/First slice | “Delete” is controlled retirement; workflow Sections 3-4; DEC-007 |
| SRCR numbering and revisions | First slice | Glossary and feature SRCR-001; exact syntax is OQ-002 |
| One SRCR contains multiple requirement changes | First slice | SRCR definition and workflow Required SRCR Content |
| Multi-person review, comments, rejection, approval | First slice | Workflow Section 3; features WF-001/WF-002 |
| SRCR targets a software/release version and may skip an upcoming version | Corrected/First slice | Release target and controlled deferral, SRCR-003; Scenario 4 |
| SRCR problem, analysis, solution, plus individual changes | First slice | Workflow Required SRCR Content |
| Link PRs to SRCRs | Delivered incrementally | Controlled PR selection on SRCRs, approved corrective-action projection, and verification evidence are delivered; broader lifecycle policy remains later |
| Globally unique requirement numbers, never reused across projects | First slice | Principle 2; feature PF-005; exact syntax OQ-002 |
| Requirement revisions and complete history | First slice | Glossary; feature SR-002 |
| Verification method and derived status | First slice | Feature SR-004; allowed values OQ-006 |
| Future upward linkage to ICD or similar | Later | Scope allows upstream/interface artifacts; precise model deferred |
| Requirement images | First slice | Workflow Section 4; feature SR-001/PF-008 |
| SYSRD contains approved system requirements | Corrected/First slice | SYSRD contains exact approved revisions selected in an approved baseline |
| Current baseline plus selected approved SRCRs creates next SYSRD | First slice | Workflow Section 5 |
| Draft SYSRD with watermark | First slice | Feature DOC-001; Scenario 5 |
| Test case and procedure levels, but procedure-first may be preferable | Corrected/Open | DEC-009 chooses procedure-first; separate test-case need is OQ-007 |
| Many-to-many requirement/procedure links and test reuse | First slice | Workflow Section 6; Scenario 6 |
| Unique/versioned/reviewed system tests | First slice | Feature ST-001 |
| External testing with results fed back | First slice | DEC-008; feature ST-004 |
| Test suite such as real or simulated engine | Open/First-slice candidate | Feature ST-003; semantics in OQ-007 |
| System tests linked to PRs and evidence | Delivered incrementally | TCR and execution evidence can remain in the PR causal thread; broader PR policy remains later |

## `Software Level.docx`

| Source Intent | Disposition | Authoritative Destination |
| --- | --- | --- |
| Reuse most system behavior for HLRs, LLRs, and tests | Delivered | HLR/LLR requirements, controlled procedures, downstream assessments, and verification workflows are build scoped |
| Do not manage code initially | Boundary preserved | GitLab manages code; AeroLink now stores only exact approved LLR-to-merge traceability pointers under DEC-088 |
| software change request and SWRD | Delivered incrementally | software change request editing/review and controlled HLR/LLR document generation are delivered; later increments may deepen document policy |
| Unique HLR/LLR identities, derived status, PR links, and tests | Delivered incrementally | Features SW-001/SW-003 and PR-002; exact-revision traces and build isolation are enforced |
| Repeated system-level material | Consolidated | Disposition is captured in the `System Level.docx` table above |

## `PR Details.docx`

| Source Intent | Disposition | Authoritative Destination |
| --- | --- | --- |
| PR module remains in the conversation | Delivered MVP | Features PR-001/PR-002 and DEC-087/DEC-089 |
| PR contains description, dates, originator, type, severity, and other controlled fields | Delivered selectively | Immutable origin/date, rich description and supporting fields, target build, owner, corrective action, root cause, impact decisions, closure date, and evidence are delivered; optional classifications remain later |
| PR lifecycle includes new, approval, in-work, closed, and rejected states | Delivered with product terminology | Draft, Ready for SCCB, Open, Implementing, Verifying, Awaiting SQA Closure, and Closed are controlled states |
| PRs link broadly across requirements, tests, changes, and releases | Delivered incrementally | Feature PR-002; change request/TCR selection, corrective actions, and test evidence are implemented |
| PR-driven impact analysis is crucial | Delivered incrementally | Feature PR-002; requirement change remains downstream of a PR, not an automatic PR creator |

## `Things I dont Need Tool to do.docx`

| Source Intent | Disposition | Authoritative Destination |
| --- | --- | --- |
| No plans and standards management | Excluded now | [SCOPE_AND_BOUNDARIES.md](SCOPE_AND_BOUNDARIES.md) |
| No architecture, design, or implementation management | Excluded now | Scope boundaries; later external references may be considered |
| Tool qualification not necessary | Excluded now | DEC-003 and scope boundaries |
| No AI now; revisit later | Excluded now/Later | Feature AI-001 and future roadmap |

## `Initial Response from Chatgpt.docx`

| Source Theme | Disposition | Authoritative Destination |
| --- | --- | --- |
| Product is feasible but production assurance is much harder than a prototype | Accepted | Production ambition, phased roadmap, and quality attributes |
| System level should be distinguished from DO-178 software scope | Corrected | Standards posture in [PROJECT_VISION.md](PROJECT_VISION.md) |
| Database/artifact platform rather than document master | Accepted | DEC-005 and Product Principle 1 |
| Stable identities, immutable revisions, typed links, baselines, audit, controlled documents | Accepted | Domain glossary and Product Principles 2-11 |
| Review independence, quorum, signatures, comments, and separate artifact/baseline approval | Corrected/Open | The author selects an ordered approval sequence and unanimous sequential approval is required; future approvers may be replaced, while a wrong completed approver forces cancellation/restart. Independence and approval ceremony remain OQ-004/OQ-005 |
| Test procedure, execution, result, configuration, evidence, failure, and retest are distinct | Accepted | Glossary and workflow Sections 6-7 |
| Interactive traceability and completeness analysis | Accepted | Features TR-003/TR-004 |
| Full PR schema and lifecycle | Delivered agreed MVP / Later options | PR-001/PR-002 and DEC-087 deliver the chosen fields and lifecycle; unrequested classifications, attachments, containment/preventive action, and configurable policy remain later |
| Plans/standards, architecture/code/builds, broader verification/CM/QA/certification functions | Mostly excluded now or later | Explicit scope boundaries and future phases prevent accidental first-slice expansion |
| On-premises modular-monolith technology suggestion | Deferred | No technology or architecture decision is authorized in Phase 0 |
| AI suggestions under human control | Later | Feature AI-001 and Product Principle 12 |
| Broad MVP spanning system, HLR, LLR, tests, and PRs | Corrected | Replaced by the system-level first slice in DEC-004 |

## Original `PROJECT_GOAL_Aerospace_Development_Assurance_Platform.md`

| Original Theme | Disposition | Authoritative Destination |
| --- | --- | --- |
| “Most” or “complete” lifecycle language | Corrected | Reconciled project goal and explicit scope boundaries |
| System, HLR, LLR, test, traceability, PR, CM, documents, audit, security, AI, and technical stack in one goal | Corrected | Separated across phased feature catalog, roadmap, and quality attributes |
| Initial MVP as a thin full V | Corrected | System-level first slice |
| Named React/Angular, .NET/Java, PostgreSQL, identity and deployment choices | Deferred | Technical discovery follows the Phase 0 baseline |
| Reliability through constraints, immutable history, tests, and recovery rather than infallibility | Accepted | Product Principle 14 and quality attributes |
| AI cannot be authoritative | Accepted for future | Product Principle 12 and AI-001 |

## Completeness Statement

The source material has been consolidated into accepted scope, first-slice behavior, later capability, explicit exclusion, correction, assumption, or open question. Repeated source passages are mapped once to their consolidated destination. No original Word file was modified.
