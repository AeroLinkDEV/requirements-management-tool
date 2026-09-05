# AeroLink project state — start here

**Last materially reconciled: 2026-09-04.**

**Product checkpoint used for this snapshot:** the #816 Slice 7 authority-provenance and integrated-acceptance completion, built on protected `main` at `14fdffc7c7f70fb9960f198c50f0913dc34b11f7`. That is a checkpoint, not a promise that live `main` will not move. Always refresh GitHub before starting work.

This is the single living product-level orientation record for AeroLink. It answers what the product is, what architecture is currently supported, what remains intentionally outside its claims, and where authoritative detail lives.

Do **not** use a dated handoff, old audit report, or historical issue count as a substitute for this file plus current GitHub state.

## What AeroLink is

AeroLink is an on-premises aerospace requirements-management and development-assurance platform. It is intended to provide a defensible controlled record across requirements, change, review/signature, exact traceability, verification, results/evidence, Problem Reports, baselines, documents, and release readiness.

It exists to make questions such as these answerable from controlled data rather than scattered documents/spreadsheets/memory:

- Which exact requirement revision belongs to this build?
- Which approved change authorized it?
- What exact upstream/downstream revisions does it trace to?
- Which Test Case verifies it?
- Which executable Test Procedure implements that Case?
- What was executed, against which controlled revision, and what evidence/result was retained?
- Which Problem Report or change package explains an issue?
- Who reviewed/approved a controlled package, under what frozen workflow/snapshot?
- Can the controlled document/evidence package be reproduced later?

## What AeroLink is not

These are deliberate boundaries unless an accepted decision changes them:

- **No certification, compliance, or tool-qualification claim.** AeroLink uses aerospace development-assurance concepts/terminology but does not claim that use of the product satisfies certification objectives.
- **No AI product capability.** AI-assisted product behavior is outside the current governed product scope.
- **Not a source-code repository or architecture/design-management replacement.** AeroLink can link to external engineering artifacts but does not become Git or a software design tool.
- **Not a generic document editor.** Structured controlled artifacts are authoritative; generated documents are controlled views. Managed Word documents remain authored in Word while AeroLink controls their revision/review/release record.
- **No automated test-bench execution.** Tests execute in external environments. AeroLink controls Test Cases/Procedures, execution records, results, evidence, retest history, and readiness.
- **No destructive rewrite of approved/released history through normal product workflows.** Historical controlled identity and evidence remain explainable.

See [Scope and Boundaries](docs/product-definition/SCOPE_AND_BOUNDARIES.md) and [Decisions and Open Questions](DECISIONS_AND_OPEN_QUESTIONS.md) for the durable boundary/decision records.

## Current technology and repository shape

- React + TypeScript client.
- ASP.NET Core / .NET application and API.
- Entity Framework Core persistence.
- PostgreSQL for real local/on-premises operation.
- SQLite and disposable PostgreSQL infrastructure for isolated automated qualification where appropriate.
- Modular-monolith organization with explicit Domain, Infrastructure, API, and client boundaries.

The application under `product/` is the single demonstrable software product. The earlier static showcase is historical/reference material.

See [product/docs/ARCHITECTURE.md](product/docs/ARCHITECTURE.md).

## Current requirements architecture

The normal software-oriented lifecycle is:

```text
System Requirement
    ↓
High-Level Software Requirement (HLR)
    ↓
Low-Level Software Requirement (LLR)
```

Exact revision identity and build/baseline membership matter. A current/latest project revision is not automatically the revision carried by a particular released/in-work build.

HLR proposals allocate to exact applicable System revisions, and LLR proposals allocate to exact applicable HLR revisions unless a governed derived classification/rationale applies. Downstream assessments are explicit controlled engineering work rather than browser-only notifications.

## Current verification architecture

The overloaded “software test procedure” model was replaced by an explicit Case → Procedure architecture through the #720 programme and related work (#722, #724, #725, #727, #728, #726), then surfaced as the normal user experience by #762 / PR #767.

### System

```text
System Requirement
    ↓
System Test Procedure (SYSTP)
    ↓
Execution / Result / Evidence
```

System remains a one-tier Procedure verification model.

### Software HLR

```text
HLR Requirement
    ↓
HLR Test Case (HLRTC)
    ↓
HLR Test Procedure (HLRTP)
    ↓
Execution / Result / Evidence
```

### Software LLR

```text
LLR Requirement
    ↓
LLR Test Case (LLRTC)
    ↓
LLR Test Procedure (LLRTP)
    ↓
Execution / Result / Evidence
```

For a software verification profile configured as `[Case, Procedure]`, the Procedure is the executable artifact. A deliberately configured `[Case]` profile remains valid and the Case remains executable there.

The ordinary Software verification UI is profile-aware rather than FMS-hard-coded. Full-profile projects expose HLR/LLR Case and Procedure change-control contexts and a unified build-scoped Test Case/Procedure Explorer. Case-only or partial profiles expose only their configured artifact keys.

### Controlled software verification identities

| Level | Artifact | Artifact prefix | Change Request | Controlled document |
| --- | --- | --- | --- | --- |
| HLR | Test Case | `HLRTC` | `HLRTCCR` | `HLRTD` |
| HLR | Test Procedure | `HLRTP` | `HLRTPCR` | `HLRTPD` |
| LLR | Test Case | `LLRTC` | `LLRTCCR` | `LLRTD` |
| LLR | Test Procedure | `LLRTP` | `LLRTPCR` | `LLRTPD` |

`HLRTD` and `LLRTD` are deliberate retained controlled Case-document identities. They are not cosmetic names to “clean up” without a governed historical-identity migration.

## Verification change control

Verification work is controlled through Test Change Requests (TCRs), downstream assessments, exact artifact revisions, controlled review workflows, and build-scoped materialization.

The vertical software chain is important:

```text
Requirement change
    ↓
Case assessment / Case change
    ↓
Procedure assessment / Procedure change
```

A Procedure is not made to directly own Requirement coverage merely to simplify a UI. Requirement coverage belongs to the Case layer and rolls downstream through exact Case ↔ Procedure relationships.

Approved verification changes materialize into controlled revisions and exact build membership. Execution/readiness uses the carried exact executable artifact, not a project-global “latest” revision.

## Change requests, reviews, and controlled editing

AeroLink supports governed System/HLR/LLR change requests and Test Change Requests with:

- explicit controlled identifiers/revisions;
- Draft authoring and controlled checkout/edit/check-in where applicable;
- source/current-build scope;
- rich engineering narrative;
- exact allocation/trace references;
- configurable sequential/parallel review workflows with frozen stage/authority/version context;
- attributable approvals/signatures over controlled snapshots;
- preserved returned/superseded history;
- separation between approval and actual build/release inclusion.

The Digital Thread accepts a stable Change Request identity and presents the server-composed exact Change Request/provenance projection as a visual, layered node-and-edge map with branching and selectable node detail. The CR inspector is an immediate one-hop diagnostic (0..N upstream/downstream facts) and can open the exact selected CR thread. Exact routeable identifiers use native links; unavailable targets remain explicitly non-openable. The existing baseline-exact requirement → verification → result/evidence → build path remains below it, with exact revision/artifact links where authorized. Proposed Introduce/Modify/Retire content remains visibly separate from materialized, effective-baseline, and evidence truth.

For a non-root requirement change request, the controlled Draft also records either exact upstream change-request
revision link(s) or an attributable no-upstream rationale before review. Same-build direct-parent linkage from the
effective Project ladder is the normal path; an explicitly requested earlier-build link must target an exact signed
predecessor-build revision and retain its cross-build rationale. Assessment-derived upstream evidence remains owned
by its build-scoped downstream assessment, while the review snapshot freezes the exact assessment/link identity that
satisfied the gate. Historical review contracts continue to hash under their original versions.

Approved work does not become silently rewritten because a later revision exists.

## Team Work projection

The server exposes a project-wide, read-only Team Work projection across current Change Requests, numbered Test
Change Requests, Problem Reports, and downstream assessments. The projection owns the four canonical work lanes,
0..N current-holder obligations, release/allocation provenance, and canonical record-opening identity; consumers do
not reconstruct that lifecycle truth from roles or browser state. Review holders and Review-versus-Approval meaning
come from the frozen active `ApprovalStep` records, not current workflow configuration, base project roles, or
Project Leadership. Incorporated, withdrawn, closed, rejected, superseded, linked, and unnumbered records leave the
active projection under explicit family policy. The Team Work client workspace now provides the read-only,
project-wide four-lane lifecycle board, people strip, search/build filters, layer-first contextual artifact-type
filters, current-holder grouping, and canonical record links. Selecting a person replaces the current person filter
and keeps the board visible; current-holder detail is available only through a separate explicit action. Reusable
person avatars use repository-owned synthetic portraits where available and retain an initials fallback. People
ordering and the local affinity nudge use only modern base project roles; Project Leadership remains separate
metadata and Review/Approval remain frozen workflow-stage meanings. Holder identity is 0..N, including parallel
obligations, and no write, assignment, due-date, or age-in-state behavior is implied.

## Problem Reports

Problem Reports are **Project-scoped controlled records**. Target build is an explicit attribute/filter rather than the record's ownership boundary.

The #765 improvement programme delivered a substantially richer Problem Report workflow through phase 6 / PR #774, including:

- correction/editing by appropriate Project members while preserving exclusive lease/history behavior;
- a meaningful fixed category vocabulary with provenance for migrated classifications;
- structured rich authored content and inline emphasis without storing arbitrary executable markup;
- a document-like whole-record create/edit experience with explicit Save vs Save-and-check-in semantics, natural
  image paste/drop, bounded resizing, responsive side-by-side image layout, and typed content persistence through
  revision and generated output;
- controlled supporting attachments with immutable versions, SHA-256 metadata, attributable add/remove/replace
  history, revision-frozen attachment manifests, authorized download, and generated-output manifest entries;
- impact/evidence improvements;
- controlled symmetric same-Project “Related Problem Reports” relationships visible from either report, with history and closure-candidate invalidation.

Problem Reports can drive governed change work; requirements changes do not manufacture a Problem Report merely because a change exists.

## Baselines, builds, and release control

AeroLink separates several facts that must not collapse into one:

- a controlled revision being approved;
- that revision being selected/carried by a particular build/baseline;
- verification obligations for that exact carried configuration;
- the release campaign becoming ready;
- the authorized human release decision.

Released baselines/builds remain immutable. Successor/in-work builds are explicitly assembled under user/configuration authority; AeroLink does not silently create or approve later product baselines.

Exact manifests/effectivity are authoritative for what a build carries.

## FMS live showcase context

The FMS Product Development dataset remains the principal deterministic live demonstration context.

- Build/version 1.5 is the released immutable predecessor.
- Build/version 1.6 is the active in-work successor used to demonstrate current controlled development.
- The effective software verification profile supports the full HLR/LLR Case → Procedure model.
- Named deterministic scenarios provide representative lifecycle, later-revision, trace-branching, assessment,
  verification/evidence, Problem Report, review/approval, leadership, avatar, and distributed-work
  coverage. Interface change-control scenarios are deliberately not seeded: the FMS ladder configures
  `[System, HighLevel, LowLevel]`, and an older seed's Interface records are retired by the explicit
  showcase upgrade (#889). Fresh showcase creation is rollback-atomic; upgrading an existing synthetic
  showcase is an explicit administrator action that requires positive target and backup confirmation and
  never runs during ordinary startup.

The showcase is synthetic demonstration data. It must not be confused with live customer/company controlled engineering data.

See [FMS Live Showcase Dataset](docs/showcase/FMS_LIVE_SHOWCASE_DATASET.md) and [FMS 1.6 Release Campaign](docs/showcase/FMS_1_6_RELEASE_CAMPAIGN.md).

## Documents and publications

AeroLink supports controlled generated publications over structured artifacts and a Managed Documentation Center for externally authored Word documents.

Generated outputs are derived from controlled data/templates/effectivity and carry provenance rather than becoming independent masters. Managed Word documents retain their controlled DOCX/PDF candidates/revisions while Word remains the authoring application.

See [Controlled Document Publication Standard](docs/product-definition/CONTROLLED_DOCUMENT_PUBLICATION_STANDARD.md) and [product/docs/MANAGED_DOCUMENTATION_CENTER.md](product/docs/MANAGED_DOCUMENTATION_CENTER.md).

## Identity, security, and audit

The product includes local identity/session controls, scoped roles/administration, MFA/recovery support, delegations, secure approval/signature behavior, and security/audit records appropriate to the current on-premises product foundation.

Deployment-specific federation/provider/TLS/monitoring/service-objective work remains dependent on a real deployment/customer contract where documented.

## Project authority: base roles, Project Leadership, and review workflow authority

The #816 programme split project authority into two separate facts that must never collapse again:

- **Base project roles** are jobs/eligibility many people may hold on a project (System Engineer, Software Engineer, System/Software Test Engineer, Project Engineer, Program Manager, Engineering Manager, Configuration Manager, Software Quality Assurance, Airworthiness). Holding one grants the job's own authority and nothing more.
- **Project Leadership** is a separate concept with exactly eight accountable positions (Project Engineer, Program Manager, Engineering Manager, Configuration Manager, and the four discipline leads). Each position has at most one current primary holder and one standing backup; the backup carries the same live position authority while the designation is valid. **Base-role eligibility is the qualification for a position, never the position's authority.**
- **Project Engineering Lead** and the old singular position roles are retired; their rows remain readable history and their accountability lives on the Project Leadership positions now.
- **Reviewer and Approver are not assignable jobs.** They are not offered as Personnel roles, not newly grantable as memberships, delegations, or standing backups, and not modern workflow authorities. They survive in the enum and in historical rows as compatibility data only.

A review workflow stage records two independent facts:

- the **required project authority**, represented explicitly as either `BaseRole` (a base project role) or `LeadershipPosition` (an accountable position, answered by its current primary and valid standing backup); and
- the **signature meaning**, which comes only from the stage's `ReviewStageKind` (`Review` or `Approval`) and never from a person's roles.

Workflow stages recorded before this cutover carry no authority kind: they remain readable through explicit legacy-compatibility semantics and are never reinterpreted under today's vocabulary. New and revised workflow configuration must be explicit, the server refuses legacy/ambiguous writes, and all workflow authority resolves through the one central effective-authority resolver so the candidate picker, the signing gate, and the audit record answer identically. Each newly assigned review step freezes the exact authority source and source-row identity, and the resulting electronic signature copies that provenance together with the frozen workflow, stage, cycle, position, and Review/Approval meaning. Historical rows with no recoverable source remain explicitly null rather than receiving fabricated provenance; historical and in-flight review workflow versions stay frozen and are not rewritten under current terminology.

See [Security and Identity Model](docs/product-definition/SECURITY_AND_IDENTITY_MODEL.md).

## Interchange and integrations

AeroLink includes governed import/export/interchange foundations such as CSV/XLSX onboarding, ReqIF-related workflows, versioned API behavior, service identities, webhooks/integration foundations, and external-system linking. Interchange must preserve provenance and must not bypass controlled change/review merely because data arrived from another tool.

## Operations and recovery

The repository provides stable Windows root launchers for development, production-style local operation, shared/remote demo modes, backup, restore validation, diagnostics, and related operator actions.

Those root launchers are intentionally treated as compatibility surfaces; their real logic generally delegates into `product/scripts`.

The normal persistent developer/demo PostgreSQL database uses port **54329** and is not disposable qualification state.

There are three supported operating modes, and they are deliberately independent of each other:

- **HOME canonical / production** — `START_AEROLINK_PRODUCTION.bat` on HOME, running from a **dedicated
  production source checkout** against the HOME canonical database.
- **Work-laptop local development** — `START_AEROLINK.bat` on the laptop, running that laptop's own
  checkout on any deliberate branch against that laptop's own database.
- **Protected remote demo** — `START_AEROLINK_REMOTE_DEMO.bat` on HOME or its recovery task, from the same
  dedicated production source. A remote-demo browser session is a view of HOME; the work-laptop repository
  and database are irrelevant to it.

A checkout is **source**; the persistent PostgreSQL cluster, evidence, attachments and backups are an
**installation** it points at. An ordinary clone is its own installation (`product/.local`, unchanged); a
checkout carrying `product/.local/installation.json` uses the installation that names. This is what lets
HOME have a second checkout without acquiring a second AeroLink, and it fails closed rather than falling
back — a dangling pointer is refused, because the fallback is a healthy, empty installation holding none of
the operator's data.

An installation may declare its own identity (`instance.json`), which the API publishes at
`/health/identity` and the client shows beside the wordmark. Canonical status is declared, never inferred
from the hostname. `/health/identity` also carries the source SHA and launcher mode, which is what lets a
launcher tell a matching process from a stale one — readiness alone never could.

Database upgrade posture is answered before a web server starts, by a maintenance mode of the application
host that reuses the same migration authorities startup runs. A deterministic upgrade is backed up and
validated on an isolated restored copy before the real database is touched; a modelled controlled-data
conflict is reported in seconds with the affected records and the supported operator decisions, and
AeroLink makes no authority decision itself.

See [product/docs/OPERATIONS.md](product/docs/OPERATIONS.md) and [docs/REMOTE_DEMO_OPERATOR.md](docs/REMOTE_DEMO_OPERATOR.md).

## Testing and quality gates

AeroLink has substantial Domain, Infrastructure, API, browser, production-browser, PostgreSQL, operator/recovery, and generated-contract coverage.

The repository uses:

- fast/advisory development feedback;
- a merge-ready full Product quality gate on the exact candidate SHA;
- changed-area/test-planning logic shared between local and CI workflows;
- sharded API/browser work where measurement justified it;
- durable failure diagnostics and exact-SHA provenance expectations.

### Merge-queue cutover status

The canonical GitHub repository is `AeroLinkDEV/requirements-management-tool`; established local checkout
paths did not change because of that ownership transfer. Issue #549 / PR #911 supplied the repository-side
trusted merge-queue verifier and App-bound check publisher. The queue is **active**: the repository-scoped
GitHub App is installed only on this repository, its private key is stored in the main-only
`merge-authority` environment, and the active `main` ruleset has no bypass actors. The legacy classic
required-status block, including strict "require branches to be up to date", is removed; classic administrator,
pull-request, no-force-push and no-deletion protections remain enabled.

The ruleset requires both the App-bound `Trusted merge-queue binding` and GitHub Actions'
`Full Product evidence aggregate`: the native check enters a non-success state as soon as a rerun is queued,
while the App check independently binds the exact evidence after protected-default-branch verification.
This documentation-only delivery establishes the single-entry acceptance path by passing pull-request
readiness and then landing through the exact composed queue candidate after the authority-maintenance fix.
Multi-entry composition, stale-base, deliberate-failure and non-cancellation scenarios remain tracked on
issue #549 until their evidence is recorded.

Do not change CI topology from intuition alone. Read [product/docs/BROWSER_AND_BACKEND_FEEDBACK_TIME.md](product/docs/BROWSER_AND_BACKEND_FEEDBACK_TIME.md) first.

## Important current boundaries / limitations

- No certification/tool-qualification claim.
- No AI product feature.
- No automatic external test execution.
- Deployment-owned services such as customer TLS/reverse proxy, real SMTP/provider qualification, protected off-device backup storage, external monitoring/alerting, customer RPO/RTO/SLOs, and identity-provider contracts remain deployment-specific where not otherwise implemented.
- Scale/performance claims must match measured evidence; do not turn database-client or synthetic-harness evidence into a broader browser-user claim.
- Legacy information is not given fabricated historical precision merely because today's schema is richer.

## Recent major architectural milestones

This is intentionally short. For the narrative history, use [docs/PROJECT_HISTORY.md](docs/PROJECT_HISTORY.md).

- System-level controlled lifecycle and released-baseline foundation.
- Software HLR/LLR controlled requirements and downward assessment model.
- First-class verification/TCR/results/evidence workspaces and build-scoped readiness.
- August audit/remediation hardening of exact history/effectivity/stale selection.
- Measured testing-efficiency/CI and concurrent-agent safety improvements.
- #720–#728 Case → Procedure software verification architecture.
- #762 / PR #767 unified normal Case/Procedure software UX.
- #765 phases 1–6 culminating in PR #774 richer Project-scoped Problem Reports and Related Problem Reports.
- #778 repository knowledge/hygiene programme.
- #816 Slices 2–7: Project Leadership and standing-backup authority, Personnel and workflow cutover, email/shared-access operability, ladder-aware assessment visibility, frozen approval-step/signature provenance, and disposable integrated SMTP-to-authenticated-signature acceptance.

## Live backlog and active work

**GitHub Issues are the live backlog authority.**

This file deliberately does not say “there are N open issues” or “issue X is the only open issue”; those statements age immediately. Refresh GitHub when deciding what remains to be done.

## Where to go next

- Repository/operator front door: [README.md](README.md)
- Coding-agent safety: [AGENTS.md](AGENTS.md)
- Accepted decisions/open questions: [DECISIONS_AND_OPEN_QUESTIONS.md](DECISIONS_AND_OPEN_QUESTIONS.md)
- Documentation map: [docs/README.md](docs/README.md)
- Durable product definition: [docs/product-definition/](docs/product-definition/README.md)
- Reference material: [docs/reference/](docs/reference/README.md)
- FMS showcase guidance: [docs/showcase/](docs/showcase/README.md)
- Source provenance: [docs/provenance/](docs/provenance/README.md)
- Major history: [docs/PROJECT_HISTORY.md](docs/PROJECT_HISTORY.md)
- Lessons learned: [docs/ENGINEERING_LESSONS.md](docs/ENGINEERING_LESSONS.md)
- Technical architecture: [product/docs/ARCHITECTURE.md](product/docs/ARCHITECTURE.md)
- Operations/recovery: [product/docs/OPERATIONS.md](product/docs/OPERATIONS.md)
- Merge workflow: [product/docs/MERGING.md](product/docs/MERGING.md)
- CI feedback-time evidence: [product/docs/BROWSER_AND_BACKEND_FEEDBACK_TIME.md](product/docs/BROWSER_AND_BACKEND_FEEDBACK_TIME.md)
- Live scoped work: GitHub Issues/PRs

When product architecture materially changes, update this file in the same PR. Do not turn it into a chronological handoff; put history in `docs/PROJECT_HISTORY.md` and active work in GitHub.
