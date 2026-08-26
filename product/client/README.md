# AeroLink web client

This directory contains the React/TypeScript client for AeroLink. It is not a standalone template project:
product behavior, routes, authority, and persistence come from the ASP.NET Core API under `product/src`.

For product orientation, read [PROJECT_STATE.md](../../PROJECT_STATE.md). The former 2026-08-10 restart
handoff is retained only as [historical archive context](../../docs/archive/CURRENT_PRODUCT_HANDOFF_2026-08-10.md).
For application startup, deployment-shaped demonstration, PostgreSQL setup, and complete validation commands,
read the [product README](../README.md).

## Supported development loop

From this directory:

```powershell
npm.cmd install
npm.cmd run dev
```

The normal development launcher at the repository root (`START_AEROLINK.bat`) starts the API, PostgreSQL, and
this Vite server with the correct local ports. Use `START_AEROLINK_PRODUCTION.bat` when validating or showing
the built single-origin client served by the API.

## Validation

```powershell
npm.cmd run test:fast
npm.cmd run test:focused -- tests\upward-allocation.spec.ts
npm.cmd run test:e2e:sharded
npm.cmd run test:production
```

- `test:fast` runs lint and TypeScript checks.
- `test:focused` runs selected Playwright journeys against an isolated API and SQLite database.
- `test:e2e:sharded` builds the API once and runs the complete browser matrix in three isolated shards.
- `test:production` builds the client and exercises protected mutations and deep links against the API-served
  production artifact.

Do not point browser tests at the persistent local PostgreSQL demonstration database. Do not weaken a failing
assertion to accept multiple states until the product behavior and request/response evidence establish that the
variation is intentional.

## Client boundaries

- The client never decides authority; the API derives the actor and enforces Program/build/role rules.
- Requirements Explorer is read-only. Controlled changes begin in change request workflows.
- Build 1.5 is released/read-only; Build 1.6 is active development.
- System, Software HLR, and Software LLR verification inventories are isolated.
- The client has no external runtime dependency and must remain usable on restricted on-premises networks.
- Production Concurrency and count-only integrity simulations are intentionally absent; real checkout/conflict
  records and cryptographic attachment checkpoints are the authoritative workflows.
