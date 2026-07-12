# AeroLink Product

This directory contains the real application foundation. The visual showcase remains separate under `showcase/` and acts as design inspiration; data shown here comes from the actual API and persistence layer.

## Current vertical slice

- FMS program, software project, released 3.2 baseline context, and target 3.3 release
- SCR creation with proposed system or high-level software requirement changes
- author-selected, ordered approval sequences
- same-revision return to Draft before first approval and next-revision control after approval
- append-only audit events and candidate-baseline eligibility rules
- live manager/engineer dashboard backed by persisted data

## Run locally

Use two PowerShell terminals from the repository root.

```powershell
& "$HOME\.dotnet\dotnet.exe" run --project product\src\AeroLink.Api --urls http://127.0.0.1:5080
```

```powershell
Set-Location product\client
npm.cmd install
npm.cmd run dev
```

Open `http://127.0.0.1:5173`. Local development uses SQLite and creates `aerolink-dev.db`. Set `Database:Provider` to `PostgreSql` and supply the `AeroLink` connection string when PostgreSQL is available.

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
