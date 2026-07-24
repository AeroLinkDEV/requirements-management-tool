# Massive Enterprise Update Report

> **Historical snapshot — 2026-07-13.** This records one delivery increment and the evidence that
> closed it. Test counts, backup hashes, migration numbers and "next increment" statements were true on
> that date and are not maintained. For current state read [PROJECT_STATE.md](PROJECT_STATE.md); for
> current delivery status read
> [AEROLINK_3_IMPLEMENTATION_STATUS.md](AEROLINK_3_IMPLEMENTATION_STATUS.md).

## Executive summary

The 2026-07-13 update advances AeroLink from a broad working prototype to a safer enterprise control foundation. The implemented vertical slice concentrates on the highest-risk gaps: durable artifact navigation, server-enforced exclusive SCR/SWCR editing and recovery, Program-scope security, isolated tests, safe schema evolution, and provable backup/restore operations. Existing PostgreSQL data and FMS history were preserved; FMS 1.5 remains released and immutable, while 1.6 remains derived from 1.5 and intentionally in work.

AeroLink remains local/on-premises, uses the established React/TypeScript, ASP.NET Core, Entity Framework, and PostgreSQL stack, adds no AI capability, and makes no ARP4754A, DO-178C, certification, or tool-qualification claim.

## 28-scenario acceptance closure

All 28 requested acceptance scenarios are green. The isolated browser/API suite exercises the workflows without mutating the live PostgreSQL database, while operational scenarios use the production scripts and an isolated PostgreSQL restore target.

| # | Acceptance result | Executable evidence |
| ---: | --- | --- |
| 1 | Green — new System SCR receives a server number and authenticated author | `acceptance-closure.spec.ts` |
| 2 | Green — introduced System requirement receives a server number | `acceptance-closure.spec.ts` |
| 3 | Green — suffix lookup resolves an existing System requirement and creates its exact next proposal | `acceptance-closure.spec.ts`, `universal-routing-and-locking.spec.ts` |
| 4 | Green — one SWCR carries coordinated HLR and LLR changes | `acceptance-closure.spec.ts` |
| 5 | Green — derived software requirement requires and retains explicit rationale | `acceptance-closure.spec.ts` |
| 6 | Green — checkout, read-only contention, autosave, check-in, and immutable history | `controlled-scr-workflow.spec.ts`, `universal-routing-and-locking.spec.ts` |
| 7 | Green — sequential review notifies only the active reviewer | `acceptance-closure.spec.ts` |
| 8 | Green — first approval activates and notifies the next reviewer | `acceptance-closure.spec.ts` |
| 9 | Green — parallel review activates and notifies every required reviewer | `acceptance-closure.spec.ts`, domain review tests |
| 10 | Green — request changes returns the same CR revision to Draft | `acceptance-closure.spec.ts` |
| 11 | Green — approved request creates a separate next Draft revision without rewriting approval | `acceptance-closure.spec.ts`; user-facing action in `ScrWorkspace.tsx` |
| 12 | Green — notification opens the exact review artifact | `acceptance-closure.spec.ts`; explicit open action in `ControlledAuthoringCenter.tsx` |
| 13 | Green — exact in-review CR PDF is generated | `acceptance-closure.spec.ts` |
| 14 | Green — final identifier digits are searchable | `universal-routing-and-locking.spec.ts`, `acceptance-closure.spec.ts` |
| 15 | Green — System → HLR → LLR → procedure → result → evidence reference is traversable | `acceptance-closure.spec.ts`; expanded `LifecycleExplorer.tsx` |
| 16 | Green — traceability PDF and DOCX both generate and have valid file signatures | `acceptance-closure.spec.ts` |
| 17 | Green — professional SCR/SWCR, SYSRD/SWRD, and test publications include document control | `acceptance-closure.spec.ts` |
| 18 | Green — CR publication separates new, modified old/new, and retired requirements | `acceptance-closure.spec.ts` |
| 19 | Green — requirement publications contain the upward parent-trace annex | `acceptance-closure.spec.ts` |
| 20 | Green — failed execution and passing retest remain linked and immutable | `acceptance-closure.spec.ts`, `controlled-scr-workflow.spec.ts` |
| 21 | Green — FMS 1.5 remains released and immutable | `acceptance-closure.spec.ts`, `fms-showcase-program.spec.ts` |
| 22 | Green — FMS 1.6 remains derived from 1.5 and explicitly in work | `acceptance-closure.spec.ts`, `fms-showcase-program.spec.ts` |
| 23 | Green — successor planning is explicit; 1.7 is blocked while 1.6 is in work and nothing auto-releases | `acceptance-closure.spec.ts`, release domain tests |
| 24 | Green — search navigates CR/SWCR, requirement, baseline, document, procedure, execution, build, evidence reference, and release history | `acceptance-closure.spec.ts`, `history-and-build-provenance.spec.ts` |
| 25 | Green — complete backup archive and manifest verify | `BACKUP_AEROLINK.bat`, `VERIFY_AEROLINK_BACKUP.bat` |
| 26 | Green — backup restores into isolated PostgreSQL and migrates | `RESTORE_AEROLINK.bat` isolated validation |
| 27 | Green — `START_AEROLINK.bat` restarts the stack and real sign-in succeeds | startup and diagnostics validation |
| 28 | Green — final website and API health checks succeed | `AEROLINK_DIAGNOSTICS.bat`, direct HTTP checks |

## Implemented capabilities

- Durable URL routing for the main Program/Project/release portals, SCR/SWCR records, requirements, and supported controlled artifacts; refresh and browser back/forward restore context.
- Persistent left navigation with collapsible discipline groups, breadcrumbs, active context, copy-link actions, and native links suitable for new tabs.
- `Ctrl+K` command palette with page navigation, debounced server search, identifier-fragment matching, and permission-aware deep links.
- Authoritative not-found and forbidden behavior instead of silent fallback.
- SCR/SWCR exclusive checkout with a unique server lock, fifteen-minute renewable lease, heartbeat, holder/activity/expiry visibility, and read-only access for other users.
- Server-backed autosave with status, content hashing, sequenced recoverable snapshots, explicit discard, save/check-in, optimistic version checks, and forced unlock with reason and audit evidence.
- Review submission refuses incompatible active editing sessions.
- Bounded Program-aware search across change requests, requirements, baselines, builds, procedures, test executions, evidence metadata, controlled documents, release campaigns, and release records.
- User-facing next-revision creation for approved SCR/SWCR records; the approved record and signature history remain immutable while a separate Draft revision is created.
- Review notifications include an explicit open-exact-artifact action, and traceability now traverses requirement parents/children through test procedures, executions, retests, and evidence references.
- Test procedures and committed interchange change requests receive server-authoritative identifiers; clients cannot choose final controlled numbers.
- Provider-compatible role delegation evaluation and a registered authorization handler ensure denied endpoint operations return HTTP 403 rather than server errors.
- Dedicated Playwright API/database topology, eliminating browser-test mutation of the live showcase database.
- Complete archive verification, safe isolated restore, attended production restore protection, controlled stop, and local diagnostics.

## Important product decisions

DEC-043 makes durable URLs the navigation contract. DEC-044 defines exclusive editing as a renewable server lease with immutable recovery snapshots. DEC-045 requires backup and migration proof against an isolated restore before attended recovery. These decisions extend, rather than replace, the existing server-authority, immutable-history, review-snapshot, and explicit-release rules.

## Database migration

Migration `20260713192614_AddExclusiveEditingAndDraftRecovery` adds lease expiry, exclusive lock key, closure attribution, and closure reason to `artifact_edit_sessions`; creates `artifact_draft_snapshots`; adds recovery and uniqueness indexes; and backfills existing session expiry from prior activity. It is additive and preserves existing records. The migration applied successfully to the live database and independently to a restored pre-migration PostgreSQL copy.

## Security and integrity improvements

- Authentication is required for the showcase seed mutation; it is administrator-only.
- Login uses fixed-window rate limiting.
- Program membership is enforced centrally for Project-scoped queries and supported resource identifiers, supplementing endpoint rules.
- Search is bounded and permission scoped; direct-object failures return a stable forbidden response.
- Checkout uniqueness is database enforced; commits require matching artifact and session versions.
- Autosaves, forced unlocks, and check-in state retain attribution and hashes.
- Backup ZIP entries and manifest paths are traversal checked; archive, file size, and SHA-256 integrity are verified before restore.

## Scale and performance

Search requests are debounced, cancellable in the client, bounded to at most 50 results, scoped on the server, and sorted after provider-compatible queries. Existing server pagination, scale indexes, 50,000-requirement qualification data, and mixed database workload tools remain intact. This workstation validation is not a claim of 150-user production capacity.

## User journeys completed

Automated browser workflows cover application startup and recovery, complete sequential SCR authoring/review, enterprise requirement discovery and governed work, the FMS 1.5/1.6 lifecycle, history/build provenance, System and Software authoring impact, enterprise control/qualification, security boundaries, durable requirement deep links, suffix-fragment search, and a two-user checkout/read-only/autosave/check-in journey. Existing domain/API behavior additionally preserves parallel review, derived HLR/LLR classification, exact document snapshots, immutable retest chains, release gates, and baseline lineage.

## Automated-test results

Final pre-live gate on 2026-07-13:

- Domain tests: 30 passed.
- Infrastructure/persistence tests: 16 passed.
- Client lint: passed with no reported findings.
- TypeScript and production Vite build: passed.
- Playwright: 11 of 11 Chromium scenarios passed in 41.6 seconds; the acceptance-closure scenario explicitly covers the previously missing 28-item gaps.
- Isolated PostgreSQL migration: applied successfully; new snapshot table and lease columns verified.

## Backup and restore validation

Backup `aerolink-20260713-154334.zip` was created before migration. SHA-256 `85a1481030e9cffc90848888b48453cd4e4cde36dff69e3cc76e5dc21ab90a1c` and all 31 manifest entries verified. It restored into `aerolink_restore_validation` with 12 Program records and isolated evidence, and the new migration applied successfully to that restored copy. Final acceptance backup `aerolink-20260713-164641.zip`, SHA-256 `bb7ca11336de9519fc88e2229b2b63541c179167b50fd739272ef6c3dc17396d`, verified all 41 manifest entries and restored into isolated database `aerolink_restore_acceptance28` with all 12 Program records.

## Live validation

After a controlled stop/start, diagnostics passed PostgreSQL, API, real `admin` authentication, client, 17 applied migrations, disk space, backup recency, and evidence storage. The live API confirmed FMS 1.5 `isReleased=true` and FMS 1.6 `isReleased=false`; a context-bearing requirements URL returned HTTP 200; an anonymous showcase-seed mutation returned HTTP 401; and identifier fragment `0001` returned bounded artifact matches. A real live cycle checked out `SWCR-00000078.00` as `software.author`, autosaved snapshot hash prefix `72a84acada50`, exposed the lock as read-only to `systems.reviewer`, and discarded the session without changing controlled content.

## Outside the 28-scenario acceptance boundary

The 28 requested scenarios are complete. Broader production deployment still requires organization-specific TLS and enterprise identity integration, secret replacement, scheduled off-device backups, monitoring, capacity qualification, and an approved recovery drill. External email delivery, organization-specific certification evidence, and generalizing exclusive checkout to every future artifact family remain separate roadmap work and are not implied by this acceptance closure.
