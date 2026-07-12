# Source Material Traceability

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
| Human review and approval of requirements and documents | Corrected/First slice | Requirement changes are reviewed within the complete SCR package; controlled documents have their own review/approval workflow |
| HLRs linked to system requirements; derived requirements; software requirements document | Later | [SCOPE_AND_BOUNDARIES.md](SCOPE_AND_BOUNDARIES.md), Later Product Capabilities; features SW-001/SW-002 |
| LLRs linked to HLRs or derived | Later | Feature SW-001 |
| System requirements linked to one or more system tests | First slice | Features ST-001/ST-002 and workflow Sections 6-8 |
| Capture results and generate system test result/traceability documents | First slice | Features ST-004-ST-006, TR-003/TR-004, DOC-004 |
| Equivalent software testing for HLRs and LLRs | Later | Feature SW-003 |
| PR management as a possible module with lifecycle and broad linkages | Later | Features PR-001/PR-002; external PR references allowed initially |
| Full traceability document | Accepted incrementally | System traceability in Phase 3; complete system/software traceability after Phase 4 |
| All produced documents as PDF | Corrected/Open | Controlled PDF is the intended output direction; exact document formats and reproducibility definition remain OQ-012 |
| Multiple programs, 150+ concurrent users, on-premises, username/password, admin portal | Accepted | [PROJECT_VISION.md](PROJECT_VISION.md), [QUALITY_ATTRIBUTES.md](QUALITY_ATTRIBUTES.md), features PF-001-PF-004 |

## `System Level.docx`

| Source Intent | Disposition | Authoritative Destination |
| --- | --- | --- |
| SCR introduces, modifies, or deletes requirements | Corrected/First slice | “Delete” is controlled retirement; workflow Sections 3-4; DEC-007 |
| SCR numbering and revisions | First slice | Glossary and feature SCR-001; exact syntax is OQ-002 |
| One SCR contains multiple requirement changes | First slice | SCR definition and workflow Required SCR Content |
| Multi-person review, comments, rejection, approval | First slice | Workflow Section 3; features WF-001/WF-002 |
| SCR targets a software/release version and may skip an upcoming version | Corrected/First slice | Release target and controlled deferral, SCR-003; Scenario 4 |
| SCR problem, analysis, solution, plus individual changes | First slice | Workflow Required SCR Content |
| Link PRs to SCRs | Accepted incrementally | External PR references initially; full PR lifecycle later |
| Globally unique requirement numbers, never reused across projects | First slice | Principle 2; feature PF-005; exact syntax OQ-002 |
| Requirement revisions and complete history | First slice | Glossary; feature SR-002 |
| Verification method and derived status | First slice | Feature SR-004; allowed values OQ-006 |
| Future upward linkage to ICD or similar | Later | Scope allows upstream/interface artifacts; precise model deferred |
| Requirement images | First slice | Workflow Section 4; feature SR-001/PF-008 |
| SYSRD contains approved system requirements | Corrected/First slice | SYSRD contains exact approved revisions selected in an approved baseline |
| Current baseline plus selected approved SCRs creates next SYSRD | First slice | Workflow Section 5 |
| Draft SYSRD with watermark | First slice | Feature DOC-001; Scenario 5 |
| Test case and procedure levels, but procedure-first may be preferable | Corrected/Open | DEC-009 chooses procedure-first; separate test-case need is OQ-007 |
| Many-to-many requirement/procedure links and test reuse | First slice | Workflow Section 6; Scenario 6 |
| Unique/versioned/reviewed system tests | First slice | Feature ST-001 |
| External testing with results fed back | First slice | DEC-008; feature ST-004 |
| Test suite such as real or simulated engine | Open/First-slice candidate | Feature ST-003; semantics in OQ-007 |
| System tests linked to PRs and evidence | Accepted incrementally | Evidence is first slice; external/full PR linking matures with PR module |

## `Software Level.docx`

| Source Intent | Disposition | Authoritative Destination |
| --- | --- | --- |
| Reuse most system behavior for HLRs, LLRs, and tests | Later | Phase 4 and features SW-001-SW-003 |
| Do not manage code initially | Excluded now | [SCOPE_AND_BOUNDARIES.md](SCOPE_AND_BOUNDARIES.md) |
| SWCR and SWRD | Later | Feature SW-002; `SWRD` terminology in DEC-006 |
| Unique HLR/LLR identities, derived status, PR links, and tests | Later | Features SW-001/SW-003 and later PR integration |
| Repeated system-level material | Consolidated | Disposition is captured in the `System Level.docx` table above |

## `PR Details.docx`

| Source Intent | Disposition | Authoritative Destination |
| --- | --- | --- |
| PR module remains in the conversation | Later | Features PR-001/PR-002 |
| PR contains description, dates, originator, type, severity, and other controlled fields | Later/Open | Full field set will be decided before Phase 5 |
| PR lifecycle includes new, approval, in-work, closed, and rejected states | Later/Open | PR lifecycle must be refined before Phase 5 |
| PRs link broadly across requirements, tests, changes, and releases | Later | Feature PR-002; initial external PR references preserved |
| PR-driven impact analysis is crucial | Later strategic priority | Feature PR-002 |

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
| Full PR schema and lifecycle | Later | PR-001/PR-002; exact policy remains later work |
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
