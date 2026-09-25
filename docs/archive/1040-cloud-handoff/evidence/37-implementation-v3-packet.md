(Retained copy of the packet submitted to Astra via Sean on 2026-09-24: implementation v3 — POSIX-qualified candidate d35a7d1e, executed in a disposable Linux container.)

ASTRA REVIEW REQUEST — #1040 / PR #1098 — IMPLEMENTATION CANDIDATE v3 (POSIX-qualified)

**Requested decision and next action:**
The two remaining findings are corrected in the existing PR #1098 worktree and the same two files; the integrated coverage was repaired and EXECUTED in a disposable POSIX environment; the candidate was committed and pushed with exact provenance and a corrected coverage statement. No #1066 code/head change, no rebase, no readiness label, no maintenance approval request, no live dispatch, no queue admission, no merge, no HOME action. Installation and rollback remain separately unresolved.

**CANDIDATE AND PR:**
- PR #1098 (draft, base main): new head **d35a7d1ebc63f116afbf5090373ea50e0ab72518**.
- Commits: `eace33f6` (initial) → `efe0f53a` (six corrections) → `41b75ced` (trace removal) → `d35a7d1e` (this round). Base unchanged `bac43d094cff642766eda2b91fd9d6b41d15a23f`. Worktree clean before and after; only the two files in every commit.

**I02 CLOSED — the pagination separator now derives from the base URL's own query string, for EVERY page.**
`collect_pages` computes `separator` once from the base URL (`case "$base_url" in *"?"*) separator="&" ;; *) separator="?" ;; esac`) and applies it uniformly; head_sha and event are preserved verbatim. The previous page-number-based rule (page 1 "?", later "&") is gone.
**Executed evidence — the integrated mock now validates every requested URL** against per-endpoint patterns (events: `/issues/1066/events?page=<n>&per_page=100`; requester: `.../runs?head_sha=<40-hex>&event=pull_request_target&page=<n>&per_page=100`; product: `.../ci.yml/runs?head_sha=<40-hex>&event=workflow_dispatch&per_page=100`) and answers malformed requests with 404/curl-exit-22 — so the exact defect Astra reproduced (malformed URLs accepted by a substring-recognizing mock) now fails the harness loudly. The container run's `requests.log` shows the corrected URLs as actually requested, including both pages of a multi-page events history (`?page=1`, `?page=2`) consumed by the gate (scenario below). The pagination unit test additionally asserts the actual requested URLs for both endpoint shapes (no-query base: `?page=N` on every page; query-bearing base: `&page=N` with head_sha/event intact) plus truncation refusal.

**I06 CLOSED — the integrated harness was repaired and EXECUTED on POSIX.** Astra's five sub-findings:
1. Product fixtures now sit on the integrated head/ref (`INTEGRATED_SHA` / `glm/integrated-branch`); `buildIntegrated` normalizes every record.
2. The consumed fixture includes the current requester (7000) AND the consuming run (6000) — it now tests consumption by another requester, not missing-self.
3. Mutation scenarios pin the original attempt first (`pinnedFinalAfter=2`: fetch_and_pin serves the original in-progress record, pin (101, attempt 1) succeeds) and serve the changed record only on a later direct poll — asserted via `pinnedGets >= 2` and the specific refusal (`trust identity changed` / `attempt changed`).
4. The lost-response scenario simulates an actual nonzero transport exit (curl exit 28) AFTER recording dispatch acceptance — the requester enters discovery-only, recovers the run, binds; exactly 1 POST (asserted). The never-resolves variant refuses after exactly 1 POST.
5. The mock emits multi-page Link headers (scenario-driven) and validates queries (above) — both covered by executed scenarios.
6. Failure assertions no longer assume stdout-only: the pinned-poll verdict travels through a file (`poll-check`) whose text is surfaced on stdout by the caller (`::error::` + reason) with nonzero exit; the 404 scenario asserts curl's exit-22 passthrough; every scenario asserts its POST count at the transport boundary.
7. The shim's PATH entry conversion is platform-appropriate (drive-letter conversion only on win32; POSIX paths pass through unchanged).

**EXECUTED QUALIFICATION (two environments, exact commands and results):**

1. **Disposable Linux container** (Astra's demonstrated route; image `node:24` — Node 24.21, Python 3.12; `--network none`; worktree mounted READ-ONLY; bash 5.2):
   `docker run --rm --network none -v <worktree>:/work:ro -w /work -e AEROLINK_TEST_PYTHON=/usr/bin/python3 node:24 bash -c "node --test product/test-planner/tests/full-ci-readiness-dispatch.test.mjs ..."`
   → **28 tests: 28 passed / 0 failed / 0 skipped.** All ten integrated scenarios executed on POSIX, including successful binding with the exact per-attempt jobs request, refusal-before-POST with zero POSTs, uncertainty recovery (1 POST), never-resolution (1 POST), queued-successor consumption, trust/attempt/404 refusals, multi-page events walked with query-correct URLs, and authoritative-success reuse.
2. **Windows local suite** (same command under Git Bash with a real Windows Python runtime) → **28 tests: 18 passed / 10 skipped / 0 failed** — the ten POSIX-only integrated scenarios are skipped with the documented reason (accurately disclosed limitation, not qualification); all unit/structural/shell-fragment coverage passes.
3. Every `product/test-planner/tests/*.test.mjs` (15 files) → 0 failures. Workflow YAML parse-validated.

**CORRECTED COVERAGE STATEMENT (accounting fixed per the review):**
- Implemented AND executed on POSIX (integrated harness): successful NONE/EXHAUSTED dispatch + binding with pinned attempt; refusal-before-POST for stale/consumed/predating/rerun/missing-self/malformed-shape; lost-response recovery and never-resolution; queued-successor consumption; trust/attempt/404 refusal after pinning; authoritative-success reuse; multi-page events; per-attempt qualification request exactness.
- Implemented, covered at unit level only (executed Windows + container): the embedded-Python helpers' full verb tables.
- Retained structural (regex) assertions: preserved live-PR/queue/Product/App controls.
- Remaining qualification limits (documented, unchanged): `return_run_details` shape requires pinned-API-version qualification at acceptance; no live dispatch/label was performed; installation and rollback remain separately unresolved — this draft and its tests do not establish an installation route.

**CONFIRMATION:** #1066 remains draft, auto-merge disabled, clean at `027c985e82d1d1b764a2f24eac6e6c2ed4ec2c71`; its worktree untouched (verified this return). #1040 open. Checkpoint D NOT passed. D01–D03 closed. No HOME repeat, no #1066 rebase, no product-code change.

Please return your verdict and the precise next action — I will wait for Sean to relay your actual response.

CHECKPOINT D — AWAITING ASTRA REVIEW. Queue admission, merge, rollout and issue closure remain gated.
