# Current product and backlog handoff — 2026-07-31

> **Historical handoff.** Superseded by
> [CURRENT_PRODUCT_HANDOFF_2026-08-02.md](CURRENT_PRODUCT_HANDOFF_2026-08-02.md). This file preserves the
> 31 July delivery checkpoint; its branch names, issue counts, test totals, and next-work recommendations are
> not current.

This supersedes [CURRENT_PRODUCT_HANDOFF_2026-07-29.md](CURRENT_PRODUCT_HANDOFF_2026-07-29.md), which
remains accurate for everything it describes except verification. [PROJECT_STATE.md](PROJECT_STATE.md) is
still the canonical description of the product, and decisions are appended to
[DECISIONS_AND_OPEN_QUESTIONS.md](DECISIONS_AND_OPEN_QUESTIONS.md).

Start from `main`.

## What changed on 30–31 July

A 28-item product review (the "July 30 observations") drove PRs #191–#208. Most were small corrections —
wording, chrome, a stakeholder being able to cancel an in-review change request, a released build getting a
real read-only decision room, requirements needing a real section, documents supporting sub-sections. Three
were structural, and they are the ones a new session needs to understand.

### 1. Verification is two pages per discipline (DEC-077)

The tabbed **Verification & Evidence** workspace — Procedure alignment, Requirement coverage, Test
procedures, Evidence & results — no longer exists. `product/client/src/VerificationCenter.tsx` and its
stylesheet are deleted. In its place:

| Page | Question it answers | Route |
| --- | --- | --- |
| Testing Coverage | What are this build's requirements tested by, and what test work has nobody picked up? | `…/system-verification/coverage`, `…/software-verification/{hlr,llr}/coverage` |
| Test Results | What does this build have to run, and what happened when it was run? | `…/system-verification/results`, `…/software-verification/{hlr,llr}/results` |
| Verification (chooser) | Which of the two do you want? | `…/system-verification`, `…/software-verification` |

A corrective action arriving from a problem report is `…/results/{problemReportId}`.

The software level rides on the existing route `artifactKind` (`HighLevel` / `LowLevel`) rather than on a new
`Discipline` value — `discipline` is threaded through breadcrumbs, navigation highlighting and scope
switches, and every comparison would have needed auditing for one that silently treats an unrecognised value
as System.

Everything that lived only in the tabs moved onto the pages: procedure authoring, independent approval, the
full requirement coverage inventory, the procedure library with state/latest-result filters and paging (and
its addressable worklist — `?procedure=`, `?procedureState=`, `?procedureOutcome=`, `?procedurePage=`),
verification decisions including reopening one and reading its history, run history and named retests, and
evidence attachment.

### 2. A build's test scope is a set (DEC-076)

`BuildTestSet` — one per build per discipline, a working list with history. Every entry records who put the
procedure there and why: `ChangedRequirement`, `CoverageArea`, `CorrectiveAction`, `Chosen`. Release gates
measure results against the set. An empty set is only an answer when there is no test work at all; with test
change reviews present, empty holds the gate rather than passing it.

Choosing the set is a lead's decision (Test Lead or Program manager). Recording determinations against it is
a Test Engineer's. The old per-decision "evidence required before release" checkbox is gone; the server field
survives only as one of the inputs that seeds a new set.

### 3. Test Change Requests are controlled records (DEC-074), and there are sixteen roles (DEC-075)

A test change review now has its own number and revisions, may cover more than one requirement change
request, and may be raised by hand as well as automatically on change approval. Claiming a package claims all
of it. Roles are named for real jobs, and a precise role satisfies the general one it implies
(`ProgramRoleAuthority.Satisfying`) so naming somebody's actual job never removes their capability.

## Where things stand

### 2026-07-31 downstream-impact increment

- Working branch: `codex/downstream-change-assessments`.
- Accepted product model is recorded in DEC-078 and DEC-079. System approval raises HLR assessment work; HLR
  approval raises LLR assessment work. Assessments support no-change, one-to-one, one-to-many, and consolidated
  many-to-one change-request mapping without prematurely consuming a change-request number.
- The data model, API, automatic raising service, supersession behavior, Software Change Requests queue, named
  procedure/TCR approvers, and EF migration are implemented on the branch. The original approved work stays
  readable when its source is revised, but is labelled out of date and excluded from active readiness.
- The showcase seed defect that placed `HLR-000075.02` inside System `SRCR-00032.01` is corrected at its source;
  all System showcase packages now contain System requirement changes.
- GitHub: #209 tracks this delivered increment. #210 is the deliberately separate prospective upward-trace
  authoring/materialization increment; no unused placeholder schema was merged for work that is not enforced.
- Qualification on the branch: 505 .NET tests passed after the seed expectation was updated (196 domain,
  158 infrastructure, 151 API); lint, type-check and production build passed; the browser matrix reached
  103 passed/1 skipped, its one stale selector was corrected and passed focused; 9 production journeys passed
  and the one updated named-approver journey passed focused. The downstream queue has its own passing journey.
- The PostgreSQL migration SQL was generated and inspected without applying it to the persistent demo database.
  Applying migrations to that database remains an operator/startup action; never reset the demo database merely
  to prove this increment.

- `main` is clean, CI green, tree empty.
- Backend: 499 tests (192 domain, 156 infrastructure, 151 API).
- Browser journeys: 100 (1 skipped), plus 10 production-build journeys.
- Issues closed this stretch: #181, #190, #192, and the verification rebuild #12/#14/#17 work items.
- Ten exploratory-testing issues remain open: #180, #182–#188.
- Left for the product owner: #112 branch protection, #102 four-level integrity vocabulary, #100
  product-language decision.

## Lessons this session, worth not relearning

- **An intermittent CI failure is a real defect until proven otherwise.** Two failures were blamed on
  two-core runner exhaustion. Both were real: one was a stale unfiltered reply overwriting a filtered list,
  the other was fixed by rebasing onto a main that already contained the fix. Reproduce locally before
  reaching for "flaky".
- **A failed read that renders empty is worse than one that throws.** Testing Coverage asked for coverage by
  release id, the endpoint answered `400`, the page swallowed it, and a build with 26 requirements reported
  none — on the page whose whole job is to say what is untested. Coverage is asked for by *configuration*: a
  materialized baseline, or the software build that froze one.
- **Probe the running product before theorising.** Twenty minutes of reasoning about a broken page were
  wasted; capturing the actual request with `page.on('request')` found a mangled template literal in one
  step.
- **Never relax an assertion that just failed.** One was softened to accept either state, which hid a real
  defect until the strong version was asserted again.
- **The demonstration database keeps everything.** Determinations are immutable and every run ever recorded
  stays. A journey that asserts "the first run" is asserting the last time that journey ran; find records by
  their own text.
- **Two people in one journey need two browser contexts.** Signing out and straight back in races the session
  cookie on CI.
- **Windows: multi-line `perl -0pi -e` on CRLF files silently no-ops**, and a stray `AeroLink.Api` process
  holds DLLs and produces `MSB3027`. Playwright clears `test-results` at the start of every run, so inspect a
  failure artifact before re-running.
- **`history` is a name a component can shadow.** `history.pushState` inside a component holding procedure
  revision history resolves to the state variable and throws; use `window.history`.

## Safe restart sequence

1. `git pull` on `main`.
2. `START_AEROLINK_PRODUCTION.bat` (Windows only; the product owner tests on Windows).
3. Never run the demo database reset without asking first — the demo database carries probe data.
4. One PostgreSQL instance, always. Test artefacts belong in it so they are visible.
5. Branch per task → pull request → squash merge. Never commit to `main`.
