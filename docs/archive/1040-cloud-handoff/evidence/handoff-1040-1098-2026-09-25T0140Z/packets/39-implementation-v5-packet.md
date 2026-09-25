(Retained copy of the packet submitted to Astra via Sean on 2026-09-24: implementation v5 — the four I07 regressions actually committed and POSIX-executed at candidate bf9dc3f0.)

ASTRA REVIEW REQUEST — #1040 / PR #1098 — IMPLEMENTATION CANDIDATE v5 (the I07 regressions actually committed)

**Requested decision and next action:**
You were right: the four post-pin regressions were never committed — my editing helper exited on an anchor mismatch before writing, so `2e6bb58c`'s test diff contained only the pagination logging lines while the packet claimed coverage the file did not hold. The accounting error is mine and is corrected here. The workflow correction from v4 is kept unchanged (your independent replay of both I07 probes confirmed it). The four regressions are now IN the committed test file, inspected in the diff, and executed on POSIX at the new candidate. No design change, no #1066 change, no rebase, no readiness label, no maintenance approval request, no live dispatch, no queue admission, no merge, no HOME action. Installation and rollback remain separately unresolved.

**CANDIDATE AND PR:**
- PR #1098 (draft, base main): new head **bf9dc3f039c93463209c6e0b205d8ccbccce8da1**.
- Commits: `eace33f6` → `efe0f53a` → `41b75ced` → `d35a7d1e` (v3) → `2e6bb58c` (v4: I07 workflow correction — KEPT) → **`bf9dc3f0`** ("Commit the four post-pin pinned-read refusal regressions" — test file only).
- Diff of `bf9dc3f0` vs `2e6bb58c`: one file (the contract test), +87/−2 — the four regression tests plus the verbatim-record and stale-leftover fixture support they need. Worktree clean before and after; provenance recorded.

**THE FOUR COMMITTED REGRESSIONS** (all `{ skip: skipIntegrated }` — POSIX-only, in the integrated harness; all "after a valid pin": `pinnedFinalAfter=2` makes the FIRST pinned-record read serve the original in-progress record so the pin succeeds, and injection begins at the second read):
1. *post-pin transport failure with a stale success-shaped file* — mock exits 28 on the poll read without writing any response; a stale success-shaped `pinned-101.json` is pre-seeded in the runner temp; `poll_pinned` removes it before the read (asserted: present before, absent after) and the stale contents are never interpreted.
2. *post-pin failed HTTP carrying a success-shaped body* — mock answers the poll with HTTP 500 and a success-shaped body, exit 22 → refused.
3. *post-pin 404* — refused.
4. *checker failure on an invalid run-record shape* — the served record is verbatim `[]` (valid JSON, wrong shape); the checker crashes nonzero with an empty verdict → refused (`verification failed`).
Each asserts: **the original pin occurred before the failure injection** (`pinnedGets` reaches exactly 2: one read during pinning, one failed poll), **exactly one dispatch POST**, **a meaningful refusal diagnostic**, **an empty binding summary**, and **zero qualification requests after the failed poll** (jobs-requests empty — no bind, no per-attempt evidence from a failed poll).

**EXECUTED QUALIFICATION (this time on the committed file, two environments):**
- **Linux container** (`docker run --rm --network none -v <worktree>:/work:ro -w /work -e AEROLINK_TEST_PYTHON=/usr/bin/python3 node:24 ...`, Node v24.21.0 / Python 3.11.2 / Bash 5.2.15 — matching your observed runtime): **32 tests / 32 passed / 0 failed / 0 skipped**, including the four new regressions. Command: `node --test product/test-planner/tests/full-ci-readiness-dispatch.test.mjs`.
- **Windows local** (same command, real Windows Python via `AEROLINK_TEST_PYTHON`): **18 passed / 14 skipped (10 POSIX-only integrated scenarios + 4 new regressions) / 0 failed.**
- The mock's injected failures answer 404/500 with exit 22 — the same surface real curl gives `--fail-with-body` — so the caller's handling is exercised through its actual code path.

**FILE DIGESTS at `bf9dc3f0`** (for your independent execution):
- `product/test-planner/tests/full-ci-readiness-dispatch.test.mjs` — SHA-256 `F46672C9E69EB15D935E7DF4DC81B33668AAF6831A54B5D8A62F2C065157AF86`
- `.github/workflows/request-full-ci.yml` — SHA-256 `B713E27E9F5BF9BDE35362864E9FF8E1A94F3DEB6E7DCE444944A297D2C6D46F`

**CORRECTED COVERAGE ACCOUNTING (literal, supersedes earlier packets):**
- Executed integrated scenarios on POSIX (this candidate, 32-test file): successful NONE/EXHAUSTED dispatch + binding with pinned attempt and exact per-attempt jobs request; refusal-before-POST for stale/consumed/predating/rerun/missing-self; lost-response recovery and never-resolution; post-pin transport-exit-28 with stale file, post-pin failed HTTP with success-shaped body, post-pin 404; trust-change, attempt-change, 404-after-pinning, checker-crash refusals; authoritative-success reuse; multi-page events with query-correct URLs.
- Unit-level only (executed): gate Python verb/refusal families incl. predating-requester and malformed-shape/timestamp cases; pinned-run checker pin/poll modes; bounded pagination walk and URL forms.
- Structural (regex) only: preserved live-PR/queue/Product/App controls.
- Not covered: an executed time-ordered two-requester queued-successor schedule (the spend-once consumption is asserted as a static gate refusal; the schedule itself remains remaining coverage).
- The Windows 18-pass/14-skip result is a disclosed platform limitation, not integrated qualification; the Linux 32/32 is the qualification evidence.

**CONFIRMATION:** #1066 remains draft, auto-merge disabled, clean at `027c985e82d1d1b764a2f24eac6e6c2ed4ec2c71`; worktree untouched (verified this return). #1040 open. Checkpoint D NOT passed. D01–D03 closed. No HOME qualification repeat, no #1066 rebase, no product-code change, no readiness label, no maintenance approval request, no live dispatch, no queue admission, no merge, no HOME action.

Please return your verdict and the precise next action — I will wait for Sean to relay your actual response.

CHECKPOINT D — AWAITING ASTRA REVIEW. Queue admission, merge, rollout and issue closure remain gated.
