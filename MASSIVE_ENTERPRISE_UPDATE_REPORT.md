# Massive Enterprise Update Report

## Executive summary

The 2026-07-13 update advances AeroLink from a broad working prototype to a safer enterprise control foundation. The implemented vertical slice concentrates on the highest-risk gaps: durable artifact navigation, server-enforced exclusive SCR/SWCR editing and recovery, Program-scope security, isolated tests, safe schema evolution, and provable backup/restore operations. Existing PostgreSQL data and FMS history were preserved; FMS 1.5 remains released and immutable, while 1.6 remains derived from 1.5 and intentionally in work.

AeroLink remains local/on-premises, uses the established React/TypeScript, ASP.NET Core, Entity Framework, and PostgreSQL stack, adds no AI capability, and makes no ARP4754A, DO-178C, certification, or tool-qualification claim.

## Implemented capabilities

- Durable URL routing for the main Program/Project/release portals, SCR/SWCR records, requirements, and supported controlled artifacts; refresh and browser back/forward restore context.
- Persistent left navigation with collapsible discipline groups, breadcrumbs, active context, copy-link actions, and native links suitable for new tabs.
- `Ctrl+K` command palette with page navigation, debounced server search, identifier-fragment matching, and permission-aware deep links.
- Authoritative not-found and forbidden behavior instead of silent fallback.
- SCR/SWCR exclusive checkout with a unique server lock, fifteen-minute renewable lease, heartbeat, holder/activity/expiry visibility, and read-only access for other users.
- Server-backed autosave with status, content hashing, sequenced recoverable snapshots, explicit discard, save/check-in, optimistic version checks, and forced unlock with reason and audit evidence.
- Review submission refuses incompatible active editing sessions.
- Bounded Program-aware search across change requests, requirements, baselines, builds, procedures, documents, and release campaigns.
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

- Domain tests: 29 passed.
- Infrastructure/persistence tests: 16 passed.
- Client lint: passed with no reported findings.
- TypeScript and production Vite build: passed.
- Playwright: 10 of 10 Chromium scenarios passed in 42.1 seconds.
- Isolated PostgreSQL migration: applied successfully; new snapshot table and lease columns verified.

## Backup and restore validation

Backup `aerolink-20260713-154334.zip` was created before migration. SHA-256 `85a1481030e9cffc90848888b48453cd4e4cde36dff69e3cc76e5dc21ab90a1c` and all 31 manifest entries verified. It restored into `aerolink_restore_validation` with 12 Program records and isolated evidence, and the new migration applied successfully to that restored copy. Post-update backup `aerolink-20260713-155747.zip`, SHA-256 `9d47cb46e68bd322d4d8cb4484e2141bebaaf61616c6d47e23cc0037c79e30b5`, verified all 33 manifest entries.

## Live validation

After a controlled stop/start, diagnostics passed PostgreSQL, API, real `admin` authentication, client, 17 applied migrations, disk space, backup recency, and evidence storage. The live API confirmed FMS 1.5 `isReleased=true` and FMS 1.6 `isReleased=false`; a context-bearing requirements URL returned HTTP 200; an anonymous showcase-seed mutation returned HTTP 401; and identifier fragment `0001` returned bounded artifact matches. A real live cycle checked out `SWCR-00000078.00` as `software.author`, autosaved snapshot hash prefix `72a84acada50`, exposed the lock as read-only to `systems.reviewer`, and discarded the session without changing controlled content.

## Partially implemented foundations

- The new edit-session contract is complete for SCR/SWCR drafts; requirement proposals, specification structures, test procedures, trace links, and release-planning drafts still use their existing concurrency controls.
- Universal search covers the principal controlled record types but not yet comments, users, execution/evidence contents, full facets, saved recent searches, or ranked highlighting.
- Generic authoritative detail pages exist for several artifact families; every historical audit entry and every dense relationship node is not yet converted to a preview-enabled link.
- Existing notifications, My Work, review activation, and in-product deep links are functional, but external email outbox/retry/file sink is not implemented.
- Existing controlled DOCX/PDF, trace annex, redline, test evidence, baseline, build, and release workflows are substantial; additional browser coverage and publication visual refinements remain.

## Remaining limitations and recommended next update

The next major update should generalize checkout/autosave to every controlled draft family, add the audited email outbox and local delivery viewer, expand universal search with facets/pagination/saved history, complete link/preview coverage across audit and trace matrices, and add remaining browser journeys for failed-test/retest, traceability DOCX/PDF, reviewer replacement/restart, and full 1.6 release progression without auto-releasing it. Production deployment also still requires TLS, enterprise identity, secret replacement, scheduled off-device backups, monitoring, capacity qualification, and an organization-approved recovery drill.
