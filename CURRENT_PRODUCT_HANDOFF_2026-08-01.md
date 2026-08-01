# Current product and repository handoff - 2026-08-01

This is the current restart point for AeroLink. It supersedes the dated handoffs from 28, 29, and 31 July;
those files remain historical delivery records. [PROJECT_STATE.md](PROJECT_STATE.md) is the canonical product
description, [FEATURE_CATALOG.md](FEATURE_CATALOG.md) is the stable capability inventory, and
[DECISIONS_AND_OPEN_QUESTIONS.md](DECISIONS_AND_OPEN_QUESTIONS.md) is the append-only product decision record.

## Repository checkpoint

- Repository: `seanmccarthyns/requirements-management-tool`
- Branch: `main`
- Qualified product commit before this documentation reconciliation: `067294c`
- GitHub backlog: zero open issues and zero open pull requests after the 1 August reconciliation
- Delivery rule: focused `codex/*` branch, pull request, required Product Quality Gate, squash merge, then
  requalify the exact merge commit; never push implementation work directly to `main`
- Local persistent PostgreSQL demonstration data is valuable engineering evidence. Startups and migrations are
  additive; never reset that database merely to make a test easier.

## Product model now in force

A baseline and software build are one product concept. `SW-01.50` (Build 1.5) is released and read-only;
`SW-01.60` (Build 1.6) is the active development workspace. System, Software HLR, Software LLR, and their
verification work remain isolated by Project, build, discipline, and exact revision.

The active engineering chain is:

```text
SCR approval
  -> HLR downstream assessment
      -> Draft/linked SWCR or justified no-change decision
          -> HLR approval
              -> LLR downstream assessment
                  -> Draft/linked SWCR or justified no-change decision

Approved change
  -> discipline-specific controlled Test Change Request
      -> explicit verification decisions
          -> approved procedure revisions in the build test set
              -> immutable executions, results, evidence, and retest history
```

Software requirement proposals also govern the opposite direction before approval:

- an HLR proposal selects one or more current System requirement revisions from the target build;
- an LLR proposal selects one or more current HLR revisions from the target build;
- the only alternative is an explicit derived-requirement classification with a meaningful rationale;
- the selected exact revision IDs are part of the immutable review snapshot; and
- materialization creates exact revision-to-revision `AllocatedFrom` traces while preserving superseded history.

## Delivered since the 31 July handoff

### Live workflow and build-scope corrections - PRs #222, #228, and #230

- Procedure inventories, coverage, history, direct links, and search are isolated across System, HLR, and LLR.
- Full procedure numbers such as `LLRTP-000001.00` survive search, deep links, refresh, and history close.
- Coverage is mutually exclusive: Covered, Suspect, or Uncovered.
- Test Change Request and downstream-assessment return/no-change actions use accessible in-page rationale
  dialogs rather than browser prompts.
- Approver lists exclude the acting engineer where independent review is required.
- Named demo identities no longer reuse one person for incompatible software-lead and review roles.

### Authoring and history correctness - PRs #231, #232, #235, and #236

- Reviewer counts report selected people, not empty picker rows.
- Opening or cancelling an authoring form never consumes a controlled identifier; allocation remains atomic at
  create time.
- Corrected authoring validation clears when the relevant field changes without hiding operational failures.
- Empty History states distinguish a true empty build from search and lifecycle-filter misses, with direct
  recovery actions.

### Accessibility and attribution - PR #237

- Interaction-created controls are named and visible help is programmatically associated.
- Historical build cards explain why an action is unavailable.
- Change-request publications resolve known actors through the persisted people directory while retaining
  unknown legacy identifiers honestly.
- Form semantics are covered across static and dynamically added authoring states.

### Authoritative operations only - PR #238

- The production Concurrency simulation and its fake competing-session mutation were removed. Controlled
  checkout, leases, optimistic versions, and retained conflicts are the authoritative concurrency workflow.
- The count-only IntegrityScan job entry point was removed. The supported integrity operation recomputes
  controlled attachment hashes and reports missing, altered, and unreadable content.
- Historical count-only jobs remain readable as legacy health snapshots.

### Identity authority lifecycle - PR #239

- Administrators see current Program roles, cannot create duplicate grants, and may revoke one role without
  disabling the account.
- Global system administration is distinct from Program Administrator authority.
- Account Security identifies the current session, retains session history, and can revoke other sessions.
- Delegations show Program, people, role, interval, status, reason, actor, and revocation controls.
- Expired and revoked delegations remain attributable history but grant no authority.

### Prospective upward allocation - PR #240

- HLR/LLR proposals persist exact proposed upstream revision IDs.
- Search and validation use the target build's effective baseline and reject wrong-level, wrong-Project,
  obsolete, and unknown revisions.
- One-to-one and many-parent allocations survive checkout/check-in and change-request revisioning.
- Reviewers see the exact allocation count or the explicit derived exception before signing.

## Verification state

The merge gate for PR #240 passed backend, client, PostgreSQL migration/bootstrap, production-build browser,
both browser shards, and the enforcing reporter. Independent local qualification on the same content passed:

- 159 API tests, 196 domain tests, and 159 infrastructure tests in the complete hosted backend gate;
- 112 browser journeys across three local shards, with one intentionally skipped capture-only journey;
- nine exact merged-main authoring, security, attribution, and upward-allocation browser regressions;
- two exact merged-main requirement-materialization regressions; and
- client lint and production build.

After merge, the supported launcher restarted the persistent application successfully: the website returned
HTTP 200 and `/health/ready` returned HTTP 200 with PostgreSQL connected.

## Intentional boundaries

- AeroLink makes no certification or tool-qualification claim.
- Problem Report implementation is retained but its broad product surface remains dormant pending a deliberate
  product decision; do not restore old navigation because historical documents mention it.
- OIDC/SAML, SCIM, and organization directory provisioning are not current MVP work. Resume only when a real
  customer/provider contract defines protocols, claims, group mapping, logout, recovery, and break-glass needs.
- TLS topology, protected off-device storage, external alert delivery, approved RPO/RTO/SLOs, and service-identity
  scheduling belong to a selected deployment. The local workstation now has a configurable current-user daily
  backup task which invokes and verifies the existing complete backup flow; the repository otherwise provides
  product-side health, evidence, backup, verification, restore, and runbook foundations without pretending to
  provision customer infrastructure.
- The published scale evidence remains 50,000 requirements and 150 simultaneous database clients on one
  workstation. It is not a claim of 150 rendered browser sessions or a production service-level guarantee.
- Local demonstration identities and password are non-production.

## Documentation authority and history

- Start at [README.md](README.md), then [PROJECT_STATE.md](PROJECT_STATE.md), then this handoff.
- Root `.docx` files are preserved original source inputs. They are not current product specifications and are
  intentionally not edited as implementation changes land.
- Dated handoffs, reviews, acceptance notes, mockup notes, and update reports are historical evidence. Their
  dates and outcomes remain valid, but their issue counts and next-work recommendations are not current.
- Long-lived contracts and roadmaps describe intent and acceptance boundaries; current delivery claims belong
  in `PROJECT_STATE.md`, `AEROLINK_3_IMPLEMENTATION_STATUS.md`, and this handoff.

## Safe restart

1. Confirm repository, remote, branch, and a clean worktree.
2. Pull `main` and inspect recent merged pull requests before choosing work.
3. Start with `START_AEROLINK_PRODUCTION.bat` for a deployment-shaped demonstration or
   `START_AEROLINK.bat` for development.
4. Preserve the persistent PostgreSQL database and its authored engineering records.
5. Reproduce a defect, search GitHub for duplicates, implement the smallest coherent fix, add regression
   coverage, pass the proportional local and hosted gates, merge, pull, and requalify the exact commit.
