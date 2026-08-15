# #563 phase 2 ? bounded API host-reuse pilot and class inventory

Date: 2026-08-15. Branch: `deepseek/563-api-host-reuse-pilot`. Review round: 2.

## Pilot design

`SharedApiHost` is a class-scoped xUnit fixture owning one `AeroLinkApiFactory` (one host, one SQLite database file) for the whole test class. xUnit runs the tests of one class serially, so the class fixture is the reuse boundary: the host/database is built once per class instead of once per test. Each test still gets a fresh `HttpClient` (fresh cookie/session container) and seeds its own uniquely named logical data. The fixture is opt-in; the rest of the suite keeps per-test factories.

Converted classes must satisfy, and were checked for:

- every test uses the default factory options (exceptional tests keep their own fresh factory);
- every seed uses per-test unique user accounts and Program codes (both globally unique-constrained);
- every assertion is scoped by project/program identity or by the test's own seeded IDs ? no empty-table, first-install-bootstrap, or whole-database-count assumptions;
- authentication isolation is proven per test: a fresh client is a fresh session, and an unaffiliated user is refused.

## Pilot tranche 1 (this PR)

| Class | Shared tests | Fresh-host exceptions | Basis |
|---|---:|---:|---|
| ChangeRequestReviewRationaleApiTests | 3 | 0 | Review cycle + rationale/signature persistence; project-scoped assertions |
| CoverageStateFilterApiTests | 6 | 0 | Coverage filters scoped by projectId; program-boundary test retained |
| TestProcedureDocumentApiTests | 4 | 1 | `A_project_created_through_the_api_has_its_documents_immediately` requires first-install bootstrap (user-less DB); the class has five facts total |
| ApprovalConfigurationApiTests | 6 | 0 | Workflow configuration scoped by project; holder/backup names now unique per test |
| ProjectPersonnelApiTests | 13 | 0 | Roster routes project-scoped; `EndedBy` assertion uses the per-test manager name |
| SharedHostIsolationTests | 3 | 0 | Isolation proof: unique-data counts, cross-test fixture-instance identity, program-boundary refusal, two-client session isolation |
| **Converted total** | **32 shared + 3 isolation** | **1** | 33 converted tests + 3 isolation facts |

### Factory math (corrected round 2)

- The five converted classes contain **33 tests** (3+6+4+6+13); before this PR they created **33 per-test factories**.
- After conversion they create **5 class fixtures + 1 fresh factory** (the bootstrap exception) = **6 factories**: a **-27** reduction.
- `SharedHostIsolationTests` adds **1 class fixture** (3 tests, 1 factory).
- Suite delta: 471 factories (489 tests, 563A baseline run 31858889257) to 445 factories (491 tests, run 31861317606) = **-26**, consistent with -27 + 1.
- The 563A telemetry reports class fixtures under `SharedApiHost` (method `class fixture`) as unmatched methods; their construction/host/disposal time is now included in the whole-run `summedFactoryStartupMs` total (attributed + class fixtures/helpers + ambiguous theory rows, every factory exactly once) so the before/after comparison is not structurally biased.

## Exhaustive class inventory (all *.cs files in AeroLink.Api.Tests)

### pilot (7)

- ApprovalConfigurationApiTests.cs
- ChangeRequestReviewRationaleApiTests.cs
- CoverageStateFilterApiTests.cs
- ProjectPersonnelApiTests.cs
- SharedApiHost.cs
- SharedHostIsolationTests.cs
- TestProcedureDocumentApiTests.cs

### fresh-host required (6)

- ApiTestTelemetryTests.cs
- BaselineImportApiTests.cs
- ManagedDocumentApiTests.cs
- ProblemReportPagingApiTests.cs
- ProductionRoutingTests.cs
- SecurityBoundaryTests.cs

### fixture-hosted (ShowcaseApiFixture) (4)

- CodeTraceabilityApiTests.cs
- ConfigurationPublicationApiTests.cs
- DraftDocumentApiTests.cs
- ProcedureDiscussionApiTests.cs

### reuse candidate (not converted) (63)

- AdministratorChangeRequestApiTests.cs
- AuthoredSectionTests.cs
- AuthoringTracedImpactTests.cs
- BuildScopedWorkspaceApiTests.cs
- BuildTestSetApiTests.cs
- CancelReviewAuthorityTests.cs
- ChangeAuthoringInvariantApiTests.cs
- ChangeRequestRenameApiTests.cs
- ClosedReleaseAuthoringTests.cs
- ControlledEditingCheckInApiTests.cs
- ControlledEditingProcedureAuthorityTests.cs
- ControlledProcedureApprovalBasisApiTests.cs
- ControlledProcedureDocumentApiTests.cs
- CorrectiveActionRoutingApiTests.cs
- DeferralAndRevisionListingTests.cs
- DeferredCarryForwardApiTests.cs
- DownstreamAssessmentReopenApiTests.cs
- ExternalIdentityAdminApiTests.cs
- GovernanceEvidenceApiTests.cs
- HistoricalPublicationFreezeApiTests.cs
- IdentifierAllocationTests.cs
- IntegrityCheckpointApiTests.cs
- LegacyProcedureManifestBootstrapApiTests.cs
- LiveTestRegressionApiTests.cs
- ManagedDocumentRecoveryApiTests.cs
- ManualTestChangeRequestApiTests.cs
- OpenDigitalThreadTests.cs
- PreReleaseEvidenceVisibilityTests.cs
- ProblemReportActiveMetricApiTests.cs
- ProblemReportApiTests.cs
- ProblemReportCheckoutApiTests.cs
- ProblemReportDispositionApiTests.cs
- ProblemReportDuplicateDispositionApiTests.cs
- ProblemReportOwnerAuthorityApiTests.cs
- ProblemReportVerificationApiTests.cs
- ProblemReportWaiverApiTests.cs
- ProcedureBaselineApiTests.cs
- ProcedureBrowsingApiTests.cs
- ProcedureManifestEffectivityApiTests.cs
- ProcedureSavedViewApiTests.cs
- ProcedureTraceApiTests.cs
- ProductLineApiTests.cs
- ProgramRoleAuthorityApiTests.cs
- ReleaseCampaignExactIntentApiTests.cs
- ReleasedExecutionEvidenceApiTests.cs
- ReleasedExecutionEvidenceAuthorityMismatchTests.cs
- RequirementFilterExactnessApiTests.cs
- RestartReviewApiTests.cs
- ReviseChangeRequestApiTests.cs
- SavedViewLifecycleApiTests.cs
- SearchableAuthoringPickerApiTests.cs
- SecurityHardeningTests.cs
- ServerAuthorityContractTests.cs
- SoftwareChangeRequestLevelHistoryTests.cs
- TestChangeRequestConsolidationTests.cs
- TestChangeRequestRegisterApiTests.cs
- TestChangeRequestReviewWorkflowTests.cs
- TestChangeRequestScopeApiTests.cs
- TestExecutionEffectivityApiTests.cs
- TestProcedureAuthoringApiTests.cs
- TestProcedureRevisionHistoryApiTests.cs
- VerificationImpactApiTests.cs
- VerificationProgramIsolationApiTests.cs

### non-hosted (2)

- ClientHostingTests.cs
- ProductionConfigurationTests.cs

### support (4)

- ApiTestTelemetry.cs
- SaveRaceGate.cs
- ShowcaseApiFixture.cs
- TimingConnectionInterceptor.cs

## Measurement model

- Factory/host/database starts: 563A telemetry (`factories` per class and whole-run totals, schema v2). After this PR, each converted class reports one `SharedApiHost` factory instead of one per test, and the whole-run startup total includes unmatched fixture startup explicitly.
- Wall clock: the API shards' summed wall and startup come from the same telemetry; a before/after comparison is made on exact-head CI runs. Single runs are variance-dominated; the broad-rollout decision requires at least ten repeated full-concurrency observations, order randomization, summed CPU/disk-I/O, and the >=15% API critical-path threshold.

## Persistent data

No persistent PostgreSQL (port 54329) is used, migrated, or reset. The pilot uses only disposable SQLite databases in the system temp directory and the repository's existing disposable test evidence roots.
