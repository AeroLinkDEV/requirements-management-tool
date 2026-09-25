(Retained copy of the packet submitted to Astra via Sean on 2026-09-23: corrected executable offline model, concise evidence correction, updated decision table.)

ASTRA REVIEW REQUEST — #1040 — SAME-HEAD RETRY — CORRECTED EXECUTABLE MODEL

**Requested decision and next action:**
Per the permitted next step: one corrected executable offline model with assertions for the four findings, a concise evidence correction, and the updated decision table. No candidate branch, protected-workflow edit, PR, readiness-label change, CI dispatch, queue admission or installation exists; #1066 remains draft at `027c985e82d1d1b764a2f24eac6e6c2ed4ec2c71`, worktree clean; the installation route remains explicitly unresolved (v2, unchanged).

**Artifacts (disposable, outside the product worktree, at `probe-matcher-extraction/`):**
- `caller_model.py` — the proposed offline caller model (strictly labeled: a design probe, NOT the amended production workflow).
- `test-caller-model.py` — executable assertions; every case is a scripted API transcript; **dispatch is recorded only when the model actually calls `api.dispatch()`**; binding, pinning, refusal and polling are actual state transitions.
- `test-caller-model-output.log` — the executed run. **Result: 12/12 passed.**

**EVIDENCE CORRECTION (concise, per the finding):** the round-2 probe's caller-behavior lines ("RETRY 102 DISPATCHED", "PIN 101, BIND SUCCESS", the refusals and the refresh/NONE row) were printed descriptions, not executed transitions; its two dispatch-counter increments were unconditional. That probe remains retained UNCHANGED (`probe-round2.sh`, `probe-round2-results.log` — nothing erased) and its timestamp/matcher computations remain valid partial evidence; all caller-behavior claims are now carried exclusively by the executable model below.

**EXECUTED MODEL RESULTS (12/12):**
```
t1   PASS    failed trusted run with missing updated_at        -> REFUSE-UNESTABLISHABLE-FAILURE-BOUNDARY (was FRESH-NO-FAILURE)
t2   PASS    failed trusted run with updated_at="invalid"      -> REFUSE-UNESTABLISHABLE-FAILURE-BOUNDARY (was FRESH-NO-FAILURE)
t3   PASS    newest label event with invalid timestamp (+older valid) -> REFUSE-UNESTABLISHABLE-AUTHORIZATION (older one NOT selected)
t4   PASS    failure boundary uses exact head/ref/PR/bot filter only (foreign-sha, human-trigger, other-PR, and an untrusted invalid-timestamp record are all excluded)
t5   PASS    A/B schedule: stale request refuses with ZERO dispatches; genuinely fresh request -> reconcile window -> exactly ONE dispatch -> binds; queued B after the retry failed -> refuses, zero dispatches
t6   PASS    interrupted dispatch: A dispatches (accepted, response LOST, run not listed); B's reconciliation surfaces the run -> B pins it, ZERO second dispatch (total 1); true-loss variant (dispatch never accepted): window elapses empty -> exactly ONE dispatch
t7   PASS    terminal-first: EXHAUSTED 100 -> dispatch -> FOUND 101 success (never seen PENDING) -> pins (101, attempt 1) -> per-attempt jobs -> BINDS
t8   PASS    pinned identity: rerun changing run_attempt 1->2 -> REFUSE-ATTEMPT-CHANGED; triggering_actor changed to a human -> record not trusted -> pin refused; 404/absent record -> pin refused
t9   PASS    jobs evidence for attempt 2 only -> REFUSE-JOBS-NOT-PINNED-ATTEMPT (attempt-2 jobs cannot qualify pinned attempt 1)
t10  PASS    refresh lane: EXHAUSTED -> reports failure, never dispatches; refresh + NONE -> bounded wait for the separate dispatcher -> binds its success, still never dispatches
t11  PASS    one consistent post-pin policy: call log after pinning contains ONLY get_run:<pinned> and jobs:<pinned>@<attempt> — no global matcher observation exists after pinning
t12  PASS    mutation self-test: the fail-open helper variant reproduces FRESH-NO-FAILURE on the invalid record while the model's real helper refuses — an executable test catches exactly this difference
```

**UPDATED DECISION TABLE (v3 — incorporates the reconciliation sub-state, attempt+trust pinning, and the single post-pin policy):**

| Observation | Behavior |
|---|---|
| Initial `FOUND <id> success` | `_pin(id)` rechecks the full record (exact head/ref/PR/bot trust, attempt recorded) → poll loop binds via per-attempt jobs — an authoritative success is reused, but only after identity recheck |
| Initial `PENDING <id>` | `_pin(id)` (id + attempt), direct polls |
| Initial `EXHAUSTED` (refresh/any non-dispatch lane) | refuse immediately, naming the newest attempt — no freshness read, no dispatch |
| Initial `EXHAUSTED` (dispatch lane) | `boundary_id` = newest attempt id; freshness over COMPLETE event history: any failed trusted attempt with missing/unparsable `updated_at` → refuse; any readiness-label event with invalid timestamp → refuse (an older authorization is never selected past it); incomplete pagination → refuse; latest label ≤ boundary → refuse stale |
| Fresh → **reconciliation** | bounded window (polls): a new trusted attempt (id > boundary) surfaces → the authorization was already consumed → pin/wait or refuse-if-failed, NEVER a second dispatch; window elapses empty → dispatch ONCE (a truly lost, never-accepted request leaves the authorization genuinely unused) |
| Initial `NONE` (dispatch lane) | dispatch once (no freshness needed); refresh lane → bounded wait, never dispatches |
| Waiting to pin | global matcher on `list_runs` pages only until an id > boundary (or any run, when boundary=0) is observed — pinning accepts the first observation even if already terminal |
| **Pinned** `(run_id, run_attempt)` | DIRECT `get_run(pinned)` only: record absent/404 or trust filter fails → refuse (identity unresolvable); `run_attempt` differs → refuse (rerun kept the id, changed the attempt); running → sleep; success → fetch jobs via the **per-attempt** endpoint for exactly `(pinned id, pinned attempt)`; missing → refuse (filter=latest alone never qualifies); bind records id + attempt; failure → refuse naming id + conclusion. **No global-newer-run veto is claimed or implemented — there is no global observation after pinning, by design.** |

**At-most-once, restated with the interrupted case closed:** serialization orders same-head requesters; freshness ensures each dispatching requester's authorization postdates every OBSERVED failure; reconciliation ensures a fresh authorization is treated as CONSUMED whenever any prior dispatch's run surfaces within the bounded window, so an interrupted requester (accepted dispatch, lost response) can never be double-spent; a dispatch that never reached GitHub leaves the window permanently empty and the authorization genuinely unused, so exactly one dispatch occurs. No external state service is used.

**Non-goals (unchanged):** no test or check weakening; no timeout changes; no #1040 product code; no automatic repeat-until-green; no rebase or head change of #1066; HOME untouched; failed runs and artifacts preserved; installation/rollback remain unresolved pending a separately reviewed transition and are not promised by any of this.

Please return your verdict and the precise next action — I will wait for Sean to relay your actual response.

CHECKPOINT D — AWAITING ASTRA REVIEW. Queue admission, merge, rollout and issue closure remain gated.
