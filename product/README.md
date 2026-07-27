# AeroLink Product

This directory contains the application — the only software artifact in the repository. All data shown
comes from the actual API and persistence layer. The former `showcase/` prototype was retired on
2026-07-24 (DEC-046); design reference now lives in `design/mockups` and
[DESIGN_VISION_AND_DASHBOARDS.md](../DESIGN_VISION_AND_DASHBOARDS.md).

For project-wide orientation, start at [PROJECT_STATE.md](../PROJECT_STATE.md).

## Current vertical slice

The 2026-07-13 enterprise control increment adds durable URL routing and context restoration, a keyboard command palette, Program-aware universal artifact search, authoritative artifact detail links, SCR/SWCR exclusive checkout, renewable leases, server autosave snapshots, read-only observers, check-in/discard, forced-unlock auditing, authentication throttling, Program-scope enforcement, isolated browser-test infrastructure, and verified backup/restore operations.

- optional deterministic FMS live program with a released 1.5 baseline and active 1.6 development release
- 150 system requirements, 400 HLRs, 700 LLRs, 105 historical SCR/SWCR records, 1,100 typed traces, 515 procedures, 520 executions, and six controlled outputs

- clean Program, software Project, and initial Release onboarding; optional FMS demonstration data
- SCR creation with proposed system or high-level software requirement changes
- author-selected, ordered approval sequences
- same-revision return to Draft before first approval and next-revision control after approval
- append-only audit events and candidate-baseline eligibility rules
- live manager/engineer dashboard backed by persisted data
- guided SCR Draft authoring with Problem, Analysis, Solution, and one or more proposed requirement changes
- SCR workspace with safe Draft editing, requirement replacement, control status, and append-only audit history
- author-configured ordered review sequences with frozen snapshot hashes
- active-reviewer approval or change requests, same-revision Draft rework, resubmission, and unanimous approval
- explicit record versions and stale-browser conflict protection on Draft and review actions
- release-targeted candidate baseline creation with eligible Approved SCR discovery
- exact SCR-revision selection and removal, derived requirement-impact manifest, persistent baseline events, and immutable SHA-256 freeze
- searchable SCR history across every revision, state, target release, baseline, and software build
- searchable software-requirement revision history with source-SCR provenance and lifecycle impact
- immutable software-build records tied to one exact frozen baseline, with drill-down to included SCR and requirement revisions
- stable requirement identities separated from immutable requirement revisions
- deterministic baseline materialization that applies Introduce, Modify, and Retire changes over an exact predecessor baseline
- generated SWRD views and effective-requirement SHA-256 manifests traceable to source SCRs and software builds
- reusable, revision-controlled test procedures with many-to-many links to exact requirement revisions
- externally executed Pass, Fail, and Blocked results with configuration, human determination, and evidence references
- immutable retest chains and release/build-specific coverage and verified-status dashboards
- governed FMS 1.6 release campaign with nine computed readiness gates, explicit impact dispositions, build selection, and ordered release approval
- checksum-protected evidence upload, download, and exact test-execution linkage with cross-project isolation
- live 1.5-to-1.6 comparison covering effective and proposed requirement changes
- deterministic, downloadable SYSRD, HLR SWRD, LLR SWRD, and three test-procedure documents in DOCX and PDF
- formal atomic release that binds the approved baseline, selected build, controlled outputs, and release manifest hash
- integrated release-execution workbench connecting change inputs, impact disposition, baseline materialization, build control, outputs, verification, and approval
- predecessor-aware reconciliation that creates only target-baseline-valid versioned trace and coverage links while exposing genuinely new coverage gaps
- exact JSON verification-manifest export and atomic bulk import of hundreds of build-specific results with shared checksum-protected campaign evidence
- professional DOCX/PDF publication of SCRs, requirements, and test procedures with editorial covers, named approval provenance, document-control registers, revision history, and controlled-copy markings
- controlled product-line libraries with immutable revisions, reference/synchronized/diverged reuse, retained accept/defer/reject decisions, exact variant configurations, and configuration-correct requirements, traces, tests, and metrics
- approved organization template revisions, equivalent rich engineering content in the UI/DOCX/PDF, exact baseline redlines, resumable deterministic publication jobs, integrity verification, and manifest-backed release evidence packages
- enterprise requirements workspace spanning the complete System/HLR/LLR repository, with high-density table and document modes, structured specifications, paging, filters, and revision inspection
- Program-configurable artifact-schema records and structured specification/section placement while stable requirement identity remains independent of document position
- attributable requirement discussions with mentions, resolution/disposition state, exact-revision context, saved personal/shared views, and visual revision redlines
- governed bulk classification and specification placement through previewed, attributable jobs rather than silent direct edits
- checksum-recorded CSV/XLSX onboarding with row validation and a controlled commit boundary that creates a Draft SCR/SWCR instead of bypassing approval
- provider-compatible PostgreSQL/SQLite persistence, versioned migration, and deterministic workspace synchronization for existing Programs
- direct “analyze impact and propose change” workflow from an approved requirement into its proposed next revision inside a Draft SCR/SWCR; no parallel requirement-approval path
- controlled structured-text authoring with lists, tables, aerospace symbols, exact references, Program fields, safe preview, and five mandatory impact-disposition categories included in the review snapshot hash
- relationship-aware impact intelligence spanning parent/child requirements, verification procedures, baselines, builds, documents, active change packages, comments, and assigned follow-up
- requirement watchers, threaded notifications, accountable assignments, due/overdue work queues, completion concurrency, and a combined engineering operations center
- advanced permission-scoped requirement filters for lifecycle state, owner, source SCR/SWCR, open discussions, verification, tag, specification, and deterministic sorting
- reusable import-mapping records, persistent interchange-job history, and downloadable CSV error reports
- versioned controlled attachment vault with exact-revision association, protected storage, SHA-256 integrity verification, provenance, retrieval, supersession, and immutable history
- comprehensive visual redlines spanning statement, rationale, rich content, Program attributes, verification method, and exact-revision attachment changes
- visual structured-query builder with personal/shared permission-aware worklists and stable URLs that reopen the saved view
- durable background export/integrity jobs with idempotency, progress, attempts, retry/cancel state, attributable outcomes, and downloadable controlled CSV output
- multi-session edit detection with optimistic versions, retained base/local/remote content, explicit three-way resolution, and no silent overwrite
- operator-facing Enterprise Control dashboard with repository, file-storage, job, editor, conflict, performance, and integrity-checkpoint signals
- isolated PostgreSQL qualification workspace generator with 50,000 mixed-level Requirement/Revision records, measured enterprise workspace benchmarks, and a 150-client mixed-load gate

## Run locally

Three paths, and the difference matters.

**For a demonstration, or to see what a deployment serves:** `START_AEROLINK_PRODUCTION.bat`. It builds the
client, then serves it from the API on a single origin at `http://127.0.0.1:5080` — one process, one port, no
CORS. That is the shape an on-premises install has, and it is the only path that exercises the built client.

**To let other people on the network open it:** `START_AEROLINK_SHARED.bat`, which is the same thing with
`-Shared`. Reaching this machine from another one takes two changes and not one, and the second is the one
nobody expects. Binding Kestrel to `0.0.0.0` makes the socket accept connections from off the box; ASP.NET
Core's host filtering then compares the `Host` header against `AllowedHosts`, which `appsettings.json` sets to
`localhost;127.0.0.1`. Change only the binding and a colleague reaches a server that is listening perfectly
well and gets a bare HTTP 400 with no body — which reads exactly like a binding fault and is not one. The
switch moves both together. Windows Firewall is a third thing again, outside this process's gift: it drops
inbound connections on 5080 until an administrator allows them, and the launcher checks and prints the command
rather than changing a firewall rule on somebody's machine by itself.

**For development:** `START_AEROLINK.bat`, or the manual steps below. Both run the Vite dev server, which
recompiles on save and is the wrong thing to show anybody. It is deliberately not shareable — two ports, a
CORS policy joining them, and a bundle that rebuilds mid-demonstration.

Run the PostgreSQL setup once, then use two PowerShell terminals from the repository root.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File product\scripts\Setup-Postgres.ps1
```

```powershell
& "$HOME\.dotnet\dotnet.exe" run --project product\src\AeroLink.Api --urls http://127.0.0.1:5080
```

```powershell
Set-Location product\client
npm.cmd install
npm.cmd run dev
```

Open `http://127.0.0.1:5173`. Local development uses PostgreSQL on port `54329`; application startup applies versioned migrations. The checked-in Development profile enables the explicit FMS showcase and local demonstration identities, while the production defaults keep both disabled. Set `DemoData__Enabled=false` and `Identity__SeedDemoAccounts=false` for a blank local onboarding run. SQLite remains available for isolated tests.

The default showcase contains only `Flight Management System Live Program`, with released version 1.5 and in-work version 1.6. Older local databases can be reduced safely with the preview-first `product\scripts\Prune-LocalShowcasePrograms.ps1` maintenance command documented in [Operations and recovery](docs/OPERATIONS.md#fms-only-local-showcase-cleanup).

For a production database, demo identities remain disabled. Configure the one-time, zero-user administrator bootstrap through the protected `Identity__BootstrapSecret` service environment setting, then remove it immediately after creating the first `admin` account. The complete procedure is in [Operations and recovery](docs/OPERATIONS.md#production-first-install-administrator); no bootstrap secret belongs in this repository.

Local demonstration identities include `admin`, `systems.author`, `software.author`, `systems.reviewer`, and `release.manager`; their local-only password is `AeroLink!2026`. These credentials are intentionally non-production and must be replaced before any operational deployment. See [Operations and recovery](docs/OPERATIONS.md).

## Verify

**Stop AeroLink before running anything that builds.** On Windows a running API holds
`AeroLink.Domain.dll` and `AeroLink.Infrastructure.dll` open, and the build fails with `MSB3027 … the file is
locked by AeroLink.Api`. Both launchers leave the API running after their window closes — deliberately — so
this is the normal state of a machine somebody has been demonstrating from, and the error names a copy step
rather than the cause. `STOP_AEROLINK.bat` first. Linux has no such problem, which is why nothing catches it.

Use the smallest trustworthy loop while developing:

```powershell
Set-Location product\client
npm.cmd run test:fast
npm.cmd run test:focused -- tests\lifecycle-decision-room.spec.ts
npm.cmd run test:smoke
```

The commands above are shown for PowerShell because that is the operator platform, but the browser
journeys are not Windows-only. On Linux or macOS the same scripts work unchanged:

```bash
cd product/client
npx playwright install chromium   # once
npm run test:fast
AEROLINK_E2E_SKIP_BUILD=true npx playwright test
```

`AEROLINK_E2E_SKIP_BUILD=true` reuses an API you have already built in Release and saves about a minute
per run. If `dotnet` is not on `PATH`, set `AEROLINK_DOTNET` to its full path.

- `test:fast` runs lint and TypeScript checks without starting the product.
- `test:focused -- <spec>` starts an isolated product and runs only the named Playwright journey.
- `test:smoke` exercises login recovery and the showcase-critical UI path.
- `test:e2e:sharded` builds the API once, then runs three Playwright shards against separate ports, SQLite databases, reports, and diagnostics.
- `test:e2e` preserves the original single-worker serial path for troubleshooting and compatibility.
- `test:production` builds the client and runs the journeys in `tests/production/` against the **built**
  client, served by the API on one origin. Everything above serves the client with `vite dev`, which is a
  different artifact: dev hands over unbundled modules and injects each stylesheet as its module evaluates,
  while a build chunks the code, extracts every stylesheet into one hashed file, and minifies. Use this before
  a demonstration, and expect it to catch things the dev journeys structurally cannot.
- Every browser run prints its five slowest journeys and writes `test-results/test-timings.json` (one file per shard for sharded runs).

Before publishing, run the complete backend and parallel client gates:

```powershell
& "$HOME\.dotnet\dotnet.exe" test product\AeroLink.slnx
Set-Location product\client
npm.cmd run test:full
```

GitHub runs backend tests, the client production build, three isolated browser shards, and PostgreSQL migration/bootstrap verification concurrently. NuGet, npm, and Chromium caches keep repeat runs fast while the aggregate `Build, test, and exercise product journeys` check preserves one branch-protection result.

## Structure

- `src/AeroLink.Domain`: lifecycle behavior and invariants
- `src/AeroLink.Infrastructure`: Entity Framework persistence and provider selection
- `src/AeroLink.Api`: HTTP boundary and local seed context
- `tests/AeroLink.Domain.Tests`: executable product decisions
- `client`: React and TypeScript user interface
- `docs/ARCHITECTURE.md`: technical direction and boundaries
- `docs/SCALE_FOUNDATION.md`: PostgreSQL setup, migrations, scale generator, targets, and measured results
- `api/AeroLink API`: Bruno collection for exercising the local HTTP API
