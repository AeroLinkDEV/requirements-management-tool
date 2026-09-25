(Retained copy of the packet submitted to Astra via Sean on 2026-09-23: corrected executable model v4 — spend-once via requester-run history; the reconciliation window is removed.)

ASTRA REVIEW REQUEST — #1040 — RETRY DECISION TABLE v4 (corrected executable model)

**Requested decision and next action:**
Per the precise next step: the corrected reconciliation rule, the successor-uncertainty specification, and the executable model with assertions and actual results. Artifacts (disposable, outside the product worktree, at `probe-matcher-extraction/`): `caller_model.py` (v4 — a design probe, NOT the amended production workflow), `test-caller-model.py`, and `test-caller-model-output.log` — **executed result: 13/13 passed**. No candidate branch, protected-workflow edit, PR, label action, CI dispatch, queue admission or installation exists; #1066 remains draft at `027c985e82d1d1b764a2f24eac6e6c2ed4ec2c71`, clean; installation and rollback remain separately unresolved. The evidence correction stands: all caller-behavior claims are carried exclusively by this executable model (dispatch recorded only via `api.dispatch()`; the earlier descriptive probes stay retained as non-proofs).

**BLOCKER ACCEPTED AND CLOSED — the reconciliation window is removed.** "Window elapses empty → dispatch ONCE" was invalid: for any finite W, an accepted run may surface after W, yielding two dispatches under one authorization. Time never converts an uncertain outcome into a non-attempt. The three dispatch states are now explicitly distinguished, and the third never collapses into the first:

1. **Positively never attempted** — only the FIRST same-head requester run younger than the authorization may reach the dispatch decision at all (see spend-once below); before it, no dispatch exists and none is pending.
2. **Accepted with known identity** — the dispatch call uses the documented `return_run_details: true`; the response's `workflow_run_id` is validated through the exact existing head/ref/PR/bot checks (fetched record, not the response alone) and pinned with its `run_attempt`. Subject to qualification against the workflow's pinned API version; the safe fallback if the response carries no identity is the uncertain path below — never an assumed-accepted shortcut.
3. **Attempted, outcome uncertain** (response lost, requester interrupted) — ends in REFUSAL for this requester (bounded wait, never redispatch) **and for every successor** (see spend-once). No automatic redispatch exists anywhere in the model.

**SPEND-ONCE — how a queued successor conservatively respects an unresolved dispatch without external state.** The observable artifact of a prior attempt is the prior requester's own workflow run: `event=pull_request_target`, `head_sha` = the PR head. The freshness check therefore additionally refuses whenever any prior same-head requester run (not this requester itself) is younger than the authorization — `REFUSE-AUTHORIZATION-CONSUMED <run id>`. Consequences, all asserted: a successor cannot distinguish "never attempted" from "accepted but undiscoverable", so it never acts on an authorization any earlier requester may have acted on; the only path that re-enables dispatch is a fresh human re-label younger than every prior requester run; and the refusal path is itself observable (the requester run fails), which is the report-before-retry boundary. The model contains no reconciliation window and no local consumption flag that dies with the requester.

**AT-MOST-ONCE (stated precisely, no longer claimed via time):** at most one same-head requester run may dispatch under a given label event — every later requester run observes the younger prior run and refuses; the dispatching requester either pins its run through the response identity, recovers it through discovery (spend-once means any new trusted attempt can only be its own), or ends in refusal. A redispatch therefore requires a fresh label event in every case.

**EXECUTED RESULTS (13/13 — `test-caller-model-output.log`):**
```
t1  PASS  failed trusted run, missing updated_at            -> REFUSE-UNESTABLISHABLE-FAILURE-BOUNDARY
t2  PASS  failed trusted run, updated_at="invalid"          -> REFUSE-UNESTABLISHABLE-FAILURE-BOUNDARY
t3  PASS  newest label event with invalid timestamp         -> REFUSE-UNESTABLISHABLE-AUTHORIZATION (older label NOT selected)
t4  PASS  failure boundary: exact head/ref/PR/bot filter only
t5  PASS  return_run_details response yields workflow_run_id -> fetched record validated through exact checks -> pinned (101, attempt 1) -> per-attempt jobs -> BOUND; wrong-head identity in the response -> REFUSE-IDENTITY-UNRESOLVABLE (validate-then-pin); 1 dispatch each
t6  PASS  uncertain dispatch: A's response lost, run never surfaces -> A ends REFUSE-BOUNDED-WAIT-EXHAUSTED (1 dispatch); queued successor B -> REFUSE-AUTHORIZATION-CONSUMED 7000 (A's run named), ZERO dispatches; third successor C -> same refusal, ZERO dispatches; delayed-visibility variant: the run surfaces during A's own discovery -> A pins and binds it, still exactly 1 dispatch total
t7  PASS  fresh re-label (13:00Z) younger than every prior requester run -> exactly ONE new attempt -> binds (report-before-retry honored)
t8  PASS  terminal-first: FOUND 101 success observed before any PENDING -> pins (101,1) -> binds
t9  PASS  pinned (101, attempt 1): rerun -> REFUSE-ATTEMPT-CHANGED; triggering_actor changed -> record not trusted -> pin refused; 404 -> pin refused
t10 PASS  jobs evidence exists only for attempt 2           -> REFUSE-JOBS-NOT-PINNED-ATTEMPT (filter=latest never qualifies)
t11 PASS  refresh lane: EXHAUSTED -> reports failure, never dispatches; refresh + NONE -> bounded wait -> binds the dispatcher's success, never dispatches
t12 PASS  post-pin call log contains ONLY get_run:<pinned> and jobs:<pinned>@<attempt> — no global observation after pinning
t13 PASS  mutation self-test: fail-open helper variant reproduces FRESH-NO-FAILURE while the real helper refuses — the executable test catches it
```

**DECISION TABLE v4 (changes from v3 marked):**

| Observation | Behavior |
|---|---|
| Initial `FOUND <id> success` | `_pin(id)` full-record recheck → per-attempt jobs → bind (authoritative success reuse preserved) |
| Initial `PENDING <id>` | `_pin(id)` (id + attempt), direct polls |
| Initial `EXHAUSTED` (non-dispatch lane) | refuse immediately naming the newest attempt |
| Initial `EXHAUSTED` (dispatch lane) | `boundary_id` = newest attempt id; freshness: malformed failure `updated_at` → refuse; malformed label timestamp → refuse; incomplete pagination → refuse; **prior same-head requester run younger than the authorization → REFUSE-AUTHORIZATION-CONSUMED [v4]**; latest label ≤ boundary → refuse stale |
| Dispatch (reaches only the first young requester) | `return_run_details: true` [v4]; response identity → validate via exact checks → pin (id, attempt); response lost → uncertain: discovery-pin only (spend-once makes any new trusted attempt its own), bounded wait ends in refusal — **never redispatch [v4]** |
| Initial `NONE` (dispatch lane) | dispatch once; refresh lane → bounded wait, never dispatches |
| Waiting to pin | global matcher until an id > boundary (or any run, boundary=0) is observed — pre-boundary failures are history, not terminal |
| **Pinned** `(run_id, run_attempt)` | DIRECT polls only: 404/trust-fail → refuse; attempt change → refuse; running → sleep; success → per-attempt jobs for exactly (pinned id, pinned attempt), missing → refuse, else bind recording id + attempt; failure → refuse naming it. No global-newer-run veto claimed — no global observation after pinning. |

**Limitations stated:** `return_run_details` is used as documented and must be qualified against the workflow's pinned API version during implementation; the lost-response fallback does not depend on it. Spend-once is conservative in one respect, by design: any same-head requester run younger than the authorization (including one triggered by a different, non-readiness label) consumes it, so such an event forces a fresh readiness re-label; this is a refusal, never an unsafe dispatch. Installation and rollback remain separately unresolved; nothing here deploys anything.

Please return your verdict and the precise next action — I will wait for Sean to relay your actual response.

CHECKPOINT D — AWAITING ASTRA REVIEW. Queue admission, merge, rollout and issue closure remain gated.
