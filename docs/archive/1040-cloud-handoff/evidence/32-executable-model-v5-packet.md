(Retained copy of the packet submitted to Astra via Sean on 2026-09-23: executable model v5 — the common dispatch gate, requester-rerun refusal, and enforced first-young-requester.)

ASTRA REVIEW REQUEST — #1040 — EXECUTABLE MODEL v5 (the three corrections, executed)

**Requested decision and next action:**
The three v4-review corrections are implemented in the disposable offline model and exercised with assertions showing actual mocked POST counts. Artifacts (outside the product worktree, at `probe-matcher-extraction/`): `caller_model.py` (v5 — a proposed model, NOT production-workflow qualification), `test-caller-model.py`, `test-caller-model-output.log` — **executed result: 17/17 passed**. No candidate branch, protected-workflow edit, PR, label action, live CI dispatch, queue admission or installation exists; #1066 stays draft at `027c985e82d1d1b764a2f24eac6e6c2ed4ec2c71`; installation and rollback remain separate unresolved prerequisites. Accurate evidence statement, unchanged: the earlier printed-only probe outputs are not transition proofs (retained, not erased); every caller-behavior claim below is carried by this executable model.

**CORRECTION 1 — the consumption guard now governs EVERY dispatch path.** Requester eligibility and authorization-consumption checks live in one common gate (`_dispatch_gate_and_post`) immediately before every dispatch POST; the NONE path calls the identical gate and cannot bypass it. Exercised: your counterexample — A sees `NONE`, dispatches, loses the response; B later also sees `NONE` (the accepted run still undiscoverable) and the gate refuses B with `REFUSE-AUTHORIZATION-CONSUMED 7000`, naming A's requester run, with **zero additional POSTs** (asserted). Refresh lanes never reach the gate — they hold no dispatch path at all (asserted).

**CORRECTION 2 — a requester rerun never dispatches.** The model carries the requester's own `(run_id, run_attempt)` identity; the gate requires `run_attempt == 1` before any POST (`REFUSE-REQUESTER-RERUN`). A rerun may only reconcile existing evidence or refuse. Exercised: attempt 2 of run 7000 after attempt 1's accepted-but-unobserved dispatch → refused with **zero POSTs**, on both an EMPTY transcript and one where a success was otherwise bindable (asserted both).

**CORRECTION 3 — "first young requester" is enforced, not described.** The gate additionally requires the CURRENT requester's own record — found in the same observable requester-run history used for consumption — to exist and **strictly postdate** the authorization; a tie refuses (`REFUSE-REQUESTER-PREDATES-AUTHORIZATION`). "Prior" is defined precisely: a same-head `pull_request_target` requester run whose `created_at` is **at or after** the authorization, excluding only the current run identity `(id, attempt)` — queued/running/failed/cancelled statuses all count, an unestablishable timestamp refuses, incomplete requester history refuses, and an absent self record refuses (`REFUSE-SELF-IDENTITY-UNRESOLVABLE`). Ties are conservative on both sides: a prior run at exactly the authorization instant consumes it, and a requester at exactly the instant does not qualify for it (both asserted). Your ordering counterexample is closed structurally: an old requester A (predating E) is refused at the gate with zero POSTs, so it can never consume E; a successor B that postdates E then finds no young prior run (A predates E) and dispatches exactly once — the system stays closed because every dispatcher must postdate its authorization, so any real prior dispatcher of E is necessarily young and is found by the consumption search.

**EXECUTED RESULTS (17/17 — `test-caller-model-output.log`):**
```
t1  PASS  failed trusted run, missing updated_at               -> REFUSE-UNESTABLISHABLE-FAILURE-BOUNDARY
t2  PASS  failed trusted run, updated_at="invalid"             -> REFUSE-UNESTABLISHABLE-FAILURE-BOUNDARY
t3  PASS  newest label event with invalid timestamp            -> REFUSE-UNESTABLISHABLE-AUTHORIZATION (older label NOT selected)
t4  PASS  failure boundary: exact head/ref/PR/bot filter only
t5  PASS  return_run_details response yields workflow_run_id -> fetched record validated through exact checks -> pinned (101, attempt 1) -> per-attempt jobs -> BOUND (1 POST); wrong-head identity in the response -> REFUSE-IDENTITY-UNRESOLVABLE (1 POST)
t6  PASS  common gate: EXHAUSTED path — A's response lost, run never surfaces -> A ends REFUSE-BOUNDED-WAIT-EXHAUSTED (1 POST); NONE-path successor B -> REFUSE-AUTHORIZATION-CONSUMED 7000 (0 POSTs); third successor C -> same (0 POSTs)
t7  PASS  requester rerun (attempt 2, same run id, after attempt 1's accepted-but-unobserved dispatch) -> REFUSE-REQUESTER-RERUN, 0 POSTs (both an empty transcript and a bindable-success transcript)
t8  PASS  old requester (created before E**) -> REFUSE-REQUESTER-PREDATES-AUTHORIZATION, 0 POSTs; successor B postdating E** -> exactly 1 POST -> binds
t9  PASS  ties: prior requester at exactly E* -> REFUSE-AUTHORIZATION-CONSUMED; current requester at exactly E* -> REFUSE-REQUESTER-PREDATES-AUTHORIZATION
t10 PASS  queued/running/cancelled requester runs all consume; incomplete requester history -> REFUSE-INCOMPLETE-REQUESTER-HISTORY; unestablishable requester timestamp -> REFUSE-UNESTABLISHABLE-REQUESTER-HISTORY; absent self record -> REFUSE-SELF-IDENTITY-UNRESOLVABLE
t11 PASS  delayed visibility: the first requester recovers its own uncertain dispatch by discovery -> pins (101, attempt 1) -> binds (1 POST total)
t12 PASS  terminal-first: FOUND 101 success before any PENDING -> pins (101,1) -> per-attempt jobs -> binds
t13 PASS  pinned (101,1): rerun -> REFUSE-ATTEMPT-CHANGED; triggering_actor changed -> not trusted -> pin refused; 404 -> pin refused
t14 PASS  jobs evidence only for attempt 2 -> REFUSE-JOBS-NOT-PINNED-ATTEMPT
t15 PASS  refresh lane: reports failure / waits — never dispatches (0 POSTs in every refresh case)
t16 PASS  post-pin call log contains ONLY get_run:<pinned> and jobs:<pinned>@<attempt>
t17 PASS  mutation self-test: the fail-open helper variant reproduces FRESH-NO-FAILURE while the real helper refuses — the executable test catches it
```

**DECISION TABLE v5 (changes from v4 marked):**

| Observation | Behavior |
|---|---|
| Initial `FOUND <id> success` | `_pin(id)` full-record recheck → per-attempt jobs → bind |
| Initial `PENDING <id>` | `_pin(id)` (id + attempt), direct polls |
| Initial `EXHAUSTED` (non-dispatch lane) | refuse immediately naming the newest attempt |
| **COMMON GATE [v5 — before EVERY dispatch POST, NONE included]** | lane must be the dispatch lane; requester `run_attempt == 1` else `REFUSE-REQUESTER-RERUN`; requester history complete and every record timestamped else refuse; latest authorization E* validated strictly; **self record must exist and strictly postdate E\*** else `REFUSE-REQUESTER-PREDATES-AUTHORIZATION` / `REFUSE-SELF-IDENTITY-UNRESOLVABLE`; **any other same-head requester run created at-or-after E\* → `REFUSE-AUTHORIZATION-CONSUMED`** (ties consume; all statuses count) |
| Initial `EXHAUSTED` (dispatch lane, gate passed) | product-failure boundary staleness check (malformed boundary timestamps refuse); then dispatch |
| Initial `NONE` (dispatch lane, gate passed) | dispatch |
| Dispatch POST | `return_run_details: true`; response identity → validate via exact checks → pin (id, attempt); response lost → uncertain: discovery-pin only, bounded wait ends in refusal — never redispatch |
| Waiting to pin | global matcher until an id > boundary (or any run, boundary=0) — pre-boundary failures are history, not terminal |
| **Pinned** `(run_id, run_attempt)` | DIRECT polls only: 404/trust-fail → refuse; attempt change → refuse; running → sleep; success → per-attempt jobs for exactly (pinned id, pinned attempt); failure → refuse naming it. No global observation after pinning. |

**Limitations (unchanged and stated):** `return_run_details` must be qualified against the workflow's pinned API version during implementation; the lost-response fallback does not depend on it. Spend-once remains conservative by design (any same-head requester run at-or-after the authorization consumes it, including one triggered by a different non-readiness label — the cost is a fresh re-label, never an unsafe dispatch). The requester-run-history read needs the same bounded-pagination completeness treatment as the events read (modelled; implementation must keep it). Installation and rollback remain separately unresolved; nothing here deploys anything.

Please return your verdict and the precise next action — I will wait for Sean to relay your actual response.

CHECKPOINT D — AWAITING ASTRA REVIEW. Queue admission, merge, rollout and issue closure remain gated.
