# AeroLink 3.0 — Implementation Status

**Status date:** 2026-07-28  
**Authority:** This record summarizes implementation evidence and limitations. The detailed workstream contract remains `AEROLINK_3_ENTERPRISE_LIFECYCLE_COMPLETION.md`.

> This file sat at 2026-07-24 through fifteen merges while `PROJECT_STATE.md` named it the authority for
> per-workstream status. A scorecard that is not updated with the work it scores is worse than no scorecard,
> because it is believed. Update it in the change that moves a workstream, as
> [PROJECT_STATE.md](PROJECT_STATE.md) already requires of itself.

## Status vocabulary

- **Complete:** the workstream acceptance gate is implemented and evidenced.
- **In progress:** production code exists, but one or more acceptance-gate capabilities remain.
- **Foundation only:** shared domain or architectural primitives exist, but no end-to-end capability is claimed.
- **Not started:** no material implementation evidence has been accepted.

## What has merged since the last status date

Two evenings of product review (26 and 27 July) and their follow-up merged through PR #98. **None of it moves a
workstream boundary**, and the scorecard below is unchanged as a result — that is the honest reading, not an
oversight. The work was defect repair, reachability and product decisions inside capabilities already claimed:
change-request allocation separated from state with a deferral shelf (DEC-056), a released build closed to new
change requests (DEC-055), revision from the state approved change requests actually rest in (DEC-054),
author-chosen specification sections applied at materialization (DEC-057), documents offered where requirements
are read, and computed trace impact shown beside the declared disposition (DEC-059).

The pattern worth recording for the workstreams still open: four of the eleven findings on 27 July were **not
missing features but unreachable ones** — a gate admitting a state nothing rested in, a domain method with no
endpoint, a field that was read-only where it mattered, and a read-side filter no authoring path could aim. A
workstream can be implemented and evidenced and still be unreachable, and this scorecard does not currently
distinguish those. Acceptance evidence should name the path a user takes, not only the capability.

## Overall position

AeroLink has a substantial controlled-lifecycle product foundation and a green releasable `main`, but **AeroLink 3.0 is not complete**. Completion requires every workstream acceptance gate, safe migration proof, production operations evidence, workload qualification, and security closure.

No entry in this file claims certification, regulatory compliance, or tool qualification.

## Workstream scorecard

| Workstream | Status | Implemented evidence | Remaining acceptance boundary |
| --- | --- | --- | --- |
| 1. Universal controlled editing | In progress | Shared policy registry; complete SCR/SWCR checkout, renewable lease, autosave snapshots, recovery, check-in/discard, contention and forced unlock | Connect the shared resolver and editing contract to all nine controlled draft families; add complete two-user and lifecycle-transition coverage for each family |
| 2. Full problem-report lifecycle | In progress | Problem-report references and lifecycle links exist in parts of the product | First-class PR identity/revisions, investigation and disposition workflow, closure approval/reopen, release blocker/waiver rules, publications, dashboards, API/events and complete bidirectional acceptance journey |
| 3. Product-line configuration and reuse | In progress | Configuration-aware baseline and integration foundations exist; Workstream 3 delivery has begun | Complete streams, controlled change sets, retained three-way conflicts, reusable libraries, synchronization decisions, variants, composite configurations and configuration-correct outputs |
| 4. Enterprise identity and account assurance | Delivered slice; remainder deferred | Local accounts, Program membership, sessions, MFA/recovery and security audit; trusted provider and Program-scoped external-group role-mapping domain contracts; durable provider/mapping persistence with an applied additive migration, administrator-only administration API, fail-closed audited role resolution, and PostgreSQL smoke coverage that exercises the migrated tables | **Deferred by decision (2026-07-24), not in progress:** OIDC/SAML login/logout; SCIM; break-glass; step-up; account recovery and password expiration; administrator session inventory; provider health; administration UI. See the Workstream 4 decision record in the completion contract |
| 5. Resumable interchange and monitored integrations | In progress | ReqIF binary integrity/provenance, mapping versioning, OSLC consumer monitoring/replay and integration completion evidence; **a named Jira connector with field mapping and link-back**; **email delivery of approval notifications through a transactional outbox** | Confirm the complete acceptance gate across interrupted large import, durable checkpoints, cancellation/restart, idempotent replay, conditional writes, provider/consumer configuration-aware links, queues and dead letters. Email delivery has never been exercised against a real SMTP relay |
| 6. Rich technical content and controlled publications | In progress | Controlled SYSRD/SWRD, change, test, traceability and release outputs; valid DOCX/PDF generation and document control; **rich authored content — tables, figures and symbols stored as structure, never markup — in requirements and change-request narrative, reproduced in DOCX and PDF**; **approved template revisions that decide what a generated document contains**, replacing a template body no generator opened | Controlled equations, exact redlines across every field, reproducibility proof and verified release-package manifests |
| 7. Quality, evidence and portfolio intelligence | In progress | Role-aware dashboards, drill-down foundations, traceability/completeness measures and qualification datasets | Objective/evidence expectation records, blockers/waivers, historical event-time metrics, PR/review/verification trends, permission-safe cross-Program aggregation, metric contracts and controlled exports |
| 8. Production operations and qualification | In progress | One-click local startup, diagnostics, integrity-manifested backup, isolated restore validation, PostgreSQL migration smoke and mixed-workload tools; **a production-build launcher serving the client from the API on one origin, gated by browser journeys against that artifact** (DEC-052); **both launchers waiting on a readiness check that opens a database connection** rather than on liveness; **an authenticated 150-session HTTP load harness** whose first run found the product refusing sign-in to 121 of 150 users | Structured telemetry, dependency checks, alerts/runbooks, retention holds, scheduled protected off-device backups, scheduled restore drills, measured RPO/RTO, safe upgrade workflow, and a published 150-user/50,000-requirement workload result. The workspace query still caps the page at ~380ms; the costed path is in `CAPABILITY_ROADMAP.md` and is deliberately not started |

## Current accepted delivery focus

Workstream 4 is **no longer the active delivery focus**. Its delivered slice is described below; its
remaining capabilities were deferred by explicit decision on 2026-07-24 and are recorded in the Workstream 4
decision record in `AEROLINK_3_ENTERPRISE_LIFECYCLE_COMPLETION.md`. Issue #34 stays open as the tracking
record for that deferred work and must not be closed as if the full gate were met.

The merged increments establish:

- explicit OIDC and SAML provider definitions;
- canonical provider keys and canonicalized absolute issuer trust anchors;
- Program-scoped external-group-to-role mappings;
- provider-specific, fail-closed matching for malformed external identity input;
- explicit enable/disable lifecycle;
- durable persistence owned by the EF model, with database uniqueness for the provider key, the issuer
  anchor and the provider/group/Program/role authority tuple;
- an administrator-only administration API whose every mutation is saved together with its security
  audit event, so an authority change cannot be recorded without evidence; and
- domain, persistence, API and PostgreSQL smoke coverage that exercises the migrated tables.

This does **not** claim identity federation or provisioning is operational. It precedes authentication
handlers, logout propagation, SCIM endpoints, administration UI, service accounts, break-glass access,
step-up enforcement and provider health monitoring.

### Correction — closure-integrity note

The first attempt at the persistence increment shipped a migration class that carried neither
`[Migration]` nor `[DbContext]`, and defined its tables outside the EF model. Entity Framework discovers
migrations by attribute, so `Database.Migrate()` skipped it silently on PostgreSQL and `EnsureCreated()`
had no model to build from elsewhere: the tables existed only inside a hand-written test fixture, and
every administration endpoint would have failed at runtime. The quality gate stayed green because no test
or smoke step called those endpoints. The migration is now generated by `dotnet ef`, the entities are part
of the model, and two guard tests fail the build if any migration is undiscoverable or if the model drifts
from its snapshot. Treat "the gate was green" as insufficient evidence that a capability is reachable.

## Delivery sequence from this checkpoint

Workstream 4's remaining sequence is held, not scheduled. It resumes at the trigger recorded in the
contract — the first commitment to deploy AeroLink for an organization authenticating against its own
directory — and in the order given there. Issue #34 may close only when that sequence is either completed
and evidenced, or formally withdrawn from the program.

The active focus is the reconciled product-review remediation backlog in issues #99-#139. It is being delivered
in dependency order: production mutation/test gates; controlled-change correctness and authorization;
verification/readiness/traceability; content/audit/accessibility/maintainability; then operations, integrity,
reconciliation and repository governance. This work repairs reachability, correctness and qualification inside
existing workstreams; it does not by itself move a scorecard boundary or resume Workstream 4 federation.

The first controlled-change correctness increment closes proposal metadata loss and lifecycle bypasses:
schema-governed authored attributes, server-authoritative derived state, durable specification placement, and
canonical impact dispositions now share one contract across browser, API, domain, check-in and materialization
(DEC-062). Legacy gaps are reported rather than silently invented or rewritten.

## Repository and authority corrections

The following facts supersede earlier planning assumptions that predated repository publication:

- The shared repository exists at `seanmccarthyns/requirements-management-tool`.
- Repository visibility is private.
- `main` is the source-of-truth integration branch and must remain releasable.
- GitHub issues and pull requests are the delivery-control mechanism for AeroLink 3.0 increments.
- Markdown in Git remains authoritative for product definition and implementation limitations.

Earlier assumptions or open questions suggesting that the GitHub repository had not yet been selected should be treated as historical planning context, not current uncertainty. A future append-only decision-log update should formally supersede those entries.

## Rules for future implementation claims

A workstream status may move to **Complete** only when:

- its complete acceptance gate is executable and green;
- persistence changes include safe migration and PostgreSQL smoke evidence;
- user-visible behavior includes browser acceptance coverage;
- authorization, audit, immutable-history and configuration contracts are preserved;
- operational and security impacts are documented; and
- the implementation limitation records are updated in the same delivery increment.
