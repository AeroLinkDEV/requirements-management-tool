---
name: test-audit
description: "Use when writing, changing, reviewing, or deleting AeroLink tests, or when auditing a test area for low-value, duplicated, implementation-coupled, or falsely green tests and the test-only production seams they keep alive."
---

# Test audit

The rules this skill applies live in [`product/docs/TEST_RISK_LAYERS.md`](../../../product/docs/TEST_RISK_LAYERS.md):
the layer model, the **authoring gate**, the **low-value test patterns**, the **retention bar**, and the placement
checklist with its deletion evidence. Read that document first. This skill is the workflow around it.
[`AGENTS.md`](../../../AGENTS.md) still governs safety, generated contracts, merging, and the persistent database.

Adapted from OpenClaw's `test-audit` skill (https://github.com/openclaw/openclaw/tree/main/.agents/skills/test-audit)
for AeroLink's layers, generated test contracts, and controlled-history retention rules.

There are three modes and one value bar. Aim for confidence, not a deletion count.

## 1. Authoring mode (every new or materially changed test)

Answer the four authoring-gate questions and check the low-value patterns before writing the test. For a bug fix,
run the new regression test against the pre-fix code and see it fail for the intended reason. For a new browser
journey, say which failure no Domain, Infrastructure, or hosted API test can see; browser time is the most expensive
tier (`product/docs/BROWSER_AND_BACKEND_FEEDBACK_TIME.md`).

## 2. Audit mode (a focused sweep)

Keep discovery read-only and record the evidence (the R/F/C/D ledger below) before editing. Stop for review only
when the task asked for an audit without fixes. For a broad scope, split the work into lanes that match the test
tiers:

- `product/tests/AeroLink.Api.Tests` (hosted API);
- `product/tests/AeroLink.Infrastructure.Tests` and `AeroLink.Domain.Tests`;
- `product/client/tests` (logic, rendered, and Full Playwright tiers; see `product/client/fast-client-tests.json`);
- tooling: `product/test-contracts/tests`, `product/test-planner/tests`, `product/scripts/*.Tests.ps1`.

Prefer a few high-confidence candidates over a large speculative inventory. These patterns have already found
real problems in this repository:

| Look for | Why it matters |
|---|---|
| An "outsider" signed in to a separate `new AeroLinkApiFactory()` | A separate host has its own database, so the record is missing and the access check never runs. |
| `if (!ServerConfigured(...)) return;` or a similar early return | Reports **Passed** when nothing ran; it should report Skipped. |
| Conditional `Skip =` attributes whose variable no CI lane or script sets | Coverage that never executes. |
| `*.Tests.ps1` suites missing from the `script-contracts` job and `Get-AeroLinkTestPlan.ps1` | Suites that never execute. |
| Exported client functions with no caller under `product/client/src` | Dead production code kept alive by tests. |
| `ForTests`, `ForTesting`, or reflection into a private member | A test-only seam; test through the real boundary. |
| Specs outside the Fast tiers that never sign in or call the API | Paying for a backend and a seed they do not use. |
| Expected values computed with the comparator, formatter, or hash under test | Agrees with a broken implementation. |

Before judging a candidate, read the whole test, the production owner, its callers, overlapping tests, CI routing,
and relevant history (`git log -S`). If the test claims dependency-backed behavior, read the dependency. Mark each
candidate:

- **R** retain: name the contract and the bug it catches;
- **F** fix: keep the contract, repair the assertion (for example, add the missing positive control);
- **C** consolidate: name the owner test that absorbs the assertion first;
- **D** delete: name the stronger test that remains, or why no contract exists.

For an **R** or **F**, a finding that the product or the evidence is wrong (a falsely green negative control, an
untested guard, a never-run suite) is its own GitHub issue unless the fix is trivial and inside the batch.

Every **D** needs the deletion evidence from `TEST_RISK_LAYERS.md`. A candidate missing a field is not ready.

The audit is done when every test file in the named scope has been read and each candidate carries a complete R/F/C/D
mark with evidence; list the files read with no candidates.

## 3. Campaign mode (one owner area's whole test surface)

1. **Baseline.** Record the area's test and support line counts and each test file's result at a pinned `main` SHA.
   Keep baseline failures in their own list; treat them as possible product defects.
2. **Lanes.** Split the surface along production-owner boundaries, not file prefixes, so each test file belongs to
   exactly one lane.
3. **Ledger.** Give each lane to a read-only reviewer who reads every test in full and marks every declaration
   (a `[Theory]` or `test.each` counts once unless its rows need different marks) with R/F/C/D and one line of
   evidence. Judge a test by its assertions, not its name.
4. **Layer plan.** A second read-only pass looks for the redundant *layer*, not only redundant tests. Name one
   keeper per contract and the seams each lane unlocks.
5. **Cutover.** Edit lane by lane. Remove unlocked test-only seams. Serialize edits to shared test support.
6. **Preservation review.** Independent reviewers compare deleted coverage with the keepers and look for contracts
   that lost their only proof and for new assertions that cannot fail. For each restored contract, make one
   deliberate break in the production owner, confirm the keeper goes red, then restore the source exactly.
7. **Defects.** Fix a baseline failure that survives into a keeper at its owner, in a separate commit, with a control
   run that shows the old behavior.
8. **Reconcile.** Merge `main` rather than rebasing a long campaign, carry any new `main` tests into their keepers,
   and rerun the area on the merged head.
9. **Done** when every declaration in the baseline has a mark, the preservation review found no contract without a
   keeper, and the area is green on the merged head apart from baseline failures already filed as issues.

## Editing and validation

- One coherent owner-area batch per commit. Delete obsolete test-only exports, wrappers, and dead production paths
  rather than preserving aliases. Do not add replacement tests that restate the implementation.
- Removing or adding API test methods changes `product/test-contracts/api-test-intent.json`,
  `api-host-classification.json`, and the summary in `product/docs/API_TEST_INTENT_INVENTORY.md`. Run the three
  generators under `product/test-contracts/tools/` twice and require identical output. Then update the pinned totals
  in `product/test-contracts/tests/inventory.test.mjs` from the generator output (lesson 9 in
  `docs/ENGINEERING_LESSONS.md`) and run `node --test product/test-contracts/tests/*.test.mjs`.
- Read the host-classification diff, not just its totals. Moving a helper out of a class can hide a service
  replacement from the static classifier and wrongly reclassify the class as reusable.
- Never add to `product/test-contracts/grandfathered-uncovered.json` to make a deletion pass. Route coverage must stay
  green without it.
- Changes under `product/test-contracts/`, `product/test-planner/`, or `.github/` force broad CI, and the latter two
  need the owner-reviewed path in `product/docs/MERGING.md`.
- Focused proof: `dotnet build product/AeroLink.slnx`; `dotnet test <project> --filter <class>`; in
  `product/client`, `npm run typecheck`, `npx oxlint src tests`, and `npm run test:fast:logic`; a focused
  `npx playwright test <spec>` for journeys. Capture `git rev-parse HEAD` before and after long runs.
- After documentation edits, run `product/scripts/Test-RepositoryLayout.ps1`.
- Report production and test line changes separately (`git diff --numstat`).

## Handoff

Use the three reporting headings in `AGENTS.md` (Blocked on me, Changed, Found). Under them, report the removed
low-value categories; production simplifications; retained false positives and why they stay;
the proof actually run; production versus test line counts; issues filed; and follow-ups.
