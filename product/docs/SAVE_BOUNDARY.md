# Save boundary contract

`AeroLinkDbContext.SaveChangesAsync` is the one supported application write boundary. The context retains EF
model mapping, the #952 ladder-sealing preparation, the async-only public contract, and the final EF save. The
phase implementations live beside it under `Persistence/`; they are deliberately internal and receive the
concrete context so they cannot become a second persistence API.

## Ordered phases

Every asynchronous save runs the following phases in this order:

1. **Origin precheck.** Added or modified `TestChangeReview` entities run their domain persistence-origin
   check before any provider operation.
2. **Ladder sealing.** `PrepareLadderSealsAsync` performs the first-qualifying-writer ladder work introduced
   by #952. Its cached graph snapshots, deterministic candidate order, policy override, and conflict mapping
   remain owned by the context and are not duplicated by the extracted phases.
3. **State repair.** `SaveBoundaryStateRepair` performs one bounded existence query per affected application-
   assigned child family (chunked for provider parameter limits), then applies the existing Added/Modified and
   immutability rules. It also repairs application-assigned child state, advances controlled numeric versions,
   and turns ownerless review comments into deletions. It changes tracked state but never saves or clears it.
4. **Integrity validation.** `SaveBoundaryIntegrityValidator` runs the complete existing verification, exact
   link, parent, baseline, profile, provenance, and historical-evidence validation sequence. It may perform
   provider reads for persisted context and fails closed on invalid authority or identity.
5. **Lifecycle append.** `SaveBoundaryLifecycleAppender` appends integration events and webhook deliveries,
   then invokes `NotificationOutbox` to append notification deliveries. It performs no dispatch and no nested
   save. Existing tracked-event deduplication and payload/recipient rules remain in force.
6. **EF write.** The context calls `base.SaveChangesAsync` exactly once. `acceptAllChangesOnSuccess` retains
   EF's normal deferred-acceptance contract, and the context maps ladder conflicts before rethrowing other
   failures unchanged.

The extracted phases are ordered because later phases depend on earlier tracked-state decisions. A phase may
add or modify tracked entities and may read provider state where the existing behavior required it. Only the
last phase writes. No phase opens an ambient, serializable, or nested transaction.

## Transaction and failure semantics

When the caller has no explicit transaction, EF's provider transaction begins at the final base save. The
pre-save reads and tracked-state mutations therefore occur before that implicit write transaction. Callers that
need the preparation reads, validation, and final write to share one database transaction must begin an explicit
transaction around the whole `SaveChangesAsync` call. This preserves the prior behavior and avoids implying a
stronger isolation guarantee than EF provides.

When preparation or validation fails, no database write has been attempted, but tracked state can contain the
phase mutations. When the base save fails, the provider rolls back its write transaction while the context's
tracked state follows EF's normal failed-save behavior. Callers must inspect or discard that context before
starting a new logical unit of work; the save boundary does not globally clear unrelated pending changes. The
recoverable ladder-seal conflict is the exception: its existing conflict mapper clears the tracker so the caller
can retry against the winning seal.

`SaveChangesAsync(false)` preserves EF's deferred `AcceptAllChanges` behavior. The caller owns accepting or
discarding those tracked states before beginning another logical save. A retry that needs a fresh database view
must use a new context or explicitly reconcile the tracked entities; the boundary does not silently recreate
aggregates or rewrite controlled history.

## Performance boundary

Child-state existence resolution is batched by entity family and bounded chunks. It does not perform a
`GetDatabaseValuesAsync` query per modified row or rescan the aggregate graph for each child. The CQ07 benchmark
records command counts, allocations, and elapsed time for 1, 100, and 1,000 affected entities with representative
unrelated tracked records; the benchmark is evidence for this implementation and does not broaden a measured
local result into a production throughput claim.
