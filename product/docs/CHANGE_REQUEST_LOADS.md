# Change Request Load Contracts

`IChangeRequestRepository.GetAsync` accepts an explicit `ChangeRequestLoadShape`. The shape is part of the
caller contract: a route asks for the child graph its operation needs instead of making every read pay for every
controlled history collection.

`Detail` contains requirement changes, review cycles and steps, audit events, authored upstream links, and
authored upstream history. It matches the ordinary read-only change-request detail response. `ReviewDiscussion`
also loads requirement changes and review steps: requirement changes validate revision anchors, and steps enforce
the rule that a reviewer who is still deciding sees only their own comments. Comments remain separate from the
detail response because their visibility is viewer-dependent. `ReviewDecision` and `ReviewClosure` are complete
tracked graphs for approval, return, cancellation, deferral, withdrawal, and review restart; those operations may
publish draft comments and approval side effects need requirement/upstream children. `UpstreamLinks` is sufficient
for upstream-candidate selection. `RequirementChanges` supports the Jira description and the source side of
next-revision creation. `NextRevisionSource` names the combined source graph used by next-revision creation. The
flags remain composable for repository tests and future operation-specific contracts, but application callers use
the named shapes.
`None` loads only the aggregate scalar row and is sufficient for baseline selection/removal. The parameterless
overload remains a compatibility alias for `Complete`, which includes review comments for legacy callers; new
callers must choose a shape explicitly.

The repository adds the include graph only for requested flags. A shape with two or more independent collection
roots uses split queries to avoid multiplying requirement, review, audit, and upstream history rows. Split queries
are executed inside one consistent read transaction when the context does not already have a transaction:
PostgreSQL uses `RepeatableRead`, and SQLite uses its serializable transaction mode. An existing caller-owned
transaction with `RepeatableRead`, `Serializable`, or `Snapshot` isolation is reused and retains its lifetime. If
the existing transaction is weaker (for example PostgreSQL's default `ReadCommitted`), the repository falls back to
one SQL statement so the graph cannot be assembled from different committed states. A shape with zero or one
collection root remains a single statement and does not create an extra transaction.

This transaction covers the load only. A tracked command may mutate the returned aggregate and save through the
ordinary application unit of work after the read transaction has committed; optimistic version checks and the
save-boundary transaction retain the existing write conflict behavior. Callers that already own a transaction
must choose an isolation level that provides the snapshot guarantee they require before requesting a split shape.

The load contract preserves aggregate semantics. Requirement edits receive requirement children; review decisions
receive cycles and steps; upstream authoring receives active links and any requested history; comment operations
receive cycles and comments; and complete detail responses receive the complete detail graph. No caller may infer
missing children as an empty collection and then persist that partial graph.

CQ09 qualification measures both the legacy complete include shape and each scoped shape on SQLite and disposable
PostgreSQL. It records provider-delivered rows, SQL statements and parameters, returned JSON-column UTF-8 bytes,
allocation, and elapsed time. PostgreSQL `EXPLAIN (ANALYZE, BUFFERS)` separately records database scan work; the
interceptor's delivered-row count is not a plan-row count. Instrumentation overhead is measured independently.
