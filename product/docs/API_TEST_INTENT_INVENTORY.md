# API test intent inventory

Every test in `product/tests/AeroLink.Api.Tests` is classified by what it proves and by auditable
per-method host evidence. This is acceptance criterion 1 of #566. It is an observation of the current
tree, not proof that the #566 migration ceiling has been reached.

Regenerate with:

```bash
node product/test-contracts/tools/generate-test-intent.mjs
node product/test-contracts/tools/generate-host-classification.mjs
node --test product/test-contracts/tests/inventory.test.mjs
```

The generators split each `[Fact]`/`[Theory]` after its method signature, so braces in `[InlineData]`, raw
JSON, and expression-bodied methods cannot become the method body. The contract test recomputes both
artifacts from the current C# tree and fails on any row, total, or class-classification drift. Every row in
`api-test-intent.json` carries source lines, its case count, case source, host status, and the method-body
tokens that supplied host evidence.

## Result

<!-- BEGIN GENERATED API TEST INTENT SUMMARY -->
**844 test methods, 937 known invocations, 125 classes.** This source-exact forecast supports planning only;
it is not migration or rollout authority. All current theories use explicit `InlineData`,
so every case count is known.
The inventory does not infer host use from a whole class:
**806 methods / 885 cases have direct host evidence**,
**30 methods / 44 cases are explicitly non-hosted**, and
**8 methods / 8 cases remain unknown** because their class contains a host fixture or factory but
the method body does not show the host operation.

| Intent | Tests | Cases | Classes | Correct level |
|---|---:|---:|---:|---|
| HTTP boundary: route, status, JSON shape | 526 | 587 | 102 | API (must stay hosted) |
| EF translation / relational constraints | 164 | 178 | 58 | Infrastructure (needs a database, not a host) |
| Authentication / authorization wiring | 91 | 95 | 53 | API (must stay hosted) |
| In-process logic with no HTTP and no client | 35 | 35 | 12 | Domain or Infrastructure (migration candidate) |
| Filesystem / evidence-root behaviour | 19 | 19 | 10 | API or Infrastructure (must stay hosted) |
| Startup, hosting and configuration | 7 | 7 | 5 | API (must stay hosted) |
| Business-rule matrix over data variations | 2 | 16 | 2 | Domain (migration candidate) |

The machine-readable artifact records **16 explicitly hosted candidate methods / 16 cases** and **4 unknown candidate methods / 4 cases**.
The known hosted candidate share is **16 of 885 cases (1.8%)**,
but that is not a safe ceiling while unknown invocations remain. The static criterion-7 result is therefore
**unresolved**; it does not close #566 and does not justify closing #563.
<!-- END GENERATED API TEST INTENT SUMMARY -->

The unknown rows are intentionally visible rather than silently placed in the denominator:

- `ApiTestTelemetryTests.Reset_for_test_clears_telemetry_state` is in-process, while its class also creates
  factories for other tests.
- `SecurityBoundaryTests.File_backed_sqlite_contention_uses_the_provider_lock_retry_budget_without_a_custom_busy_handler`
  is filesystem/provider logic, while its class also contains hosted tests.
- `ServerAuthorityContractTests.Authenticated_browser_contracts_expose_no_caller_selectable_identity`
  and `Standard_diagnostics_contains_no_human_login_or_committed_password` are in-process, while the class
  also contains hosted tests.

Resolve these rows with a reviewed fixture/helper map before using the inventory to assert a migration
ceiling. A mixed class must not make a non-hosted method look hosted merely because another method creates a
factory.

## How intent and host evidence are assigned

Each test gets exactly one primary intent, resolved in a fixed order:

1. **auth-policy** — asserts 401/403, role behaviour, or anonymous access.
2. **filesystem** — touches an evidence root, real paths, or file contents.
3. **startup-config** — exercises hosting, configuration, or static-file wiring.
4. **ef-translation** — depends on EF query translation or a relational constraint.
5. **http-boundary** — asserts a status code, route, or JSON shape.
6. **rule-matrix** — two or more `InlineData` cases and none of the above.

Anything unmatched is in-process logic if it never names a client, and HTTP boundary if it does — the
latter because a test can reach HTTP through a same-class helper without naming a verb itself.

Host status is computed per method body:

- `hosted` requires direct evidence such as `new AeroLinkApiFactory`, `CreateClient`, `CreateFactory`,
  factory services, a shared-host fixture, or `WithWebHostBuilder`.
- `not-hosted` means neither the method nor its class has a host context.
- `unknown` means the class has a host context but the method body has no direct host evidence. Unknown is
  excluded from closure arithmetic until reviewed.

## What this means for #566

The issue asks for hosted **invocations**, not merely methods. The current tree has a small explicitly
hosted candidate set, but two candidate invocations have unknown host status. Therefore the completed
inventory does **not** yet demonstrate the criterion-7 escape clause.

The intent categories still explain why the potential migration set is small: HTTP boundary, EF
translation, auth/policy, filesystem, and startup/configuration tests remain at the appropriate hosted or
infrastructure level. Moving the confirmed in-process candidates remains worthwhile for placement, but no
performance closure claim is made here.

## Limits

- Static host evidence cannot prove runtime-dispatched helpers or fixture setup hidden outside the method.
  Those cases are `unknown`, not silently counted as hosted or non-hosted.
- The inventory records explicit `InlineData` counts. A future theory using `MemberData` or `ClassData`
  will be marked with unknown case count and will block any ceiling claim until its invocations are supplied.
- Intent is inferred from what a test does, not from its name. A test asserting a status code while proving a
  business rule is counted as HTTP-boundary.
- The counts move when tests are added. Re-derive the ratios from the generated artifact after every test-tree
  change.
