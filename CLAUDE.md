# Working in this repository

AeroLink is an aerospace requirements-management and development-assurance product: controlled
requirements, change requests under review and signature, baselines that freeze what a build
contains, verification procedures and evidence, and generated controlled documents. Its purpose is a
**defensible record**, and that shapes almost every design decision — a screen that asserts
something untrue about a controlled artifact is worse than a missing feature.

This file is working knowledge that is expensive to rediscover. It is not a style guide.

## More than one agent works in this checkout

Claude and Codex share this clone, and a `git checkout` is repo-wide: whoever moves it last wins.

This has bitten. On 2026-08-20 the reflog recorded `checkout: moving from
claude/694-orphaned-by-reopen to main` mid-task, which nothing in the running session had issued.
The danger is not a crash — it is a **false pass**: a Playwright run builds and serves from whatever
is on disk, so after a silent switch it tests the other branch and goes green, proving nothing about
the code under test.

- Prefer `git worktree add` to sharing this checkout for anything long-running.
- Check `git rev-parse --abbrev-ref HEAD` immediately before *and after* any long test run, and
  treat a green result from a run that spanned a switch as void.
- Compiled output is unaffected by a source-tree switch, so a `dotnet test --no-build` result stays
  valid — and the test *count* is good evidence of which code actually ran.
- Never leave untracked files loose in the tree; another agent's `git add -A` will sweep them up.

## Local environment

- **PostgreSQL runs on port 54329**, not 5432. It is the persistent demo database — do not reset it.
- **Line endings are LF everywhere.** `.gitattributes` sets `* text=auto eol=lf` (`.bat`/`.cmd`/`.ps1`
  excepted). EF tooling still writes CRLF and a BOM into migrations and the model snapshot —
  normalise those to LF before committing.
- **A running `AeroLink.Api` locks the Release build outputs.** Before blaming a build failure
  (`MSB3027`, "file is locked by"), check for a process on port 5080.
- The app runs on **5080** (API) and **5173** (client). Playwright uses **5082** and **5174**, so
  browser journeys and a running app coexist — but Playwright rebuilds the API by default and will
  collide with a running instance. `AEROLINK_E2E_SKIP_BUILD=true` avoids the rebuild.

## Migrations

- **`dotnet ef` fails closed by design.** `AEROLINK_MIGRATIONS_CONNECTION` must be set, and the
  factory never falls back to the persistent database. Point it at a throwaway name, never
  `aerolink`. See `product/docs/OPERATIONS.md`.
- `dotnet ef migrations list --no-connect` validates the chain without touching any database.
- **Two branches cut from the same base that each add a migration** need care: the one that lands
  second sorts earlier by timestamp while applying later, and its `Designer.cs` snapshot never saw
  the other's model. Check whether the two touch overlapping tables. If they do, delete and
  regenerate the second migration after rebasing. A clean *textual* merge of
  `AeroLinkDbContextModelSnapshot.cs` proves nothing.
- PostgreSQL has no assignment cast from `integer` to `varchar`; a scaffolded `AlterColumn` between
  them is refused outright. Write the conversion by hand with an explicit `USING`.

## Tests and CI

- **`dotnet test` does not build `product/tools` or `product/test-planner`.** Build every `.csproj`
  before claiming a sweep is done.
- **Adding or removing an API test changes three pinned manifests.** Regenerate all of them and
  re-derive the pinned totals in the same commit, or a Domain/Infrastructure job fails on what looks
  like an API-only change:
  ```
  node product/test-contracts/tools/generate-test-intent.mjs
  node product/test-contracts/tools/generate-route-manifest.mjs
  node product/test-contracts/tools/generate-host-classification.mjs
  node --test product/test-contracts/tests/*.test.mjs
  ```
  Never hand-merge the generated JSON on a rebase — regenerate it. Re-running the generators should
  then produce **no diff**; that is the check that the committed artifacts are what the tooling emits.
- Route coverage is measured against the #588 baseline. A new route must arrive covered — the
  generator reports `outside the baseline`, which must stay at 0.
- API tests run on **SQLite with `EnsureCreated`**, so migrations are not exercised by them. Raw
  PostgreSQL SQL in a migration is therefore invisible to the test suite.
- Playwright positional arguments are **regexes against the spec path**, not filenames.
- Full CI is driven by the `ready-for-full-ci` label. Verify the Product quality gate concluded
  `success` **at the exact head being merged**, not at an earlier commit.

## API and domain facts that are easy to get wrong

- `POST /api/change-requests` does **not** carry requirement changes. Use
  `POST /api/change-request-drafts` with `requirementChanges`.
- A build carries **one** candidate baseline; a second is refused.
- A successor release cannot be created while the current one is in work.
- A System `Introduce` needs a `targetSectionId` before it can be submitted for review.
- Test procedures are numbered for their level — `SYSTP-` / `HLRTP-` / `LLRTP-` — and
  `TestProcedure` defaults to `HighLevel`. `AeroLinkDbContext` refuses cross-level coverage at
  `SaveChanges`, but only for links whose procedure revision is already persisted.
- Controlled identifiers are allocated from a **monotonic counter per prefix, repository-wide across
  every project**. Gaps are expected and deliberate; a claimed number is burned even if the create
  rolls back.
- Requirement revisions are created when a baseline is **frozen and materialized**, not when a change
  request is approved. Much of the change-control design follows from this.

## Verifying claims

The repository rewards checking over remembering, and several defects this year came from not:

- **Verify merges against GitHub**, not against notes — `gh pr view`, `gh run list`.
- **Intermittent CI failures have twice been real product defects**, not runner flakiness.
- **A passing test suite is not a compiling solution** — see the `dotnet test` note above.
- When changing one thing across several look-alike sites, **verify each site** rather than trusting
  the pattern.

## Writing style in this codebase

Comments and commit messages explain **why**, in prose, at the point where the reasoning is not
obvious from the code — including what was rejected and what would break. Follow the surrounding
density rather than adding narration to code that does not have it. Domain types carry substantial
XML doc comments explaining the rule they enforce and the alternative that was not chosen.
