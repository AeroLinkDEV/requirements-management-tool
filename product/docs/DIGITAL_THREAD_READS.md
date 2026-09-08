# Digital Thread read bounds

The rooted CR/TCR trace and build change network use the same exact-identity and provenance composer. They select the relevant identities before loading descriptive records or frozen evidence.

## Scope and limits

A rooted trace expands a typed frontier in both directions over authored/assessment CR links, frozen review links, TCR origins and claims, Case/Procedure origins, exact requirement revisions and live requirement traces, and code evidence. Every query retains the owning Project predicate. An inaccessible or foreign Project does not become a traversal shortcut.

Rooted reads return a complete connected graph or HTTP 413 with Problem Details code `trace_work_limit`. They do not return a partial graph labelled complete. Limits are 1,000 nodes, 20,000 materialized query rows across discovery/detail/state reads, 64 frontier waves, and 8,000,000 snapshot characters reserved before payload loading. Cancellation is passed to every database read and checked between expansion waves. These are work limits, not permissions or controlled-history retention limits.

The build network retains exact target-build membership rather than connected-component membership. Cross-build nodes remain excluded on that surface; the rooted trace retains exact cross-build relationships and historical provenance. The network selects in the existing Kind/DisplayNumber/Id order with a shared node ceiling, clamps requested ceilings to 1,000, and reads at most one extra identity per kind to detect omitted members. `Truncated` reports that cut. Edges to omitted nodes are omitted without inventing replacement relationships. A separate row/history work-limit refusal still returns HTTP 413.

## Frozen history lookup

`frozen_review_trace_links` is a derived lookup containing Project, review cycle, owning CR and exact upstream CR identities. The cycle/upstream pair is unique; Project/owner and Project/upstream indexes support forward and reverse discovery. This table does not replace historical evidence. Original `ReviewCycle.SnapshotJson` and `SnapshotHash` remain unchanged, and the existing snapshot parser supplies all provenance after selection.

The asynchronous save boundary adds lookup entries with new version-3-or-later CR review cycles in the same EF save transaction. TCR cycles do not create CR snapshot links. Repeated preparation in the same context does not duplicate keys.

Normal PostgreSQL startup applies the additive schema migration and then `FrozenReviewTraceAdjacencyMigrationAuthority` before serving requests. The authority processes existing snapshots in deterministic batches of 100, inserts missing derived rows, and commits one unique `FrozenReviewTraceAdjacency.v1` completion marker in the same transaction. A PostgreSQL advisory transaction lock serializes concurrent startup backfills. New-version writers maintain the lookup atomically; deployments must follow the supported startup/upgrade path rather than running an old writer against a newly migrated schema. Read-only restore validation requires the completed lookup marker and never performs this upgrade. No snapshot, hash, signature or governed identifier is rewritten. A failed or cancelled transaction cannot claim completion.

SQLite fixtures use `EnsureCreated` and the same live-save hook. Focused tests explicitly exercise the backfill against disposable legacy-shaped data. Persistent developer/demo databases are never qualification fixtures.

## Regression evidence

`ChangeRequestTraceScalingTests` holds a two-node/one-edge requested graph constant while increasing unrelated CR/TCR/review history from 100 to 1,000. Normal SQLite tests assert bounded delivered rows and zero unrelated snapshot payload. The opt-in PostgreSQL test uses a uniquely named disposable database, applies real migrations, records SQL and `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)`, and rejects a project-history scan at the larger population.

Rows mean provider-delivered rows, and JSON bytes mean UTF-8 payload bytes in returned JSON columns; neither is a substitute for database scan-plan evidence. Allocation and latency observations include measurement overhead and are diagnostic, not brittle wall-clock CI thresholds. The existing semantic tests retain exact typed identities, frozen provenance, suspect-link rules and seven-query register-state coverage. Work-limit and backfill tests cover explicit refusal, cancellation, reverse historical links, idempotence, concurrent PostgreSQL startup and byte-identical snapshots.
