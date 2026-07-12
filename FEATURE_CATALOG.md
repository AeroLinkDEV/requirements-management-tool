# Feature Catalog

This catalog is the authoritative capability inventory. Feature identifiers are stable and must not be reused. Priority uses `Must`, `Should`, or `Could`; phase indicates intended sequencing rather than a committed schedule.

## Platform Foundations

| ID | Capability | Rationale | Priority | Phase | Dependencies | Acceptance Outcome |
| --- | --- | --- | --- | --- | --- | --- |
| PF-001 | Multiple programs | Separate lifecycle data and access while supporting one installation | Must | 1 | None | Authorized users can work in permitted programs without viewing unauthorized program data |
| PF-002 | Authentication and sessions | Attribute actions to known users | Must | 1 | None | Users authenticate securely; sessions expire and can be revoked |
| PF-003 | Role- and action-based authorization | Enforce lifecycle responsibilities and least privilege | Must | 1 | PF-001, PF-002 | Access can be restricted by program, role, artifact type, state, and action |
| PF-004 | Administration | Operate users, roles, programs, and controlled policy | Must | 1 | PF-001-PF-003 | Authorized administrators manage configuration without erasing controlled history |
| PF-005 | Stable global identifiers | Prevent ambiguous or reused artifact numbers across programs | Must | 1 | PF-001 | Every controlled artifact receives a globally unique, never-reused identity |
| PF-006 | Artifact revision framework | Preserve controlled history consistently | Must | 1 | PF-005 | Draft and immutable approved revisions are distinguishable and retrievable |
| PF-007 | Append-only audit history | Prove who did what and when | Must | 1 | PF-002, PF-006 | Every material action produces a queryable audit event |
| PF-008 | Controlled attachments | Retain images and evidence with provenance and integrity | Must | 1 | PF-005-PF-007 | Authorized users attach, retrieve, version, and integrity-check files |
| PF-009 | Search and navigation | Make controlled records usable at organizational scale | Should | 1 | PF-001, PF-005 | Users find permitted artifacts by identifier, content, type, state, and release |

## Dashboards and Decision Support

| ID | Capability | Rationale | Priority | Phase | Dependencies | Acceptance Outcome |
| --- | --- | --- | --- | --- | --- | --- |
| DASH-001 | Role-aware dashboard framework | Give each user relevant progress, risk, and work without creating competing sources of truth | Must | 1-3 | PF-001-PF-003, PF-007 | Managers, engineers, CM/quality, and administrators receive authorized views derived from the same controlled records |
| DASH-002 | Manager release-readiness dashboard | Make progress, bottlenecks, completeness, and risk understandable at program/release level | Must | 2-3 | BL-001, WF-001, TR-001 | Managers see scoped readiness and can drill into every contributing or blocking record |
| DASH-003 | Engineer work dashboard | Turn assignments, change impact, suspect links, and verification gaps into actionable work | Must | 2-3 | WF-001, SCR-001, TR-002 | Engineers can navigate directly from their dashboard to the exact artifact and required action |
| DASH-004 | Configuration and quality dashboard | Surface baseline, approval, document, audit, and integrity exceptions | Should | 2-3 | BL-001, DOC-001, PF-007 | Authorized users can identify and investigate every blocking control exception |
| DASH-005 | Trusted metric contracts | Prevent unexplained or misleading summary indicators | Must | 1-3 | PF-007, domain-specific features | Every important metric exposes definition, scope, freshness, source records, owner, authorization behavior, and drill-down |
| DASH-006 | Shareable filtered views and controlled exports | Preserve context when dashboard evidence is discussed or reported | Should | 3 | DASH-001-DASH-005 | Shared/exported views identify scope, filters, time, and provisional/final state without exposing unauthorized data |
| SHOW-001 | Interactive concept showcase | Validate desirability and workflows before production architecture | Must | 0.5 | Approved Phase 0 baseline, design vision | Stakeholders can navigate the complete fictional dashboard-to-trace story with no production claims |

## System Requirements and Change

| ID | Capability | Rationale | Priority | Phase | Dependencies | Acceptance Outcome |
| --- | --- | --- | --- | --- | --- | --- |
| SR-001 | System requirement records | Establish authoritative structured requirements | Must | 1 | PF-001, PF-005-PF-008 | Users create system requirements with required content, attributes, and images |
| SR-002 | Requirement revisions | Preserve changes without changing stable identity | Must | 1 | SR-001, PF-006 | Users can compare and retrieve every revision of a requirement |
| SR-003 | Requirement retirement | Remove future applicability without erasing history | Must | 2 | SR-002, SCR-001 | Approved retirement affects successor baselines only |
| SR-004 | Verification method and derived status | Support planning and completeness checks | Must | 1 | SR-001 | Each applicable requirement records controlled verification and derived information |
| SCR-001 | SCR records and revisions | Explain and control system requirement change | Must | 2 | PF-005-PF-008, SR-001 | One SCR revision can propose multiple introductions, modifications, and retirements |
| SCR-002 | SCR review and approval | Prevent uncontrolled changes | Must | 2 | SCR-001, WF-001 | Comments, dispositions, rework, rejection, and approval are fully attributable |
| SCR-003 | Target release and deferral | Control which approved changes enter a release | Must | 2 | SCR-001, BL-001 | SCRs can be targeted, selected, deferred, and retargeted with retained rationale |
| SCR-004 | Impact analysis | Expose affected links and evidence before change approval | Must | 2 | TR-001, SCR-001 | Reviewers see affected artifacts and unresolved suspect links before approval |

## Workflow, Baselines, and Documents

| ID | Capability | Rationale | Priority | Phase | Dependencies | Acceptance Outcome |
| --- | --- | --- | --- | --- | --- | --- |
| WF-001 | Review workflows | Produce controlled review evidence | Must | 2 | PF-002, PF-003, PF-006 | Named reviewers can comment, disposition, reject, request rework, and approve exact revisions |
| WF-002 | Approval policy | Enforce unanimous required-reviewer approval and independence | Must | 2 | WF-001 | Approval is blocked unless every assigned required reviewer approves the exact revision and comment/independence rules are met |
| BL-001 | Releases and candidate baselines | Assemble controlled successor configurations | Must | 2 | SR-002, SCR-002 | A candidate records predecessor and exact selected revisions |
| BL-002 | Immutable approved baselines | Preserve exact released content | Must | 2 | BL-001, WF-002 | Approved baseline contents cannot be edited; correction creates a successor |
| BL-003 | Baseline comparison | Explain changes between releases | Must | 2 | BL-002 | Users see introduced, modified, retired, and unchanged artifacts between baselines |
| DOC-001 | Draft SYSRD generation | Review candidate contents before approval | Must | 2 | BL-001 | A deterministic draft SYSRD is generated with visible `DRAFT` marking and source metadata |
| DOC-002 | Approved SYSRD generation | Produce a controlled requirements document | Must | 2 | BL-002, WF-002 | Approved output identifies exact inputs, revisions, approvals, and file hash |
| DOC-003 | Reproducible controlled output records | Support long-term explanation and regeneration | Must | 2 | DOC-001, DOC-002 | Every output records baseline, template, generator version, time, state, and hash |

## System Verification and Traceability

| ID | Capability | Rationale | Priority | Phase | Dependencies | Acceptance Outcome |
| --- | --- | --- | --- | --- | --- | --- |
| ST-001 | Versioned test procedures and steps | Control reusable verification definitions | Must | 3 | PF-005-PF-008, WF-001 | Users author, review, approve, and revise procedures without overwriting history |
| ST-002 | Requirement-procedure relationships | Establish many-to-many verification intent | Must | 3 | ST-001, SR-002, TR-001 | Approved typed links are navigable in both directions |
| ST-003 | Test suites or campaigns | Group tests by meaningful execution context | Should | 3 | ST-001 | Users can group procedures without confusing grouping with execution configuration |
| ST-004 | External execution entry/import | Capture testing performed outside the platform | Must | 3 | ST-001, PF-008 | An execution records exact procedure revision, configuration, performer, time, and provenance |
| ST-005 | Results and evidence | Retain pass/fail/NA outcomes and supporting files | Must | 3 | ST-004 | Completed results and evidence are immutable and reviewable |
| ST-006 | Failure, amendment, and retest chain | Preserve complete verification history | Must | 3 | ST-005 | Failures, clerical amendments, corrections, and retests remain distinctly linked |
| TR-001 | Typed, version-aware trace links | Make relationships meaningful and auditable | Must | 2 | PF-005-PF-007 | Links record type, direction, endpoints, rationale, actor, state, and history |
| TR-002 | Suspect-link management | Require reassessment after change | Must | 2 | TR-001, SR-002 | Defined changes flag affected links until reviewed or replaced |
| TR-003 | Completeness and anomaly checks | Detect missing or invalid lifecycle evidence | Must | 3 | ST-002, ST-005, TR-001 | Users can identify unverified, unpassed, orphaned, failed, and suspect chains |
| TR-004 | Interactive impact and trace views | Answer lifecycle questions without a static matrix | Must | 3 | TR-001-TR-003 | Users navigate full upward/downward chains and release contents |
| DOC-004 | System test and traceability outputs | Produce controlled review and release evidence | Must | 3 | ST-001-ST-006, TR-003 | Generated documents identify exact source baseline and evidence records |

## Later Software and PR Capabilities

| ID | Capability | Rationale | Priority | Phase | Dependencies | Acceptance Outcome |
| --- | --- | --- | --- | --- | --- | --- |
| SW-001 | HLR and LLR management | Extend controlled requirements down the software V | Must | 4 | Proven system-level model | HLRs/LLRs support identity, revisions, derived status, review, baseline, and upward trace |
| SW-002 | SWCR and SWRD lifecycle | Control software change and document generation | Must | 4 | SW-001, reusable SCR/baseline framework | Approved SWCRs produce exact software baselines and controlled SWRDs |
| SW-003 | Software verification artifacts | Verify HLRs, LLRs, integration, and robustness behavior | Must | 4 | SW-001, reusable verification framework | Software requirements trace to reviewed procedures, executions, results, and evidence |
| PR-001 | Full PR lifecycle | Control investigation, disposition, resolution, and closure | Should | 5 | PF foundations, TR-001 | PR state, classification, effects, resolution, verification, and closure are attributable |
| PR-002 | PR-driven impact analysis | Build the change story across artifacts and releases | Should | 5 | PR-001, TR-004 | Users navigate from a PR through affected changes, requirements, tests, results, and release |
| INT-001 | Enterprise identity | Integrate with organizational authentication | Should | 5 | PF-002, deployment decisions | Authorized enterprise identities and groups can be mapped to product roles |
| INT-002 | Git/build/reference integrations | Link controlled lifecycle data to external implementation evidence | Could | 5 | SW-001, integration policy | Users follow immutable external references without the platform becoming a source-code host |
| AI-001 | Human-controlled local AI suggestions | Assist drafting and analysis without authority | Could | Future | Mature domain model and governance | Suggestions are labeled, provenance-recorded, draft-only, and explicitly accepted by a human |

## Catalog Rules

- New features require a new stable identifier and a recorded scope decision.
- A feature may move phases only with rationale in [DECISIONS_AND_OPEN_QUESTIONS.md](DECISIONS_AND_OPEN_QUESTIONS.md).
- Acceptance outcomes describe observable behavior; implementation design belongs in later documents.
- Phase 1-3 features define the first complete system-level slice. Phase 4 and later do not block its validation unless a foundation would make them impossible.
