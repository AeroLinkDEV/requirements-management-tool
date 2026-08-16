# #563 phase 2 — bounded API host-reuse rollout

Date: 2026-08-16

Branch: `chatgpt/563-shared-host-tranche-2b`
Issue state: implementation tranche only; #563 remains open pending measured Windows evidence and any further source-audited conversions.

## Design retained

`SharedApiHost` is an opt-in, class-scoped xUnit fixture. It owns one `AeroLinkApiFactory`, one API host, and one disposable SQLite database for a test class. xUnit keeps the methods in one class serial, so the class is the reuse boundary.

Every converted test still receives a fresh `HttpClient`, and therefore a fresh cookie/session container. Converted classes must also satisfy all of the following:

- default `AeroLinkApiFactory` options only;
- unique per-test data for globally constrained values such as Program codes, user names, and email addresses;
- assertions scoped to the test's own project, release, package, or seeded identifiers;
- no empty-database, first-install, fixed-bootstrap, whole-database-count, custom-service, custom-filesystem, or startup-configuration assumptions;
- no persistent PostgreSQL or persistent evidence-root use.

Tests that need a fresh host/database remain outside this fixture. No global xUnit parallelism setting changed.

## Previously merged pilot

| Increment | Classes | Methods/cases sharing a host | Fresh-host exceptions | Theoretical per-method factory reduction |
|---|---:|---:|---:|---:|
| PR #584, tranche 1 | 5 product classes | 32 | 1 first-install method | 27 |
| PR #585, tranche 2a | 4 product classes | 16 | 0 | 12 |
| SharedHostIsolationTests | 1 isolation class | 3 | 0 | 2 |
| **Merged total before this tranche** | **10 classes** | **52** | **1** | **42** |

The generated v3 host-classification artifact is authoritative. Historical hand-maintained candidate counts in earlier versions of this document are intentionally not repeated.

## Tranche 2b — this branch

This tranche converts six smaller legacy classes whose only blockers were fixed globally constrained seed identities and per-test factory construction.

| Class | Methods/cases | Isolation correction | Assertion scope | Factory change |
|---|---:|---|---|---:|
| `AuthoredSectionTests` | 3 | Unique Program code and author/approver accounts per test | Seeded project, release, draft, and section | 3 → 1 |
| `CorrectiveActionRoutingApiTests` | 3 | Unique Program code, engineer, and outsider accounts per test | Seeded project, reports, procedures, and IDs | 3 → 1 |
| `DeferralAndRevisionListingTests` | 3 | Unique Program code and author/approver accounts per test | Seeded change request and project-filtered history | 3 → 1 |
| `PreReleaseEvidenceVisibilityTests` | 3 | Unique Program code and engineer/lead accounts per test | Seeded release, impact, procedure, and build set | 3 → 1 |
| `RequirementFilterExactnessApiTests` | 4 | Unique Program code and reader account per test | Seeded project workspace | 4 → 1 |
| `RestartReviewApiTests` | 3 | Unique Program code and author/approver accounts per test | Seeded change request and review-cycle ID | 3 → 1 |
| **Tranche 2b total** | **19** |  |  | **19 → 6 (-13)** |

No test was removed, skipped, merged, or weakened. The HTTP, authorization, EF translation, audit-history, and error-mapping assertions remain hosted.

## Generated inventory after tranche 2b

| Classification | Classes | Test methods | Known cases |
|---|---:|---:|---:|
| converted | 16 | 71 | 71 |
| reusable-host | 24 | 167 | 192 |
| fresh-host | 40 | 208 | 233 |
| migration-candidate | 1 | 1 | 1 |
| **Total** | **81** | **447** | **497** |

The converted classes now replace 71 per-method factories with 16 class fixtures, a theoretical reduction of 55 factory/host/database starts for those methods. This tranche contributes 13 of those reductions.

The remaining static reuse headroom is 24 classes, 167 methods, and 192 known cases. Its method-level theoretical maximum is 143 additional factory reductions; that number is planning evidence, not a claim that every remaining class is safe or worthwhile to convert.

## Validation completed in the implementation workspace

- regenerated `api-test-intent.json` from the current C# source;
- regenerated `api-host-classification.json` from the current C# source and reviewed overrides;
- regenerated `route-coverage.json` from the current source;
- inventory and route-contract tests: **31 passed, 0 failed**;
- generated-artifact byte-stability: passed;
- exact method/case totals remained **447 / 497**;
- no test discovery reduction occurred.

The local execution environment did not contain the .NET SDK or PowerShell, so C# compilation, the API suite, and the Windows-only measurement-contract tests are GitHub Actions responsibilities for this branch. This limitation is recorded rather than represented as a passing result.

## Performance evidence still required

This tranche does not claim the #563 performance threshold is met. Before broad rollout or issue closure:

1. GitHub Actions must compile and run the complete applicable API suite with exact totals.
2. The paired Windows harness must compare an exact baseline tree with the exact treatment tree using ten valid seeds and identical discovered test identities.
3. The treatment must improve the median worst API shard by at least 15%, with at least 15% median paired-seed improvement, without cleanup, authentication-isolation, or test-count failures.
4. Negative or variance-dominated results must remain recorded; they are not grounds to weaken tests or hide the experiment.

## Persistent-data confirmation

Only disposable SQLite databases and disposable test evidence roots are used. Persistent PostgreSQL on port `54329` and the persistent AeroLink evidence store are untouched.
