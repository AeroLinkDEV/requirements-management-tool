# #563 phase 2 ? bounded API host-reuse pilot and class inventory

Date: 2026-08-15. Branch: `deepseek/563-api-host-reuse-tranche-2`. Review round: 1.

## Pilot design

`SharedApiHost` is a class-scoped xUnit fixture owning one `AeroLinkApiFactory` (one host, one SQLite database file) for the whole test class. xUnit runs the tests of one class serially, so the class fixture is the reuse boundary: the host/database is built once per class instead of once per test. Each test still gets a fresh `HttpClient` (fresh cookie/session container) and seeds its own uniquely named logical data. The fixture is opt-in; the rest of the suite keeps per-test factories.

Converted classes must satisfy, and were checked for:

- every test uses the default factory options (exceptional tests keep their own fresh factory);
- every seed uses per-test unique user accounts and Program codes (both globally unique-constrained);
- every assertion is scoped by project/program identity or by the test's own seeded IDs ? no empty-table, first-install-bootstrap, or whole-database-count assumptions;
- authentication isolation is proven per test: a fresh client is a fresh session, and an unaffiliated user is refused.

## Tranche 1 (merged as #584, `ec75f91`)

| Class | Shared tests | Fresh-host exceptions |
|---|---:|---:|
| ChangeRequestReviewRationaleApiTests | 3 | 0 |
| CoverageStateFilterApiTests | 6 | 0 |
| TestProcedureDocumentApiTests | 4 | 1 (first-install bootstrap) |
| ApprovalConfigurationApiTests | 6 | 0 |
| ProjectPersonnelApiTests | 13 | 0 |
| SharedHostIsolationTests | 3 (isolation facts) | 0 |
| **Tranche 1 totals** | **32 shared + 3 isolation** | **1** |

## Tranche 2a (this PR)

| Class | Shared tests | Fresh-host exceptions | Basis |
|---|---:|---:|---|
| DownstreamAssessmentReopenApiTests | 5 | 0 | Withdraw/reopen capability and authority, project-scoped; decidedBy/actor assertions use per-test names |
| LiveTestRegressionApiTests | 3 | 0 | Released-build effectivity and audit projection, seeded IDs and project scoping |
| ProcedureBrowsingApiTests | 6 | 0 | Paging/filter/sort/search all project-scoped; outsider refusal retained |
| TestProcedureRevisionHistoryApiTests | 2 | 0 | History/trace/coverage/search scoped by projectId and seeded IDs |
| **Tranche 2a totals** | **16** | **0** | 16 per-test factories -> 4 class fixtures (**-12**) |

## Cumulative pilot scope

- Classes: 9 (5 tranche 1 + 4 tranche 2a), meeting the 8?12-class pilot target.

- Factories: tranche 1 -27 + isolation +1; tranche 2a -12; cumulative suite factory count drops from 471 (489 tests) to 433 (508 tests) on the tranche-2a head.

## Fresh-host required additions

- `ChangeRequestRenameApiTests` uses `ProblemReportApiTests.BootstrapAndLoginAsync` (first-install bootstrap requires a user-less DB), so it cannot share a host with tests that seed users; it remains fresh-host required.

## Exhaustive class inventory (all *.cs files in AeroLink.Api.Tests)

### pilot (11)

- ApprovalConfigurationApiTests.cs
- ChangeRequestReviewRationaleApiTests.cs
- CoverageStateFilterApiTests.cs
- DownstreamAssessmentReopenApiTests.cs
- LiveTestRegressionApiTests.cs
- ProcedureBrowsingApiTests.cs
- ProjectPersonnelApiTests.cs
- SharedApiHost.cs
- SharedHostIsolationTests.cs
- TestProcedureDocumentApiTests.cs
- TestProcedureRevisionHistoryApiTests.cs

### fresh-host required (7)

- ApiTestTelemetryTests.cs
- BaselineImportApiTests.cs
- ChangeRequestRenameApiTests.cs
- ManagedDocumentApiTests.cs
- ProblemReportPagingApiTests.cs
- ProductionRoutingTests.cs
- SecurityBoundaryTests.cs

### fixture-hosted (ShowcaseApiFixture) (4)

- CodeTraceabilityApiTests.cs
- ConfigurationPublicationApiTests.cs
- DraftDocumentApiTests.cs
- ProcedureDiscussionApiTests.cs

### reuse candidate (not converted) (58)

- AdministratorChangeRequestApiTests.cs
- AuthoredSectionTests.cs
- AuthoringTracedImpactTests.cs
- BuildScopedWorkspaceApiTests.cs
- BuildTestSetApiTests.cs
- CancelReviewAuthorityTests.cs
- ChangeAuthoringInvariantApiTests.cs
- ClosedReleaseAuthoringTests.cs
- ControlledEditingCheckInApiTests.cs
- ControlledEditingProcedureAuthorityTests.cs
- ControlledProcedureApprovalBasisApiTests.cs
- ControlledProcedureDocumentApiTests.cs
- CorrectiveActionRoutingApiTests.cs
- DeferralAndRevisionListingTests.cs
- DeferredCarryForwardApiTests.cs
- ExternalIdentityAdminApiTests.cs
- GovernanceEvidenceApiTests.cs
- HistoricalPublicationFreezeApiTests.cs
- IdentifierAllocationTests.cs
- IntegrityCheckpointApiTests.cs
- LegacyProcedureManifestBootstrapApiTests.cs
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

- Factory/host/database starts: 563A telemetry (`factories` per class and whole-run `summedFactoryStartupMs`, schema v2). After this PR, each converted class reports one `SharedApiHost` factory instead of one per test, and the whole-run total includes every factory exactly once (attributed + fixtures/helpers + ambiguous theories).
- Wall clock: the API shards' summed wall and startup come from the same telemetry; a before/after comparison is made on exact-head CI runs. Single runs are variance-dominated; the broad-rollout decision requires at least ten repeated full-concurrency observations, order randomization, summed CPU/disk-I/O, and the >=15% API critical-path threshold.

## Persistent data

No persistent PostgreSQL (port 54329) is used, migrated, or reset. The pilot uses only disposable SQLite databases in the system temp directory and the repository's existing disposable test evidence roots.
