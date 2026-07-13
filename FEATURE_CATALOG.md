# Feature Catalog

This catalog is the authoritative capability inventory. Feature identifiers are stable and must not be reused. Priority uses `Must`, `Should`, or `Could`; phase indicates intended sequencing rather than a committed schedule.

Implementation status is tracked against acceptance outcomes, not feature titles. As of 2026-07-12, Wave 1 has integrated foundations for EA-001 through EA-004, COL-001 through COL-003, SRCH-001, SRCH-002, BULK-001, EXCH-001, and EXCH-002. Controlled authoring remains inside Draft SCR/SWCR authority. Controlled attachment versions and integrity verification, structured/attachment redlines, a visual saved-query builder with stable URLs, idempotent downloadable background exports, retained three-way merge conflicts, integrity checkpoints, a 50,000-requirement PostgreSQL qualification dataset, and a 150-client mixed database workload now join the established impact, collaboration, interchange, and work-management capabilities. Remaining acceptance depth includes embedded inline-image rendering, import-worker resumability, browser-level concurrency at production topology, and operational backup/restore drills.

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

## Enterprise Authoring and Collaboration

| ID | Capability | Rationale | Priority | Phase | Dependencies | Acceptance Outcome |
| --- | --- | --- | --- | --- | --- | --- |
| EA-001 | Configurable artifact schemas | Support different Program processes without code or database changes | Must | Enterprise Wave 1 | PF-001, PF-003, PF-006 | Administrators define artifact types, fields, enumerations, requiredness, validation, display rules, and relationship rules with versioned configuration history |
| EA-002 | Specification and module hierarchy | Let engineers work in structured documents while retaining stable artifact identities | Must | Enterprise Wave 1 | EA-001, PF-005, PF-006 | Requirements appear in ordered section trees with headings, numbering, placement history, drag/reorder, and reuse without coupling identity to position |
| EA-003 | Rich requirement content | Capture technically complete requirements and supporting context | Must | Enterprise Wave 1 | EA-001, PF-008 | Revisions support formatted text, lists, tables, images, attachments, symbols, controlled references, sanitization, and deterministic rendering |
| EA-004 | High-density authoring modes | Make large specifications efficient for daily engineering work | Must | Enterprise Wave 1 | EA-002, EA-003, PF-009 | Users switch among document, tree, and configurable table views with keyboard navigation, inline editing, virtualization, and safe concurrent updates |
| COL-001 | Review comments and dispositions | Preserve the reasoning behind acceptance and rework | Must | Enterprise Wave 1 | WF-001, PF-007 | Threaded comments, mentions, decisions, required dispositions, resolution state, and exact-revision links remain attributable and reportable |
| COL-002 | Watchers, notifications, due dates, and escalation | Keep distributed work moving without external spreadsheets | Must | Enterprise Wave 1 | COL-001, DASH-003 | Users receive permission-safe assignments and notifications; owners track due, overdue, delegated, and escalated work |
| SRCH-001 | Universal permission-aware index | Find any authorized current or historical lifecycle record quickly | Must | Enterprise Wave 1 | PF-001, PF-003, PF-005 | Indexed search spans identifiers, rich content, fields, links, baselines, history, attachments, and artifact types without cross-program disclosure |
| SRCH-002 | Structured query and saved views | Turn large repositories into reusable engineering worklists | Must | Enterprise Wave 1 | SRCH-001, EA-001 | Users build field/link/history queries, select columns/grouping/sorting, save personal or shared views, and share stable filtered URLs |
| BULK-001 | Governed bulk operations | Make large-scale maintenance efficient without weakening control | Must | Enterprise Wave 1 | EA-001, SRCH-002, PF-007 | Bulk edit, classify, move, link, and assign provide preview, permission/validation results, atomic commit, concurrency checks, and one auditable job result |
| COL-003 | Visual revision and relationship redlines | Make review of complex changes fast and unambiguous | Must | Enterprise Wave 1 | PF-006, TR-001, EA-003 | Reviewers see field, rich-text, attachment, placement, and relationship changes between exact revisions and baselines |

## Interchange, APIs, and Integrations

| ID | Capability | Rationale | Priority | Phase | Dependencies | Acceptance Outcome |
| --- | --- | --- | --- | --- | --- | --- |
| EXCH-001 | Governed interchange-job framework | Make large imports and exports observable, retryable, and auditable | Must | Enterprise Wave 1 | PF-007, PF-008 | Every job records source, mapping, preview, actor, checksums, item results, errors, idempotency key, and final outcome |
| EXCH-002 | CSV and Excel import/export | Onboard legacy and supplier data safely | Must | Enterprise Wave 1 | EXCH-001, EA-001 | Users map columns, preview transformations/errors, import atomically, receive an error workbook, reuse mappings, and export controlled views |
| EXCH-003 | ReqIF 1.2 round trip | Exchange requirements across aerospace supply chains and RM tools | Must | Enterprise Wave 2 | EXCH-001, EA-001, TR-001 | ReqIF preserves identities, types, attributes, hierarchy, rich content, attachments, and relations with round-trip mapping and history |
| API-001 | Versioned public REST API | Support durable automation without database access | Must | Enterprise Wave 2 | PF-002, PF-003, PF-007 | APIs provide scoped service identities, pagination, filtering, conditional writes, idempotency, rate limits, stable errors, and compatibility policy |
| API-002 | Transactional events and webhooks | Synchronize lifecycle changes reliably | Must | Enterprise Wave 2 | API-001 | An outbox publishes attributable events with signed delivery, filters, retry/backoff, replay, dead-letter handling, and delivery health |
| API-003 | OSLC Requirements Management | Participate in standards-based digital threads | Should | Enterprise Wave 2 | API-001, EA-001, TR-001 | AeroLink exposes and consumes configuration-aware Requirement and RequirementCollection resources and typed relationships using OSLC RM |
| INT-003 | Monitored integration jobs | Keep external tools connected without hiding failures | Should | Enterprise Wave 2 | API-001, API-002 | Connectors have scoped credentials, mappings, checkpoints, conflict policy, health, error history, replay, and provenance |

## Product-Line Configuration and Enterprise Operations

| ID | Capability | Rationale | Priority | Phase | Dependencies | Acceptance Outcome |
| --- | --- | --- | --- | --- | --- | --- |
| CFG-001 | Components, streams, and change sets | Support parallel releases and teams without confusing working state with immutable baselines | Must | Enterprise Wave 3 | BL-001-BL-003, PF-006 | Teams isolate work in streams, group changes, compare/accept/merge with conflict resolution, and baseline exact contents |
| CFG-002 | Controlled libraries and synchronized reuse | Reuse approved intellectual property without uncontrolled copies | Must | Enterprise Wave 3 | CFG-001, EA-002 | Reused artifacts retain origin, mode, applicability, synchronization, divergence, propagation decisions, and version-correct links |
| CFG-003 | Product variants and composite configurations | Resolve exact lifecycle data for each product/version combination | Should | Enterprise Wave 3 | CFG-001, CFG-002, TR-001 | A composite configuration selects exact components and every trace resolves to the correct endpoint revision |
| OPS-001 | Federated identity and provisioning | Integrate with enterprise access governance | Must | Enterprise Wave 4 | PF-002-PF-004 | OIDC/SAML, SCIM, group mapping, service accounts, and break-glass administration preserve attribution and least privilege |
| OPS-002 | Backup, restore, observability, and retention | Make on-premises operation supportable and recoverable | Must | Enterprise Wave 4 | PF-007, PF-008 | Operators monitor health, export audits, enforce retention, back up, restore, verify integrity, and prove RPO/RTO through drills |
| OPS-003 | Published scale and performance qualification | Replace scalability assumptions with repeatable evidence | Must | Enterprise Wave 4 | SRCH-001, EXCH-001, CFG-001 | Benchmarks prove agreed volumes, large jobs, deep trace queries, publications, and 150-user concurrency against service objectives |

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
| SR-002 | Requirement revisions | Preserve changes without changing stable identity | Must | 1 | SR-001, PF-006 | Users can compare and retrieve every revision and identify the SCR that authorized each change |
| SR-003 | Requirement retirement | Remove future applicability without erasing history | Must | 2 | SR-002, SCR-001 | Approved retirement affects successor baselines only |
| SR-004 | Verification method and derived status | Support planning and completeness checks | Must | 1 | SR-001 | Each applicable requirement records controlled verification and derived information |
| SCR-001 | SCR records and revisions | Explain and control system requirement change | Must | 2 | PF-005-PF-008, SR-001 | One SCR revision can propose multiple introductions, modifications, and retirements |
| SCR-002 | Complete-package SCR review and approval | Prevent uncontrolled requirement changes | Must | 2 | SCR-001, WF-001 | Author-selected approvers unanimously approve or reject the submitted snapshot containing Problem, Analysis, Solution, and all proposed requirement changes |
| SCR-003 | Target release and deferral | Control which approved changes enter a release | Must | 2 | SCR-001, BL-001 | SCRs can be targeted, selected, deferred, and retargeted with retained rationale |
| SCR-004 | Impact analysis | Expose affected links and evidence before change approval | Must | 2 | TR-001, SCR-001 | Reviewers see affected artifacts and unresolved suspect links before approval |

## Workflow, Baselines, and Documents

| ID | Capability | Rationale | Priority | Phase | Dependencies | Acceptance Outcome |
| --- | --- | --- | --- | --- | --- | --- |
| WF-001 | Sequential review cycles | Produce controlled review evidence without unnecessary pre-approval revisions | Must | 2 | PF-002, PF-003, PF-006 | Each submission retains its snapshot, ordered approvers, comments and decisions; review advances one person at a time and pre-approval rework returns the same revision to Draft |
| WF-002 | Approval policy | Enforce unanimous author-selected approval and independence | Must | 2 | WF-001 | Approval is blocked unless every approver in the author-selected sequence approves the same submitted snapshot and comment/independence rules are met |
| WF-003 | Controlled approver substitution and restart | Correct future assignments without invalidating completed work, while rejecting wrong completed approvals | Must | 2 | WF-001, WF-002, PF-007 | The author may replace only not-yet-reached approvers; a wrong completed approver cancels the cycle and restarts review with complete audit history |
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
| SW-001 | HLR and LLR management | Extend controlled requirements down the software V | Must | 4 | Proven system-level model | HLRs/LLRs support identity, revisions, derived status, SCR-package review, baseline inclusion, and upward trace |
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
