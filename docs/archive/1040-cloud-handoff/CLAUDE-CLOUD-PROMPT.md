# Claude: complete issue #1040 from the cloud

Sean has asked you to take over from GLM and finish issue #1040 through verified deployment and truthful closure. The existing feature implementation is mostly complete. Your work is to establish trustworthy state, finish real implementation/qualification gaps, resolve a compliant integration route, and coordinate local-only acceptance with Astra/Sean.

Work autonomously between the review checkpoints below. Astra is the independent reviewer; Sean relays your packets and Astra's actual replies. Do not manufacture an Astra verdict, interpret a recommendation as approval, or assume an old review qualifies changed content.

## 1. Establish the cloud starting point

Read START-HERE.md, ASTRA-CORRECTIONS.md, IMPLEMENTATION-AND-ACCEPTANCE.md, CI-RUNS.md and LOCAL-OPERATOR-HANDBACK.md in this directory. The older GLM packets are historical inputs with identified errors. Read the actual source and retained results behind a material claim.

Refresh issue #1040, PR #1066, PR #1098, protected main, mergeability, required checks and queue state from GitHub. Read current AGENTS.md and the applicable product/decisions/operations/merging/testing contracts. Current code and accepted decisions outrank a dated handoff.

Starting feature head: `027c985e82d1d1b764a2f24eac6e6c2ed4ec2c71`, remote branch `glm/1040-release-picker-snapshot`, PR #1066.

Starting requester head: `23bb25d987639ada3356a7003fa0380ba08e5042`, remote branch `ci/request-full-ci-same-head-retry`, PR #1098. Its reviewed base is `bac43d094cff642766eda2b91fd9d6b41d15a23f` and its diff contains exactly the workflow and requester contract-test file.

Both implementation PRs were draft with failed cited exact-head Product gates. Preserve those heads and their evidence until the delivery-route review authorizes any change. Use an owned cloud checkout/worktree for work. Do not base new work on the accidental Windows commits or merge this evidence-only handoff branch into an implementation branch.

Verify that the fetched code resolves to the intended full SHA. In an existing clone, a read-only way to retrieve the two PR objects without changing its checkout is `git fetch origin refs/pull/1066/head refs/pull/1098/head`; verify each named SHA resolves afterward. Create any working checkout explicitly and inspect its root/status before edits.

## 2. Separate cloud work from local recovery

GLM damaged Sean's canonical Windows local main. Details, patch, reflog and preservation bundle are available here. Cloud GitHub main was not shown to contain that incident.

Do not attempt to access a fabricated Windows path from Linux or claim cloud commands repaired Sean's machine. Prepare the bounded recovery packet in LOCAL-OPERATOR-HANDBACK.md for Astra/Sean. Continue independent cloud source review, acceptance mapping and delivery planning while local recovery is pending.

The local operator must obtain Astra's recovery review before resetting/moving refs, removing the drill branch or changing author configuration. Preserve unrelated and newly discovered work. The previous local main SHA and current remote main SHA represent different states; do not silently substitute one for the other.

Never execute the inherited rehearsal scripts. A replacement rehearsal must explicitly validate its disposable root and repository identity and abort on failed setup/clone/path checks. Every mutating Git call must target the verified disposable repository. Prove failed preparation causes zero canonical ref/tree/config changes. Do not rely on unguarded cd, `set -e` alone, or automatic conflict resolution.

## 3. Preserve the accepted feature architecture

Review the actual code map in IMPLEMENTATION-AND-ACCEPTANCE.md. The insertion ordinal freezes membership while canonical order, exact IDs, labels and deep links remain unchanged. PostgreSQL allocation is sequence-based after the project fence; page one locks then reads cutoff in Read Committed; continuation membership is applied before keyset paging and Take. Legacy NULL means the pre-feature schema cohort. SQLite has its separate operational cohort flag and installed triggers. EF generated-value/readback configuration is deliberate. Cursor v2 is Release-specific and bounded; non-Release cursors stay unchanged. Authorization stays live.

Carry forward valid A/B/C evidence where the relevant content is demonstrably unchanged. Reopen real defects, missing proof and changed behavior; do not repeat the baseline campaign or redesign the feature merely because a new agent arrived.

Build an R01–R12 acceptance matrix linking actual source, tests, immutable SHAs, providers, commands, results and artifacts. The copied issue JSON supplies the complete starting contract; refresh the live issue before editing it.

## 4. Resolve the actual delivery blocker before changing heads or retrying CI

The feature's cited Product run 35845728143 and the requester's cited Product run 36064279956 failed. Their requester runs refused success binding. Preserve failures and diagnostics. A passing local test is not green hosted Full Product evidence.

Current-main requester behavior historically selects the already failed same-head attempt instead of dispatching anew. A human rerun fails the bot-triggering-actor trust rule. Waiting for a fix on main and simply relabeling the old candidate does not change that candidate or the existing failed-run selection. Validate current code before proposing any route.

The #1098 change is a protected approval-kernel change. Installing it to obtain its own missing qualification is circular. The routine maintenance route refuses it. DEC-121's PR #960 installation exception is spent. DEC-124/DEC-135 authorize otherwise-qualified routine delegated maintenance approvals; they do not waive kernel refusals, failed proof, required checks or separately reviewed transition requirements.

CHECKPOINT 1 — TAKEOVER AND DELIVERY ROUTE:
Submit to Astra the verified state, corrected evidence map, local recovery hand-back, and the smallest compliant delivery plan. Explain whether #1040 can proceed through existing controls with a legitimately updated, newly reviewed feature candidate, or whether a separate CI dependency truly must be completed first. Do not treat installing #1098 at any cost as the objective.

State exactly which candidate(s) would change, why, how current trusted machinery would produce and accept qualification, what review/authority is required, and how any necessary kernel installation and rollback would work. A genuine new candidate has its own qualification obligations; no empty commit merely to hide red evidence. A fresh SHA alone does not solve kernel installation.

Wait for Astra's actual reply before altering the frozen PR heads, changing readiness labels, requesting retries/dispatches or taking protected transition actions. Continue independent authorized investigation. If genuinely new owner authority is required, prepare one concrete executable proposal with risks/restoration, then ask only for that new decision. Do not recycle a disproven proposal.

## 5. Implement and qualify the reviewed route

After approval, work in the appropriate owned cloud worktree and keep feature and CI scopes separate. Preserve others' work; stage explicit paths. Follow the changed-area planner. Record exact HEAD and dirty status before/after long tests, and use `--no-build` only for known exact-content binaries. Regenerate affected contracts twice; require the second run to be stable. Keep API test counts, actual command results, failed/skipped status and provider distinctions accurate.

Use disposable PostgreSQL and SQLite for qualification. The cloud has no access to Sean's persistent port 54329 by default; never point migration tooling there or invent credentials. Do not copy persistent HOME data into cloud fixtures.

Behavioral assertions must exercise the actual implementation. Integrated, helper-unit, structural and prototype evidence are distinct. If working on #1098, close any material coverage gaps selected by the review through its real shell/Python harness, including temporal scheduling if claimed. Archived sanitized probe copies are not executable qualification inputs; use the unmodified reviewed source on the implementation branch.

For CI failures, retain diagnostics and report before another attempt. Do not weaken tests, broaden timeouts without evidence, change trusted publishers, fabricate checks, or repeat until green. The observed browser intermittent has unresolved cause; local coordinate correction withdrew GLM's overflow/transient-only-pass assertions.

CHECKPOINT 2 — FINAL CANDIDATE AND CI REQUEST:
Present the exact committed head(s), changed files, review dispositions, executed qualification and remaining limitations, and the precise CI request. Obtain Astra's actual decision before that action. Old green runs do not qualify a new head or attempt.

## 6. Integrate and finish

CHECKPOINT D — INTEGRATION/ROLLOUT READINESS:
Before queue admission or merge, provide green exact-head trusted Full Product evidence, run/attempt and publisher identities, fresh main/mergeability/queue state, reviewed composed-candidate scope and required checks, plus the supported HOME upgrade/recovery plan. Use the ordinary protected queue where applicable. Do not rebase solely because main advanced when the queue can validate the composition.

Under DEC-135, submit otherwise-qualified routine delegated maintenance approvals when applicable without asking Sean to reconfirm every digest; record that this is delegated, not independent human review. This does not authorize a kernel exception or self-approval of review.

After actual Astra integration approval, complete the authorized integration. Coordinate the Windows/HOME operations with Astra/Sean using LOCAL-OPERATOR-HANDBACK.md. A cloud limitation does not invalidate cloud work, and cloud success does not prove HOME acceptance.

CHECKPOINT E — DEPLOYED ACCEPTANCE:
Obtain attributable local evidence of the intended protected-main deployment identity and ancestry to the feature merge, supported migration application, healthy serving state, and focused read-only picker acceptance on existing records. If other changes deploy later, record both commits precisely instead of claiming the merge SHA is the deployed SHA. Never reset/reseed/truncate persistent data, rewrite signatures/manifests, or apply unmerged tooling to HOME.

Submit final R01–R12 results and local acceptance to Astra. After actual acceptance, close #1040 with accurate PR/merge/CI/deployment evidence. Keep an unresolved separate #1098 workstream explicitly dispositioned; do not silently close it to make #1040 appear complete.

## 7. Communication

Each packet states the requested decision/next action, full head/base SHA, worktree and dirty status, reviewed scope, exact commands/results/provider/runtime, artifact links, open findings and uncertainty. Keep Sean informed, group related questions, and distinguish executed facts from proposals.

Continue to completion between required gates. Do not stop at a plan, local green tests or PR merge. Begin with cloud state refresh, the evidence/correction map and delivery-route analysis; hand local recovery to Astra/Sean rather than waiting for nonexistent cloud access to the Windows machine.
