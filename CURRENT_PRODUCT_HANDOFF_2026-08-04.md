# Current product and repository handoff - 2026-08-04

This is the current restart point for AeroLink. It supersedes the 3 August handoff, which remains a historical
delivery record. [PROJECT_STATE.md](PROJECT_STATE.md) is the canonical product description and
[FEATURE_CATALOG.md](FEATURE_CATALOG.md) is the stable capability inventory.

## Repository checkpoint

- Repository: `seanmccarthyns/requirements-management-tool`
- Delivery rule unchanged: focused `codex/*` branch, pull request, required Product Quality Gate, squash
  merge, exact-merge requalification. Never push implementation directly to `main`.
- The persistent PostgreSQL database remains the sole real-life database. Nothing in this session reset,
  reseeded or destructively migrated it. One migration was added and is additive
  (`20260804014548_AddDownstreamAssessmentReopenings`).
- The demo API was stopped during this session to free the Release-build assembly locks that block a Windows
  rebuild. **Restart it before demonstrating anything**, see the restart sequence below.

## What was delivered

Issues #290 through #293 and #310, #311, #315 landed earlier in the session. This handoff covers the rest.

### Verification decisions can ask for a procedure that does not exist (#316, PR #321)

`NewProcedureRequired` is a real verification-impact outcome. Until it existed the answers were "an approved
procedure covers this" and "no test is required", so an engineer whose honest answer was "one has to be
written" had to leave the item unanswered — indistinguishable from an item nobody had looked at. The outcome
is an *answer* and never *verification*: it settles the `verification_impact` readiness gate and deliberately
does not settle `coverage`, so the release keeps waiting until the requested procedure is approved. Approving
that procedure auto-settles the item. Where a build has not materialised its requirements there is no exact
revision to bind a procedure to, and the page says so rather than hiding the action.

Not delivered, and worth its own issue: the TCR workbench half — raising the requested procedure as work
inside the test change request rather than authoring it from the decision.

### The downstream assessment drawer follows the assessment's state (#313, PR #322, DEC-090)

Every queue row now carries one control, `Open assessment`, in every state. What may be done is decided inside
the drawer from the assessment's state; both conclusions appear in exactly one state, claimed and undecided.
Wherever a conclusion exists the drawer states it with its author, its rationale and, once approved, its
approver. Correcting a wrong conclusion is `Reopen assessment`: a stated reason, back to undecided, linked
Draft SWCRs detached without altering the SWCRs, and an immutable `downstream_assessment_reopenings` row
holding everything the withdrawn conclusion carried. An unapproved conclusion is the assignee's to withdraw;
an approved one takes Approver authority; one in review is *returned*, never withdrawn behind its approver.

### A Problem Report is edited under the universal lease (#314, PR #323, DEC-091)

It was the one controlled record edited through a form of its own, posting the whole record with an expected
version. Its edit policy still named `Investigating` and `ResolutionProposed`, states the MVP lifecycle no
longer produces, so in practice only a Draft could be checked out. It now takes the same exclusive server
lease as everything else in every state except Closed and the terminal dispositions, and `POST /details` is
retired. Each check-in writes `DetailsCheckedIn` into the report's own history.

### The source authority names its own record (#312, PR #317)

The requirement inspector always read `Open SCR`, including on HLRs and LLRs whose authority is an SWCR. The
label now follows the controlled identifier of the change request that authorised the revision, carried on the
workspace projection. Deriving it from the requirement's level was rejected: the database still holds a System
change request carrying an HLR change, and a label computed from the rule would confidently mislabel it.

## Lessons this session, worth not relearning

- **A finished branch is not a merged branch.** #312 was reported closed and #317 reported merged; both were
  open, and `main` still had the bug the issue described. Squash merges make this invisible to
  `git log main..branch`, which shows commits ahead for merged and unmerged branches alike. Verify with
  `gh pr list --state all` and `gh issue list --state open` before claiming anything is done.
- **A conflicting pull request runs no checks at all, and that looks exactly like a slow queue.** PR #323 sat
  for an hour reporting "no checks reported" because `main` had moved underneath it. Waiting produces
  nothing; `gh pr view <n> --json mergeable` is the check that actually answers the question. Rebase, push,
  and CI starts.
- **CI failures were product and test defects, not flakes — again.** The shard-2 failure on PR #321 was a
  journey that claimed one coverage package and then opened whichever package sorted first, reselecting the
  row by the presence of the very button that claiming removes. Read the identifier before the action that
  destroys it. This is the third time this session a "flaky" failure was real.
- **A JSON snapshot read by a browser must name every field explicitly.** The Problem Report checkout
  adapter mixed PascalCase shorthand (`item.Title`) with explicitly-named camelCase members (`severity = …`),
  so the editor silently saw about half its working copy. The test-procedure adapter had it right.
- **`AuditEvent.AggregateId` is a foreign key to a change request.** Attaching an audit event to any other
  aggregate fails the constraint at `SaveChanges` and surfaces as a 500. Problem Reports use their own
  `ProblemReportRevision` chain.
- **SQLite cannot compare a `DateTimeOffset` server-side.** This bit the checkout collision-recovery path,
  which existed to turn a concurrent checkout into a usable answer and instead returned 500 on every SQLite
  deployment — including every browser-journey run. Materialise, then filter in memory.
- **Do not use scripted multi-line `perl`/`sed` for source edits here.** A single-line `perl -pi -e` with a
  `${number}` replacement was expanded by the shell to the empty string and quietly blanked six locators in a
  spec file. Use the editing tools.

## Where things stand

- `main` carries every issue listed above. Issues #312, #313, #314 and #316 are closed through pull requests
  #317, #321, #322 and #323. There are no open issues and no open pull requests.
- Backend suite on `main` at the end of this session: **589 passed, 0 failed** — 221 domain, 177
  infrastructure, 191 API. Browser journeys, production-build journeys and the PostgreSQL migration gate all
  passed on the final merge.
- Stale local `codex/*` branches remain from squash merges. They are leftovers, not unmerged work — verify
  with `gh pr list --state all`, never with `git log main..branch`.

## Safe restart sequence

1. `git checkout main && git pull --ff-only`
2. Confirm nothing is holding the Release assemblies or the API port before rebuilding.
3. Start the product with `START_AEROLINK_PRODUCTION.bat` (or
   `product/scripts/Start-AeroLinkProduction.ps1`).
4. Never run the demo-database reset. The persistent PostgreSQL database is the only real one and holds the
   probe data every observation session depends on.
