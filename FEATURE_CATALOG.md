# Feature Catalog

This catalog is the authoritative capability inventory. Feature identifiers are stable and must not be reused. Priority uses `Must`, `Should`, or `Could`; phase indicates intended sequencing rather than a committed schedule.

Implementation status is tracked against acceptance outcomes, not feature titles. **This catalog is the
capability inventory, not the status record** — for current status read
[PROJECT_STATE.md](PROJECT_STATE.md) and
[AEROLINK_3_IMPLEMENTATION_STATUS.md](AEROLINK_3_IMPLEMENTATION_STATUS.md). For the current supported routes,
dormant UI and aligned issue backlog, read
[CURRENT_PRODUCT_HANDOFF_2026-08-02.md](CURRENT_PRODUCT_HANDOFF_2026-08-02.md). A capability listed here may be
implemented but deliberately not exposed; the catalog is not authority to reconnect dormant modules. The paragraph below is a
2026-07-18 snapshot retained for history and is not maintained.

As of 2026-07-18, Wave 1 has integrated foundations for EA-001 through EA-004, COL-001 through COL-004, SRCH-001, SRCH-002, BULK-001, EXCH-001, and EXCH-002. The Requirements Explorer is now an explicitly read-only surface for structure, trace, verification, history, discussion, and active-change awareness; introductions, modifications, retirements, bulk mutations, and governed imports enter through dedicated Draft SCR/SWCR workflows in Changes. Important artifacts have durable URLs, browser history, context-preserving breadcrumbs, a keyboard command palette, and bounded permission-aware identifier search. SCR/SWCR editing has an exclusive renewable server lease, read-only observers, server autosave snapshots, explicit check-in/discard, forced-unlock audit, and optimistic concurrency. Controlled attachment versions and integrity verification, structured/attachment redlines, a visual saved-query builder with stable URLs, idempotent downloadable background exports, retained three-way merge conflicts, integrity checkpoints, a 50,000-requirement PostgreSQL qualification dataset, and a 150-client mixed database workload join the established impact, collaboration, interchange, and work-management capabilities. Backup integrity and an isolated PostgreSQL restore drill are proven. Remaining acceptance depth includes extending checkout to every controlled draft type, embedded inline-image rendering, import-worker resumability, production-topology browser concurrency, and scheduled recovery drills.

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
| PF-010 | Persistent discipline workspaces | Keep daily Systems, Software, System Test, and Software Test work continuously reachable | Must | 1 | PF-001-PF-003, PF-009 | Main navigation remains visible on every authenticated page and preserves Program/release context while applying discipline scope |
| PF-011 | Server-generated artifact numbering | Prevent collisions and user-selected identities | Must | 1 | PF-005, PF-007 | SCR, SWCR, and requirement identifiers are assigned atomically by installation-wide per-prefix sequences and never reused |
| PF-012 | Searchable people directory controls | Make large review and ownership lists usable | Must | 1 | PF-002-PF-004 | Typing any part of a name, username, title, or role returns permitted active people immediately |
| PF-013 | Identity authority lifecycle | Govern current roles, sessions, and delegated authority without erasing history | Must | 1 | PF-002-PF-004, PF-007 | Administrators revoke individual Program roles, users identify/revoke sessions, and time-bounded delegations retain attributable active/expired/revoked states |

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
| COL-004 | Controlled check-out and auto-save | Protect active authorship without hiding work | Must | Enterprise Wave 1 | PF-002, PF-007, EA-004 | One accountable editor holds a renewable checkout, drafts auto-save server-side, others retain read-only access, and abandoned locks are recoverable with audit evidence |

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
| OPS-004 | Single-origin delivery of the built client | Make what is demonstrated and what is deployed the same artifact | Must | Delivered 2026-07-26 | OPS-002, PF-002 | The API serves the built client on one origin with a document-appropriate content security policy and a fallback that lets a deep link reload; a launcher builds and starts it; browser journeys run against that artifact rather than against a development server. See DEC-052 |

## AeroLink 2.0 implementation checkpoint

The connected-foundation and ReqIF round-trip increments establish project-scoped machine identities,
`/api/v1` requirement reads, idempotent event ingestion, per-credential rate limiting, lifecycle-wide
transactional event capture, durable HMAC-signed webhook delivery, retry/backoff, dead-letter replay, and
operator-facing Integration and ReqIF Exchange Centers. OPS-002 now includes a configurable current-user daily
backup task that invokes and verifies the existing complete backup archive without creating another database.
EXCH-003 is implemented for the documented AeroLink
governed profile, including stable identities, content, hierarchy, relations, schema attributes/tags,
immutable source retention, attachment binaries, preview/reconciliation, and Draft-only controlled commit.
Vendor-specific mappings and customer-specific connector contracts remain deployment/integration work.

## Dashboards and Decision Support

| ID | Capability | Rationale | Priority | Phase | Dependencies | Acceptance Outcome |
| --- | --- | --- | --- | --- | --- | --- |
| DASH-001 | Role-aware dashboard framework | Give each user relevant progress, risk, and work without creating competing sources of truth | Must | 1-3 | PF-001-PF-003, PF-007 | Managers, engineers, CM/quality, and administrators receive authorized views derived from the same controlled records |
| DASH-002 | Manager release-readiness dashboard | Make progress, bottlenecks, completeness, and risk understandable at program/release level | Must | 2-3 | BL-001, WF-001, TR-001 | Managers see scoped readiness and can drill into every contributing or blocking record |
| DASH-003 | Engineer work dashboard | Turn assignments, change impact, suspect links, and verification gaps into actionable work | Must | 2-3 | WF-001, SCR-001, TR-002 | Engineers can navigate directly from their dashboard to the exact artifact and required action |
| DASH-004 | Configuration and quality dashboard | Surface baseline, approval, document, audit, and integrity exceptions | Should | 2-3 | BL-001, DOC-001, PF-007 | Authorized users can identify and investigate every blocking control exception |
| DASH-005 | Trusted metric contracts | Prevent unexplained or misleading summary indicators | Must | 1-3 | PF-007, domain-specific features | Every important metric exposes definition, scope, freshness, source records, owner, authorization behavior, and drill-down |
| DASH-006 | Shareable filtered views and controlled exports | Preserve context when dashboard evidence is discussed or reported | Should | 3 | DASH-001-DASH-005 | Shared/exported views identify scope, filters, time, and provisional/final state without exposing unauthorized data |
| SHOW-001 | Interactive concept showcase — *delivered and retired 2026-07-24 (DEC-046)* | Validate desirability and workflows before production architecture | Must | 0.5 | Approved Phase 0 baseline, design vision | Met. Stakeholders navigated the complete fictional dashboard-to-trace story with no production claims. The prototype was retired once the product surpassed it; demonstrations use the application with the `FMSLIVE` dataset. Identifier retained and never reused |

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
| WF-004 | Parallel review cycles | Support coordinated independent review when serial ordering is unnecessary | Must | 2 | WF-001, WF-002, COL-002 | The author selects parallel mode, every reviewer is activated and notified together, and unanimous approval of one exact snapshot is still required |
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
| DOC-005 | Traceability annexes and change redlines | Make generated lifecycle outputs reviewable without separate reconstruction | Must | 2-4 | DOC-001-DOC-004, TR-001, COL-003 | Requirement documents contain upward-trace annexes and CR documents visibly group introduced, modified old/new, and retired requirements |
| ST-007 | Test change requests | Turn an approved change into named, controlled test work | Must | 3 | ST-001, ST-002 | Approving a change request raises one controlled Test Change Request per affected discipline; one may cover several change requests, and an engineer may raise one deliberately |
| ST-008 | Build test set | Scope what a build has to run, and let the release gates measure it | Must | 3 | ST-001, ST-007 | Each build carries one set per discipline recording who added each procedure and why; release gates read results against that set |
| ST-009 | Verification decisions with reopening | Let a wrong decision be revisited without losing what was decided | Must | 3 | ST-007 | Every impacted requirement carries an explicit decision; reopening returns it to the release gate, puts claimed coverage back to suspect, and keeps the prior decision in immutable history |
| TR-005 | Interactive release lineage tree | Explain predecessor, branch, baseline, build, and release progression | Must | 2-4 | BL-001-BL-003 | Users navigate a clickable tree from released predecessors through in-work successors, candidate baselines, builds, and selected change packages |

## Software and PR Capabilities

| ID | Capability | Rationale | Priority | Phase | Dependencies | Acceptance Outcome |
| --- | --- | --- | --- | --- | --- | --- |
| SW-001 | HLR and LLR management | Extend controlled requirements down the software V | Must | Delivered | Proven system-level model | HLRs/LLRs support identity, revisions, derived status, independently scoped history and editing, SWCR-package review, exact build inclusion, and navigable upward trace |
| SW-002 | SWCR and SWRD lifecycle | Control software change and document generation | Must | Delivered | SW-001, reusable SCR/baseline framework | Approved SWCRs produce exact software baselines and controlled HLR/LLR SWRDs |
| SW-003 | Software verification artifacts | Verify HLRs, LLRs, integration, and robustness behavior | Must | Delivered | SW-001, reusable verification framework | HLR and LLR requirements have isolated controlled procedures, Test Change Requests, build test sets, executions, results, and evidence |
| SW-004 | Consuming-discipline downstream assessments | Put downstream impact decisions with the engineers who consume an approved change | Must | Delivered | SCR-002, SW-001, WF-002 | System approval raises governed HLR work and HLR approval raises LLR work; explicit engineering statuses, source-case/downward-trace context, justified no-change, level-correct Draft creation and automatic/retryable SWCR linkage retain independent approval, read-only history, and supersession |
| SW-005 | Prospective exact upward allocation | Prevent software proposals from reaching review without an allocation or explicit derived exception | Must | Delivered | SW-001, TR-001, BL-001 | HLR proposals select current System revisions and LLR proposals select current HLR revisions from the target build; exact IDs enter the review snapshot and materialize as immutable `AllocatedFrom` traces |
| PR-001 | Problem Report lifecycle | Control investigation, implementation, verification, and independent closure without inventing unused process fields | Should | Delivered | PF foundations, TR-001 | Build-scoped PRs progress Draft → Ready for SCCB → Open → Implementing → Verifying → Awaiting SQA Closure → Closed; rich fields, impact decisions, immutable origin, reassignable owner/build, AND filters, internal history, and independent SQA closure are enforced; released builds are read-only |
| PR-002 | PR-driven corrective-action and evidence chain | Build the change story across artifacts and releases | Should | Delivered | PR-001, TR-004, SCR-001, ST-007 | Any SCR, SWCR, or TCR can select driving PRs; approved changes project as corrective actions, applicable test results project as test evidence, and connected controlled records are searchable, deep-linked, and refresh-safe |
| INT-001 | Enterprise identity | Integrate with organizational authentication | Should | 5 | PF-002, deployment decisions | Authorized enterprise identities and groups can be mapped to product roles |
| INT-002 | GitLab code traceability | Link exact approved LLR revisions to external implementation evidence | Should | MVP delivered | SW-001, integration policy | GitLab remains source of truth; each required exact LLR revision has an immutable MR/merge SHA pointer or justified no-code disposition, with a build-scoped release gate and read-only released history |
| AI-001 | Human-controlled local AI suggestions | Assist drafting and analysis without authority | Could | Future | Mature domain model and governance | Suggestions are labeled, provenance-recorded, draft-only, and explicitly accepted by a human |

## Catalog Rules

- New features require a new stable identifier and a recorded scope decision.
- A feature may move phases only with rationale in [DECISIONS_AND_OPEN_QUESTIONS.md](DECISIONS_AND_OPEN_QUESTIONS.md).
- Acceptance outcomes describe observable behavior; implementation design belongs in later documents.
- Phase 1-3 features define the first complete system-level slice. Phase 4 and later do not block its validation unless a foundation would make them impossible.
