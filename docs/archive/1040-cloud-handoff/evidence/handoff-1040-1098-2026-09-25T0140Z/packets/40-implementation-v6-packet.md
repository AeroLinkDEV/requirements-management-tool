(Retained copy of the packet submitted to Astra via Sean on 2026-09-24: implementation v6 — fixture-wiring gap closed, POSIX re-executed at 23bb25d9.)

ASTRA REVIEW REQUEST — #1040 / PR #1098 — IMPLEMENTATION CANDIDATE v6 (fixture-wiring gap closed)

**Requested decision and next action:**
The bounded test-fixture correction is made in the existing PR #1098 worktree and the same two files; the POSIX requester suite was rerun at the new head; the new full SHA, accurate results, and the fixture assertions are below. No workflow redesign, no product change, no HOME qualification repeat, no #1066 head change, no readiness label, no maintenance approval request, no live dispatch, no queue admission, no merge. Installation and rollback remain unresolved; Checkpoint D remains pending.

**CANDIDATE AND PR:**
- PR #1098 (draft, base main): new head **23bb25d987639ada3356a7003fa0380ba08e5042**.
- Commits: `eace33f6` → `efe0f53a` → `41b75ced` → `d35a7d1e` → `2e6bb58c` (I07 workflow correction — unchanged from the accepted fix) → `bf9dc3f0` (the four committed regressions) → **`23bb25d9`** ("Normalize the pinned-read failure fixtures onto the integrated identity" — test file only).
- The I07 workflow correction itself: untouched since your acceptance.

**FIXTURE-WIRING GAP CLOSED (both findings):**
1. `buildIntegrated` now writes `pinned-read-body.json` from `scenario.pinnedReadBody`, normalized onto the integrated head/ref — the failed-HTTP mock delivers the named success-shaped body instead of the injected-failure default.
2. The stale pre-pin leftover file and the failed-response body are both built from the trusted successful record normalized onto `INTEGRATED_SHA` / `glm/integrated-branch` — identity mismatches can no longer mask the read-failure protection under test.

**ADDITIONAL ASSERTIONS (existing cases now exercise their stated inputs):**
- Transport-failure scenario precondition: the stale leftover file carries the trusted integrated identity (head_sha, head_branch, completed/success) before the run.
- Failed-HTTP scenario: the mock's actually-emitted failed-response body (read back from the runner temp) carries the normalized integrated identity (head_sha, head_branch, completed/success, attempt 1) — proving the named condition was delivered; the trusted body fixture itself is also asserted on disk (`pinned-read-body.json`).
- Existing refusal, empty-summary, zero-qualification-request and single-POST assertions unchanged and passing.

**EXECUTED QUALIFICATION:**
- **POSIX (Linux container)**: `docker run --rm --network none -v <worktree>:/work:ro -w /work -e AEROLINK_TEST_PYTHON=/usr/bin/python3 node:24 bash -c "node --test product/test-planner/tests/full-ci-readiness-dispatch.test.mjs"` → runtime: **Node v24.21.0, Python 3.11.2, Bash 5.2.15** → **32 tests: 32 passed / 0 failed / 0 skipped.**
- **Windows local** (Git Bash + real Windows Python): **18 passed / 14 skipped / 0 failed.**
- Test-file SHA-256 at `23bb25d9`: `7C14F14CAEE06D9C63887BCB0610AEE4C532A6658A0651DA9EB68A74A2CD659B`.

**CONFIRMATION:** #1066 remains draft, auto-merge disabled, clean at `027c985e82d1d1b764a2f24eac6e6c2ed4ec2c71`; its worktree untouched (verified this return). #1040 open. Checkpoint D NOT passed. D01–D03 closed. No readiness label, no maintenance approval request, no live dispatch, no queue admission, no merge, no HOME action.

Please return your verdict and the precise next action — I will wait for Sean to relay your actual response.

CHECKPOINT D — AWAITING ASTRA REVIEW. Queue admission, merge, rollout and issue closure remain gated.
