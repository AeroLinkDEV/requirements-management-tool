# Current product and repository handoff - 2026-08-06

This is the current restart point for AeroLink. It supersedes the 5 August handoff, which remains a
historical delivery record. [PROJECT_STATE.md](PROJECT_STATE.md) is the canonical product description and
[FEATURE_CATALOG.md](FEATURE_CATALOG.md) is the stable capability inventory.

## Repository checkpoint

- Repository: `seanmccarthyns/requirements-management-tool`
- Delivery rule unchanged: focused `codex/*` branch, pull request, required Product Quality Gate, squash
  merge. Never push implementation directly to `main`.
- `main` at `0615c5d`, in sync with origin, working tree clean.
- **One open pull request, and it is not this session's work:** #355, the controlled Word Documentation
  Center, is being carried by Codex.
- Backend suite: **682 passed, 0 failed** — 271 domain, 187 infrastructure, 224 API, measured against merged
  `main` rather than a branch. Full browser journey suite: **3 shards passed**. Production-build journeys:
  **10 passed**.
- The persistent PostgreSQL database remains the sole real-life database. Nothing this session reset,
  reseeded or destructively migrated it. Four migrations were added, all additive, and all applied:
  `20260806002614_AddTestProcedureChanges`, `20260806020541_AddTestProcedureBaseline`,
  `20260806021511_AddTestProcedureChangeTitle` and `20260806024333_KeyTestChangeRequestExclusivityByRevision`.
- **The demo database is one migration ahead of `main`.** `20260806032704_AddManagedDocumentationCenter`
  belongs to the unmerged #355 and has already been applied. Harmless at startup — EF ignores an applied
  migration it cannot find — but if #355 changes shape before merging, that table is orphaned and the
  migration identifier will never be reproducible on a fresh database.

## What was delivered

One body of work, in seven pull requests: **the test change request build-out**. The product could raise and
approve a test assessment but had nowhere to say what test work it actually proposed. That is now a complete
chain, and the testing pages are the requirements pages rather than resembling them.

### Test procedures handled exactly as requirements are (#350, #351, #353, DEC-097)

The mirror is literal, not thematic:

| Requirements | Test procedures |
| --- | --- |
| `RequirementRevision.SourceChangeRequestId` | `TestProcedureRevision.SourceTestChangeRequestId` |
| `RequirementRevision.EffectiveBaselineId` | `TestProcedureRevision.EffectiveBaselineId` |
| `BaselineRequirementSelection` | `BaselineTestProcedureSelection` |
| `BaselineChangeRequestSelection` | `BaselineTestChangeRequestSelection` |
| `CandidateBaseline.RequirementsHash` | `CandidateBaseline.TestProceduresHash` |
| `RequirementBaselineMaterializer` | `TestProcedureBaselineMaterializer` |

Two places the mirror is deliberately inexact, both about sequencing: a package may be selected into a
baseline **after** the freeze, because procedures are written against requirements the freeze has already
fixed; and `MarkReleased` does **not** require a procedure manifest, because every build released so far has
none and gating would make them retrospectively invalid.

### A procedure covers requirements at its own level (#356, DEC-098)

A coverage link crossing a level is refused, and a procedure's number must agree with its level. This is the
root cause of a System change request raising work in the HLR queue, removed rather than routed around: a
retirement can now only strand procedures of its own level, so the mis-disciplined record is not
constructible. Verified against live data first — **0 of 1,251** links crossed a level, **0 of 516**
procedures disagreed with their prefix.

### The testing surface is the requirements surface (#357, #359, DEC-099)

A test assessment row is now the requirements card with one `Open assessment` control in every state. That
drawer holds the conclusion, the actions that used to sit on the row, and the per-requirement decisions. The
SYSTCR opens in its **own** workspace, where procedures are introduced, modified and retired — two drawers,
because a package is a record of its own.

### Approved procedure work reaches a build (#358)

`GET`/`POST`/`DELETE /api/baselines/{id}/test-change-requests` and
`POST /api/baselines/{id}/materialize-test-procedures`, with a **Materialize test procedures** control that
appears once the SWRD is materialized.

## Three defects worth knowing about

- **A revision could not be stored.** `StartNextRevision` shipped in #350 against a unique index of
  `(ChangeRequestId, Discipline)`, so the successor collided with its predecessor and returned 500 the first
  time anything tried to persist it. The domain test passed because it never touched a database. Fixed in
  #353 by putting `Revision` in the key, as the change request's own key already does.
- **The materializer had no caller.** Written, tested, registered in DI and merged across two pull requests
  with no endpoint invoking it — every gate green throughout. See
  [LES-006](DECISIONS_AND_OPEN_QUESTIONS.md#les-006---a-capability-with-no-caller-is-not-delivered).
- **A dialog taller than the viewport did not scroll**, so the procedure form could be filled but never
  submitted. Only running the real screen found it.

## What is open

- **DEC-099 aside, one design question is unanswered:** who owns a folded-in change request claim across a
  test change request revision. A change request belongs to at most one package, so a successor cannot hold
  what its predecessor still holds. `StartNextRevision` refuses with that reason rather than guessing;
  silently dropping the claims would make the new revision cover less than the old one without saying so.
- **Issue #332 Phase 3** — the DOORS/ReqIF parser — still waits on a real extract.
- **A browser-journey flake is unexplained.** One early test occasionally hangs the full 120 seconds at first
  app render, always on a Vite dev-server shard and never on the production-build shard. Both target specs
  pass locally in seconds. `global-setup.ts` seeds the API but never loads the page, so the first test to
  sign in pays the whole cold module-graph compile inside its own budget. That is a hypothesis, not a
  finding, and no fix has been attempted on the strength of it.
- **No build carries a procedure manifest.** The mechanism is complete and reachable; every existing build
  predates it, so `baseline_test_procedures` is empty in the demonstration data.

## How to pick this up

Start the product with `START_AEROLINK_PRODUCTION.bat`. The API applies pending migrations at startup, so a
restart is all that is needed to see this session's work.
