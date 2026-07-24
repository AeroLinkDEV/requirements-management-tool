# AeroLink 3.0 — Implementation Status

**Status date:** 2026-07-24  
**Authority:** This record summarizes implementation evidence and limitations. The detailed workstream contract remains `AEROLINK_3_ENTERPRISE_LIFECYCLE_COMPLETION.md`.

## Status vocabulary

- **Complete:** the workstream acceptance gate is implemented and evidenced.
- **In progress:** production code exists, but one or more acceptance-gate capabilities remain.
- **Foundation only:** shared domain or architectural primitives exist, but no end-to-end capability is claimed.
- **Not started:** no material implementation evidence has been accepted.

## Overall position

AeroLink has a substantial controlled-lifecycle product foundation and a green releasable `main`, but **AeroLink 3.0 is not complete**. Completion requires every workstream acceptance gate, safe migration proof, production operations evidence, workload qualification, and security closure.

No entry in this file claims certification, regulatory compliance, or tool qualification.

## Workstream scorecard

| Workstream | Status | Implemented evidence | Remaining acceptance boundary |
| --- | --- | --- | --- |
| 1. Universal controlled editing | In progress | Shared policy registry; complete SCR/SWCR checkout, renewable lease, autosave snapshots, recovery, check-in/discard, contention and forced unlock | Connect the shared resolver and editing contract to all nine controlled draft families; add complete two-user and lifecycle-transition coverage for each family |
| 2. Full problem-report lifecycle | In progress | Problem-report references and lifecycle links exist in parts of the product | First-class PR identity/revisions, investigation and disposition workflow, closure approval/reopen, release blocker/waiver rules, publications, dashboards, API/events and complete bidirectional acceptance journey |
| 3. Product-line configuration and reuse | In progress | Configuration-aware baseline and integration foundations exist; Workstream 3 delivery has begun | Complete streams, controlled change sets, retained three-way conflicts, reusable libraries, synchronization decisions, variants, composite configurations and configuration-correct outputs |
| 4. Enterprise identity and account assurance | In progress | Local accounts, Program membership, sessions, MFA/recovery and security audit; trusted provider and Program-scoped external-group role-mapping domain contracts; durable provider/mapping persistence with an applied additive migration, administrator-only administration API, fail-closed audited role resolution, and PostgreSQL smoke coverage that exercises the migrated tables | Administration UI; OIDC/SAML login/logout; SCIM; service accounts; break-glass controls; recovery and step-up enforcement; session revocation; provider health |
| 5. Resumable interchange and monitored integrations | In progress | ReqIF binary integrity/provenance, mapping versioning, OSLC consumer monitoring/replay and integration completion evidence | Confirm the complete acceptance gate across interrupted large import, durable checkpoints, cancellation/restart, idempotent replay, conditional writes, provider/consumer configuration-aware links, queues and dead letters |
| 6. Rich technical content and controlled publications | In progress | Controlled SYSRD/SWRD, change, test, traceability and release outputs; valid DOCX/PDF generation and document control | Equivalent rich rendering of images/tables/symbols/references, controlled equations, approved template lifecycle, exact redlines, resumable jobs, reproducibility proof and verified release-package manifests |
| 7. Quality, evidence and portfolio intelligence | In progress | Role-aware dashboards, drill-down foundations, traceability/completeness measures and qualification datasets | Objective/evidence expectation records, blockers/waivers, historical event-time metrics, PR/review/verification trends, permission-safe cross-Program aggregation, metric contracts and controlled exports |
| 8. Production operations and qualification | In progress | One-click local startup, diagnostics, integrity-manifested backup, isolated restore validation, PostgreSQL migration smoke and mixed-workload tools | Structured telemetry, readiness/dependency checks, alerts/runbooks, retention holds, scheduled protected off-device backups, scheduled restore drills, measured RPO/RTO, safe upgrade workflow and published 150-user/50,000-requirement qualification |

## Current accepted delivery focus

The active delivery focus is **Workstream 4 — enterprise identity and account assurance**, tracked by Issue #34.

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

1. Consume the shared mapping service from OIDC/SAML authentication.
2. Add SCIM user/group provisioning using the same server-authoritative mapping model.
3. Add the permission-scoped identity administration UI.
4. Complete session, MFA, recovery, step-up, service-account and break-glass acceptance slices.
5. Close Issue #34 only after its complete end-to-end acceptance gate is evidenced.
6. Complete production operations and qualification under Issue #38.

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
