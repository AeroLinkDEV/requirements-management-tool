# API test intent inventory

Every hosted API test, classified by what it proves and therefore by the level it belongs at. This is
acceptance criterion 1 of #566, and the evidence for criterion 7's ceiling.

Regenerate with:

```bash
node product/test-contracts/tools/generate-test-intent.mjs
```

The generator reads `product/tests/AeroLink.Api.Tests`, splits each `[Fact]`/`[Theory]` by brace depth,
and classifies from the test body rather than the name. Output is deterministic — re-running produces a
byte-identical `product/test-contracts/api-test-intent.json`.

## Result

**442 test methods, 492 cases, 81 classes.** Of these, **434 actually build a host**; 8 never do and are excluded from the ceiling below, because #566 asks about *hosted* invocations.

| Intent | Tests | Cases | Classes | Correct level |
|---|---:|---:|---:|---|
| HTTP boundary: route, status, JSON shape | 292 | 319 | 67 | API — must stay hosted |
| EF translation / relational constraints | 74 | 77 | 31 | Infrastructure — needs a database, not a host |
| Authentication / authorization wiring | 52 | 55 | 33 | API — must stay hosted |
| Filesystem / evidence-root behaviour | 9 | 9 | 5 | API or Infrastructure — must stay hosted |
| **In-process logic, no HTTP and no client** | **8** | **8** | **7** | **Domain or Infrastructure — candidate** |
| Startup, hosting and configuration | 5 | 5 | 4 | API — must stay hosted |
| **Business-rule matrix over data variations** | **2** | **12** | **2** | **Domain — candidate** |

**Migration candidates: 8 of 434 hosted test methods (1.8%).**

## How intent is assigned

Each test gets exactly one primary intent, resolved in a fixed order. The order encodes what a test is
really *for* when it does several things at once:

1. **auth-policy** — asserts 401/403, role behaviour, or anonymous access.
2. **filesystem** — touches an evidence root, real paths, or file contents.
3. **startup-config** — exercises hosting, configuration or static-file wiring.
4. **ef-translation** — depends on how EF renders a query, or on a relational constraint.
5. **http-boundary** — asserts a status code, route, or JSON shape.
6. **rule-matrix** — two or more `InlineData` cases and none of the above.

Anything unmatched is in-process logic if it never names a client, and HTTP boundary if it does — the
latter because a test can reach HTTP through a same-class helper without naming a verb itself.

The ordering matters and it is why this inventory is stricter than a structural count. A test with
`InlineData` that also crosses HTTP is an HTTP-boundary test whose data variation is incidental, not a
rule matrix that happens to use a host. An earlier structural pass over the same suite suggested roughly
11% were migratable; classifying by intent rather than by shape gives **1.8%**.

Two figures in an earlier revision were wrong and are corrected here.

The parser took the first `{` after the attribute rather than after the method signature. Whenever an
attribute argument contains a brace — which `[InlineData("{}", …)]` and raw JSON string literals do
routinely — the "body" it extracted was the attribute's own braces. Two tests were misclassified into
the migration bucket as a result, one of them an HTTP-boundary test that creates a client and posts to
a route. Both showed up in the generated artifact with no method name at all, which was a signal in my
own output that I did not read.

And the denominator counted every xUnit method in the project, including the 8 that never build a host.
#566 asks about *hosted* invocations, so those inflate the total and understate the ceiling.

## What this means for #566

Criterion 7 asks for at least 20% of hosted test invocations to be removed or consolidated, *"unless the
completed inventory demonstrates a smaller safe ceiling with evidence."*

The inventory demonstrates exactly that. **The safe ceiling is 1.8%**, and the reason is not that the
tests are poorly written:

- **66% are HTTP-boundary tests.** Criterion 8 requires public route, status, JSON, authorization, audit
  and persistence behaviour to remain unchanged, and criterion 2 requires every mutating route to keep
  explicit hosted boundary coverage. These tests *are* that coverage.
- **17% are EF-translation tests.** Criterion 6 names these explicitly as tests that must remain at the
  appropriate integration level. Translation is not portable; the SQLite path will accept expressions
  Npgsql cannot produce.
- **12% are authentication and authorization wiring**, also named in criterion 6.

That leaves 10 tests. Eight are in-process logic that never opens a client — `IdentifierAllocationTests`
is the clearest case, constructing a `WebApplicationFactory` purely to obtain a `DbContext` and then
calling a static allocator directly. Two are genuine rule matrices.

Moving those 10 is worth doing on placement grounds: a test that needs a database and not a host belongs
in the infrastructure suite. It is not worth doing for speed. At the measured median host cost of 665 ms
it saves roughly **8 seconds** at the measured mean host cost of 959 ms.

## Where the time actually goes

Measured from #563 phase-1 telemetry across eight runs: **433 host constructions for roughly 442 hosted
tests** — very close to one host per test — at a median of 665 ms and a p95 of 3,581 ms, totalling
503.8 s summed across the three API shards in a single run.

The cost is not the number of tests. It is that each one builds a host. Reducing the count by 1.8%
changes almost nothing; making hosts reusable changes the multiplier for all 442. That is #563's thesis,
and this inventory is the evidence that it is the only lever of the two that matters.

## Limits of this inventory

- Static analysis cannot see runtime-dispatched helpers, so a test reaching HTTP through an unusual
  indirection could be misclassified. The classification is deliberately conservative in the direction
  of "must stay hosted", so the 1.8% is a floor on what must remain, not a ceiling on what could move.
- Intent is inferred from what a test *does*, not from what its author meant. A test asserting a status
  code while really proving a business rule is counted as HTTP-boundary.
- The counts move when tests are added. The figure to re-derive is the ratio, not the absolute number.
