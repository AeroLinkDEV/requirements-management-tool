# AeroLink 3.0 - implementation status

**Status date:** 2026-08-01
**Qualified product commit before this documentation reconciliation:** `067294c`

This is the current scorecard for the long-lived
[AeroLink 3.0 completion contract](AEROLINK_3_ENTERPRISE_LIFECYCLE_COMPLETION.md). The contract describes the
full enterprise ambition; this file describes what the repository truthfully delivers now. Detailed restart
context is in [CURRENT_PRODUCT_HANDOFF_2026-08-01.md](CURRENT_PRODUCT_HANDOFF_2026-08-01.md).

## Status vocabulary

- **MVP delivered:** the product-owned acceptance boundary is implemented and qualified.
- **Delivered foundation:** useful production code is implemented, but the complete enterprise contract remains
  broader than the supported product surface.
- **Deferred by decision:** intentionally outside the current MVP until a recorded trigger is met.
- **Deployment-owned:** requires a selected customer topology, provider, credentials, or service objectives and
  cannot be completed truthfully inside this repository alone.
- **Historical/dormant:** retained implementation or evidence is not a supported current route.

## Overall position

The AeroLink 3.0 parent program (#29) and every review follow-up are closed. The 1 August reconciliation found
no remaining implementation-ready MVP defect in the open backlog. Applicable work was delivered through PRs
#237-#240; satisfied items were closed with evidence; broad refactors and customer-specific deployment work
were closed with explicit reopen conditions.

Closing the program does **not** mean AeroLink claims certification, tool qualification, completed customer
deployment, or every capability named in the enterprise ambition. It means the supported MVP is coherent,
qualified, and has no known open GitHub backlog at this checkpoint.

## Workstream scorecard

| Workstream | Current status | Product evidence and boundary |
| --- | --- | --- |
| 1. Universal controlled editing | **MVP delivered** | SCR/SWCR checkout, renewable leases, autosave snapshots, recovery, check-in/discard, read-only observers, optimistic versions, forced unlock audit, and retained conflict evidence. Production Concurrency simulation was removed; authoritative editing is exercised through real artifacts. |
| 2. Problem-report lifecycle | **Historical/dormant foundation** | Problem/corrective relationships and retained lifecycle implementation support trace and corrective routing, but the broad Problem Reports navigation/search surface remains intentionally dormant. Restore only through a new product decision. |
| 3. Product-line configuration and reuse | **Delivered foundation** | Canonical software builds, exact immutable baselines, released 1.5/read-only and active 1.6 workspaces, controlled libraries, propagation decisions, variants, configuration-correct outputs, deterministic publications, and release evidence. |
| 4. Enterprise identity and account assurance | **MVP delivered; federation deferred** | Local accounts, MFA/recovery codes, Program roles, individual role revocation, distinct global/Program administration, current/other session controls, time-bounded delegation lifecycle, electronic signatures, security audit, provider/mapping foundations, and PostgreSQL migration coverage. OIDC/SAML and SCIM resume only with a real directory contract. |
| 5. Resumable interchange and monitored integrations | **Delivered foundation** | Governed CSV/XLSX onboarding, ReqIF profile round trip, scoped service identities, versioned API, transactional events, HMAC webhooks, retry/dead-letter replay, Jira mapping/link-back, OSLC foundations, and inspectable notification outbox. A real SMTP relay and vendor/provider-specific contracts remain external qualification work. |
| 6. Rich technical content and controlled publications | **MVP delivered** | Structured rich content, approved template revisions, deterministic SYSRD/SWRD/test/change outputs in DOCX/PDF, exact provenance, document control, redlines, publication jobs, manifests, and release evidence packages. |
| 7. Quality, evidence and portfolio intelligence | **MVP delivered** | Build-scoped Command Center, System/HLR/LLR Testing Coverage and Test Results, controlled Test Change Requests, Build Test Sets, verification decision history/reopening, downstream assessments, exact upward allocations, release readiness, permission-safe drill-downs, and immutable evidence/retest history. |
| 8. Production operations and qualification | **Product foundation delivered; deployment-owned remainder** | One-click development/production/shared launchers, readiness, diagnostics, cryptographic attachment checkpoints, manifested backup/verification, isolated restore, retention/hold evidence, upgrade evidence, PostgreSQL migration/bootstrap, production-build browser tests, 50,000-requirement qualification, and 150-client database workload. Protected off-device storage, external alert delivery, TLS/reverse proxy, scheduler provisioning, and approved RPO/RTO/SLOs require a selected deployment. |

## Current control model

- Build 1.5 (`SW-01.50`) is released, immutable, and read-only.
- Build 1.6 (`SW-01.60`) is the active controlled development workspace.
- System, Software HLR, Software LLR, and each verification discipline use exact build scope.
- System approval raises an HLR downstream assessment; HLR approval raises an LLR assessment.
- HLR proposals allocate to current System revisions; LLR proposals allocate to current HLR revisions. An
  explicit derived classification with rationale is the only alternative.
- Approved changes raise controlled, discipline-specific Test Change Requests. Explicit decisions and approved
  procedures populate the build's test set; immutable results and evidence determine readiness.
- Independent review is server-enforced and selected approvers are named before submission where required.

## Qualification evidence

The final PR #240 Product Quality Gate passed:

- complete backend build and tests;
- client lint, type checking, and production build;
- PostgreSQL first-install migration/bootstrap and identity administration paths;
- production-build browser journeys against the API-served single-origin client;
- both complete browser shards; and
- the required enforcing reporter.

Independent local qualification on the final content passed all three browser shards: 112 passed and one
capture-only journey skipped. Exact merged-main qualification passed nine focused browser regressions, two
requirement-materialization regressions, client lint, production build, and live PostgreSQL readiness.

## Boundaries that must remain explicit

- No certification, compliance, or tool-qualification claim.
- No generic identity federation without a provider/customer contract.
- No fake production concurrency or integrity simulations in the product.
- No claim of 150 rendered browser users; the published evidence is 150 simultaneous database clients.
- No claim that repository scripts provision customer backup storage, monitoring, TLS, or recovery objectives.
- No reset of the persistent demonstration database merely to prove an increment.

## Governance

Current issue/PR state must always be refreshed from GitHub; counts in dated records are historical. New work
starts from a reproduced product need, not from an old roadmap sentence. Use focused branches and pull requests,
wait for the required Product Quality Gate, merge, pull `main`, and requalify the exact merge commit.
