# AeroLink engineering lessons

These are durable lessons that have already cost time, defects, or false confidence to learn. They are not style preferences and they are not a substitute for the current code/decision record. They exist so the next person or agent does not have to relearn the same thing.

## 1. A green test is only evidence when you know exactly what SHA it tested

A long-running test can go green after the checkout, branch, or binaries changed underneath it. That is especially dangerous with multiple agents sharing a clone.

**Practice:** use isolated worktrees; record `git rev-parse HEAD` and dirty status before and after long runs; treat a run spanning an unexpected checkout/SHA change as void.

A `--no-build` run is only evidence for the binaries already present. If their source SHA is unknown, the result is not provenance.

## 2. Intermittent failures are product evidence until they are dispositioned

A test that passes on retry may be flaky, but “flaky” is a conclusion, not a synonym for “failed once”. AeroLink has repeatedly found real product defects while investigating failures that initially looked intermittent.

**Practice:** retain diagnostics, reproduce on the exact SHA, isolate the failure, and only then classify it. CI retries can reduce wasted gate reruns, but they must still report the initial failure as flaky/evidence rather than hiding it.

## 3. Never use the persistent demonstration database as disposable qualification state

The local PostgreSQL database on port 54329 carries real persistent AeroLink developer/demo state. Destroying or reseeding it to prove a change is convenient but invalidates the very continuity the tool is intended to demonstrate.

**Practice:** use SQLite fixtures, owned disposable PostgreSQL databases/containers, and isolated evidence directories for destructive tests. Migration tooling must fail closed rather than silently target the persistent database.

See [`../product/docs/OPERATIONS.md`](../product/docs/OPERATIONS.md).

## 4. Build/revision effectivity needs one authority

A recurring class of defects comes from one screen asking “what belongs to this build?” differently from another screen, API, readiness gate, or document generator.

**Practice:** use the existing baseline/manifest/effectivity authority for exact carried revisions. Do not infer build membership from “latest revision”, a project-wide inventory, or browser-side filters.

This became especially important when software Procedures moved from dormant/project inventory to first-class build-scoped executable artifacts in the Case → Procedure programme (#720–#728, #762).

## 5. Historical signatures, hashes, manifests, and identifiers are not presentation data

A new label or cleaner UI does not justify changing the identity/evidence of what was historically reviewed, approved, generated, or released.

**Practice:** preserve historical exact references and render compatibility labels where needed. If a controlled identifier really must change, treat it as a governed migration with explicit historical semantics.

One concrete example: `HLRTD`/`LLRTD` were deliberately retained as controlled Case-document identities rather than cosmetically renamed during #762.

## 6. “Approved” and “included in this build/release” are different facts

Approval is engineering/governance acceptance. Inclusion is configuration/release scope. Conflating them creates bad audit history and bad readiness behavior.

**Practice:** preserve the separation between controlled approval state and build/baseline selection/effectivity. Deferred/retargeted work must not rewrite the fact that it was previously approved.

## 7. Server paging must really page the combined server source

Fetching page 1 of list A and page 1 of list B, concatenating them in React, and calling that a combined paged list is wrong even if the first screen looks plausible.

It breaks totals, ordering, page boundaries, search, saved views, and later pages.

**Practice:** when a UI presents one bounded inventory, implement one authoritative server-side query over the combined source. #762 made this explicit for Case + Procedure Explorer results.

## 8. Shared lifecycle concepts should share implementation, not just appearance

Case and Procedure, HLR and LLR, and similar controlled-artifact families often differ by a typed key rather than by an entirely different workflow.

**Practice:** parameterize shared Explorer/register/inspector/change-control machinery where the domain semantics are shared. Copying a look-alike page creates two future sources of truth.

Only split implementation where the engineering semantics genuinely differ.

## 9. Generated test contracts are regenerated, never hand-merged

The API/test contract manifests and pinned totals are generated evidence. Hand-editing them during a rebase can produce a green-looking artifact that no generator would create.

**Practice:** regenerate from source, update pinned totals from the generator output, run the contract tests, then run the generators again and require a clean diff.

## 10. `dotnet test` is not the same claim as “the solution compiles”

A test project builds its dependency graph, not every tool/project in the repository.

**Practice:** keep an explicit compile/build proof for the full required solution/project set. Do not infer compile coverage from a passing subset of test projects.

## 11. SQLite API tests do not qualify PostgreSQL migrations

Many API tests use SQLite with `EnsureCreated`. They can prove API/domain behavior while never executing the PostgreSQL migration path or provider-specific SQL.

**Practice:** raw SQL migrations, type conversions, bootstrap behavior, and provider-sensitive queries require disposable PostgreSQL qualification.

## 12. CI optimization must follow the measured critical path

More shards are not automatically faster. Splitting the wrong job can add runner/setup cost while the actual bottleneck remains unchanged.

AeroLink's CI work demonstrated this directly: browser sharding was not useful while the API/validate job dominated; after API sharding moved the bottleneck, additional browser sharding became useful. Later, duration-based packing beat count-based symmetry.

**Practice:** measure runner wall clock, setup cost, and the current slowest lane before changing CI topology.

See [`../product/docs/BROWSER_AND_BACKEND_FEEDBACK_TIME.md`](../product/docs/BROWSER_AND_BACKEND_FEEDBACK_TIME.md).

## 13. Exact controlled relationships should not be silently remapped

When an author selected an exact controlled revision and the world changes underneath that selection, silently pointing the operation at a newer revision creates an action the user never reviewed.

**Practice:** fail visibly as stale/conflict, preserve authored work where safe, clear only the invalid target-dependent state, and require explicit reselection.

## 14. Search/display should use the same exact revision authority

A search result is misleading if the title searched came from one revision while the title displayed came from another.

**Practice:** exact-revision lists, search, deep links, and generated evidence should project from the same authoritative revision source wherever they claim to describe the same controlled artifact.

## 15. A migration must not fabricate historical precision

Legacy data sometimes predates today's exact manifests/fields. Backfilling a value does not mean a human historically selected or approved that exact value.

**Practice:** record migration provenance explicitly when a value is derived; establish attributable compatibility snapshots rather than pretending to reconstruct history that never existed.

## 16. Accessibility failures often reveal real semantic ambiguity

Ambiguous labels/headings are not merely test-locator annoyances. If `getByRole` cannot distinguish two headings or a `<select>` announces option text as part of its name, a screen-reader user may face the same ambiguity.

**Practice:** fix the semantic/accessibility contract where appropriate; only tighten a test locator when the UI semantics are already correct.

## 17. Rich authored content must remain safe and reproducible

AeroLink deliberately stores structured rich content rather than arbitrary executable markup. Content written by one engineer is later read by approvers and reproduced in controlled outputs.

**Practice:** preserve a closed structured model, plain readable projections, safe rendering, deterministic canonicalization, and explicit publication behavior. Do not trade signature safety or reproducibility for convenient HTML injection.

## 18. Root Windows launchers are compatibility surfaces, not clutter

A tiny `.bat` file may only delegate to `product/scripts`, but its exact root path can be embedded in Task Scheduler, a desktop shortcut, a recovery task, or another machine.

**Practice:** do not move root launchers for cosmetic cleanup until external dependencies are audited. Keeping a few stable Windows “buttons” at root can be the safer architecture.

## 19. Historical records should stay historical

Dated handoffs and audit reports are valuable evidence of what was believed and delivered at a checkpoint. Problems arise when they remain positioned as current operating authority months or architecture changes later.

**Practice:** current truth goes in `PROJECT_STATE.md`; live work goes in GitHub Issues; accepted decisions go in the decision log; durable lessons go here; old handoffs/audits belong in the indexed archive.

## 20. Preserve truth first, then optimize convenience

This is the common thread behind most of the lessons above. AeroLink exists to make controlled engineering state explainable later. Convenience improvements are worthwhile only when they do not create a second authority, erase attribution, weaken exact identity, or make the UI say something the controlled record does not actually support.
