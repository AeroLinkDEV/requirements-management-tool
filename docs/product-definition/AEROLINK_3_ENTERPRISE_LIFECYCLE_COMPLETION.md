# AeroLink 3.0 — Enterprise Lifecycle Completion

## Purpose

AeroLink already implements the controlled requirements lifecycle that originally defined the product: system and software requirements, change request review and approval, immutable baselines, controlled documents, version-aware traceability, verification evidence, role-aware dashboards, collaboration, notifications, audit history, ReqIF exchange, APIs, webhooks, backup/restore, and performance qualification foundations.

AeroLink 3.0 completes the remaining enterprise lifecycle capabilities without introducing AI assistance. It must extend existing domain rules rather than duplicate or bypass them.

> **Current product-surface note — 2026-08-10.** This file is the long-lived completion contract, not the
> live status record. Later decisions intentionally narrowed or reshaped parts of the contract: Problem Reports
> are active and Project-scoped (DEC-089); test procedures are introduced/modified/retired only through a
> controlled Test Change Request (DEC-103); Candidate Baselines is a supported Configuration Management route at
> `/baselines`, including explicit legacy procedure-manifest bootstrap (#364), while `/release-planning` and the
> redundant Product Versions surface remain retired/dormant. Do not infer current backlog or current UI exposure
> from an older workstream sentence below. Use [PROJECT_STATE.md](PROJECT_STATE.md) and current GitHub state for
> present product truth. The former [AeroLink 3 implementation scorecard](docs/archive/AEROLINK_3_IMPLEMENTATION_STATUS.md)
> and [2026-08-10 handoff](docs/archive/CURRENT_PRODUCT_HANDOFF_2026-08-10.md) are retained as historical checkpoints.

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

The program builds on, and must not regress:

- System, HLR, and LLR requirement records and revision history;
- change request package authoring, review, approval, and next-revision creation;
- immutable baselines and release lineage;
- typed traceability, suspect links, impact analysis, and completeness checks;
- controlled SYSRD, SWRD, test, traceability, review, and release outputs;
- versioned procedures, executions, results, evidence, failures, and retests;
- role-aware dashboards and decision-room workflows;
- comments, dispositions, assignments, watches, notifications, and audit;
- universal search, saved views, bulk operations, and governed onboarding;
- ReqIF 1.2 governed round trip;
- versioned REST reads, transactional events, webhooks, replay, and service identities;
- PostgreSQL production defaults, isolated SQLite tests, and safe migrations;
- complete backup verification and isolated restore tooling; and
- 50,000-requirement qualification data and mixed database workload tools.

## Implementation checkpoint — universal editing foundation

The first AeroLink 3.0 implementation increment is now present on the program branch:

- a single fail-closed controlled-artifact editing policy registry;
- canonical artifact families and aliases, including SRCR, software change request, and PR;
- explicit editable lifecycle states for every family;
- exclusive-editing policy for all nine AeroLink 3.0 draft families;
- shared lease limits of two to 120 minutes with a fifteen-minute default; and
- domain tests proving full family coverage, alias normalization, state eligibility, lease bounds, and unsupported-type rejection.

The existing API still needs to consume this registry and resolve each family to its authoritative project, revision, lifecycle state, snapshot, and atomic commit operation. This checkpoint is therefore a real shared-code foundation, not a claim that universal controlled editing is complete.

## Workstream 1 — Universal controlled editing

Extend the existing renewable exclusive checkout, autosave, immutable snapshot, recovery, check-in, discard, expiry, and forced-unlock contract beyond change request drafts.

Controlled families:

1. change requests;
2. requirement proposals;
3. specification structures;
4. test procedures;
5. trace-link proposals;
6. release-planning drafts;
7. document templates;
8. problem reports; and
9. configuration change sets.

Required behavior:

- one canonical server-side artifact resolver;
- artifact-specific permission and editable-state policies;
- database-enforced exclusive locks;
- stable lease heartbeat and expiry;
- version-checked autosave snapshots;
- atomic check-in against artifact and session versions;
- discard without controlled-content mutation;
- administrator/configuration-manager recovery with mandatory reason;
- review/freeze/release transitions blocked by incompatible active sessions;
- read-only observation for other authorized users; and
- audit and security events for every material transition.

Acceptance gate:

- two-user contention journeys for every supported family;
- snapshot recovery after browser interruption;
- stale artifact and stale session conflict tests;
- forced-unlock and expiry tests;
- approved/frozen/released artifacts proven non-editable; and
- no regression to existing change request behavior.

## Workstream 2 — Full problem-report lifecycle

Implement first-class controlled problem reports rather than retaining PRs as external references.

Capabilities:

- server-authoritative PR numbering and revisions;
- classification, severity, priority, origin, affected configuration, and ownership;
- investigation, root cause, effects, containment, corrective action, and disposition;
- duplicate, cannot reproduce, no fault found, deferred, accepted risk, fixed, and rejected paths;
- links to requirements, SRCR/software change requests, procedures, executions, evidence, builds, baselines, documents, and releases;
- resolution verification and independent closure approval;
- reopen while retaining prior closure history;
- PR-driven impact analysis and suspect-link propagation;
- release blocker/waiver policy;

Current product boundary (DEC-085): the build-scoped center and PR-to-CR/TCR corrective thread are active.
Containment and preventive-action authoring remain outside the current increment; this broader capability list
is retained as enterprise ambition, not a statement that every field is currently exposed.
- dashboards, saved views, notifications, comments, assignments, and escalations;
- controlled PR publications and audit export; and
- search, API, event, and ReqIF/reference integration where applicable.

Acceptance gate:

- create a PR from a failed execution;
- investigate and classify it;
- raise linked requirement/software changes;
- verify the correction in a successor build while retaining the original failure;
- approve closure;
- publish the exact closure package; and
- navigate the complete chain bidirectionally.

## Workstream 3 — Product-line configuration and reuse

Implement components, streams, change sets, controlled libraries, synchronized reuse, variants, and composite configurations.

Capabilities:

- project components and named streams;
- controlled change sets with exact artifact deltas;
- compare, accept, deliver, rebase, and merge;
- retained three-way conflicts and attributable resolution;
- immutable component baselines;
- controlled reusable libraries with origin and synchronization state;
- reference, synchronized-copy, and intentionally diverged reuse modes;
- propagation previews and accept/defer/reject decisions;
- variant definitions and applicability expressions;
- composite configurations selecting exact component baselines; and
- configuration-correct traceability, documents, verification, dashboards, and APIs.

Acceptance gate:

- two parallel streams change shared content;
- merge conflict is retained and deterministically resolved;
- approved library content is reused by two product variants;
- one variant accepts an upstream update while another defers it; and
- every output resolves to the correct exact configuration.

## Workstream 4 — Enterprise identity and account assurance

**Status: partially delivered, remainder deferred by explicit decision.** See the decision record below.
Until that deferral is lifted, this workstream's full acceptance gate is not a condition of the AeroLink 3.0
program completion gate.

Delivered capabilities:

- local controlled accounts, Program membership and role scoping;
- temporary-password rotation, mandatory before workspace access, and administrator reset;
- MFA and recovery codes, with encrypted secrets and downgrade protection;
- scoped service accounts for versioned API access;
- trusted group-to-Program-role mapping: durable configuration, administrator-only administration API,
  fail-closed resolution and complete mutation and resolution audit;
- session issue, expiry, self-service inventory and logout revocation; and
- security auditing without secret disclosure.

Deferred capabilities:

- OIDC and SAML federation;
- SCIM user/group provisioning;
- tightly controlled break-glass local administrator;
- privileged step-up authentication;
- secure account recovery with single-use short-lived tokens;
- password expiration policy where locally managed;
- administrator session inventory and single-session revocation;
- identity-provider and provisioning health; and
- identity administration user interface.

Acceptance gate that applies now:

- role mapping and least-privilege proof; and
- MFA enrollment/recovery.

Acceptance gate deferred with its capabilities:

- federated sign-in and logout;
- SCIM create/update/disable;
- privileged step-up enforcement; and
- break-glass recovery with complete audit evidence.

### Deferred scope — decision record

Recorded 2026-07-24.

AeroLink is not yet operated by an organization whose people sign in with a corporate directory. Federation
therefore has no user today, and most of the deferred list exists only to support federation: break-glass is
insurance against a federated login outage, provider health monitors a connection that does not exist, and
the administration UI is a control panel for configuration that currently has one operator. SCIM automates
account lifecycle that is presently a handful of manual acts. Building these now would mean maintaining
security-critical code against no real usage, which is how such code silently rots.

What remains in place is a coherent identity system for a tool at this stage: controlled local accounts,
enforced temporary-password rotation, MFA with recovery codes, scoped service credentials, Program role
scoping, and complete security audit.

**Trigger to resume:** the first commitment to deploy AeroLink for an organization that will authenticate
against its own directory. Federation is the item with the longest lead time and the least ability to be
faked afterwards, so it should start before that commitment is due, not after.

**Order when resumed**, because these depend on each other rather than being independent choices:

1. OIDC protocol configuration and token validation;
2. OIDC sign-in, external-subject binding, mapped-role projection and logout;
3. break-glass local administrator, before any federated deployment goes live;
4. privileged step-up authentication;
5. SCIM provisioning;
6. account recovery and password expiration. These required an email transport the product did not have;
   one now exists — an outbox over the in-app notification record, delivering through the organization's own
   SMTP relay — so the dependency is met in code but has never been exercised against a real relay. Prove
   that before relying on it for a recovery path, because a recovery email that silently fails is worse than
   no recovery path at all;
7. administrator session inventory and single-session revocation; and
8. provider health and the identity administration UI.

SAML should be re-examined rather than assumed when this resumes. It carries roughly the cost of OIDC again
and most enterprise directories do not require it.

**This deferral does not lower the bar for what is already built.** The delivered capabilities above remain
subject to the full delivery rules, and the deferred items must not be described as complete, partially
complete, or "foundation only" anywhere in the product record.

## Workstream 5 — Resumable interchange and monitored integrations

Capabilities:

- resumable import workers with durable checkpoints;
- item-level idempotency and retry;
- cancellation and restart without duplicate controlled records;
- mapping-version history and reusable mappings;
- downloadable error workbooks;
- attachment and embedded-binary round trip;
- conditional `/api/v1` requirement writes using ETags and idempotency keys;
- OSLC RM provider and consumer support;
- monitored connector checkpoints;
- connector health, error history, replay, and provenance; and
- operator-visible queues and dead letters.

Acceptance gate:

- interrupt and resume a large CSV/XLSX import;
- replay failed items without duplicating successful items;
- conditional API writes reject stale ETags;
- OSLC resources preserve configuration-aware links; and
- connector failure/recovery remains observable and attributable.

## Workstream 6 — Rich technical content and controlled publications

Capabilities:

- inline-image rendering in authoring and generated outputs;
- deterministic tables, symbols, equations-as-controlled-content, and references;
- approved template lifecycle;
- organization-specific SYSRD, SWRD, procedure, trace, PR, review, and release templates;
- exact revision/baseline redlines;
- resumable publication jobs;
- publication integrity verification and regeneration proof;
- controlled distribution metadata; and
- release evidence packages with manifests and reproducible contents.

Acceptance gate:

- one rich requirement with image, table, symbols, and references renders equivalently in UI, DOCX, and PDF;
- regeneration from identical controlled inputs produces equivalent authoritative content;
- a template revision change affects only later outputs; and
- release package manifest verifies every included file and source record.

## Workstream 7 — Quality, certification-evidence, and portfolio intelligence

This workstream supports assurance planning and evidence discovery without claiming compliance or certification.

Capabilities:

- configurable lifecycle objective and evidence-expectation records;
- readiness blockers, exceptions, waivers, owners, due dates, and rationale;
- certification-evidence index and review status;
- release and baseline completeness trends;
- PR arrival, aging, recurrence, escape, and closure metrics;
- review time and bottleneck analysis;
- verification pass/retest/failure trends;
- suspect-link and orphan trends;
- cross-program portfolio aggregation with permission-safe suppression;
- metric contracts exposing definition, scope, freshness, and drill-down; and
- controlled dashboard exports.

Acceptance gate:

- every headline metric drills to its exact records;
- unauthorized Programs do not influence disclosed totals;
- historical metrics reconstruct from immutable event time rather than current state; and
- waivers and exceptions remain visible in readiness decisions.

## Workstream 8 — Production operations and qualification

Capabilities:

- structured logs, metrics, traces, correlation IDs, and audit separation;
- liveness, readiness, dependency, storage, queue, and migration health;
- alert thresholds and operator runbooks;
- retention policies, preview, legal/quality holds, and auditable execution;
- scheduled backups copied to protected off-device storage;
- scheduled verification and isolated restore drills;
- measured RPO/RTO evidence;
- safe upgrade preflight, backup, migration, rollback decision, and post-checks;
- capacity and performance qualification against published workloads; and
- operational evidence export.

Qualification target:

- 50,000 controlled requirements;
- representative revisions, traces, procedures, executions, evidence metadata, comments, and assignments;
- 150 authenticated mixed-workload clients;
- bounded search, trace, dashboard, publication, import, and webhook objectives; and
- repeatable results with documented hardware and configuration.

## Delivery sequence

1. Universal editing policy and artifact resolver.
2. Universal checkout API and reusable client shell.
3. Problem-report lifecycle.
4. Configuration streams/change sets.
5. Controlled reuse and variants.
6. Rich publications and resumable jobs.
7. Enterprise identity — *deferred remainder; see Workstream 4. Re-enters this sequence at its recorded
   trigger and resumes in the order given there.*
8. Operations and qualification.
9. Portfolio and evidence intelligence.

Dependencies may cause small supporting slices to land earlier, but no workstream may bypass the shared authorization, revision, audit, configuration, or immutable-history contracts.

## Increment rules

Every implementation PR must:

- start from current green `main`;
- identify its workstream and acceptance slice;
- include safe additive migration or a documented no-migration rationale;
- include domain and persistence tests;
- include client/browser tests for user-visible behavior;
- retain existing tests;
- pass PostgreSQL bootstrap/migration smoke when persistence changes;
- document security and operational impacts;
- avoid unrelated cleanup; and
- merge only when the complete required gate is green.

## Program completion gate

AeroLink 3.0 is complete only when:

- all eight workstreams meet their acceptance gates, except where a workstream records an explicit
  deferral — currently Workstream 4, whose deferred capabilities and their gate items are excluded until
  that decision record is lifted;
- every such deferral is recorded in this contract, with its reason, its resume trigger and its excluded
  gate items, before the program is described as complete;
- all prior acceptance scenarios remain green;
- migrations are proven against a restored pre-3.0 PostgreSQL database;
- backup, restore, and upgrade drills are recorded;
- the configured scale workload meets published objectives;
- security review finds no unresolved critical/high issue;
- controlled documents and evidence packages regenerate correctly;
- no AI functionality exists in the release; and
- `main` is releasable with complete implementation and limitation records.
