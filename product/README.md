# AeroLink Product

This directory contains the real application foundation. The visual showcase remains separate under `showcase/` and acts as design inspiration; data shown here comes from the actual API and persistence layer.

## Current vertical slice

- clean Program, software Project, and initial Release onboarding; optional FMS demonstration data
- SCR creation with proposed system or high-level software requirement changes
- author-selected, ordered approval sequences
- same-revision return to Draft before first approval and next-revision control after approval
- append-only audit events and candidate-baseline eligibility rules
- live manager/engineer dashboard backed by persisted data
- guided SCR Draft authoring with Problem, Analysis, Solution, and one or more proposed requirement changes

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

Open `http://127.0.0.1:5173`. Local development uses PostgreSQL on port `55432`; application startup applies versioned migrations. A fresh database opens the guided New Program workflow, and demonstration data is disabled by default. SQLite remains available for isolated tests. Set `DemoData:Enabled` to `true` only when the explicit FMS sample workspace is wanted.

## Verify

```powershell
& "$HOME\.dotnet\dotnet.exe" test product\AeroLink.slnx
Set-Location product\client
npm.cmd run lint
npm.cmd run build
```

## Structure

- `src/AeroLink.Domain`: lifecycle behavior and invariants
- `src/AeroLink.Infrastructure`: Entity Framework persistence and provider selection
- `src/AeroLink.Api`: HTTP boundary and local seed context
- `tests/AeroLink.Domain.Tests`: executable product decisions
- `client`: React and TypeScript user interface
- `docs/ARCHITECTURE.md`: technical direction and boundaries
- `docs/SCALE_FOUNDATION.md`: PostgreSQL setup, migrations, scale generator, targets, and measured results
