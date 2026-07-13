# Scale Foundation

## Production-like local database

Development now uses PostgreSQL 18.4 on `127.0.0.1:54329`. The binaries and data directory live under ignored `product/.local/`; they are never committed. Run the scripts with `powershell.exe -NoProfile -ExecutionPolicy Bypass -File product\scripts\<script-name>` so setup also works on Windows machines whose default policy blocks local scripts. Run `Setup-Postgres.ps1` once, then use `Start-Postgres.ps1` and `Stop-Postgres.ps1`.

PostgreSQL is the production path. SQLite remains available only for isolated tests and zero-administration experiments.

## Schema control

Entity Framework migrations under `src/AeroLink.Infrastructure/Persistence/Migrations` are the authoritative schema history. Application startup applies pending PostgreSQL migrations. `EnsureCreated` is retained only for SQLite test databases.

## Bounded queries

SCR and proposed-requirement list endpoints perform filtering, ordering, counting, and pagination in PostgreSQL. Page sizes are capped at 200. Dashboard counts are SQL aggregates and do not load full artifact collections.

Key indexes currently cover stable artifact identifiers, project plus state, project plus update time, SCR-to-requirement membership, and requirement identifiers.

## Concurrency

SCRs carry a numeric optimistic-concurrency version. Every changed SCR advances the version. A stale concurrent write fails rather than overwriting newer work; the API translates this condition to HTTP `409 Conflict` with a refresh-and-reapply instruction.

## Deterministic scale data

`tools/AeroLink.Scale` generates synthetic, non-proprietary lifecycle data with fixed identifiers, content, timestamps, review sequences, and state distribution.

| Profile | SCRs | Proposed requirement changes |
| --- | ---: | ---: |
| `smoke` | 200 | 1,000 |
| `small` | 1,000 | 5,000 |
| `medium` | 10,000 | 50,000 |

The separate `workspace` command generates materialized Requirement artifacts, immutable revisions, baseline membership, Program schemas, structured System/HLR/LLR specifications, revision profiles, and specification placements. Its `small` profile is the Wave 1 qualification dataset with 10,000 requirements; `smoke` produces 1,000 and `medium` produces 50,000. This database is intentionally separate from the 1,250-requirement FMS showcase.

Example:

```powershell
$env:AEROLINK_SCALE_CONNECTION='Host=127.0.0.1;Port=54329;Database=aerolink_scale;Username=postgres'
& "$HOME\.dotnet\dotnet.exe" run --project product\tools\AeroLink.Scale -- generate --profile medium --reset
& "$HOME\.dotnet\dotnet.exe" run --project product\tools\AeroLink.Scale -- benchmark
```

Enterprise Requirements Workspace qualification:

```powershell
& "$HOME\.dotnet\dotnet.exe" run --project product\tools\AeroLink.Scale -- workspace --profile medium --reset
& "$HOME\.dotnet\dotnet.exe" run --project product\tools\AeroLink.Scale -- benchmark
& "$HOME\.dotnet\dotnet.exe" run --project product\tools\AeroLink.Scale -- load --users 150 --iterations 8
```

The `--reset` safeguard works only when the connection string names an `aerolink_scale` database.

## First medium-scale result

Run on July 12, 2026 using local PostgreSQL 18.4:

- 10,000 SCRs
- 50,000 proposed requirement changes
- 92,000 audit events
- 8,000 review cycles
- 24,000 ordered approval steps
- 200 candidate baselines
- 59 MB PostgreSQL database
- deterministic generation time: 12.6 seconds

Warm-query p95 observations over five samples:

| Operation | Target | Observed p95 |
| --- | ---: | ---: |
| Dashboard aggregates | 2,000 ms | 4 ms |
| First 50 SCRs | 500 ms | <1 ms |
| Exact requirement identifier | 300 ms | <1 ms |
| First 50 requirements | 500 ms | <1 ms |

These are local engineering observations, not production guarantees. They exclude browser rendering, network latency, authentication, concurrent users, file evidence, complete trace networks, and controlled-document generation. Results must be repeated as those capabilities are added.

## First 10,000-requirement workspace result

Run on July 12, 2026 using local PostgreSQL 18.4 and deterministic seed 4754:

- 10,000 stable Requirement artifacts and immutable active revisions
- 1,500 System requirements, 3,500 HLRs, and 5,000 LLRs
- exact membership in one frozen materialized baseline
- three Program schemas, three specifications, revision profiles, and 10,000 specification placements
- deterministic materialization and workspace synchronization: 7.5 seconds

Warm-query p95 observations over five samples:

| Operation | Target | Observed p95 |
| --- | ---: | ---: |
| Exact current requirement revision | 300 ms | <1 ms |
| Enterprise workspace page of 100 | 500 ms | 21 ms |
| Structured System/Test filter | 500 ms | 6 ms |
| Specification-tree aggregation | 500 ms | 2 ms |

This proves the persistence/query shape at 10,000 requirements on one local workstation. It does not yet prove 150 concurrent browser users, cold-cache behavior, attachment throughput, deep trace expansion, or production network/identity overhead.

## 50,000-requirement and 150-client result

Run on July 12, 2026 using local PostgreSQL 18.4 and deterministic seed 4754:

- 50,000 stable Requirement artifacts, immutable active revisions, exact baseline memberships, revision profiles, and specification placements
- deterministic materialization and synchronization: 24.7 seconds
- enterprise workspace page of 100: 150 ms warm p95 against a 500 ms target
- structured System/Test filter: 10 ms warm p95
- specification-tree aggregation: 14 ms warm p95
- 150 simultaneous database clients, eight mixed operations per client: 1,200 operations with zero failures
- 401.8 operations/second; 16 ms p50, 1,265 ms p95, and 2,461 ms p99
- the 150-client p95 passed the 2,000 ms engineering gate

The load command mixes paging, verification aggregation, specification-tree queries, and identifier search using separate pooled database contexts. It demonstrates persistence/query concurrency on this workstation; it is not yet a claim of 150 simultaneous rendered browser sessions or a production service-level guarantee.

## Next scale gates

- Add a deep and realistically distributed trace/coverage network to the 10,000-requirement qualification dataset.
- Add 150-browser/API-session mixed-workload testing on production-like application topology.
- Test cold-cache behavior and realistic search terms.
- Test backups, restores, evidence storage, and failure recovery.
- Record query plans and detect regressions in continuous integration.
