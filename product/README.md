# AeroLink Product

This directory contains the real application foundation. The visual showcase remains separate under `showcase/` and acts as design inspiration; data shown here comes from the actual API and persistence layer.

## Current vertical slice

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

Open `http://127.0.0.1:5173`. Local development uses PostgreSQL on port `54329`; application startup applies versioned migrations. A fresh database opens the guided New Program workflow, and demonstration data is disabled by default. SQLite remains available for isolated tests. Set `DemoData:Enabled` to `true` only when the explicit FMS sample workspace is wanted.

## Verify

```powershell
& "$HOME\.dotnet\dotnet.exe" test product\AeroLink.slnx
Set-Location product\client
npm.cmd run lint
npm.cmd run build
npm.cmd run test:e2e
```

## Structure

- `src/AeroLink.Domain`: lifecycle behavior and invariants
- `src/AeroLink.Infrastructure`: Entity Framework persistence and provider selection
- `src/AeroLink.Api`: HTTP boundary and local seed context
- `tests/AeroLink.Domain.Tests`: executable product decisions
- `client`: React and TypeScript user interface
- `docs/ARCHITECTURE.md`: technical direction and boundaries
- `docs/SCALE_FOUNDATION.md`: PostgreSQL setup, migrations, scale generator, targets, and measured results
- `api/AeroLink API`: Bruno collection for exercising the local HTTP API
