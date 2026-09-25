(Retained copy of the exact Checkpoint C Round 3 packet submitted to Astra via Sean on 2026-09-22. Supersedes file 21, which was the same submission mislabeled ROUND 2 before the #1072 merge was incorporated.)

ASTRA REVIEW REQUEST — #1040 — CHECKPOINT C — ROUND 3

**Requested decision and next action:**
Review the bounded corrections to the C-R2 blocking findings (C1040-C03 copied-host, C05 contract tests, C04 accounting). On PASS, the permitted next action is applying `ready-for-full-ci` to PR #1066 at the exact head below and proceeding to Checkpoint D. No Full-CI request, queue admission, auto-merge, rollout or issue closure has been made.

**Exact new candidate and status:** HEAD `e0509c0cb292d5c9f952cc108226d20ba918cb8e`, worktree clean, branch `glm/1040-release-picker-snapshot`, pushed. **Refreshed main/mergeability:** origin/main advanced twice during this round — `cf49875d` (merged as `e67e8bd4`) and `58442182`/#1072 (merged as `e0509c0c`, resolving renewed conflicts in `api-test-intent.json`, `api-host-classification.json`, `tests/inventory.test.mjs`, `API_TEST_INTENT_INVENTORY.md` by regeneration over the combined source). GitHub reports `mergeable: MERGEABLE`, `mergeStateStatus: BLOCKED` only by draft/label gates. Sixteen commits over the reviewed baseline `b64301b1`. The reviewed-baseline and prior-candidate provenance statements carry forward with the correction noted in C04.

**C1040-C03 — DISPOSITION: copied-host qualification fixed and passing; both fixture defects repaired.**
- **WAL (the Round-3 reported failure):** all template connections are now **nonpooled** (`Pooling=False` on the setup context, the raw-fixture helper and the checkpoint connection — a pooled handle had kept the `-wal` file alive after the checkpoint, and disposal ordering differed between configurations, which is also the honest reconciliation of my earlier 5/5 local result versus Astra's clean-rebuild failure: the passing run had not exercised the refusal path under Release). After the checkpoint the connection is closed and the test **asserts no `-wal` file exists** before `AeroLinkApiFactory(showcaseTemplate:)` is constructed. The factory's fail-closed seam remains untouched.
- **Guid casing:** all raw fixture inserts now bind **Guid values** (never `.ToString()`), so the stored text is the provider's uppercase representation, matching EF's own bindings — the legacy row stays inside the EF project identity and the host-backed read returns **both** rows.
- Retained and passing: startup completes through Program.cs on the copied database; the three guards exist on the served database; the legacy row keeps `PickerLegacyCohort=1/NULL` ordinal (no reclassification); the pre-existing allocated ordinal survives; a host-backed link-options read returns `[BUILD-1.0, BUILD-1.5]`; a new insertion through the served host allocates ordinal 2. Factories and clients disposed.
- The standalone copy/open/installer segment remains removed as previously directed; installer coverage stays in the first-install/restart/rollback/matrix tests.

**C1040-C05 — DISPOSITION: contract inventory expectations updated to current source; suite green.**
`inventory.test.mjs` snapshots updated (assertions strengthened, none weakened): fresh-host summary `{ classes: 62, tests: 380, knownCases: 444 }`; CLI rows `fresh-host 62 380 444 0 38.1%` and `reusable-host 57 350 396 0 35.1%`; `intentArtifact.totals.tests` 995 → merged-tree value **998** and cases **1138**; `hostArtifact.totals.knownCases` **1138**. Classification holds, provenance checks, source-equality and case-join assertions preserved. `node --test tests/inventory.test.mjs tests/routes.test.mjs` → **33/33 passed** on the merged tree.

**C1040-C04 — DISPOSITION: provenance and accounting corrected.**
- **Provenance statement (corrected wording):** the Round-2-retained green gate ran at `599e742c6c32cc7080517ab7395b3afc56e1ecf3` with clean before/after status; Astra verified product/src and product/tests are unchanged between `599e742c` and `dafb08c8` — that is **verified content equivalence**, and the packet no longer describes the runs as executing at the final SHA. For THIS round's candidate, the required PostgreSQL gate was re-run and recorded: HEAD and clean status recorded before (`8b2df2e6`, the copied-host fix commit) and after (`da37503d`, contract-span refresh) — with the final merged head `e0509c0c` adding only the #1072 merge (whose picker-relevant content is unchanged; the picker set was re-run on the merged head, 14/14).
- **PR body corrected** via `gh pr edit`: fourteen focused SQLite picker cases (nine membership/access + five guard); explicit statement that **no clean exact-final-head full-suite result exists**, with the retained history (1123/6 → 1128/1 single-failure-fixed, one independent rebuild failure pre-dating the final fixes) and resource degradation as an unconfirmed hypothesis.
- **Browser evidence references corrected**: `browser-results-c3`/`browser-report-c3` = single-journey verification of the rewritten journey; `browser-results-c2`/`browser-report-c2` = the earlier combined run whose four failures (two pre-existing old-spec repeats, two earlier picker attempts pre-dating the journey rework) are retained and attributed; the discoverability claim is dropped (the pattern matched no spec).

**Commands and results, tied to the candidate:**
1. `Test-ProjectSetupPostgres.ps1` (final, clean before/after at the committed content) → **Infrastructure 11/11 passed; Api 11/11 passed; none skipped** (`trx-required-pg-c3/`). All eleven Api results Passed (the eleven named in Round 2 plus `Picker_continuation_on_a_larger_fixture_stays_bounded_and_records_latency`).
2. Focused SQLite: both picker classes together → **14/14 passed, no host abort**, in Debug and **Release** (the configuration where the Round-2 rejection reproduced), and on the merged head (14/14).
3. Contract suite → **33/33 passed**; generators ×2 byte-stable; layout guard passed; planner Fast passed on the final head (all four areas selected).

**R01–R12 status:** R01–R10 complete (R06 closed with executed evidence; R09 browser journey real; R08 with 601-candidate latency/plan evidence; R10 local work done). R11, R12 — not started (D/E).

**Remaining limits:** no clean exact-final-head full local API-suite rerun (deferred to D per agreement; CI authoritative); pre-existing showcase spec not repetition-safe (independent, demonstrated); scale latency measured locally only.

**Prior dispositions:** C1040-C01, C02, C04 and the small corrections remain closed (merge regenerated and now conflict-free; owned-fixture repetitions pass; 40-char labels and >640 assertion in place; message-asserted guard matrices; corrected accounting). A/B findings remain closed.

**Recommended verdict and why:** PASS for Checkpoint C — the three blocking findings are repaired with executed evidence on the exact candidate: the copied host starts through Program.cs from a checkpointed, WAL-free, Guid-consistent template and serves both rows with correct new allocation; the contract suite is green against current-source counts with assertions preserved; and the provenance/accounting now matches the retained runs without inflation. The earlier accepted architecture and all closed findings are unchanged.

Please independently return PASS, CHANGES REQUIRED, or NOT REVIEWABLE, identify the exact SHA/design and scope reviewed, list actionable findings, and state which next phase may proceed. I will wait for Sean to relay your actual response.

CHECKPOINT C — AWAITING ASTRA REVIEW. D/E gates remain; no ready-for-full-ci label, no queue admission, rollout or issue-closure action has been taken.
