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

## Hosted API tests

Keep a test hosted when the public boundary is part of what it proves: route/method registration, binding and validation, request/response JSON shape, stable status/error mapping, authentication, authorization/policy wiring, cookie/session behavior, startup/configuration, cross-component transaction boundaries, or an intentionally representative end-to-end lifecycle.

EF/provider checks also remain integration-level when the test is specifically proving concurrency or persisted authority across independent contexts. A direct service call is not equivalent evidence for an authenticated HTTP request, and an in-memory substitute is not evidence for relational behavior.

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

## Evidence and measurement

`product/test-contracts/api-test-intent.json` and `api-host-classification.json` are the source-derived placement inventories. `product/docs/API_TEST_INTENT_INVENTORY.md` explains how they are generated.

Placement changes are not declared successful from counts alone. Closeout evidence must preserve route/security/persistence behavior and, where performance is claimed, measure Windows wall clock, CPU, and host-start behavior rather than extrapolating from theoretical factory counts.
