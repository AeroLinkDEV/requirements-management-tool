# Implementation and acceptance map

Use the actual feature [tree at 027c985e](https://github.com/AeroLinkDEV/requirements-management-tool/tree/027c985e82d1d1b764a2f24eac6e6c2ed4ec2c71), not the handoff branch's main-based product tree, for feature review.

## Feature locations within that tree

| Path | Responsibility |
| --- | --- |
| `product/src/AeroLink.Domain/Programs/ProgramRecord.cs` | Nullable operational SoftwareRelease.PickerInsertionOrdinal; not a controlled identity or creation timestamp. |
| `product/src/AeroLink.Infrastructure/Persistence/AeroLinkDbContext.cs` | ValueGeneratedOnAdd, after-save Ignore, Npgsql identity-strategy suppression, SQLite per-table RETURNING/readback and trigger metadata. |
| `product/src/AeroLink.Infrastructure/Persistence/Migrations/20260921140636_AddReleasePickerMembership.cs` and designer/snapshot | Nullable column; global PostgreSQL sequence CACHE 1/NO CYCLE; supplied-value rejection; project advisory lock before nextval; immutability. |
| `product/src/AeroLink.Infrastructure/Persistence/ReleasePickerSqliteGuard.cs` | Atomic one-time legacy-cohort classification, supplied-value/flag rejection, AFTER INSERT per-project MAX+1, immutable membership. |
| `product/src/AeroLink.Api/Program.cs` | Supported SQLite host initialization installs guards after EnsureCreated. Direct schema-only fixtures are not guard-installation proof. |
| `product/src/AeroLink.Api/ManagedDocumentEndpoints.cs` | Release page-one transaction/fence/cutoff; membership before canonical keyset/Take; continuation and exact-ID results. |
| `product/src/AeroLink.Api/ManagedDocumentPaging.cs` | Release v2 token, 4096 raw bound, required cutoff/snapshot/scope/filter, fail-closed old Release cursor. |
| `product/tests/AeroLink.Api.Tests/ProjectSetupPostgresQualificationTests.ReleasePickerSnapshot.cs` | Real provider upgrade/preservation, old-snapshot writer schedules, wait/cancellation, guard matrix, actual SQL and scale evidence. |
| `product/tests/AeroLink.Api.Tests/ReleasePickerMembershipApiTests.cs` | Membership, cursor, EF lifecycle and access qualification. |
| `product/tests/AeroLink.Api.Tests/ReleasePickerSqliteGuardTests.cs` | Installer, rollback, restart, forbidden mutations and copied-host behavior. |
| `product/client/tests/managed-documentation-center-picker-continuation.spec.ts` | Real UI continuation/fresh traversal and exact selected Release link. |
| `product/docs/MANAGED_DOCUMENTATION_CENTER.md` | User-facing guarantee and documented legacy/provider boundary. |

The global PostgreSQL sequence can have permanent gaps; SQLite can reuse rolled-back uncommitted MAX+1 values. Neither means display ordering changes. Retained release membership and immutable ordinals are part of the design assumptions. SQLite's operational flag is not part of the shared PostgreSQL/EF schema.

## Issue acceptance checklist to reconcile with live #1040

| Criterion | Required proof / next review focus |
| --- | --- |
| R01 | Retained disposable PostgreSQL baseline defect proof; avoid repeating unless evidence invalid. |
| R02 | Original traversal membership exactly once; later inserts on either side excluded; fresh traversal includes them; search binding. |
| R03 | Real concurrent writers, both fence orders, stale writer snapshots, rollback/cancellation and the existing canonical-identity race. |
| R04 | Empty install and actual preceding-schema upgrade; controlled identities, predecessor/build/baseline references and persisted manifest/hash/ElectronicSignature evidence preserved; idempotent migration. |
| R05 | Trigger-level coverage of every insertion path; supplied value and mutation refusal; EF mapping/readback compatibility. |
| R06 | Old/malformed/oversized/missing-field/cross-filter cursors, page bounds, revoked/foreign access and relationship-write refusal. |
| R07 | Canonical numeric order, historical labels/ties, exact selected Release IDs/deep links, non-Release behavior. |
| R08 | Actual generated continuation SQL and parameters, predicate-before-materialization/limit, honest plan and scale measurements. |
| R09 | Required PostgreSQL runner with no skipped required tests; separate SQLite proof; real browser journey. |
| R10 | Applicable docs, planner, builds and stable generated contracts. |
| R11 | Fresh exact-head trusted Full Product proof, independent integration review, protected composed-candidate checks and supported rollout readiness. **Still open at handoff.** |
| R12 | Post-merge deployed identity/migration and local read-only acceptance before issue closure. **Still open at handoff.** |

Historical R01–R10 completeness claims must be tied to actual artifacts/content. The issue's starting body and all checkboxes are retained in [github-snapshot/issue-1040.json](github-snapshot/issue-1040.json). [TEST-RESULTS.json](TEST-RESULTS.json) is a discovery index, not a fresh run.

## Separate #1098 code and limits

Use [the requester candidate tree](https://github.com/AeroLinkDEV/requirements-management-tool/tree/23bb25d987639ada3356a7003fa0380ba08e5042). The change is only `.github/workflows/request-full-ci.yml` and `product/test-planner/tests/full-ci-readiness-dispatch.test.mjs`.

It introduces EXHAUSTED/PENDING identity, a common gate before every POST including NONE, completeness/timestamp/attempt/self checks, conservative spend-once requester consumption, one dispatch, uncertain-response discovery without redispatch, pinned run+attempt direct polling, and per-attempt qualification. Existing App/publisher/queue exclusion controls remain separate requirements.

The complete intended design and corrected implementation packets are in the evidence tree. Historical prototypes and printed-only probes are not real workflow transition qualification. Installation and rollback remain unresolved; the local suite's 32/32 result does not overcome the failed hosted gate or the protected-kernel refusal.
