# Test risk layers

AeroLink places a test at the **lowest layer that can still catch the class of defect the test exists to catch**. Faster is useful only when the lower layer preserves the same defect sensitivity; moving a test downward merely to avoid a host is not an optimization if it stops proving a database, HTTP, security, filesystem, or rendering boundary.

This policy complements the source-derived API inventories in `product/test-contracts/` and the route-coverage guard. It does not replace either one.

## Domain tests

Use Domain tests for deterministic aggregate and value-object rules that require no ASP.NET host, database provider, filesystem, or authenticated request context. Examples include legal and illegal state transitions, revision progression, deterministic calculations, effectivity boundaries, and role/capability decisions after the actor and project context have already been supplied.

When a hosted rule matrix moves here, add the Domain coverage **before** removing the hosted cases and prove defect sensitivity with a deliberate mutation of the migrated rule. A browser journey touching the same behavior is not a reason to delete Domain coverage.

## Application/service tests

Use a fast application/service layer when the decision genuinely belongs between transport and persistence: explicit authenticated context and command inputs go in; a stable decision/result and intended side effects come out. Do not create pass-through wrappers just to manufacture a lower test layer.

If business decisions are embedded directly in an endpoint, extract the real decision first. Only then may its combinatorial matrix move below HTTP. Keep a bounded hosted API case proving the endpoint supplies the authenticated context and maps the result correctly.

## Infrastructure tests

Use Infrastructure tests when the risk is the persistence or operating-system implementation rather than HTTP: EF Core translation, unique indexes, foreign keys, concurrency tokens, provider-specific transactions, database-backed identifier allocation, and evidence/file persistence or integrity.

These tests may require a disposable database or disposable filesystem root. They do **not** justify starting the full ASP.NET host unless hosting is itself part of the contract. Persistent developer PostgreSQL under `product/.local` and persistent evidence state are never test inputs.

Mark a PostgreSQL qualification test `[DisposablePostgresFact]` (`AeroLink.Infrastructure.Tests/TestSupport`). With no `AEROLINK_MIGRATIONS_CONNECTION` it reports Skipped; with `AEROLINK_REQUIRE_POSTGRES_QUALIFICATION` set, a missing connection fails. Do not add a private copy of that attribute, and do not return early from the test body.

## Hosted API tests

Keep a test hosted when the public boundary is part of what it proves: route/method registration, binding and validation, request/response JSON shape, stable status/error mapping, authentication, authorization/policy wiring, cookie/session behavior, startup/configuration, cross-component transaction boundaries, or an intentionally representative end-to-end lifecycle.

EF/provider checks also remain integration-level when the test is specifically proving concurrency or persisted authority across independent contexts. A direct service call is not equivalent evidence for an authenticated HTTP request, and an in-memory substitute is not evidence for relational behavior.

Sign a seeded member in with `MemberSession` (`AeroLink.Api.Tests/TestSupport`). `SignInAsync` also attaches the CSRF token a browser sends with mutations; `SignInForReadsAsync` does not. The API demands that token only from requests carrying a browser `Origin` or `Sec-Fetch-Site` header, so a test proving CSRF refusal must send one. Do not add another private copy.

For a mutating endpoint family, retain enough hosted coverage to prove the public operation exists, authentication and one unauthorized path are enforced, a valid request persists the expected state, a representative domain error maps to its stable contract, and stale/concurrent intent is rejected where relevant. The route/contract manifest is the automated floor; it is not a quota for test count.

## Production-browser journeys

Production-browser journeys prove that the built client and built server work together as shipped: production asset routing, client-to-API integration, and a bounded set of critical user workflows. They should cover integration failures that a component test cannot see, not repeat every state or validation permutation already proven below the browser.

Passing a browser journey does not make a route, policy, persistence, or domain test redundant. Each test is dispositioned by the risk it proves.

## Full-browser journeys

Full-browser journeys are the broad diagnostic safety net for cross-surface regressions and longer lifecycle combinations. They are intentionally more expensive than the fast developer loop and production-browser smoke. Use them when the interaction among multiple UI/workflow surfaces is the risk, not as the default home for business-rule enumeration.

## Intentional duplication

Some risks deserve evidence at more than one layer. Security-critical, destructive, audit, release, backup/restore, and exact-intent behavior may keep fast rule coverage **and** a smaller hosted or browser proof. The duplication must have a written reason: each layer should be able to name a different failure mode it catches.

Typical examples:

- Domain rule + hosted API mapping for a release approval predicate.
- Domain state transition + EF concurrency test for stale writes.
- File-integrity implementation test + API authorization test for evidence download.
- Fast release-package calculation test + bounded hosted exact-intent/signature test.

## Authoring gate

Every test costs review time, CI time on every affected pull request, and maintenance on every refactor of the code it touches. Before adding or materially changing a test, answer all four questions. A missing answer means the test is not ready to add.

1. What observable behavior, invariant, or independent contract does it protect?
2. What credible regression makes it fail?
3. Why does existing coverage not already catch that failure? Each contract has one primary owner test at the strongest layer that can see the defect (see the layers above). Another layer needs its own written failure mode, as in *Intentional duplication*. Prefer a new row in an existing `[Theory]`, table, or shared fixture over a near-copy of an existing test.
4. Does it need a production seam (an export, `internal` member, flag, wrapper, reflection hook, or "ForTests" overload) that no production caller needs? If so, test through the real boundary instead.

A regression test for a bug must fail on the pre-fix code for the intended reason and pass after the repair. A regression test that never demonstrably failed proves the fixture, not the fix. One regression at the owning layer covers the bug; do not replay the same scenario at every layer it crosses.

A test that would break under a behavior-preserving refactor is asserting implementation rather than behavior. Rewrite it at the owning boundary before it lands.

## Low-value test patterns

Check a new test against this list, and use the list when auditing existing tests. A match fails the authoring gate unless the retention bar below names the contract the test independently guards.

- No assertion, or only an assertion that cannot fail once the code runs (a non-null result, or a 200 where a stronger test already checks the body).
- Expected values produced by the helper, formatter, comparator, or hash function under test, or a value compared with itself where repeatability is not the contract.
- Copied inventories that restate a source list, enum, constant, or manifest, and so change in step with it rather than catching a drift.
- Exact source, import, or string greps where executing the owning script, dry run, or endpoint is feasible.
- Tests of a private predicate or call shape that a test at the real boundary already proves.
- Duplicate invocations of the same contract, including a test that equals one iteration of another test.
- A mock or fixture that implements the behavior being asserted, or supplies the ordering, receipt, or state the owner should produce.
- Negative controls that pass for an unrelated reason. Examples: a "no access" request that is refused because the record does not exist in that host's database, a skipped-environment test that returns early and reports Passed, or a rejection the production path never reaches.
- Names or comments that promise more than the assertions check.
- Tests whose only purpose is keeping a test-only export, wrapper, or dead production path alive.

## Retention bar

Keep a test when it independently enforces a controlled-history, revision-identity, effectivity, authorization, signature/hash/manifest, migration, storage, route/API, security, launcher-path, or generated-contract rule, even when it looks repetitive. Also keep:

- ordering assertions when the order is observable behavior;
- regressions with a credible failure mode;
- source inspection when it is the cheapest independent guard of a user-facing path, key, or byte (for example the root launcher contracts) and survives an identifier-only refactor;
- a retained test that fails on the baseline. Treat it as a possible product defect: reproduce it and repair the owner rather than deleting the test.

Slowness alone is not a reason to delete coverage; it is a reason to move or share the expensive setup. A test that resembles implementation may still be the only independent proof of a contract; show otherwise before removing it.

## Placement review checklist

Before moving or deleting a test, answer all of the following:

1. What defect class does the current test catch?
2. Does the proposed lower layer exercise the production code that owns that defect?
3. Does the test depend on HTTP, authentication/policy wiring, EF translation/constraints/concurrency, filesystem behavior, startup, rendering, or a transaction boundary?
4. If a rule matrix moves, is equivalent-or-stronger lower-layer coverage green first, and does a deliberate mutation fail it?
5. What representative hosted case remains for the public operation?
6. Does the route/contract guard remain green?
7. Is any retained duplication intentional and documented by risk?

If the answer to #2 is no, or #3 is yes for the risk being asserted, the test stays at the integration layer.

When a test is deleted rather than moved, also record in the commit or pull request: the test and its location, the failure it could actually detect, the stronger test that still owns the contract (or why no contract exists), and any test-only production seam the deletion lets you remove. A candidate missing any of these is not ready to delete. Deleting an API test changes the generated inventories in `product/test-contracts/`; regenerate them from source as described in `API_TEST_INTENT_INVENTORY.md` and never hand-merge them.

## Evidence and measurement

`product/test-contracts/api-test-intent.json` and `api-host-classification.json` are the source-derived placement inventories. `product/docs/API_TEST_INTENT_INVENTORY.md` explains how they are generated.

Placement changes are not declared successful from counts alone. Closeout evidence must preserve route/security/persistence behavior and, where performance is claimed, measure Windows wall clock, CPU, and host-start behavior rather than extrapolating from theoretical factory counts.
