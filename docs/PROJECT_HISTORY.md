# AeroLink project history

This is the milestone-level history of how AeroLink reached its current architecture. It is deliberately not a PR diary and is not the live backlog. Use [`../PROJECT_STATE.md`](../PROJECT_STATE.md) for current product truth and GitHub Issues for active work.

## July 2026 — product definition and controlled-lifecycle foundation

AeroLink began as a structured aerospace requirements/change/verification concept and established the decisions that still shape the product:

- controlled artifacts and exact revisions are authoritative rather than independently edited document masters;
- approval and release/baseline inclusion are separate decisions;
- historical controlled records are retained rather than destructively rewritten;
- generated documents are views of controlled data;
- test execution is external while AeroLink controls procedures, results, evidence, and traceability;
- standards such as DO-178/ARP4754 inform terminology and rigor without making a certification/tool-qualification claim.

The early system-level vertical slice proved the complete controlled chain from change request through requirement revision, review, baseline, document publication, verification evidence, and audit history.

The static showcase served as a design-validation step and was then retired once the real application exceeded it. The production application under `product/` became the single software artifact.

## Late July — software lifecycle, build-scoped work, and enterprise controls

The product expanded from the original System slice to Software HLR/LLR requirements and broader enterprise lifecycle behavior:

- build/release workspaces and immutable released predecessors;
- controlled software change requests and downward assessments;
- typed, exact-revision traceability;
- product-line/reuse and configuration foundations;
- richer security/session/approval behavior;
- managed/generated documents;
- backup/recovery and operational evidence;
- APIs, interchange, notifications/integrations, and other production-oriented foundations.

The FMS dataset became the deterministic live demonstration program, retaining a released 1.5 product while 1.6 is the active in-work successor.

## Early August — verification becomes first-class controlled work

Verification moved from a secondary procedure list into explicit controlled workspaces:

- discipline-specific Change Requests;
- Test Procedure/verification Explorer surfaces;
- Test Results and evidence;
- build-scoped verification impact and readiness;
- first-class Test Change Requests (TCRs);
- automatic and manually raised verification change packages;
- controlled Procedure Introduce/Modify/Retire proposals;
- review workflows and exact materialization into the selected build.

Downstream assessments became explicit engineering conclusions with retained withdrawn/reopened history rather than transient queue state.

## August 7–10 — audit/remediation and exact-history hardening

A focused audit/remediation sequence corrected several places where presentation, search, selection, historical titles, effectivity, or migration assumptions could drift from exact controlled truth.

Important themes from that sequence include:

- exact revision titles/search use the same authority;
- superseded controlled work remains navigable history;
- legacy procedure manifests are established by an attributable migration snapshot rather than fabricated historical precision;
- stale controlled targets fail visibly instead of being silently remapped;
- PostgreSQL-specific qualification remains necessary where SQLite tests cannot exercise migration/provider behavior.

Historical handoffs from this period are retained as checkpoint evidence, but they are not current-state authority.

## Mid-August — testing-efficiency and agent-safety program

As the suite and agent count grew, the repository formalized two important operating concerns.

### Test feedback time

The Product quality gate was measured rather than guessed. API and browser work were sharded only when they became the measured critical path, browser shards were later packed using recorded duration data, and the expensive Full gate was moved to the merge-ready SHA rather than every development push. See [`../product/docs/BROWSER_AND_BACKEND_FEEDBACK_TIME.md`](../product/docs/BROWSER_AND_BACKEND_FEEDBACK_TIME.md).

### Concurrent agents

Multiple agents working in one clone exposed a serious false-proof risk: a checkout/SHA can change underneath a long test run. Isolated worktrees and exact-SHA provenance became normal operating rules. These are now captured canonically in [`../AGENTS.md`](../AGENTS.md).

## Late August — configurable verification ladder and Case → Procedure architecture

The software verification model was made explicit rather than treating “test procedure” as one overloaded concept.

The completed architecture is:

```text
System Requirement
  ↓
System Test Procedure
  ↓
Execution / Result / Evidence

HLR Requirement
  ↓
HLR Test Case
  ↓
HLR Test Procedure
  ↓
Execution / Result / Evidence

LLR Requirement
  ↓
LLR Test Case
  ↓
LLR Test Procedure
  ↓
Execution / Result / Evidence
```

The programme centered on #720 and completed through the related Case/Procedure issues including #722, #724, #725, #727, #728 and #726. Cases and Procedures share infrastructure where appropriate but have distinct engineering semantics and controlled identities.

Key controlled identities include:

- HLR Case: `HLRTC` / `HLRTCCR` / `HLRTD`;
- HLR Procedure: `HLRTP` / `HLRTPCR` / `HLRTPD`;
- LLR Case: `LLRTC` / `LLRTCCR` / `LLRTD`;
- LLR Procedure: `LLRTP` / `LLRTPCR` / `LLRTPD`.

`HLRTD` and `LLRTD` remain deliberate controlled Case-document identities; they were not cosmetically renamed during the Procedure programme.

Issue #762 / PR #767 then completed the ordinary software-verification UX so users no longer needed hidden query modes to see the architecture. The Software Change Requests workspace became profile-aware across HLR/LLR Case/Procedure contexts, and the normal Software Test Case/Procedure Explorer became one shared build-scoped view over configured Cases and Procedures.

## August 2026 — Problem Reports mature into richer Project-scoped controlled records

Problem Reports evolved from a limited defect record into a Project-scoped controlled engineering workflow. The #765 phase programme delivered, among other things:

- correction/editing by appropriate Project members while preserving exclusive checkout/history;
- a richer category vocabulary with provenance for migrated classifications;
- structured rich authored content/emphasis without storing arbitrary markup;
- a larger whole-record editor with explicit save/check-in semantics;
- richer impact/evidence behavior;
- symmetric same-Project “Related Problem Reports” relationships with controlled history and closure-candidate invalidation.

Phase 6 merged through PR #774 on 2026-08-26.

## August 26 — repository knowledge architecture

By this point AeroLink had accumulated many dated handoffs, audits, roadmaps, launchers, and agent notes. Some historically valuable files still presented themselves near the repository front door even after their assumptions were superseded.

Issue #778 started the repository-hygiene programme to create a small authoritative front door, one current project-state snapshot, one cross-agent contract, organized durable documentation, an indexed historical archive, safe treatment of Windows launcher paths, and guardrails against future documentation sprawl.

## How to use this history

Use this document to understand major architectural transitions and why old terminology may appear in historical files. Do not use it to decide whether an issue is open, a PR is merged, or a particular route/state exists today. Refresh GitHub and consult `PROJECT_STATE.md` for that.