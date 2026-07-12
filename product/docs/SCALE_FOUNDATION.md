# Scale Foundation

## Production-like local database

Development now uses PostgreSQL 18.4 on `127.0.0.1:55432`. The binaries and data directory live under ignored `product/.local/`; they are never committed. Run the scripts with `powershell.exe -NoProfile -ExecutionPolicy Bypass -File product\scripts\<script-name>` so setup also works on Windows machines whose default policy blocks local scripts. Run `Setup-Postgres.ps1` once, then use `Start-Postgres.ps1` and `Stop-Postgres.ps1`.

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

Example:

```powershell
$env:AEROLINK_SCALE_CONNECTION='Host=127.0.0.1;Port=55432;Database=aerolink_scale;Username=postgres'
& "$HOME\.dotnet\dotnet.exe" run --project product\tools\AeroLink.Scale -- generate --profile medium --reset
& "$HOME\.dotnet\dotnet.exe" run --project product\tools\AeroLink.Scale -- benchmark
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

## Next scale gates

- Add actual Requirement/Revision and Trace Link aggregates when those modules are implemented; current counts represent requirement changes proposed inside SCRs.
- Add 150-user mixed-workload testing.
- Test cold-cache behavior and realistic search terms.
- Test backups, restores, evidence storage, and failure recovery.
- Record query plans and detect regressions in continuous integration.
