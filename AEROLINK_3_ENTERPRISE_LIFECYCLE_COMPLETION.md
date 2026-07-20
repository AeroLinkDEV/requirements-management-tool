# AeroLink 3.0 — Enterprise Lifecycle Completion

## Purpose

AeroLink already implements the controlled requirements lifecycle that originally defined the product: system and software requirements, SCR/SWCR review and approval, immutable baselines, controlled documents, version-aware traceability, verification evidence, role-aware dashboards, collaboration, notifications, audit history, ReqIF exchange, APIs, webhooks, backup/restore, and performance qualification foundations.

AeroLink 3.0 completes the remaining enterprise lifecycle capabilities without introducing AI assistance. It must extend existing domain rules rather than duplicate or bypass them.

## Non-negotiable delivery rules

1. `main` remains releasable; work is delivered through independently reviewable increments.
2. Every controlled mutation is server-authoritative, permission-scoped, auditable, concurrency-safe, and recoverable.
3. Approved or released history is immutable. Corrections create successor revisions, configurations, or records.
4. Every dashboard measure drills into the exact records that produced it.
5. Generated outputs identify exact source revisions, configuration, generator version, approval state, and content hash.
6. New database changes are additive unless an explicitly validated migration proves safe transformation.
7. No feature claims certification, compliance, or tool qualification.
8. No AI or generative capability is included in this program.

## Existing foundation retained

The program must preserve and reuse the current implementations for:

- system requirements, HLRs, LLRs, SCRs, and SWCRs;
- sequential and parallel review, electronic approval, comments, dispositions, due work, and notifications;
- immutable candidate/released baselines and baseline comparison;
- SYSRD, SWRD, change-request, test, traceability, PDF, and DOCX publication;
- typed links, suspect links, impact analysis, completeness checks, lifecycle exploration, and release lineage;
- test procedures, executions, results, evidence, failures, amendments, and retests;
- controlled attachments, redlines, saved queries, bulk operations, CSV/XLSX and ReqIF exchange;
- REST APIs, service identities, event ingestion, outbox events, signed webhooks, retries, dead letters, and replay;
- Program-scoped authorization, exclusive SCR/SWCR editing, autosave recovery, audit, backup, restore, diagnostics, and qualification tooling.

## Workstream 1 — Universal controlled editing

Extend the proven checkout/lease/autosave/recovery contract from SCR/SWCR records to every controlled draft family:

- requirement proposals;
- specification hierarchy and placement edits;
- test procedures and steps;
- trace-link proposals and suspect-link dispositions;
- release-planning and candidate-baseline drafts;
- controlled document-template drafts;
- problem-report investigations and resolutions;
- configuration change sets.

### Required behavior

- one accountable editor per controlled draft scope;
- renewable server lease with visible owner, activity, and expiry;
- read-only observers;
- server autosave snapshots with sequence, hash, actor, and timestamp;
- optimistic version checks at commit;
- explicit check-in, discard, recovery, and administrator forced unlock with reason;
- review submission blocked while an incompatible editing session exists;
- no lock may make approved or released data editable.

### Acceptance gate

Two-user browser journeys prove contention, read-only observation, autosave recovery, check-in, abandoned-lock recovery, forced unlock, and immutable approved history for every supported artifact family.

## Workstream 2 — Full problem-report lifecycle

Implement PR-001 and PR-002 as first-class controlled records.

### Controlled content

- server-generated PR identifier and immutable revision history;
- title, description, discovery context, affected product/configuration, reporter, owner, dates, severity, priority, classification, safety/security relevance, reproducibility, and attachments;
- investigation, root cause, effects, containment, alternatives, selected disposition, resolution, verification plan, verification results, closure rationale, and residual risk;
- statuses covering Draft, Submitted, Screening, Investigating, AwaitingDisposition, Implementing, Verification, ClosureReview, Closed, Rejected, Duplicate, Deferred, and Reopened;
- configurable required fields and transition guards;
- independent closure approval where policy requires it.

### Relationships

PRs must link bidirectionally to:

- SCRs/SWCRs;
- requirements and exact revisions;
- trace links and suspect-link dispositions;
- test procedures, executions, failures, and retests;
- builds, releases, baselines, documents, evidence references, and external implementation references.

### Required views

- PR workspace and controlled editor;
- triage and ownership queues;
- investigation and disposition history;
- impact graph;
- verification and closure package;
- release blockers and escaped-defect metrics;
- controlled PR PDF/DOCX publication.

### Acceptance gate

An end-to-end scenario creates a PR from a failed execution, classifies and investigates it, raises linked change requests, resolves and verifies the issue in a successor build, retains the original failure, obtains closure approval, publishes the closure package, and shows the complete chain in search, dashboards, trace views, audit, and release readiness.

## Workstream 3 — Product-line configuration and reuse

Implement CFG-001 through CFG-003.

### Capabilities

- components and component ownership;
- streams derived from exact predecessor configurations;
- controlled change sets containing exact artifact revisions and relationship changes;
- compare, accept, reject, merge, and conflict-resolution workflows;
- immutable component baselines;
- controlled libraries and reusable approved artifacts;
- origin, reuse mode, applicability, synchronization state, divergence, propagation decisions, and version-correct links;
- product variants and applicability expressions;
- composite configurations selecting exact component baselines;
- configuration-aware trace, publication, verification, search, and release readiness.

### Acceptance gate

Two streams modify overlapping reusable content, expose a deterministic three-way conflict, resolve it through a controlled merge, baseline both components, assemble two variants, and prove that every document, trace endpoint, test result, and metric resolves against the selected composite configuration.

## Workstream 4 — Enterprise identity and account assurance

Implement OPS-001 and the committed identity-hardening backlog.

### Capabilities

- OIDC and SAML federation;
- SCIM users and groups;
- Program-role mapping from trusted groups;
- service accounts with scoped credentials and expiry;
- tightly controlled local break-glass administration;
- first-sign-in temporary-password rotation for local accounts;
- configurable password-expiration policy where local passwords remain enabled;
- secure self-service recovery using short-lived, single-use, rate-limited, revocable tokens;
- MFA, recovery codes, and step-up authentication for privileged actions;
- session inventory and revocation;
- complete identity and privilege audit.

### Acceptance gate

Automated integration tests prove group provisioning, deprovisioning, least-privilege Program access, step-up enforcement, recovery-token replay prevention, service-account scoping, emergency local access, and preservation of historical attribution after identity changes.

## Workstream 5 — Resumable interchange and monitored integrations

Complete EXCH-001 through EXCH-003, API-001 through API-003, and INT-003 depth.

### Capabilities

- resumable import workers with durable checkpoints;
- retry-safe item processing and idempotent commits;
- cancellation and restart without duplicate controlled records;
- mapping-version history and reusable mappings;
- embedded image and attachment round-trip for CSV/XLSX/ReqIF-supported profiles;
- conditional requirement writes through `/api/v1`;
- OSLC RM provider and consumer support;
- connector credentials, mappings, health, checkpoints, conflicts, error history, replay, and provenance;
- signed webhook delivery observability and operator alerts.

### Acceptance gate

A large ReqIF/import job is interrupted mid-run, resumes from its checkpoint, produces no duplicate identities or relationships, preserves rich content and binaries, and reports complete item-level provenance. API and OSLC tests prove conditional writes, configuration-aware reads, authorization, pagination, idempotency, and stable errors.

## Workstream 6 — Controlled documents and rich technical content

Complete enterprise publication depth.

### Capabilities

- embedded inline-image rendering in authoring, redlines, PDF, DOCX, and ReqIF;
- deterministic table, symbol, equation, reference, and attachment rendering;
- controlled template versions with approval and retirement;
- organization-specific SYSRD/SWRD/SDD/test/trace/PR/review-package templates;
- revision-to-revision and baseline-to-baseline document redlines;
- publication queue, retry, cancellation, retention, and integrity verification;
- reproducibility check that regenerates and compares hashes where deterministic formats allow it;
- release evidence package containing exact approved documents and manifest.

### Acceptance gate

A rich requirement containing tables, symbols, inline images, attachments, and links renders consistently in the application, redline, PDF, DOCX, and ReqIF round trip. A release evidence package verifies every file hash and source reference.

## Workstream 7 — Certification, quality, and portfolio intelligence

Extend the existing trusted dashboard framework without claiming certification.

### Capabilities

- configurable lifecycle objectives and evidence expectations by Program;
- requirement, review, trace, verification, document, baseline, build, PR, and audit completeness contracts;
- DAL/assurance-level, verification-method, independence, derived-requirement, safety, security, and compliance attributes through configurable schemas;
- release readiness with explicit blockers, waivers, owners, due dates, and approval provenance;
- certification-evidence index linking objectives to exact controlled records;
- cross-release trends for churn, review duration, suspect links, verification closure, defects, escapes, rework, and waiver aging;
- authorized cross-program portfolio views using aggregated data that cannot disclose restricted artifact content;
- metric definition, scope, freshness, owner, authorization behavior, and drill-down for every displayed measure;
- controlled dashboard exports with filters, timestamp, provisional/final state, and hashes.

### Acceptance gate

Every readiness percentage and trend can be recomputed from source records. Permission tests prove that cross-program aggregation cannot reveal restricted identities or content. A release cannot be represented as ready while an unwaived configured blocker remains.

## Workstream 8 — Production operations and recovery assurance

Complete OPS-002 and OPS-003.

### Capabilities

- structured application, security, audit, job, webhook, database, storage, and backup telemetry;
- health, readiness, and dependency checks;
- alert rules and operator runbooks;
- retention policies with legal/quality holds and dry-run previews;
- scheduled off-device backup handoff and verification;
- scheduled isolated restore drills with recorded RPO/RTO evidence;
- upgrade preflight, backup gate, migration, rollback boundary, and post-upgrade verification;
- capacity tests covering 150 concurrent mixed users, 50,000+ requirements, deep traces, large documents, imports, exports, and webhook load;
- published qualification report with environment, dataset, workload, objectives, results, bottlenecks, and limitations.

### Acceptance gate

A scheduled recovery drill restores the latest eligible backup into isolation, verifies integrity, migrates, starts the application, runs smoke tests, records achieved RPO/RTO, and leaves production untouched. Capacity qualification meets the approved service objectives or records explicit blockers.

## Delivery sequence

### Increment A — Universal control and PR foundation

1. Shared edit-session abstraction and migrations.
2. Requirement, procedure, trace, release-plan, and configuration draft coverage.
3. PR domain, API, persistence, search, audit, and basic workspace.
4. PR impact, verification, closure, documents, dashboards, and release gates.

### Increment B — Configuration and rich publication

1. Components, streams, change sets, compare, merge, and conflicts.
2. Controlled libraries, reuse, synchronization, and divergence.
3. Variants and composite configurations.
4. Inline rich media, template lifecycle, document redlines, and release evidence packages.

### Increment C — Connected enterprise

1. Resumable interchange worker.
2. Conditional REST writes.
3. OSLC RM.
4. Monitored connector checkpoints and operator health.
5. OIDC/SAML, SCIM, MFA, recovery, and step-up authentication.

### Increment D — Intelligence and production qualification

1. Configurable lifecycle objective/evidence model.
2. Certification, quality, PR, and portfolio dashboards.
3. Observability, retention, upgrade safety, scheduled restore drills.
4. Full scale and concurrency qualification.

## Mandatory automated coverage

Each increment must add:

- domain transition and invariant tests;
- persistence tests against isolated SQLite where valid and isolated PostgreSQL for provider-specific behavior;
- authorization and direct-object boundary tests;
- migration-upgrade tests from the current production schema;
- API contract tests;
- Playwright journeys for author, reviewer, manager, quality/configuration, administrator, and operator roles;
- document and interchange signature/hash checks;
- concurrency, retry, idempotency, and recovery tests;
- backup/restore impact validation;
- accessibility and keyboard-navigation coverage for new critical surfaces.

## Release gate

AeroLink 3.0 is complete only when:

1. all workstream acceptance gates are executable and green;
2. all previous acceptance scenarios remain green;
3. current PostgreSQL data migrates without loss of controlled history;
4. released FMS 1.5 remains immutable and in-work successors remain explicit;
5. backup, restore, and upgrade validation pass in isolation;
6. security review finds no unresolved critical or high-severity defect;
7. performance qualification evidence is published with limitations;
8. user-facing documentation and operator runbooks are current; and
9. no AI feature, API call, model dependency, or generative user control is introduced.