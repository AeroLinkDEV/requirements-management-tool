(Retained copy of the exact Checkpoint C Round 3 packet submitted to Astra via Sean on 2026-09-22. Supersedes file 21, which was mislabeled ROUND 2.)

ASTRA REVIEW REQUEST — #1040 — CHECKPOINT C — ROUND 3

**Requested decision and next action:**
Review the bounded corrections to the C-R2 blocking findings (C1040-C03 copied-host, C05 contract tests, C04 accounting). On PASS, the permitted next action is applying `ready-for-full-ci` to PR #1066 at the exact head below and proceeding to Checkpoint D. No Full-CI request, queue admission, auto-merge, rollout or issue closure has been made.

**Exact new candidate and status:** HEAD `e0509c0cb292d5c9f952cc108226d20ba918cb8e`, worktree clean, branch `glm/1040-release-picker-snapshot`, pushed. **Refreshed main/mergeability:** origin/main advanced twice during this round — `cf49875d` (merged as `e67e8bd4`, Round 2) and `58442182`/#1072 with #1067/#1070 (merged as `e0509c0c` this round, resolving renewed conflicts in `api-test-intent.json`, `api-host-classification.json`, `tests/inventory.test.mjs`, `API_TEST_INTENT_INVENTORY.md` by regeneration over the combined source). GitHub reports `mergeable: MERGEABLE`, `mergeStateStatus: BLOCKED` only by draft/label gates. Commit count over the reviewed baseline `b64301b1`: **24** (counted by rev-list after the #1064/#1065/#1067/#1070/#1072 merges were brought in; accounting corrected — earlier packets understated the count).

**C1040-C03 — DISPOSITION: copied-host qualification repaired; both fixture defects fixed and verified in Debug and Release.**
- **WAL persistence (the reported rejection):** every template connection is now **nonpooled** (`Pooling=False` on the setup context, the raw-fixture helper and the checkpoint connection — a pooled handle had kept the `-wal` file alive after the checkpoint, and disposal ordering differed between Debug and Release, which is also the honest reconciliation of my earlier 5/5 local result versus Astra's clean-rebuild failure: the passing run had not exercised the refusal path under Release). After the checkpoint the connection is closed and the test **asserts no `-wal` file exists** before `AeroLinkApiFactory(showcaseTemplate:)` is constructed; the factory's fail-closed seam is untouched.
- **Guid casing:** all raw fixture inserts bind **Guid values** (never `.ToString()`), so the stored text is the provider's uppercase representation, matching EF's own bindings — the legacy row stays inside the EF project identity and the host-backed read returns **both** rows `[BUILD-1.0, BUILD-1.5]`.
- Retained and passing: startup completes through Program.cs on the copied database; the three guards exist on the served database; the legacy row keeps `PickerLegacyCohort=1/NULL` ordinal (no reclassification); the pre-existing allocated ordinal survives; a host-backed link-options read returns both rows; a new insertion through the served host allocates ordinal 2. Factories and clients disposed.
- The standalone copy/open/installer segment remains removed as previously directed; installer coverage stays in the first-install/restart/rollback/matrix tests.

**C1040-C05 — DISPOSITION: contract inventory expectations updated to current source; suite green.**
`inventory.test.mjs` snapshots updated (assertions preserved, none weakened): fresh-host summary `{ classes: 62, tests: 380, knownCases: 444, unknownCaseTests: 0 }`; CLI rows `fresh-host 62 380 444 0 38.1%` and `reusable-host 57 350 396 0 35.1%` (verified against the generator's actual output on the merged tree); `intentArtifact.totals.tests` **998** and cases **1138**; `hostArtifact.totals.knownCases` **1138**. Classification holds, provenance checks, source-equality and the case-join test are preserved. `node --test tests/inventory.test.mjs tests/routes.test.mjs` → **33/33 passed** on the merged tree.

**C1040-C04 — DISPOSITION: provenance and accounting corrected.**
- **Provenance (precise):** the retained green gate from Round 2 ran at `599e742c` (clean before/after) on content that Astra verified unchanged through `dafb08c8` — described as **verified content equivalence**, not "ran at the final SHA". For THIS round: the reworked copied-host test and the full required gate were re-run and recorded — HEAD and clean status recorded **before** (`8b2df2e6…` fix commit; gate run `trx-required-pg-c3/`: Infrastructure 11/11, Api 11/11, none skipped) and **after** (`da37503d` contract spans, then the #1072 merge) — with the picker set re-run on the merged head: 14/14. The merge's picker-relevant C# content is unchanged from `8b2df2e6`; the authoritative broad-suite evidence remains the GitHub Actions lane at Checkpoint D on `e0509c0c`.
- **PR body corrected** via `gh pr edit`: fourteen focused SQLite picker cases (nine membership/access + five guard); explicit statement that **no clean exact-final-head full-suite result exists**, with the retained history (1123/6 → 1128/1, the single failure fixed and passing since; one independent rebuild failure pre-dating the final fixes) and resource degradation as an unconfirmed hypothesis.
- **Browser evidence references corrected:** `browser-results-c3`/`browser-report-c3` = single-journey verification of the rewritten journey; `browser-results-c2`/`browser-report-c2` = the Round-2 combined run (whose four failures comprise the two pre-existing old-spec repeats and two earlier picker attempts pre-dating the journey rework — all retained and attributed); the discoverability claim is dropped (the pattern matched no spec).

**Changed files this round:** `ReleasePickerSqliteGuardTests.cs` (copied-host fixture: nonpooled template, Guid-value binding, WAL-absence assert, template lifecycle cleanup), `ReleasePickerMembershipApiTests.cs` (factory disposal), `tests/inventory.test.mjs` (merged-tree count expectations), regenerated `api-test-intent.json`/`API_TEST_INTENT_INVENTORY.md` spans, and the #1072 merge resolution of the four contract files. Product source unchanged from the approved implementation.

**Commands and results, tied to the candidate:**
1. HEAD/dirty before the gate: `8b2df2e6` (copied-host fix), clean.
2. `Test-ProjectSetupPostgres.ps1` → **Infrastructure 11/11 passed; Api 11/11 passed; none skipped** (`trx-required-pg-c3/`); picker set re-run on the merged head → **14/14 passed** (Debug and Release).
3. Contract suite → **33/33 passed**; generators ×2 byte-stable; layout guard passed; planner Fast passed on the final head (all four areas selected).
4. Browser requalification: picker journey ×2 PASSED; old spec alone ×2 → the same 2 failures reproduced (pre-existing, independent); single-run ×2 → 10/10 passed. Dedicated dirs per purpose; all failures accounted.
5. HEAD/dirty after everything: `e0509c0c`, clean.

**R01–R12 status:** R01–R10 complete (R06 closed with executed evidence; R09 browser journey real; R08 with 601-candidate latency/plan evidence; R10 local work done). R11, R12 — not started (D/E).

**Remaining limits:** no clean exact-final-head full local API-suite rerun (deferred to D per agreement; CI authoritative); pre-existing showcase spec not repetition-safe (independent, demonstrated); scale latency measured locally only.

**Prior dispositions:** C1040-C01, C02, C04, C05 and the small corrections remain closed (merge regenerated and now conflict-free; owned-fixture repetitions pass; 40-char labels and >640 assertion in place; message-asserted guard matrices; corrected accounting). A/B findings remain closed.

**Recommended verdict and why:** PASS for Checkpoint C — the three blocking findings are repaired with executed evidence on the exact candidate: the copied host starts through Program.cs from a checkpointed, WAL-free, Guid-consistent template and serves both rows with correct new allocation; the contract suite is green against current-source counts with assertions preserved; and the provenance/accounting now matches the retained runs without inflation. The earlier accepted architecture and all closed findings are unchanged.

Please independently return PASS, CHANGES REQUIRED, or NOT REVIEWABLE, identify the exact SHA/design and scope reviewed, list actionable findings, and state which next phase may proceed. I will wait for Sean to relay your actual response.

CHECKPOINT C — AWAITING ASTRA REVIEW. D/E gates remain; no ready-for-full-ci label, no queue admission, rollout or issue-closure action has been taken.
