# AeroLink — Aerospace Development Assurance Platform

AeroLink is a local/on-premises aerospace requirements-management and development-assurance platform. It manages controlled requirements, change, review/signature, exact traceability, verification Cases/Procedures, results/evidence, immutable baselines, generated/managed documents, Problem Reports, and release readiness without claiming certification or tool qualification.

The production-oriented application uses React/TypeScript, ASP.NET Core/.NET, Entity Framework Core, and PostgreSQL.

## Start here

New to the repository — human or coding agent?

1. **[PROJECT_STATE.md](PROJECT_STATE.md)** — what AeroLink is and does now.
2. **[AGENTS.md](AGENTS.md)** — how to work safely in this repository.
3. **[DECISIONS_AND_OPEN_QUESTIONS.md](DECISIONS_AND_OPEN_QUESTIONS.md)** — authoritative accepted product decisions.
4. **[docs/README.md](docs/README.md)** — documentation map, project history, lessons, reference, showcase, provenance, and archive structure.
5. **GitHub Issues** — live backlog and scoped implementation contracts. Do not infer current backlog state from a dated handoff.

Historical handoffs and audit reports are retained as evidence of earlier checkpoints, but they are not current product authority.

## Common Windows launchers

The root `.bat` files are deliberate Windows operator entry points. Their real implementations generally live under `product/scripts`; root paths are kept stable because external shortcuts/Task Scheduler/recovery configuration may depend on them.

| Purpose | Launcher |
| --- | --- |
| Normal development | [`START_AEROLINK.bat`](START_AEROLINK.bat) |
| Stop local AeroLink | [`STOP_AEROLINK.bat`](STOP_AEROLINK.bat) |
| Production-style single-origin run | [`START_AEROLINK_PRODUCTION.bat`](START_AEROLINK_PRODUCTION.bat) |
| Protected remote demo | [`START_AEROLINK_REMOTE_DEMO.bat`](START_AEROLINK_REMOTE_DEMO.bat) |
| Shared LAN run | [`START_AEROLINK_SHARED.bat`](START_AEROLINK_SHARED.bat) |
| Complete backup | [`BACKUP_AEROLINK.bat`](BACKUP_AEROLINK.bat) |
| Diagnostics | [`AEROLINK_DIAGNOSTICS.bat`](AEROLINK_DIAGNOSTICS.bat) |

**Shared-LAN warning:** `START_AEROLINK_SHARED.bat` deliberately listens on all network interfaces so another machine can reach the same persistent demonstration instance. It is intended for a trusted local network only: the demonstration environment uses known/demo credentials and ordinary HTTP, so traffic is not transport-encrypted. Do not expose this shared mode directly to an untrusted network or the public Internet. For remote demonstration, use the protected remote-demo launcher/policy instead.

For the full startup/recovery/backup/operator surface, see [Operations and recovery](product/docs/OPERATIONS.md). For protected remote-demo setup and status/recovery commands, see [Remote demo operator](docs/REMOTE_DEMO_OPERATOR.md).

### Development run

`START_AEROLINK.bat` starts/verifies the local PostgreSQL/API/client development stack and opens the Vite client. PostgreSQL must be installed/configured once first; see [`product/README.md`](product/README.md) and [Operations](product/docs/OPERATIONS.md).

### Production-style run

`START_AEROLINK_PRODUCTION.bat` builds the client and serves it from the API on one origin. This is the closest local launcher to the normal on-premises deployment shape.

### Remote demo

`START_AEROLINK_REMOTE_DEMO.bat` reuses the production path and exposes it through the repository's protected remote-demo policy/tunnel configuration. Follow [`docs/REMOTE_DEMO_OPERATOR.md`](docs/REMOTE_DEMO_OPERATOR.md); do not improvise a public tunnel around those controls.

## Repository map

| Path | Purpose |
| --- | --- |
| `product/` | The AeroLink application and its tests/tooling |
| `product/src/AeroLink.Domain` | Lifecycle rules and invariants |
| `product/src/AeroLink.Infrastructure` | EF Core persistence, migrations, provider behavior |
| `product/src/AeroLink.Api` | HTTP/API boundary |
| `product/client` | React/TypeScript client and Playwright journeys |
| `product/tests` | Backend/domain/infrastructure/API tests |
| `product/docs/` | Architecture, operations, testing/CI, implementation documentation |
| `docs/` | Durable product/project knowledge, history, reference, showcase, provenance, archive |
| `design/mockups`, `docs/mockups` | North-star/reference visual material |
| root `.bat`/`.cmd` | Stable Windows operator entry points |

The retired Phase 0.5 static showcase is historical; the application under `product/` is the demonstrable product.

## Current product shape

See [PROJECT_STATE.md](PROJECT_STATE.md) for the maintained detail. At a glance:

```text
Requirements:
System → HLR → LLR

System verification:
System Requirement → System Test Procedure → Execution / Result / Evidence

Software verification (full profile):
HLR Requirement → HLR Test Case → HLR Test Procedure → Execution / Result / Evidence
LLR Requirement → LLR Test Case → LLR Test Procedure → Execution / Result / Evidence
```

Software verification profiles are configuration-driven; Case-only configurations remain valid where explicitly configured.

## Important operating rules

- The persistent local AeroLink PostgreSQL database is not disposable test state. Do not reset/reseed it merely to qualify a change.
- Controlled historical records, signatures, hashes, manifests, exact revision references, and identifiers are not presentation data.
- GitHub `main`, accepted decisions, and current code/tests outrank historical handoffs.
- Use focused branches/worktrees and exact-SHA test evidence when multiple agents are active.
- Use the changed-test planner rather than guessing the required validation surface.
- Read [Merging into main](product/docs/MERGING.md) before merge/rebase decisions.
- Read [Feedback time](product/docs/BROWSER_AND_BACKEND_FEEDBACK_TIME.md) before changing CI topology or shard counts.

The canonical agent/repository rules are in [AGENTS.md](AGENTS.md).

## Documentation authority

Markdown in Git remains authoritative for product-definition records. Generated Word/PDF copies are snapshots or controlled outputs according to their specific workflow.

Use one home for each kind of information:

- current product truth → `PROJECT_STATE.md`;
- accepted product decisions → `DECISIONS_AND_OPEN_QUESTIONS.md`;
- live work/findings → GitHub Issues;
- repository/agent safety → `AGENTS.md`;
- durable project/product knowledge → `docs/`;
- implementation/operations/testing docs → `product/docs/`;
- historical handoffs/audits → indexed archive under `docs/archive/` as the hygiene migration proceeds.

See [docs/README.md](docs/README.md) for the full documentation map.
