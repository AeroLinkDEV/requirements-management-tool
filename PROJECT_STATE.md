# AeroLink project state — start here

**Last materially reconciled: 2026-08-26.**

**Product checkpoint used for this snapshot:** protected `main` at `0947b9c9cf4e85b1b7ac4bb898f09d6a148d4d13`, after PR #774 merged. That SHA is a checkpoint, not a promise that live `main` will not move. Always refresh GitHub before starting work.

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

See [SCOPE_AND_BOUNDARIES.md](SCOPE_AND_BOUNDARIES.md) and [DECISIONS_AND_OPEN_QUESTIONS.md](DECISIONS_AND_OPEN_QUESTIONS.md) for the durable boundary/decision records while the documentation reorganization is in progress.

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

Approved work does not become silently rewritten because a later revision exists.

## Problem Reports

Problem Reports are **Project-scoped controlled records**. Target build is an explicit attribute/filter rather than the record's ownership boundary.

The #765 improvement programme delivered a substantially richer Problem Report workflow through phase 6 / PR #774, including:

- correction/editing by appropriate Project members while preserving exclusive lease/history behavior;
- a meaningful fixed category vocabulary with provenance for migrated classifications;
- structured rich authored content and inline emphasis without storing arbitrary executable markup;
- a larger whole-record create/edit experience with explicit Save vs Save-and-check-in semantics;
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

The showcase is synthetic demonstration data. It must not be confused with live customer/company controlled engineering data.

See [FMS_LIVE_SHOWCASE_DATASET.md](FMS_LIVE_SHOWCASE_DATASET.md) and [FMS_1_6_RELEASE_CAMPAIGN.md](FMS_1_6_RELEASE_CAMPAIGN.md) while those files remain at root during the documentation reorganization.

## Documents and publications

AeroLink supports controlled generated publications over structured artifacts and a Managed Documentation Center for externally authored Word documents.

Generated outputs are derived from controlled data/templates/effectivity and carry provenance rather than becoming independent masters. Managed Word documents retain their controlled DOCX/PDF candidates/revisions while Word remains the authoring application.

See [CONTROLLED_DOCUMENT_PUBLICATION_STANDARD.md](CONTROLLED_DOCUMENT_PUBLICATION_STANDARD.md) and [product/docs/MANAGED_DOCUMENTATION_CENTER.md](product/docs/MANAGED_DOCUMENTATION_CENTER.md).

## Identity, security, and audit

The product includes local identity/session controls, scoped roles/administration, MFA/recovery support, delegations, secure approval/signature behavior, and security/audit records appropriate to the current on-premises product foundation.

Deployment-specific federation/provider/TLS/monitoring/service-objective work remains dependent on a real deployment/customer contract where documented.

See [SECURITY_AND_IDENTITY_MODEL.md](SECURITY_AND_IDENTITY_MODEL.md).

## Interchange and integrations

AeroLink includes governed import/export/interchange foundations such as CSV/XLSX onboarding, ReqIF-related workflows, versioned API behavior, service identities, webhooks/integration foundations, and external-system linking. Interchange must preserve provenance and must not bypass controlled change/review merely because data arrived from another tool.

## Operations and recovery

The repository provides stable Windows root launchers for development, production-style local operation, shared/remote demo modes, backup, restore validation, diagnostics, and related operator actions.

Those root launchers are intentionally treated as compatibility surfaces; their real logic generally delegates into `product/scripts`.

The normal persistent developer/demo PostgreSQL database uses port **54329** and is not disposable qualification state.

See [product/docs/OPERATIONS.md](product/docs/OPERATIONS.md) and [docs/REMOTE_DEMO_OPERATOR.md](docs/REMOTE_DEMO_OPERATOR.md).

## Testing and quality gates

AeroLink has substantial Domain, Infrastructure, API, browser, production-browser, PostgreSQL, operator/recovery, and generated-contract coverage.

The repository uses:

- fast/advisory development feedback;
- a merge-ready full Product quality gate on the exact candidate SHA;
- changed-area/test-planning logic shared between local and CI workflows;
- sharded API/browser work where measurement justified it;
- durable failure diagnostics and exact-SHA provenance expectations.

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

## Live backlog and active work

**GitHub Issues are the live backlog authority.**

This file deliberately does not say “there are N open issues” or “issue X is the only open issue”; those statements age immediately. Refresh GitHub when deciding what remains to be done.

## Where to go next

- Repository/operator front door: [README.md](README.md)
- Coding-agent safety: [AGENTS.md](AGENTS.md)
- Accepted decisions/open questions: [DECISIONS_AND_OPEN_QUESTIONS.md](DECISIONS_AND_OPEN_QUESTIONS.md)
- Documentation map: [docs/README.md](docs/README.md)
- Major history: [docs/PROJECT_HISTORY.md](docs/PROJECT_HISTORY.md)
- Lessons learned: [docs/ENGINEERING_LESSONS.md](docs/ENGINEERING_LESSONS.md)
- Technical architecture: [product/docs/ARCHITECTURE.md](product/docs/ARCHITECTURE.md)
- Operations/recovery: [product/docs/OPERATIONS.md](product/docs/OPERATIONS.md)
- Merge workflow: [product/docs/MERGING.md](product/docs/MERGING.md)
- CI feedback-time evidence: [product/docs/BROWSER_AND_BACKEND_FEEDBACK_TIME.md](product/docs/BROWSER_AND_BACKEND_FEEDBACK_TIME.md)
- Live scoped work: GitHub Issues/PRs

When product architecture materially changes, update this file in the same PR. Do not turn it into a chronological handoff; put history in `docs/PROJECT_HISTORY.md` and active work in GitHub.