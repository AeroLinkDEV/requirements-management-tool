# #563 phase 2 — bounded API host-reuse pilot and class inventory

Date: 2026-08-15. Branch: `deepseek/563-api-host-reuse-pilot`.

## Pilot design

`SharedApiHost` is a class-scoped xUnit fixture owning one `AeroLinkApiFactory` (one host, one SQLite
database file) for the whole test class. xUnit runs the tests of one class serially, so the class fixture
is the reuse boundary: the host/database is built once per class instead of once per test. Each test still
gets a fresh `HttpClient` (fresh cookie/session container) and seeds its own uniquely named logical data,
so nothing is shared across tests except the process and the schema. The fixture is deliberately opt-in:
the rest of the suite keeps per-test factories.

Converted classes must satisfy, and were checked for:

- every test uses the default factory options (no `commandInterceptor`, `storageFaultInjector`,
  `staticFilesRoot`, `seedDemoAccounts`, etc.), or the exceptional test keeps its own fresh factory;
- every seed uses per-test unique user accounts and Program codes (both globally unique-constrained);
- every assertion is scoped by project/program identity or by the test's own seeded IDs — no empty-table,
  first-install-bootstrap, or whole-database-count assumptions;
- authentication isolation is proven per test: a fresh client is a fresh session, and an unaffiliated user
  is refused.

## Pilot tranche 1 (this PR)

| Class | Shared tests | Fresh-host exceptions | Basis |
|---|---:|---:|---|
| ChangeRequestReviewRationaleApiTests | 3 | 0 | Review cycle + rationale/signature persistence; project-scoped assertions |
| CoverageStateFilterApiTests | 6 | 0 | Coverage filters scoped by projectId; program-boundary test retained |
| TestProcedureDocumentApiTests | 5 | 1 | `A_project_created_through_the_api_has_its_documents_immediately` requires first-install bootstrap (user-less DB) |
| ApprovalConfigurationApiTests | 6 | 0 | Workflow configuration scoped by project; holder/backup names now unique per test |
| ProjectPersonnelApiTests | 13 | 0 | Roster routes project-scoped; `EndedBy` assertion uses the per-test manager name |
| SharedHostIsolationTests | 2 | 0 | Dedicated isolation proof: unique-data counts and program-boundary refusal on a shared host |
| **Total** | **35** | **1** | |

Before this tranche, these classes created one factory per test (35 factories across the five classes);
after it they create five class fixtures plus one fresh factory for the bootstrap exception (6 factories),
so 29 host builds/database creations are removed from the suite. The 563A telemetry reports the class
fixture under `SharedApiHost` (method `class fixture`), the same fixture-owner model as
`ShowcaseApiFixture`.

## Classification (all API test classes)

### Non-hosted (no `AeroLinkApiFactory`)

Classes in `AeroLink.Api.Tests` that never construct the factory. They are already zero-cost and are
candidates for #566 (moving deterministic matrices to domain/application tests), not for host reuse.

### Fresh-host required (not shareable as written)

| Class | Reason |
|---|---|
| SecurityBoundaryTests | owns the factory; `seedDemoAccounts` variants; bootstrap/security matrix |
| ApiTestTelemetryTests | telemetry guard tests intentionally disable/suppress their own telemetry |
| ManagedDocumentApiTests | several tests use `storageFaultInjector`/`commandInterceptor`; managed-document surface |
| ProblemReportPagingApiTests | uses `commandInterceptor` |
| ProductionRoutingTests | uses `staticFilesRoot` |
| BaselineImportApiTests | `Source_history_is_recorded_as_reported_and_never_becomes_a_revision` asserts the whole `RequirementRevisions` table is empty; other tests use a second fixed seed |

### Reuse candidate — not yet converted

Every remaining class that uses default `new AeroLinkApiFactory()` and shows project-scoped assertions.
These are the next tranche candidates, but each one must be re-verified seed-by-seed before conversion:
most seeds use fixed user names and Program codes and need the same unique-tag conversion applied in this
pilot. High-startup candidates from the 563A baseline include `ManualTestChangeRequestApiTests`,
`TestChangeRequestReviewWorkflowTests`, `ProcedureBaselineApiTests`, `TestProcedureAuthoringApiTests`,
`ProcedureBrowsingApiTests`, and `TestChangeRequestRegisterApiTests`.

## Measurement model

- Factory/host/database starts: 563A telemetry (`factories` per class, schema v2). After this PR, each
  converted class reports one `SharedApiHost` factory instead of one per test.
- Wall clock: the API shards' summed wall and startup come from the same telemetry; a before/after
  comparison is made on the exact-head CI run vs the previous 563A baseline (run 31858889257).
- Acceptance: broad rollout requires the pilot to materially reduce host starts, database starts, summed
  CPU, and complete API shard wall clock (target >=15% API critical-path improvement), with at least ten
  repeated full-concurrency observations, order randomization, and no flake increase. This PR is the
  implementation + first measurement, not the broad rollout.

## Persistent data

No persistent PostgreSQL (port 54329) is used, migrated, or reset. The pilot uses only disposable SQLite
databases in the system temp directory and the repository's existing disposable test evidence roots.
