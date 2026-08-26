# AeroLink 3.0 - implementation status

> **ARCHIVED / SUPERSEDED.** This scorecard is preserved as the 10 August 2026 implementation checkpoint. It does not describe the repository's current delivered state today. Use [`../../PROJECT_STATE.md`](../../PROJECT_STATE.md) and current GitHub Issues/PRs for present product truth. The historical wording is intentionally retained after this notice.

**Status date:** 2026-08-10
**Qualified product checkpoint:** `main` at `af8760a6ad17b6266a770fb8c0beb2b67eaf3c90` after the August procedure-control closeout through #367

This is the current scorecard for the long-lived
[AeroLink 3.0 completion contract](../../AEROLINK_3_ENTERPRISE_LIFECYCLE_COMPLETION.md). The contract describes the
full enterprise ambition; this file describes what the repository truthfully delivers now. Detailed restart
context is in [CURRENT_PRODUCT_HANDOFF_2026-08-10.md](CURRENT_PRODUCT_HANDOFF_2026-08-10.md).

## Status vocabulary

- **MVP delivered:** the product-owned acceptance boundary is implemented and qualified.
- **Delivered foundation:** useful production code is implemented, but the complete enterprise contract remains
  broader than the supported product surface.
- **Deferred by decision:** intentionally outside the current MVP until a recorded trigger is met.
- **Deployment-owned:** requires a selected customer topology, provider, credentials, or service objectives and
  cannot be completed truthfully inside this repository alone.
- **Historical/dormant:** retained implementation or evidence is not a supported current route.

## Overall position

The original AeroLink 3.0 parent program (#29) and its planned enterprise workstreams are delivered, deferred by
recorded decision, or deployment-owned. Subsequent focused increments delivered active Problem Reports,
downstream assessments, exact build-scoped verification, the production-served single-origin client, Stage 3B
verification navigation, and Stage 4 first-class manual Test Change Request authoring.

Closing those increments does **not** mean AeroLink claims certification, tool qualification, completed customer
deployment, or that no product defects remain. It means the supported MVP is coherent and qualified at the named
checkpoint. The independent Aug. 7–10 audit/remediation sequence is now closed through #424/#442/#367. The original #395–#402/#214 queue, the later #417–#424 queue, and procedure-control follow-ups #364/#365/#367 are corrected and closed. #332 is the only open product issue at the audited checkpoint.

## Workstream scorecard

| Workstream | Current status | Product evidence and boundary |
| --- | --- | --- |
| 1. Universal controlled editing | **MVP delivered** | change request checkout, renewable leases, autosave snapshots, recovery, check-in/discard, read-only observers, optimistic versions, forced unlock audit, and retained conflict evidence. Test procedures are deliberately excluded from direct universal editing under DEC-103; their controlled changes occur through TCRs. |
| 2. Problem-report lifecycle | **MVP increment active** | Project-scoped Problem Reports are navigable, searchable and controlled-editable from every build context, carry explicit target-build attribution, drive change request/TCR work, and project approved corrective actions and selected evidence; their lifecycle authority is Project-scoped rather than inherited from the active build. Broader classification and closure policy remains incremental under DEC-085/DEC-089. |
| 3. Product-line configuration and reuse | **Delivered foundation** | Canonical software builds, exact immutable requirement and procedure manifests, released 1.5/read-only and active 1.6 workspaces, controlled libraries, propagation decisions, variants, configuration-correct outputs, deterministic publications, and release evidence. The explicit legacy procedure-manifest bootstrap is delivered: Configuration Management records one attributable exact migration snapshot before normal successor carry-forward. |
| 4. Enterprise identity and account assurance | **MVP delivered; federation deferred** | Local accounts, MFA/recovery codes, Program roles, individual role revocation, distinct global/Program administration, current/other session controls, time-bounded delegation lifecycle, password-confirmed electronic signatures including TCR stages, security audit, provider/mapping foundations, and PostgreSQL migration coverage. OIDC/SAML and SCIM resume only with a real directory contract. |
| 5. Resumable interchange and monitored integrations | **Delivered foundation** | Governed CSV/XLSX onboarding, ReqIF profile round trip, scoped service identities, versioned API, transactional events, HMAC webhooks, retry/dead-letter replay, Jira mapping/link-back, OSLC foundations, and inspectable notification outbox. A real SMTP relay and vendor/provider-specific contracts remain external qualification work. |
| 6. Rich technical content and controlled publications | **MVP delivered** | Structured rich content, approved template revisions, deterministic SYSRD/SWRD/test/change outputs in DOCX/PDF, exact provenance, document control, redlines, publication jobs, manifests, and release evidence packages. Managed Word documents use the desktop connector and retain exact DOCX/PDF candidates. |
| 7. Quality, evidence and portfolio intelligence | **MVP delivered** | Build-scoped Command Center; direct System/HLR/LLR Change Requests, Test Procedure Explorer and Test Results surfaces; numbered manual TCRs and automatic test-change assessments; staged review; Build Test Sets; verification decision history/reopening; downstream assessments; exact upward allocations; release readiness; and immutable evidence/retest history. Audit issues #214, #395–#402 and #417–#424 are closed; exact effectivity, search, history, document provenance, execution/evidence authority, legacy bootstrap and controlled stale-target handling are delivered. |
| 8. Production operations and qualification | **Product foundation delivered; deployment-owned remainder** | One-click development/production/shared launchers, API-served production client, readiness, diagnostics, cryptographic attachment checkpoints, manifested backup/verification, isolated restore, retention/hold evidence, upgrade evidence, PostgreSQL migration/bootstrap, production-build browser tests, 50,000-requirement qualification, and 150-client database workload. Protected off-device storage, external alert delivery, TLS/reverse proxy, scheduler provisioning, and approved RPO/RTO/SLOs require a selected deployment. |

## Current control model

- Build 1.5 (`SW-01.50`) is released, immutable, and read-only.
- Build 1.6 (`SW-01.60`) is the active controlled development workspace.
- System, Software HLR, Software LLR, and each verification discipline use explicit build context.
- System approval raises an HLR downstream assessment; HLR approval raises an LLR assessment.
- PRs may drive every change-request type; requirement changes never manufacture a PR.
- Every procedure covering an introduced or modified requirement is mandatory pre-release scope and cannot be
  removed from that build's test set.
- HLR proposals allocate to current System revisions; LLR proposals allocate to current HLR revisions. An
  explicit derived classification with rationale is the only alternative.
- Approved changes raise discipline-specific test assessments. Test Change Requests may also be raised manually
  over one or more approved source changes and carry governed procedure-change proposals.
- Configured review workflows freeze stage identity, order/mode, authority, and version on each review cycle;
  where none is configured, the independent-Approver fallback remains.
- Approved procedure changes materialize only through an approved TCR; there is no direct procedure mutation or
  separate procedure-level approval.
- Immutable results and evidence over the build test set determine readiness.

## Qualification evidence

Stage 4 PR #388 closed at head `6cc22acd36a1f984d54dabf2a11a952325051c2b` with:

- Domain 286 / 286;
- Infrastructure 202 / 202;
- API 293 / 293;
- client lint and type-check (one pre-existing warning only);
- production build;
- focused browser journeys 20 / 20;
- production-build journeys 10 / 10;
- full local browser suite: 147 passed plus one intentional capture-only skip; and
- PostgreSQL migration and secure-bootstrap validation.

The squash merge produced `main` commit `d06fcee94473a9128a98e58b3699c1f6c0ad3af6`. Post-merge Product Quality
Gate run `31269258110` completed successfully on that exact commit. Browser shards skipped by the main-push
classifier had run successfully on the immediately preceding PR merge candidate.

## Latest qualification checkpoint

The final product change in the closeout sequence, #367 / PR #445, was qualified on exact head
`65d5c72ab5fc0f24bfd3c898827efc472a3c0726` by Product Quality Gate run `31436826622` (run 839). Backend/client,
production-build browser, browser shard 1/2, browser shard 2/2 and the required aggregate reporter all completed
successfully. The PostgreSQL migration/bootstrap shard was truthfully skipped by classification because that
change had no migration/persistence surface.

#364 qualification also exposed and corrected a provider-sensitive `/api/baselines/predecessors` query and
stale routing/test assumptions. A later browser timing failure was retried on the identical commit and passed;
merge still waited for the required aggregate gate to be green.

## Historical Aug. 7–10 audit backlog — closed

The focused audit sequence is retained as history rather than current backlog. #395–#402/#214, #417–#424,
#442, #364, #365 and #367 are closed after focused corrections and qualification. #442 was merged through
replacement PR #444; older draft PR #443 was closed as superseded and was never merged.

At the audited checkpoint the only open product issue is #332: complete the immutable controlled baseline,
source-identity outcome and representative-extract qualification for importing an existing program.

## Boundaries that must remain explicit

- No certification, compliance, or tool-qualification claim.
- No generic identity federation without a provider/customer contract.
- No fake production concurrency or integrity simulations in the product.
- No claim of 150 rendered browser users; the published evidence is 150 simultaneous database clients.
- No claim that repository scripts provision customer backup storage, monitoring, TLS, or recovery objectives.
- No reset of the persistent demonstration database merely to prove an increment.
- No claim that an existing program has been imported as controlled requirements until #332 is completed.

## Governance

Current issue/PR state must always be refreshed from GitHub; counts in dated records are historical. New work
starts from a reproduced product need, not from an old roadmap sentence. Use focused branches and pull requests,
wait for the required Product Quality Gate, obtain explicit owner merge authorization, squash merge, pull
`main`, and requalify the exact merge commit. Repository governance settings and the persistent database are not
changed to make delivery convenient.
