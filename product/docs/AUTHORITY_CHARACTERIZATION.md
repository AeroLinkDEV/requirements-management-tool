# Authority characterization — pre-#816 migration contract

Status: **characterization snapshot of `main` at `ae395e7b38c4c507ac6cfa8f7eca75737e8c670b` (2026-08-27).**
Parent: [#816](https://github.com/seanmccarthyns/requirements-management-tool/issues/816) (Slices 1–4).
This document is the durable authority matrix that Slices 2–4 consume. It describes **what is**, before any
behavior changes; the classification column is the deliberate destination decided by the owner in #816.

## 1. The current role vocabulary (`ProgramRole`)

`AeroLink.Domain/Identity/IdentityRecords.cs` — one enum currently carries five different ideas:

| Value | Current meaning | #816 classification |
|---|---|---|
| `Engineer` | Generic engineering authority (demanded at ~30 authoring/editing sites) | Base role capability (legacy generic value, stays readable) |
| `Reviewer` | Generic review standing authority | Workflow-stage meaning / legacy compatibility — never a new grant |
| `Approver` | Generic approval standing authority (9 direct gate demands) | Workflow-stage meaning / legacy compatibility — never a new grant |
| `Administrator` | Program-scoped administrator (distinct from global `SystemAdministratorUserName`) | Administrator |
| `TestEngineer` | Undivided verification work | Legacy compatibility (discipline-split titles satisfy it) |
| `TestLead` | Verification distribution authority | Base role capability (legacy generic value) |
| `ProgramManager` | Singular position + base role conflated | **Project Leadership position** + base eligibility role |
| `ConfigurationManager` | Singular position + base role conflated | **Project Leadership position** + base eligibility role |
| `SystemEngineer` | Discipline job | Base role |
| `SoftwareEngineer` | Discipline job | Base role |
| `SystemEngineeringLead` | Singular discipline lead | **Project Leadership position** (eligibility: System Engineer) |
| `SoftwareEngineeringLead` | Singular discipline lead | **Project Leadership position** (eligibility: Software Engineer) |
| `ProjectEngineeringLead` | Singular "lead of the engineering effort" | **Retired.** Deliberate authority migration; never newly granted |
| `EngineeringManager` | Singular position + base role conflated | **Project Leadership position** + base eligibility role |
| `SoftwareQualityAnalyst` | Independent assurance (SQA) | Base role (assurance, not leadership) |
| `Airworthiness` | Independent assurance | Base role (assurance, not leadership) |
| `SystemTestEngineer` | Discipline verification job | Base role |
| `SoftwareTestEngineer` | Discipline verification job | Base role |
| `SystemTestLead` | Singular discipline lead | **Project Leadership position** (eligibility: System Test Engineer) |
| `SoftwareTestLead` | Singular discipline lead | **Project Leadership position** (eligibility: Software Test Engineer) |
| `ProjectEngineer` | Singular position + engineering authority | **Project Leadership position** (eligibility: Project Engineer) + base role |

## 2. The current authority mechanisms (four separate resolvers)

Any Slice 2 change must account for **all four**; two of them do NOT go through `IdentityService`:

### 2.1 `IdentityService.HasRoleAsync` — both paths (`IdentityService.cs` L55/L85)

- **Session path** `(AuthenticatedUser, programId, role)`: administrator bypass via `user.IsAdministrator`
  (the global `SystemAdministratorUserName` flag baked into the session); memberships come from the
  **session-baked** `user.Programs` (re-read per request by `ResolveAsync`→`MapAsync`, so ended memberships
  never reach authority, but a session is only as fresh as its last resolve).
- **UserId path** `(Guid userId, programId, role)`: live DB check; account must be `Active`; administrator
  bypass via `UserName == SystemAdministratorUserName` (NOTE: a Program-scoped `Administrator` membership is
  NOT a bypass here — it only satisfies an explicit `ProgramRole.Administrator` demand).
- Both paths share the resolution order: **`ProgramRoleAuthority.Satisfying(role)` memberships → standing
  backup (requires current membership) → active time-bounded delegation (EXACT role match, not satisfying
  set)**.

Characterization subtleties pinned by tests (see §5):

- A delegation of `ProjectEngineeringLead` does **not** satisfy an `Engineer` demand — delegations are
  exact-role, unlike memberships which use the satisfying set.
- A backup without any current membership in the program fails closed.
- The two paths disagree for a Program-scoped `Administrator` membership acting as a global bypass: the
  session path bypasses via `IsAdministrator` (global flag only), the userId path via
  `SystemAdministratorUserName` (global account only) — both are global, neither is the program role.

### 2.2 `ProgramRoleAuthority.Satisfying` (`IdentityRecords.cs`)

The implication matrix (fully pinned by `ProgramRoleAuthorityTests`):

| Demanded | Satisfied by |
|---|---|
| `Engineer` | Engineer + SystemEngineer + SoftwareEngineer + SystemEngineeringLead + SoftwareEngineeringLead + **ProjectEngineeringLead** + EngineeringManager + **ProjectEngineer** |
| `Reviewer` / `Approver` | that role + SystemEngineeringLead + SoftwareEngineeringLead + SystemTestLead + SoftwareTestLead + **ProjectEngineeringLead** |
| `TestEngineer` | TestEngineer + SystemTestEngineer + SoftwareTestEngineer |
| `TestLead` | TestLead + SystemTestLead + SoftwareTestLead |
| everything else | itself only (ConfigurationManager, ProgramManager, Administrator, SQA, Airworthiness, precise roles) |

### 2.3 `ManagedDocumentReviewAuthority` (`AeroLink.Api/ManagedDocumentReviewAuthority.cs`)

A **separate resolver** for managed-document review/release that does NOT use `ProgramRoleAuthority`:

- `Technical` accepted set: `Reviewer, Approver, SystemEngineeringLead, SoftwareEngineeringLead, ProjectEngineeringLead, EngineeringManager`
- `Final` accepted set: `SoftwareQualityAnalyst, ConfigurationManager, Approver, ProgramManager` — note
  **`ProjectEngineeringLead` is NOT in the Final set** and `ProjectEngineer` is in neither set today.
- Resolution order: active account → global-admin substitution → direct membership (first accepted role in
  set order) → program `Administrator` membership substitution → active delegation (exact role) → admin
  delegation → standing backup **only while the account has ≥1 current membership** (same fail-closed rule
  as IdentityService, re-implemented locally).
- Provenance is recorded on the signature: `DirectMembership` / `AdministratorSubstitution` /
  `ActiveDelegation` / `StandingBackup` + source id.

### 2.4 `AssuranceAuthorityPolicy` (`AeroLink.Domain/Assurance/AssuranceAuthorityPolicy.cs`)

Data-driven, versioned, per deviation class: ProjectPolicy = ProgramManager or SQA; Verification /
Independence / Evidence / ReleaseGate = SQA only; Airworthiness = Airworthiness. Delegation per class;
self-approval always false. Unaffected structurally by #816 (roles keep their names) but classified:
ProgramManager demand here becomes a **leadership-position** demand; SQA/Airworthiness stay base roles.

## 3. Consumer classification (the matrix)

Direct `HasRoleAsync(..., ProgramRole.X, ...)` demands on `main` (17 inline sites):

> **Erratum (2026-08-28):** the "17 inline sites" count, and the exhaustiveness this section implies, are
> wrong. The tree holds 138 literal-role demands across 34 files, and four live consumers are missing from
> the tables below entirely. The original text is retained as the historical record; the corrected inventory
> is in the erratum at the end of this document. Read what follows as a classification of demand *kinds*,
> not as a census.

| Demanded role | Sites | Classification |
|---|---|---|
| `Approver` | ChangeRequestEndpoints ×4 (stage authorize ×2, closure approvals ×2, decision ×2 more via Approver), DownstreamAssessmentEndpoints ×1 | Workflow-stage meaning (stage gates) + generic standing membership demand → Slice 4 cutover |
| `ConfigurationManager` | ×2 (baseline/freeze + config gates) | Project Leadership position demand |
| `ProgramManager` | ×2 | Project Leadership position demand |
| `SoftwareQualityAnalyst` | ×2 (Problem Report closure paths) | Base role |
| `Engineer` | ×1 (ManagedDocumentAssignmentPolicy) | Base role |
| `TestEngineer` | ×1 | Base role |
| dynamic `workflow.Stages[i].RequiredRole` | ChangeRequestEndpoints ×2 | Workflow-stage meaning (Slice 4: becomes `ProjectAuthorityRequirement`) |
| dynamic `role` iteration | ProblemReportEndpoints (owner authority loop), ApiSupport `RequireRoles` loop, AdministrationEndpoints (delegation grant check) | Mixed — classified per loop site below |

Non-`HasRoleAsync` consumers that must move with the model:

| Consumer | Uses | Classification |
|---|---|---|
| `PersonnelEndpoints` roster management (grant/end membership, backups) | fixed set `{ProgramManager, ProjectEngineeringLead, ProjectEngineer, Administrator}` (program-scoped) | Project Leadership capability (roster stewardship) + Administrator; **`ProjectEngineeringLead` membership must move to Project Engineer leadership** |
| `PersonnelEndpoints` positions read | `SingularProgramRoles.All` (9 values incl. ProjectEngineeringLead) | Slice 2 replaces with the 8-position leadership projection |
| `ProgramMembership` singularity enforcement | `SingularProgramRoles.IsSingular` at grant | Slice 2: singular per leadership position; base roles become multi-member |
| `WorkflowEndpoints` candidate resolution | exact-role for direct membership, `Satisfying` otherwise; delegation as exact-role grant | Slice 4 cutover via central resolver |
| `ManagedDocumentReviewAuthority` | own accepted sets (above) | Slice 2: Technical/Final sets re-expressed over leadership positions |
| `ProblemReportOwnerAuthority` | eligible = Engineer∪TestEngineer∪TestLead satisfiers; recovery = {ProjectEngineeringLead, EngineeringManager, ProgramManager} | Recovery becomes Project Engineer/Engineering Manager/Program Manager **leadership** |
| `AssuranceAuthorityPolicy` | ProgramManager/SQA/Airworthiness per class | ProgramManager demand → leadership position |
| Client `PersonnelCenter.tsx` | groups Project positions / Engineering / Verification / Control authority / Independent assurance | Slice 3 redesign (eight Project Leadership cards) |
| Client `presentation.ts` `programRoleLabels`/`grantableProgramRoles` | grantable list includes Reviewer/Approver/leads/PEL | Slice 3: new UX vocabulary; Reviewer/Approver removed from grants |
| Client `ApprovalConfigurationCenter.tsx` | requiredRole picker incl. Reviewer/Approver, defaults new rows to `Reviewer` | Slice 4 cutover |
| Client `People.tsx`/`PeopleRegistry.ts` | display names only (no authority) | #776-owned; **not touched by #816** |

## 4. Seed / persisted-state compatibility matrix

`IdentitySeeder.People` (the demo roster; the only `ProjectEngineeringLead` in seeds):

| Account | Roles today |
|---|---|
| admin | Administrator |
| engineer.demo | Engineer |
| systems.author | Engineer, SystemEngineer |
| software.author | Engineer, SoftwareEngineer |
| systems.reviewer | Reviewer, Approver |
| assurance.reviewer | Reviewer, Approver |
| lead.reviewer | Reviewer, Approver |
| software.lead | Reviewer, Approver, SoftwareEngineeringLead |
| systems.lead | Reviewer, Approver, SystemEngineeringLead |
| engineering.manager | ProgramManager, Approver, EngineeringManager |
| manager.reviewer | ProgramManager, Approver |
| program.manager | ProgramManager, Approver |
| release.manager | ConfigurationManager, ProgramManager |
| cm.fms | ConfigurationManager |
| test.author / test.engineer | TestEngineer |
| airworthiness.lead | Airworthiness |
| quality.analyst | SoftwareQualityAnalyst |
| **project.lead** | **ProjectEngineeringLead** (only seed) |

Characterization findings that constrain migration:

1. **No `ProjectEngineer` membership is seeded anywhere.** The showcase has a ProjectEngineeringLead
   (`project.lead`) and no Project Engineer holder — the unambiguous case is one-directional.
2. Discipline leads carry generic Reviewer/Approver memberships *in addition to* the lead role (redundant
   under `Satisfying`); generic-reviewer accounts (`systems.reviewer`, `lead.reviewer`,
   `assurance.reviewer`) hold **only** Reviewer/Approver and sign workflow stages today via exact
   membership. Slice 4 must not silently map them to a discipline.
3. `engineering.manager` holds ProgramManager **and** EngineeringManager; `release.manager` holds
   ConfigurationManager **and** ProgramManager — dual-leadership persons already exist in demo data, which
   the per-position singularity rule must accommodate.
4. `ProgramRoleBackup`/`RoleDelegation` rows are created by live endpoints and tests, keyed by
   `ProgramRole` — the standing-backup concept migrates keyed by leadership position.

### Migration conflict strategy (drafted; Slice 2 implements)

- **Unambiguous single holder** of a future leadership position (from the current singular membership):
  migrate to a primary leadership assignment; ensure the holder satisfies the required base-role
  eligibility, deriving the missing base role only where the lead role itself is the unambiguous evidence
  (e.g. SystemEngineeringLead ⇒ System Engineer) — documented and tested, never a heuristic spread to
  arbitrary data.
- **Conflicting active Project Engineer and Project Engineering Lead held by different people**: the
  migration **fails closed and reports** the ambiguity for explicit owner resolution. No winner is chosen.
  (Showcase note: the seeded data has only a PEL and no ProjectEngineer, so the showcase upgrade is the
  deliberate one-directional case — PEL retires, Project Engineer leadership is created for the same
  person, and the base Project Engineer role is granted as part of the named upgrade step.)
- Generic Reviewer/Approver memberships and active workflows: preserved readable; new grants disabled;
  active workflow definitions keep working under an explicitly identified legacy-authority requirement
  (Slice 4).

## 5. Database integrity findings (characterized)

- `program_memberships`: unique `(UserId, ProgramId, Role)` — a person can hold each role once per
  program (re-grant after ending is a new row; SQLite tests and PostgreSQL must keep this consistent).
- `project_role_backups`: unique `(ProgramId, Role)` with a **partial-index filter** `"RemovedAt" IS NULL`
  — exactly one ACTIVE backup per program+role; removed designations are retained as attributed history
  and free the slot for a new backup. The backup endpoint pre-checks (409 when an active backup exists)
  and the delete endpoint soft-removes; the constraint is the backstop, not the enforcement.
- These constraints carry over into the Slice 2 leadership-backup design; the partial-index pattern is
  the proven precedent for one-active-per-position enforcement at the database layer.

## 6. Characterization tests added (Slice 1)

- `AeroLink.Domain.Tests/ProgramRoleModelCharacterizationTests` — exhaustive singular/non-singular
  classification of **all 19** `ProgramRole` values as of today (including `ProjectEngineeringLead`,
  whose singularity was previously unpinned); PEL does not satisfy TestLead/TestEngineer demands;
  ProjectEngineer does not satisfy Reviewer/Approver.
- `AeroLink.Infrastructure.Tests/IdentityServiceAuthorityCharacterizationTests` — the two `HasRoleAsync`
  paths agree for: satisfying memberships, standing backup (and its fail-closed membership requirement),
  exact-role delegations (a PEL delegation does not satisfy an Engineer demand), ended memberships, and
  disabled accounts on the userId path.
- `AeroLink.Api.Tests/ManagedDocumentReviewAuthorityCharacterizationTests` — Technical/Final accepted sets
  (PEL in Technical only), precedence direct → program-admin → delegation → backup-with-membership, backup
  without membership fails closed.

These tests pin current outcomes so Slice 2's model change surfaces as a deliberate test delta rather than
a silent behavior shift.

## 7. #812 (Team Work) reconciliation

Recorded on #812: Team Work must consume, after Slice 4 — (a) base project roles for discipline affinity;
(b) Project Leadership assignments (not `SingularProgramRoles` membership scans) for "who leads";
(c) the effective-authority resolver's provenance for "acting as" presentation (primary vs backup vs
delegation); (d) `ReviewStageKind` for Review/Approval wording; (e) never generic Reviewer/Approver as a
person label. #812's current text referencing `ProgramRole` discipline leads and Reviewer/Approver holder
presentation is stale after Slice 4 and should be implemented against the new resolver.

---

# Erratum and post-Slice-2 addendum — 2026-08-28

**The sections above are the Slice 1 snapshot and are left as written.** They record what the
characterization work found on `main` at `ae395e7b` and remain the historical account, including the parts
this erratum corrects. Nothing below rewrites them; it records where they were wrong and what is true now,
at `6211b15f`.

## E.1 The consumer inventory was not exhaustive

Section 3 opens "Direct `HasRoleAsync(..., ProgramRole.X, ...)` demands on `main` (17 inline sites)". That
count and the exhaustiveness it implies are both wrong.

A fresh sweep of current `main` under `product/src`, counted three ways so the denominators are not mixed:

| Measure | Count |
|---|---|
| Files calling `HasRoleAsync` / `HasProjectRoleAsync` at all | **33** |
| Files containing at least one *literal-role* demand | **22** |
| Call sites carrying a literal role | **138** |
| Role literals on those call sites | **245** |

The largest consumers, by call sites (literal or otherwise):

| File | Call sites |
|---|---|
| `VerificationImpactEndpoints` | 22 |
| `BaselineEndpoints` | 16 |
| `ManagedDocumentEndpoints` | 14 |
| `ProductLineEndpoints` | 12 |
| `ReleaseCampaignEndpoints` | 11 |
| `IntegrationEndpoints` | 11 |
| `DownstreamAssessmentEndpoints` | 11 |
| `VerificationEndpoints` | 10 |

By demanded role, across all sites: `ConfigurationManager` 91, `ProgramManager` 48, `Engineer` 41,
`Administrator` 14, `TestEngineer` 13, `Approver` 12, `TestLead` 11, `ProjectEngineeringLead` 10,
`SoftwareQualityAnalyst` 5.

**Four live consumers named in the #822 review appear nowhere in the original document** —
`ReleaseCampaignEndpoints`, `VerificationImpactEndpoints`, `QualityIntelligenceEndpoints` and
`ControlledEditingCheckInEngine` each occur zero times in sections 1–7. `VerificationImpactEndpoints` is the
single largest consumer in the repository by call sites; `ReleaseCampaignEndpoints` is joint fifth.

The original count appears to have been of *distinct inline demand sites the author enumerated by hand*
rather than of demands in the tree. Read section 3's table as an illustrative classification of the demand
*kinds*, not as a census.

## E.2 Corrected inventory: the forms authority actually takes

A sweep for one textual pattern will always under-count, because authority is expressed in at least seven
distinct ways. The current set:

| Form | Where | Live-authority classification |
|---|---|---|
| `IdentityService.HasRoleAsync` (two overloads) | Direct compatibility consumers | Legacy-compatible membership, leadership, backup, and delegation answer; raw governed/retired position membership is excluded |
| `IdentityHttpExtensions.HasProjectRoleAsync` | Untyped API endpoint gates, including baseline/build, release-campaign, integration, and review-cancel control | Every named role is routed through `ProjectAuthorityResolver.LegacyRoleDemand`; governed-role membership is eligibility only, while primary, standing backup, and exact delegation answer the position demand |
| `ProgramRoleAuthority.Satisfying` | `IdentityService`, `IdentityRecords`, `WorkflowEndpoints`, `ReviewWorkflow`, `AssuranceAuthorityPolicy`, `ManagedDocumentAssignmentPolicy`, `ApprovalConfigurationEndpoints` | Role implication, unchanged by #816 |
| Raw `ProgramMemberships` role decisions | **Corrected:** `ManagedDocumentReviewAuthority.ResolveAsync`, the `WorkflowEndpoints` candidate, stage, and additional-signer projections, `ProblemReportEndpoints.HasProblemReportOwnerRecoveryAuthorityAsync`, `ApprovalConfigurationEndpoints.Resolve`, `ManagedDocumentAssignmentPolicy.HasExplicitAuthorityAsync`, `ProjectAssurancePolicyService.ApproverFactsAsync`, the roster gates | Mixed — listed per call site because the same file does both |
| `ProjectRoleBackups` (legacy) | `AdministrationEndpoints`, `ApprovalConfigurationEndpoints`, `ManagedDocumentAssignmentPolicy`, `ManagedDocumentReviewAuthority`, `PersonnelEndpoints`, `WorkflowEndpoints`, `IdentityService.IsStandingBackupAsync` | Historical readability for base roles; **no longer live authority for position roles at any of these sites**, and new position-keyed rows are refused at creation |
| `ProjectLeadershipAssignments` / `ProjectLeadershipBackups` | `IdentityService`, `ProjectLeadershipService`, `ProjectAuthorityResolver` | **The** live leadership authority |
| `RoleDelegations` | 9 files | Exact-role, time-bounded; unchanged |
| `ProblemReportOwnerAuthority` ownership / recovery | `WorkspaceEndpoints`, `ProblemReportEndpoints` | **Corrected** — owner eligibility is a base-job question with retired position memberships excluded; recovery is leadership positions, not membership roles |
| `AssuranceAuthorityPolicy` | `ProjectAssurancePolicyService` | **Corrected** — `ProgramManager` is a position; SQA/Airworthiness remain base roles |

## E.3 Base-role versus leadership-position demands

The snapshot classified demands as base-role or leadership-position in prose, but the code had no way to
express the difference: `HasRoleAsync(user, program, ProgramRole.ProgramManager)` meant both "does this
person do the Program Manager's job" and "is this person *the* Program Manager", and answered yes to
either. `ProjectAuthorityRequirement` now names the question and `ProjectAuthorityResolver` answers it,
reporting provenance (`DirectBaseRole`, `LeadershipPrimary`, `LeadershipBackup`, `Delegation`,
`AdministratorSubstitution`, `LegacyCompatibility`).

`HasRoleAsync` remains as a compatibility API. Base eligibility memberships still answer base-work questions
(including Project Engineer and Engineering Manager satisfying ordinary `Engineer` gates), while retired
position memberships answer nothing; current Project Leadership assignments/backups answer the corresponding
legacy demands. The long-standing `HasProjectRoleAsync` endpoint helper now routes every role-shaped demand
through `ProjectAuthorityResolver`: ordinary roles retain their membership implications, while Configuration
Manager, Program Manager, and retired lead labels require the corresponding leadership primary, standing
backup, or exact delegation. That central cutover closes the baseline/build, release-campaign, integration,
and review-cancel bypasses without re-implementing the split in each endpoint.

Problem Report SCCB opening and controlled-editing check-in now ask the same typed legacy demands as their
capability/checkout counterparts. A base-only Project Engineer cannot open SCCB, and ending a Configuration
Manager or Program Manager leadership assignment immediately prevents a holder from committing an already
open controlled-editing lease. Problem Report owner eligibility remains a base-job question, but filters the
five retired position-role memberships before both directory projection and reassignment, so legacy rows are
neither offered nor accepted as responsible engineers.

That cutover includes every managed-document controller and recovery gate. Ordinary authoring and assurance
demands remain base-role questions, while Configuration Manager, Program Manager, and the retired Project
Engineering Lead demand resolve through Project Leadership (including its standing backup and exact legacy
delegation). The global administrator override remains available for these controller/recovery operations;
lifecycle-relationship editing deliberately retains its stricter explicit-Project-authority policy. A base-only
Configuration Manager or Program Manager cannot create or start a document on another author's behalf, revise
formal scope, transfer stewardship/responsibility, force-unlock or withdraw work, or operate integrity/storage
recovery merely because they are eligible for the corresponding leadership position.

## E.4 Singularity moved

Section 3 recorded `SingularProgramRoles.IsSingular` enforcing singularity at membership grant for nine
values. Four of those — `ProjectEngineer`, `ProgramManager`, `EngineeringManager`, `ConfigurationManager` —
are base eligibility and are now **multi-member**; singularity for the position lives on
`ProjectLeadershipAssignment`. The other five stay singular so legacy data cannot grow a second live holder.

This is what makes the owner's atomic Replace-Leader possible: the prospective replacement can be granted
the eligibility while the incumbent still holds the post.

## E.5 Workflow-stage and legacy semantics

Unchanged by this correction, and still Slice 4's: `ReviewStageKind {Review, Approval}` carries the meaning
of a signature, and `Reviewer`/`Approver` remain grantable roles until Slice 4 retires them. Stage
`RequiredRole` is still a `ProgramRole`; candidate, configured-stage, fallback, and additional-signer
selection now resolve through the shared resolver so they cannot disagree with the signing gate or freeze an
unrelated base-role label as signature provenance, but the stored demand shape has not changed.

## E.6 Delegation and administrator substitution

Unchanged. Delegations remain exact-role and time-bounded — a delegation of one role is not a delegation of
everything that role satisfies. The global `admin` account still satisfies every check inside
`IdentityService`; program-scoped `Administrator` membership still substitutes in
`ManagedDocumentReviewAuthority` and the candidate picker; and an administrator still gains **no** assurance
authority merely by being an administrator.

## E.7 Compatibility paths remaining after this correction

- Legacy `ProjectRoleBackups` still answer **base-role** demands. They no longer answer position demands at
  any consumer, and `POST /personnel/backups` refuses to create a new one for a position — a position's
  backup is a `ProjectLeadershipBackup`.
- The five retired position roles can no longer be **granted** as memberships, by either the Personnel or the
  Administration route. Existing rows stay readable; a new grant would recreate the exact state the v2
  reconciliation refuses, so on a database that has not yet run v2 it would stop the next startup.
- A membership carrying a **retired** position role no longer answers that position's demands anywhere. A
  base-eligibility membership remains an ordinary job role, but does not answer a position-sensitive demand.
  `ProgramRoleAuthority.Satisfying` still folds the retired roles into Reviewer/Approver/Engineer so a stored
  workflow stage naming one keeps resolving, but the *membership* side of every gate filters them out and the
  leadership pass answers instead. Removing them from `Satisfying` outright is Slice 4's, once stored stages
  carry a `ProjectAuthorityRequirement` rather than a `ProgramRole`.
- Legacy position **memberships** are retired by the v2 reconciliation
  (`ProjectLeadershipReconciliationAuthority`) once the equivalent assignment exists. Until a database runs
  v2 they remain live, which is why v2 is a separate marker rather than an edit to v1.
- `ProgramRole.ProjectEngineeringLead` remains in the enum and remains readable in history. It is retired as
  an active concept, is never newly granted, and its live authority is the Project Engineer position's.
- `SingularProgramRoles.All` still returns the nine legacy position values for the existing Personnel
  projection. The eight-card Project Leadership presentation that replaces it belongs to Slice 3 (#819).
