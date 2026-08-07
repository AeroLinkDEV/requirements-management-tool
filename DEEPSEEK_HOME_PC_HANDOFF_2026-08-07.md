# DeepSeek Home-PC Continuation Prompt - AeroLink

Copy everything below the horizontal rule into a DeepSeek coding-agent session running on the home PC.
The agent must have local terminal, filesystem, browser, and GitHub access. An ordinary web-chat session cannot
perform this work.

---

You are taking over an interrupted AeroLink engineering task on the HOME PC. AeroLink is an on-premises
aerospace requirements, change-control, verification, Problem Report, code-traceability, and controlled-document
management platform.

Read this entire prompt before doing anything. Work slowly and deliberately. Complete one phase at a time,
verify the result, and report concrete evidence before moving forward. Do not race ahead, make broad changes,
or assume that a successful command proves the product works.

## Absolute merge prohibition

**NEVER MERGE ANY GITHUB PULL REQUEST UNTIL THE OWNER EXPLICITLY CONFIRMS THAT YOU MAY MERGE THAT SPECIFIC
GITHUB PULL REQUEST.**

This rule overrides autonomy, convenience, green CI, branch protection status, prior standing instructions,
and any assumption that the work is routine.

- Do not enable auto-merge.
- Do not queue a merge.
- Do not squash merge.
- Do not merge through the GitHub website, CLI, API, or another agent.
- Do not interpret "go", "continue", or an earlier general authorization as permission to merge.
- Before each merge, show the owner the GitHub pull-request URL, exact head commit, changes made, validation
  results, CI results, unresolved risks, and proposed squash title.
- Then stop and wait for an explicit response authorizing that exact merge.
- If the owner has not explicitly confirmed the merge, leave the branch and GitHub pull request open.

You may create a focused branch, commit, push, open a **draft** GitHub pull request, run tests, and wait for CI.
You must not mark it ready or merge it without the owner's confirmation. If the owner authorizes marking it
ready but does not explicitly authorize merging, do not merge.

## Repository

Local repository:

`C:\Sean Project\Requirements Management Tool`

GitHub:

`https://github.com/seanmccarthyns/requirements-management-tool`

Home-PC hostname previously verified by Claude:

`UPSTAIRSMCCARTH`

Date of this handoff:

`2026-08-07`

## Why you are taking over

Claude Code was running on this home PC and reached its usage limit during the final live qualification of
GitHub pull request #376.

Claude did **not** stop during implementation. The implementation was already merged and CI-green. Claude
completed the prequalification safety work and stopped immediately before its first live functional
qualification assertion.

Claude's task status was:

1. Back up the sole real AeroLink database - completed.
2. Compare applied and source migrations - completed.
3. Launch the merged production build against PostgreSQL - completed.
4. Qualify #376 against persistent engineering data - marked in progress, but no qualification assertion or
   browser workflow had run.

Claude's last substantive message was:

> AeroLink is up and serving the built client. Marking the launcher task done and starting qualification with
> the API-level assertion DEC-103 names explicitly.

It then reached its monthly usage limit.

Do not unnecessarily repeat the backup, migration, build, or launcher work. First verify that the existing
runtime remains healthy, and then continue at the live qualification task.

## Authoritative source state

The following was freshly verified after Claude stopped:

- Repository root: `C:\Sean Project\Requirements Management Tool`
- Remote: `https://github.com/seanmccarthyns/requirements-management-tool.git`
- Branch: `main`
- Local HEAD: `67614d55b737a15015851c8d9325854721b3aafa`
- `origin/main`: `67614d55b737a15015851c8d9325854721b3aafa`
- Local and remote were identical.
- Working tree was clean.
- No GitHub pull requests were open.

GitHub pull request #376:

`https://github.com/seanmccarthyns/requirements-management-tool/pull/376`

Title:

`A procedure changes only through a test change request`

State:

`MERGED`

Squash merge commit:

`67614d55b737a15015851c8d9325854721b3aafa`

The pull-request CI and subsequent main-branch quality gate passed, including:

- backend and client validation;
- both browser shards;
- production-build browser journeys;
- PostgreSQL migrations and secure bootstrap; and
- the final `Report what this run validated` gate.

Main CI run:

`https://github.com/seanmccarthyns/requirements-management-tool/actions/runs/31194377811`

Re-verify all repository facts before acting. If the working tree is no longer clean, stop. Preserve and
identify the changes. Do not switch, reset, clean, delete, or overwrite anything until the owner understands
what changed.

Never use `git reset --hard`.

## Sole real database - absolute safety boundary

There is exactly one real/persistent AeroLink demonstration database. It exists only on this home PC.

PostgreSQL binaries:

`C:\Sean Project\Requirements Management Tool\product\.local\postgresql\pgsql\bin`

PostgreSQL data directory currently used by the running server:

`C:\Sean Project\Requirements Management Tool\product\.local\pgdata`

Port:

`54329`

Database:

`aerolink`

This database is the sole source of truth for the owner's persistent engineering records.

Absolute database rules:

- Never reset it.
- Never reseed it.
- Never delete it.
- Never replace it with a test database.
- Never restore a backup over it without explicit owner authorization.
- Never run a reset command to make testing easier.
- Never edit its records directly to force a workflow into a convenient state.
- Exercise it only through legitimate AeroLink application/API workflows.
- Preserve existing artifacts, revision history, approvals, electronic signatures, and audit evidence.
- Do not commit anything under `product\.local`.
- Do not transfer `product\.local` through Git.
- Never point disposable automated tests at port 54329.

A database reset command exists. Do not run it.

The database was accepting connections on `127.0.0.1:54329` at handoff.

Claude compared migrations and reported:

- 80 migrations applied to the real database;
- 80 migrations present in the repository;
- zero difference;
- nothing outstanding from #368, #369, or #370; and
- #376 introduced no migration.

Do not apply a migration unless fresh evidence contradicts this. If it does, stop and explain the difference
before applying anything.

## Verified backup

Claude created and verified this backup before launching AeroLink:

`C:\Sean Project\Requirements Management Tool\product\.local\backups\aerolink-20260807-140814.zip`

Sidecar:

`C:\Sean Project\Requirements Management Tool\product\.local\backups\aerolink-20260807-140814.zip.sha256`

Size:

`114,333,280 bytes`

SHA-256:

`2037e71ea42b533c172c3aeb84b6a00452f55a5a5688e552fd98165e54abfcd2`

The archive was independently re-hashed after Claude stopped and matched its sidecar.

Do not create another redundant backup before qualification unless the current archive is missing, corrupt,
or the database has materially changed since it was created.

## Running production instance

The merged production build was already running at handoff.

Website and API:

`http://127.0.0.1:5080`

Readiness:

`http://127.0.0.1:5080/health/ready`

Fresh readiness response at handoff:

- `status: ready`
- `service: AeroLink API`
- `database: connected`

The website returned HTTP 200. PostgreSQL listened on 54329, and `AeroLink.Api.exe` listened on 5080. The API
was the Release build under `product\src\AeroLink.Api\bin\Release\net10.0`. Production stderr was empty.

First check readiness. Do not stop or restart the application unless a concrete operational reason exists.

Demonstration sign-in:

`admin / AeroLink!2026`

The same demonstration password is generally used for seeded engineering identities. This is a local demo
credential printed by the launcher and supplied by the owner. It is not an external-service credential.

For role-separation qualification, do not perform every action as admin. Use distinct seeded identities with
the correct project/build roles. Inspect actual assignments instead of guessing.

## Local handoff and prior transcript

Full prior handoff:

`C:\Users\seanm\Downloads\HANDOFF-TO-HOME-PC.md`

Claude transcript:

`C:\Users\seanm\.claude\projects\C--Sean-Project-Requirements-Management-Tool\5638cb74-41a5-465b-83f5-9973aeceda1f.jsonl`

Repository decisions:

`C:\Sean Project\Requirements Management Tool\DECISIONS_AND_OPEN_QUESTIONS.md`

Read the handoff, DEC-103, and LES-009 before substantive work. The transcript may be consulted to verify what
Claude executed, but do not replay completed work merely to appear busy.

## What #376 changed

DEC-103 establishes this product rule:

A test procedure is introduced, modified, or retired only through a Test Change Request: SYSTCR, HLRTCR, or
LLRTCR. There is no independent procedure-creation workflow and no second procedure-level approval.

#376 specifically:

- removed `+ New test procedure`;
- removed `POST /api/test-procedures`;
- deleted `CreateTestProcedureRequest`;
- preserved `GET /api/test-procedures`;
- requires an authenticated, mutation-authorized POST probe against the collection to answer 405, not 404;
- changed `Author the procedure` on a `NewProcedureRequired` decision to propose a
  `TestProcedureChange` of kind `Introduce` on the requesting TCR;
- carries the exact driving requirement revision;
- does not select a separate procedure approver;
- requires an introduced procedure to name at least one requirement revision it verifies;
- enforces that rule at TCR submission, not while the draft is being incrementally authored;
- removed `POST /api/test-procedures/{revisionId}/approve`;
- removed the `Review & approve` procedure control;
- removed the client approval path and `TestProcedureRevision.Approve`;
- treats TCR approval as authority for the procedure work;
- materializes the procedure revision directly as `Approved`;
- creates no second signature on the procedure revision;
- deliberately retained historical `SelectedApproverId` data as legacy audit evidence; and
- continues to prevent execution of an unapproved historical procedure.

The lifecycle distinction matters:

- A proposed procedure change may be authored incrementally inside a draft TCR.
- A TCR with an Introduce change that names no driving requirement must not be submitted.
- Approving the TCR once is the controlled approval.
- Separately approving the materialized procedure would be an incorrect duplicate approval.

## Phase 1 - Reconfirm safety and runtime state

Move slowly. Run read-only checks for:

- repository identity;
- remote;
- current branch;
- exact HEAD;
- worktree cleanliness;
- equality with `origin/main`;
- current open GitHub pull requests;
- API readiness;
- PostgreSQL listener; and
- production website availability.

Expected commit:

`67614d55b737a15015851c8d9325854721b3aafa`

If any expectation differs, do not immediately correct it. Explain the difference and determine whether it is
new user work, new GitHub work, an operational change, or a stale assumption.

Report Phase 1 evidence before continuing.

## Phase 2 - Verify the DEC-103 server boundary

This is where Claude stopped.

Using an authenticated local session and the application's required CSRF/mutation authorization:

1. Probe `POST /api/test-procedures`.
2. It must return HTTP 405 Method Not Allowed.
3. A 404 is not acceptable because the collection still exists for GET.
4. A successful mutation is a severe defect.
5. Do not treat a 401 or CSRF rejection as proof of the route contract. Authenticate and satisfy the ordinary
   mutation boundary first.
6. Reuse existing authentication/CSRF helpers or inspect the regression test. Do not create a workaround.
7. Confirm `GET /api/test-procedures` remains functional.
8. Confirm the former per-revision approval route is absent.
9. Search for any alternate direct-create procedure route.

This phase must not create or modify database records.

Report Phase 2 evidence before continuing.

## Phase 3 - Verify Build 1.5 historical behavior

Open Build 1.5 through the real production UI.

Confirm:

- it is unmistakably historical/read-only;
- no direct procedure creation is available;
- no TCR or procedure mutation control is improperly enabled;
- existing procedures remain viewable;
- revision history, audit presentation, traceability, and evidence remain visible;
- direct links and refresh do not make historical records editable;
- search and filters do not leak Build 1.6 controls; and
- back/forward navigation preserves the correct build context.

Capture route, artifact number, displayed revision/state, and absent controls. Do not mutate Build 1.5 merely
to prove refusal unless an established safe regression path requires it.

Report Phase 3 evidence before continuing.

## Phase 4 - Verify Build 1.6 active behavior

Open Build 1.6.

Confirm:

- it remains the active development build;
- System, HLR, and LLR procedures appear only in their proper scopes;
- there is no `+ New test procedure` anywhere;
- no equivalent direct-create control hides in an empty state, command palette, menu, drawer, or deep link;
- procedure rows and inspectors do not show `Review & approve`;
- refresh, direct links, search, filters, History, and inspector close/reopen behavior are correct;
- System, HLR, and LLR isolation remains intact; and
- existing procedure revision history and audit evidence remain intact.

Approach it from several directions:

- left navigation;
- command palette;
- direct URL;
- requirement trace link;
- browser refresh;
- back/forward navigation;
- different scopes;
- empty/filtered lists; and
- historical revisions.

Report Phase 4 evidence before continuing.

## Phase 5 - Verify the legitimate procedure-authoring entrance

Find a real Build 1.6 TCR decision with outcome `NewProcedureRequired`.

Use a suitable existing decision if one exists. If none exists, create only the minimum realistic engineering
workflow through the controlled application/API. Do not seed records or directly edit PostgreSQL.

Confirm:

- the decision offers `Author the procedure`;
- it does not say `Create test procedure`;
- it opens a dialog titled exactly `Propose a test procedure`;
- the proposal belongs to the requesting TCR;
- it uses the correct discipline;
- it can carry an exact driving requirement revision from the same project;
- cross-project or nonexistent requirement references are rejected;
- draft work may be saved incrementally;
- closing/reopening preserves draft input appropriately; and
- labels, required semantics, focus, accessible errors, and the requirement selector remain usable.

Exercise one realistic SYSTCR path. Inspect equivalent HLR/LLR behavior if it can be done without creating
unnecessary duplicate artifacts.

Report Phase 5 evidence before continuing.

## Phase 6 - Verify the submission gate

Exercise the intended corner case carefully:

- An Introduce procedure change with no driving requirement may exist only as an incomplete draft.
- Submission for review must be refused until it names a driving requirement revision.
- The refusal must be understandable and accessible.
- The refusal must not erase the engineer's entered work.
- After a valid requirement revision is named, submission should proceed normally.

Do not confuse proposal-time incremental drafting with submission-time validity.

Report Phase 6 evidence before continuing.

## Phase 7 - Complete the most important end-to-end outcome

Complete one realistic controlled SYSTCR workflow:

1. Author or use a suitable SYSTCR.
2. Ensure its Problem, Analysis, and Solution are credible.
3. Include an Introduce procedure change driven by a real Build 1.6 System requirement revision.
4. Submit the SYSTCR for independent review.
5. Sign in as a different authorized reviewer.
6. Review and electronically approve the package.
7. Use the correct baseline/build materialization workflow.
8. Confirm the resulting controlled procedure revision appears in the build.
9. Confirm its state is already `Approved`.
10. Confirm nobody separately approved or signed the procedure revision.
11. Confirm the SYSTCR approval and materialization are the authoritative audit trail.
12. Confirm the procedure links to its driving requirement revision and controlling TCR.
13. Refresh and reopen the record.
14. Confirm state, linkages, and history persist.
15. Confirm it is discoverable through the correct System scope and direct link.

Role separation is mandatory:

- The acting engineer must not approve their own package.
- Do not use admin to simulate both people.
- Record author, reviewer, approval time, artifact numbers, and resulting procedure number.

Do not complete unrelated release finalization. This phase qualifies the new control rule and persisted outcome.

Report Phase 7 evidence before doing anything else.

## Phase 8 - Qualification report and mandatory pause

Report:

- exact main commit;
- repository cleanliness;
- readiness result;
- database safety state;
- Build 1.5 evidence;
- Build 1.6 evidence;
- authenticated `POST /api/test-procedures` status;
- SYSTCR number/revision;
- driving requirement number/exact revision;
- resulting procedure number/exact revision;
- author identity;
- reviewer identity;
- electronic approval evidence;
- materialization evidence;
- resulting procedure state;
- proof no second procedure approval occurred;
- direct-link/refresh evidence;
- history/audit preservation evidence; and
- any deferred checks.

**Pause after this report. Do not begin Stage 3B until the owner confirms that the qualification evidence is
acceptable and tells you to proceed.**

## Confirmed-defect workflow

Do not silently work around a defect or repair live database records.

For every confirmed defect:

1. Reproduce it with concrete evidence.
2. Distinguish source behavior from stale runtime, role configuration, operator error, or database state.
3. Recheck readiness and logs before declaring an operational failure a product defect.
4. Search GitHub issues and GitHub pull requests for duplicates.
5. Create an implementation-ready GitHub issue if no valid duplicate exists. Include impact, reproduction,
   expected/actual behavior, evidence, affected components, minimal recommended correction, acceptance criteria,
   and regression coverage.
6. Create a focused `codex/*` branch from current main.
7. Implement the smallest coherent correction.
8. Add proportional regression coverage.
9. Run focused tests, followed by broader gates proportional to risk.
10. Commit and push the focused branch.
11. Open a **draft** GitHub pull request.
12. Wait for all required CI and investigate failures.
13. Present the owner with the GitHub pull-request URL, exact commit, diff summary, test evidence, CI results,
    unresolved risks, and proposed squash title.
14. **STOP AND WAIT FOR EXPLICIT MERGE CONFIRMATION.**
15. Do not enable auto-merge while waiting.
16. Merge only after the owner explicitly authorizes that specific GitHub pull request.
17. After an authorized merge, verify issue closure, fast-forward local main, and requalify the exact squash
    commit while preserving the database.

If persistent data appears inconsistent, stop before altering it and report exact artifact identifiers.

## Stage 3B - only after owner confirmation to proceed

Remote branch:

`origin/stage-3b/testing-coverage-becomes-change-requests`

Expected exact HEAD:

`53c6dd541e12919de8e3984b0fbc8ab92ba7d699`

At handoff the branch existed remotely and no GitHub pull request was open. It is based on main at `67614d5`.
Do not recreate it. Fetch and track the existing branch only after the owner authorizes proceeding beyond the
#376 qualification report.

Read the five full commit messages:

```powershell
git log --reverse --format="COMMIT %H%nSUBJECT %s%nBODY%n%b%n---" 67614d5..origin/stage-3b/testing-coverage-becomes-change-requests
```

Commits:

- `b8d8656` Testing Coverage becomes Change Requests; coverage moves to the Explorer
- `ac70896` Move the journeys that drove the library onto the Explorer
- `56fbb84` The Explorer scopes to its own discipline, and drops what it read when that changes
- `4296362` Order the Explorer's replies, and walk the journeys between the two pages
- `53c6dd5` Keep the materialization prerequisite where authoring starts

### Stage 3B intent

- Rename verification-side Testing Coverage to Change Requests.
- List SYSTCRs, HLRTCRs, or LLRTCRs for the active discipline.
- Present them consistently with requirements-side Change Requests.
- Remove the procedure library from this page.
- Keep procedures in Test Procedure Explorer.
- Move requirement coverage into an Explorer page-level tab.
- Keep Test Results where they are.
- Apply the behavior to System, HLR, and LLR.
- Preserve filter and address state.
- Prevent cross-discipline leakage.
- Preserve requirement-trace direct links.
- Preserve the materialization-prerequisite banner.
- Add command-palette entries for all three Explorers.
- Remove the obsolete direct Draft procedure editor.

The branch also addresses:

1. Incorrect Explorer scope values that caused all 515 project procedures to appear together.
2. Slow stale list responses overwriting newer filtered results.
3. Requirement coverage remaining stale across discipline switches.

### Existing Stage 3B evidence

At `53c6dd5`, the laptop reported:

- backend build passed with zero warnings;
- Domain 275 passed;
- Infrastructure 194 passed;
- API 231 passed;
- client type-check passed;
- client lint passed with only the known ChangeRequestEditor exhaustive-deps warning;
- client build passed;
- browser journeys with two shards: 142 passed, 1 intentionally skipped;
- production-build journeys: 10 passed;
- local PostgreSQL/secure-bootstrap gate not run; and
- no GitHub pull request opened.

Do not redesign working code. Inspect it slowly, verify its intent, and correct only confirmed issues.

### Stage 3B delivery sequence

1. Confirm owner permission to start Stage 3B.
2. Confirm main remains clean and synchronized.
3. Fetch origin.
4. Track the existing remote branch.
5. Confirm HEAD is exactly `53c6dd541e12919de8e3984b0fbc8ab92ba7d699` before new work.
6. Read all commit messages and inspect the complete diff.
7. Review for minimality, consistency, and regression risks.
8. Never run branch tests against the real PostgreSQL database.
9. Use disposable infrastructure only.
10. Re-run proportional local gates.
11. If disposable PostgreSQL is unavailable, do not substitute port 54329. Let GitHub CI run its service
    container.
12. Push only coherent confirmed corrections, if necessary.
13. Open a **draft** GitHub pull request into main.
14. Include intent, defects corrected, validation, and out-of-scope items.
15. Wait for all CI, including `Report what this run validated`.
16. Investigate failures rather than dismissing them as intermittent.
17. Present the complete merge-readiness package to the owner.
18. **STOP. DO NOT MERGE. DO NOT ENABLE AUTO-MERGE.**
19. Wait for the owner's explicit authorization to merge that exact GitHub pull request.
20. Only after authorization, squash merge.
21. Fast-forward local main and verify it equals origin/main.
22. Requalify the exact squash commit.
23. Launch the merged production build against the real database without reset/reseed.
24. Verify the renamed Change Requests page and Explorer against Builds 1.5 and 1.6.
25. Verify System/HLR/LLR isolation, Explorer tabs, context switching, filters, refresh/back behavior, direct
    links, and absence of direct procedure creation/editing/approval.

Stage 3B is client-only; no EF migration is expected.

## Stage 4 - separate work and separate confirmation

Do not mix Stage 4 into Stage 3B.

Stage 4 adds `+ New System Test Change Request`, plus HLR and LLR equivalents, at the top-right of the renamed
verification Change Requests page.

The TCR editor must be structurally consistent with the SRCR editor:

- Problem;
- Analysis;
- Solution;
- the same rich-text behavior;
- the same presentation shape;
- the same staged review; and
- the same controlled lifecycle expectations.

The material difference is that an SRCR controls requirement changes while a SYSTCR/HLRTCR/LLRTCR controls
test-procedure changes. Do not move Test Results.

Do not begin Stage 4 until:

1. Stage 3B was explicitly authorized for merge;
2. Stage 3B was merged and requalified; and
3. the owner explicitly tells you to begin Stage 4.

Stage 4 must use a new focused branch and draft GitHub pull request. It also must never be merged without
explicit confirmation for that exact GitHub pull request.

## Standing engineering rules

- Work slowly and one phase at a time.
- Show evidence before conclusions.
- Never commit or push directly to main.
- Never force-push a shared branch.
- Never auto-merge.
- Never merge without explicit confirmation for the exact GitHub pull request.
- Never reset/reseed the persistent database.
- Never use the real database for automated test isolation.
- Never discard user changes.
- Never use `git reset --hard`.
- Preserve immutable revisions, role separation, audit evidence, electronic signatures, project isolation,
  build isolation, and discipline isolation.
- Keep fixes minimal and product-consistent.
- Do not add AI features.
- Do not make certification, compliance, or tool-qualification claims.
- AeroLink is not a document editor.
- AeroLink never executes tests.
- The client makes no external network requests at runtime.
- In AeroLink, PR means Problem Report. Say `GitHub pull request` for GitHub work.

Three other worktrees are attached. Do not alter or delete them:

- Documentation Center
- Enterprise Audit
- Upward Trace

They share Git refs with this clone and may contain someone else's active work.

## Automated gates for source changes

Run focused tests first, then broader gates.

Backend:

```powershell
cd product
dotnet build
dotnet test
```

Client:

```powershell
cd product\client
npx tsc --noEmit
npm run lint
```

Browser journeys using the CI-equivalent two-shard grouping:

```powershell
$env:AEROLINK_E2E_SHARDS = "2"
node scripts/run-sharded-e2e.mjs
```

Production-build journeys:

```powershell
npm run test:production
```

The local default is three shards. Explicitly use two to match CI.

If a build result contradicts visible source, clean and rebuild before inventing an EF or lifecycle theory.
If an interrupted Playwright run leaves processes behind, identify and stop only disposable test API/Vite
processes. Do not accidentally stop the real PostgreSQL server.

## Hard-won lessons

- Some files use CRLF. Fragile multiline scripted edits can silently change nothing.
- Prefer controlled patches.
- If scripting is justified, handle `\r?\n`, verify every site, and inspect the diff.
- A moved rule is not automatically the same rule at a new lifecycle point.
- Read every look-alike call site before broad edits.
- A Playwright timeout names the test, not necessarily the hanging action.
- Read `trace.zip` and locate the unmatched action.
- Playwright locator `.all()` does not wait.
- Components may stay mounted across discipline switches; clear context-sensitive state.
- Stale data under a new heading is worse than a loading state.
- Removing a UI control without removing/governing server authority is incomplete.
- A capability with no caller is not delivered.
- Do not dismiss intermittent CI failures without evidence.
- Never use database edits to work around source/test failures.
- Push coherent checkpoints so work can safely continue across machines.

## Reporting and pauses

At each phase report:

- current branch and exact commit;
- repository cleanliness;
- runtime/readiness state;
- database safety state;
- actions performed;
- artifacts created or modified;
- roles used;
- concrete qualification/test results;
- GitHub issue and draft GitHub pull-request links, if any;
- CI state;
- unresolved risks; and
- the next proposed phase.

Pause at every mandatory checkpoint in this prompt. Most importantly, never merge before the owner explicitly
confirms the exact GitHub pull request may be merged.

Your immediate actions are:

1. Read this prompt, the prior handoff, DEC-103, and LES-009.
2. Reconfirm repository and runtime state without changing anything.
3. Report the Phase 1 evidence.
4. Continue Claude's interrupted qualification at the authenticated `POST /api/test-procedures` 405 assertion.
5. Proceed slowly through each qualification phase, reporting evidence as you go.
6. Pause after the #376 qualification report and await owner confirmation before Stage 3B.
7. If any source change creates a GitHub pull request, leave it draft and never merge until the owner explicitly
   confirms that exact merge.
