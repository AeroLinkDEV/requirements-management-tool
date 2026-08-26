# Current product and repository handoff - 2026-08-05

This is the current restart point for AeroLink. It supersedes the 4 August handoff, which remains a
historical delivery record. [PROJECT_STATE.md](PROJECT_STATE.md) is the canonical product description and
[FEATURE_CATALOG.md](FEATURE_CATALOG.md) is the stable capability inventory.

## Repository checkpoint

- Repository: `seanmccarthyns/requirements-management-tool`
- Delivery rule unchanged: focused `codex/*` branch, pull request, required Product Quality Gate, squash
  merge, exact-merge requalification. Never push implementation directly to `main`.
- `main` at `efeddf3`, in sync with origin, working tree clean, **zero open pull requests**.
- Backend suite: **644 passed, 0 failed** — 255 domain, 177 infrastructure, 212 API. Full browser journey
  suite: **139 passed, 1 skipped**.
- The persistent PostgreSQL database remains the sole real-life database. Nothing this session reset,
  reseeded or destructively migrated it. Three migrations were added, all additive:
  `20260804205728_AddImportSourceRecordCount`, `20260805190027_AddProblemReportTypeAndWorkaround` and
  `20260805195709_AddTestAssessmentOutcome`.
- **The last two migrations have not been applied to the demo database.** PostgreSQL was not running when
  they were written. They apply themselves at API startup, so starting the product is enough.

## What was delivered

Two bodies of work: issue #332 (bringing in a program from another tool) and the 5 August observation
document, which had no issue of its own.

### Bringing in an existing program (#332, PRs #334-#342, DEC-093)

An import creates an **externally sourced baseline** directly, released on arrival, and never becomes a
change request — nobody here approved those requirements, so routing them through review would produce a real
signature attesting to a fiction. Five gates run in order: Source, Analyse, Map, Reconcile, Accept. A named
person accepts, and what that signature asserts is stated on the page beside what it never asserts.

Source identifiers survive as searchable records joined by a provenance link reading *SYSR-000148.00
originates from SYS-01234*. An object the source retired before the imported baseline is recorded so a
reference to it can be answered, and joins nothing. Source history is recorded verbatim as *what the source
reported* and participates in no gate, coverage figure or readiness computation.

Reachable at **Software Builds → Imported baselines**, and there is a **DOORS Import Practice** project on
the Projects landing — its own empty Program — for rehearsing without any of it landing in a Program somebody
works in. Rehearsals are disposable: an accepted import is immutable, so name each one as its own build
(1.0, then 2.0) and run the real import into the Project that will actually keep it.

**Phase 3, the DOORS/ReqIF parser, is deliberately not built.** The gates are driven by structured JSON,
which is what makes them testable before a parser exists. Issue #332 stays open for that reason.

### The 5 August observations (PRs #343, #344, #345, DEC-094 to DEC-096)

**Assessments say whether they were done and what they found.** Nine phrasings that mixed workflow position
with engineering conclusion became one sentence with six forms. Two domain rules followed: a change request
cannot be linked until the assessment has concluded one is required, and only a no-change conclusion is
approved. Both closed real holes — a Draft could previously hang off an assessment that had concluded
nothing.

**The test world uses the same machine.** An approved change now raises an unnumbered test assessment; the
SYSTCR, HLRTCR or LLRTCR number is what the assessment produces when it concludes work is required. Before
this, every approved change produced a controlled test change request before anybody had looked at whether it
touched a single procedure.

**Problem Reports** gained Type, Workaround, and Airworthiness in place of Safety; Responsible owner is
Assigned user; the queue filters as you type with no Refresh button.

## Findings worth carrying forward

The durable ones are recorded as [LES-001 to LES-005](../../DECISIONS_AND_OPEN_QUESTIONS.md#lessons-learned).
In short:

- **A new `/api` route inherits middleware guards by prefix match.** `/api/baseline-imports` matched
  `/api/baseline` and every import gate was refused as a released-build write. Check three places in
  `Program.cs`.
- **Measure a performance hypothesis before shipping the fix.** The EF-model-building theory for the CI
  sign-in timeouts was written, measured at 167 ms against 170 ms, and deleted. The cause is still unknown.
- **Green tests are not a look at the page.** Four defects in one day passed every assertion and were caught
  only by screenshotting: UTC date shifts, a cascade collision, a duplicated icon, a stray block of colour.
- **An enum stored by name cannot default to an empty string.** EF scaffolds `defaultValue: ""`, which is not
  a member name, and every existing row then fails to materialize. It happened twice in one day.
- **Prefer the editor over scripted multi-file edits.** A `perl` substitution stripped a field from two cards
  instead of one. This is the second recorded instance in this repository.

Also worth knowing, and not general enough for a lesson:

- **Browser journeys intermittently fail at `auth.ts:19`**, waiting fifteen seconds for sign-in with the page
  still showing "Authenticating…". Seen on three runs. Re-running has cleared it every time, and the full
  local suite passes, but the cause is not understood. Do not dismiss it as a runner problem without
  measuring; see LES-002 for what has already been ruled out.
- **The `aerolink-database-transfer` repository has never contained a database backup** — two commits, a
  README and an unrelated starter package, no releases. Backups come from `Backup-AeroLink.ps1`, and
  `Restore-AeroLink.ps1` accepts an archive **only** from `product\.local\backups\` and **only** with its
  `.sha256` sidecar alongside.

## Where things stand

- Issue #332 is the only open issue, open deliberately: its code is merged but Phase 3 needs a real DOORS
  extract.
- **OQ-022 was resolved** by the decision that an import is a one-way move — a program is extracted once, at
  the point of leaving its old tool, so an identifier renamed between two extracts cannot arise.
- Open by choice: whether the four non-Closed terminal Problem Report states should become editable; and
  unifying the two assessment interaction patterns, since the requirements queue opens a drawer while the
  coverage page expands inline.

## Safe restart sequence

1. `git checkout main && git pull --ff-only`
2. Confirm nothing is holding the Release assemblies or the API port before rebuilding. A leftover
   `AeroLink.Api` process from a previous Playwright run will block a Release build with a file lock.
3. Start the product with `START_AEROLINK_PRODUCTION.bat` (or
   `product/scripts/Start-AeroLinkProduction.ps1`). The two pending migrations apply themselves on startup.
4. Never run the demo-database reset. The persistent PostgreSQL database is the only real one and holds the
   probe data every observation session depends on.
