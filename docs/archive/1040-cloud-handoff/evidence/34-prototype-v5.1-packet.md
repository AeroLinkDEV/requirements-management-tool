(Retained copy of the packet submitted to Astra via Sean on 2026-09-23: prototype v5.1 — the three prototype-review departures fixed, with negative cases and fixture-faithful coverage.)

ASTRA REVIEW REQUEST — #1040 — OFFLINE PROTOTYPE v5.1 (the three departures fixed)

**Requested decision and next action:**
The three prototype-review departures are fixed in the owned evidence directory, the complete executable suite passes, and updated digests, results, additional negative-case outcomes, and fixture-based coverage are below. Nothing else changed: the approved v5 design stands; no new architecture or decision table is included. No repository implementation, candidate branch/PR, label change, live CI dispatch, queue admission or installation exists; #1066 remains draft at `027c985e82d1d1b764a2f24eac6e6c2ed4ec2c71` and the #1040 worktree remains clean at that SHA (re-verified immediately before this return).

**UPDATED PATHS + SHA-256 DIGESTS** (`C:\Sean Project\RMT-1040-glm-evidence\probe-matcher-extraction\`):
- `caller_model.py` — SHA-256 `9012E6AF7EFEFBB373F0B6CDBA613528D3E58B8325FC612567EC881E5736BE39`
- `test-caller-model.py` — SHA-256 `9C22E4925EFA9452643A544165BB45F0FE9179895B019E69236EEFB7C63003AD`
- `test-caller-model-output.log` — SHA-256 `83FA0427EE75A856A1C8DAB843C5F9378F8CFAA080348C45895BEA8C638D210A`

**EXACT COMMAND:** `python.exe test-caller-model.py` from that directory (runtime: `C:\Users\seanm\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe`). **Actual result: 23/23 passed.** Dispatch POSTs are recorded only inside `Api.dispatch()`.

**FIX 1 (CI1040-P01) — requester verification is now mandatory at the gate.** `freshness()` gained `require_self`; the dispatch gate always passes it. With `require_self`, a missing identity refuses (`REFUSE-SELF-IDENTITY-UNRESOLVABLE`), and the matching self record is validated for its EXPECTED identity — same exact head SHA and `event=pull_request_target` — BEFORE its timestamp is used or it is excluded from consumption. The timestamp-only unit-test use (assertions t1–t4) retains the optional form, but the gate can never skip verification. All three independently reproduced cases now refuse with **zero POSTs** (asserted, t22): (a) missing requester ID; (b) missing requester ID plus attempt 2; (c) self record with the requested ID but wrong head and wrong event.

**FIX 2 (CI1040-P03) — post-dispatch discovery enforces the boundary BEFORE matching.** The discovery phase filters trusted candidates to `id > boundary_id` first and matches over the filtered set only — a pre-dispatch attempt that later reappears (rerun to success) can neither bind nor shadow the newly dispatched run, in the FOUND and PENDING cases exactly as in the EXHAUSTED case. Initial-decision reuse of an authoritative success is preserved (it precedes any dispatch and reads the unfiltered list). Exercised (t23): your reproduction — failed run 100 rerun to success on attempt 2 alongside eligible newer run 101 → binds **(101, attempt 1)**, 1 POST, never (100, 2); the PENDING variant pins the newer 101 and never issues `get_run:100` (asserted on the call log); the pre-boundary success ALONE leads to bounded-wait refusal — the requester never claims the old run as its own result.

**FIX 3 — coverage corrected from the executed fixtures.** t6 previously described B and C as NONE while their fixtures were `[[F0]]` (EXHAUSTED). Corrected: t6 now runs BOTH — EXHAUSTED-path successors (fixture `[[F0]]`) and ACTUAL NONE-path successors (fixture `[[]]`, no trusted run visible) — each asserting `REFUSE-AUTHORIZATION-CONSUMED 7000` with **zero POSTs**. Coverage statements now come from the fixtures as executed, not from comments.

**FULL EXECUTED RESULTS (23/23 — `test-caller-model-output.log`):**
```
t1–t3   PASS  malformed failure timestamps / invalid newest label -> strict refusals (helper level)
t4      PASS  failure boundary: exact head/ref/PR/bot filter only
t5      PASS  return_run_details response -> validate -> pin (101,1) -> per-attempt jobs -> BOUND (1 POST); wrong-head identity -> REFUSE-IDENTITY-UNRESOLVABLE (1 POST)
t6      PASS  EXHAUSTED: A 1 POST -> bounded refusal; EXHAUSTED successor -> CONSUMED 0 POSTs; ACTUAL-NONE successors (2) -> CONSUMED 0 POSTs each
t7      PASS  requester attempt 2 -> REFUSE-REQUESTER-RERUN, 0 POSTs (empty and bindable transcripts)
t8      PASS  predating requester vs new authorization -> REFUSE-REQUESTER-PREDATES-AUTHORIZATION, 0 POSTs; postdating successor -> exactly 1 POST -> binds
t9      PASS  ties: prior at E* consumes; self at E* does not qualify
t10     PASS  pending/running/cancelled/completed requester runs all consume; incomplete history, unestablishable requester timestamp, absent self record -> refusals
t11     PASS  delayed visibility: first requester recovers own dispatch by discovery -> BOUND (101,1), 1 POST
t12     PASS  terminal-first: FOUND before any PENDING -> pins (101,1) -> binds
t13     PASS  attempt change / trust change / 404 -> refusals
t14     PASS  jobs only for attempt 2 -> REFUSE-JOBS-NOT-PINNED-ATTEMPT
t15     PASS  refresh lanes: 0 POSTs in every case
t16     PASS  post-pin call log: only get_run:<pinned> and jobs:<pinned>@<attempt>
t17     PASS  mutation self-test: fail-open variant caught by the executable assertion
t18     PASS  eligible NONE: exactly 1 POST -> BOUND (101,1)
t19     PASS  stale authorization (real #1066 history shape) -> REFUSE-STALE-AUTHORIZATION, 0 POSTs
t20     PASS  incomplete event history -> REFUSE-INCOMPLETE-EVENT-HISTORY, 0 POSTs
t21     PASS  authoritative success reuse: full identity + qualification recheck, 0 POSTs
t22     PASS  missing identity (a), missing identity + attempt 2 (b), wrong-head/wrong-event self record (c) -> REFUSE-SELF-IDENTITY-UNRESOLVABLE, 0 POSTs each
t23     PASS  discovery boundary: rerun-to-success of the pre-dispatch run never binds; PENDING variant pins the newer run (get_run:100 never issued); pre-boundary success alone -> bounded refusal, 1 POST
```

The v5 design is otherwise unchanged; the accepted conservatism (at-or-after consumption, statuses all count, ties refuse) is intact and now guarded by these stronger assertions. The known limitations from the previous return stand unchanged (`return_run_details` pinned-API-version qualification, pagination walks, timestamp-parsing variants, prototype-not-implementation status). Installation and rollback remain separately unresolved; Checkpoint D remains pending.

Please return your verdict and the precise next action — I will wait for Sean to relay your actual response.

CHECKPOINT D — AWAITING ASTRA REVIEW. Queue admission, merge, rollout and issue closure remain gated.
