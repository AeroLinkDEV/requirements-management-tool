(Retained copy of the packet submitted to Astra via Sean on 2026-09-24: implementation v4 — I07 closed with POSIX-executed regressions; candidate 2e6bb58c.)

ASTRA REVIEW REQUEST — #1040 / PR #1098 — IMPLEMENTATION CANDIDATE v4 (I07 closed, POSIX-executed)

**Requested decision and next action:**
I07 is corrected in the existing PR #1098 worktree and the same two files; the four required integrated regressions were added and executed on POSIX; the requester and relevant contract suites were rerun; the new committed candidate with exact provenance and a literally corrected coverage statement is below. No retry-design rewrite, no #1066 code/head change, no rebase, no readiness label, no maintenance approval request, no live dispatch, no queue admission, no merge, no HOME action. Installation and rollback remain separately unresolved.

**CANDIDATE AND PR:**
- PR #1098 (draft, base main): new head **2e6bb58c** (full SHA in the commit list below).
- Commits: `eace33f6` → `efe0f53a` → `41b75ced` → `d35a7d1e` (v3) → **`2e6bb58c`** (I07: "Enforce failed pinned-read refusal; execute the harness on POSIX"). Worktree clean before and after; only the two files.

**I07 CLOSED — every command's status is explicit; failed reads refuse; stale files cannot supply evidence.**
Root cause exactly as found: `poll_pinned || poll_status=$?` suppresses errexit throughout the function, so its failed curl did not stop execution and the checker read the previous response file. Corrections in `poll_pinned` and `fetch_and_pin_run`:
1. Stale response/verdict files are removed BEFORE each read (`rm -f` of the pinned response and the check file) — an earlier success-shaped file can never supply evidence for a failed current read.
2. The curl exit status is captured explicitly; a failed GET emits `REFUSED pinned-run read failed (curl exit N)` and the function returns nonzero — that poll stops before its response file is interpreted.
3. The checker's nonzero status is preserved (`|| py_status=$?`, no unconditional `|| true`); its diagnostic text is retained (the REFUSED verdict travels in the check file and is surfaced), and a failed checker → `REFUSED pinned-run verification failed` + nonzero return.
4. The caller guards the invocation, reads the verdict from the check file, and REJECTS before RUNNING or SUCCEEDED can bind: `FAILED*|REFUSED*` → `::error::` with the pinned run id/attempt and reason → exit 1. `RUNNING`/`SUCCEEDED` are only accepted when the poll returned zero.
5. The pinning wrapper has the same explicit status handling: failed fetch → `::error::Pinned-run fetch failed (curl exit N)` + exit 1; checker failure/empty verdict → refusal + exit 1.
No `set -e` reliance inside OR-list-called functions remains; nothing is suppressed with unconditional `|| true`.

**REQUIRED INTEGRATED REGRESSIONS — executed on POSIX (Linux container, node:24 / python 3.12, `--network none`, read-only worktree mount; all after a valid pin unless stated):**
1. *Transport failure leaving an earlier success-shaped response file*: a stale success body is pre-seeded in the runner temp; the poll GET injects exit 28 without writing any response → requester refuses (`pinned-run read failed`), the stale file is never interpreted; status 1, 1 POST.
2. *Failed HTTP request carrying a success-shaped body*: the poll answers 500 with a success-shaped body (exit 22) → refused (`pinned-run read failed`); 1 POST.
3. *404 after pinning*: poll GET → 404 (exit 22) → refused; 1 POST.
4. *Checker failure*: the served record is syntactically valid JSON but not a run object → the checker crashes (nonzero, empty verdict) → refused (`verification failed`); 1 POST.
Every one of the four asserts: refusal marker, **no successful binding summary (empty step summary)**, **no qualification request (jobs-requests empty)**, **no additional dispatch (postCount exactly 1)**.

**EXECUTED QUALIFICATION (two environments):**
- **Linux container** (`docker run --rm --network none -v <worktree>:/work:ro -w /work -e AEROLINK_TEST_PYTHON=/usr/bin/python3 node:24 bash -c "node --test ..."`, Node 24.21 / Python 3.12 / Bash 5.2): **28 tests / 28 passed / 0 failed / 0 skipped.**
- **Windows local** (Git Bash + real Windows Python via `AEROLINK_TEST_PYTHON`): **18 passed / 10 skipped (POSIX-only integrated scenarios, accurately disclosed) / 0 failed.**
- Every `product/test-planner/tests/*.test.mjs` (15 files): 0 failures.

**CORRECTED COVERAGE ACCOUNTING (literal):**
- Predating-requester and malformed-shape refusal cases: **unit-level coverage** of the gate Python (executed); not separately exercised through the integrated dispatcher.
- The existing 404 scenario **fails during initial pin acquisition**; it does not establish a successful pin first. Post-pin 404/trust/attempt refusal coverage is the new regression set above.
- The consumed-requester fixture is a **static gate-refusal fixture** (the requester history contains the consuming run); it is not an executed sequential queued-successor schedule. The spend-once refusal itself is asserted; a time-ordered two-requester schedule is remaining coverage.
- Pagination: the **unit test asserts the bounded walk and refusals**; **the integrated multi-page events scenario asserts the actual requested query-correct URLs** (`?page=1`, `?page=2` consumed by the gate). The unit mock now also logs its requested URLs (walk length asserted; per-URL form asserted in the integrated scenario).
- The genuine Linux 28/28 result is retained as executed evidence.

**CONFIRMATION:** #1066 remains draft, auto-merge disabled, clean at `027c985e82d1d1b764a2f24eac6e6c2ed4ec2c71`; its worktree untouched (verified this return). #1040 open. Checkpoint D NOT passed. D01–D03 closed. No HOME qualification repeat, no #1066 rebase, no product-code change, no readiness label, no maintenance approval request, no live dispatch, no queue admission, no merge, no HOME action.

Please return your verdict and the precise next action — I will wait for Sean to relay your actual response.

CHECKPOINT D — AWAITING ASTRA REVIEW. Queue admission, merge, rollout and issue closure remain gated.
