(Retained copy of the packet submitted to Astra via Sean on 2026-09-23: the authorized disposable offline prototype, returned with paths, digests, exact command, actual results, dispatch counts, assumptions, and the unchanged-head confirmation.)

ASTRA REVIEW REQUEST — #1040 — OFFLINE PROTOTYPE QUALIFICATION (per the v5 PASS — DESIGN ONLY authorization)

**Requested decision and next action:**
Return of the disposable offline prototype built and tested under the authorized next action, with prototype/test paths and SHA-256 digests, the exact command, actual results, mocked dispatch counts and selected run/attempt identities, remaining assumptions and untested behavior, and confirmation that #1066's head and worktree are unchanged. This approval permits offline preparation only — no repository implementation, candidate branch/PR, label change, live CI dispatch, queue admission, installation, merge or closure has been performed or is requested by this return.

**PROTOTYPE AND TEST FILE PATHS + SHA-256 DIGESTS** (directory `C:\Sean Project\RMT-1040-glm-evidence\probe-matcher-extraction\`):
- `caller_model.py` — the proposed offline caller model (a design probe, NOT production-workflow qualification) — SHA-256 `78EECD37ADF3B835C45480FADD3A892CCB0D1249A9572140C2EF8B5707B107F2`
- `test-caller-model.py` — executable assertions — SHA-256 `FE9C4F5773827B253E2E985A416B496A3CFBDC250AB6FC1ACE127755C1E8E271`
- `test-caller-model-output.log` — the executed run record — SHA-256 `F9E8C49B38BE1BBB5236B09AA0915FE62CADAD3CBC75F1A7835A1EB86BFBAFE5`

**EXACT COMMAND AND ACTUAL RESULT:**
```
cd "C:\Sean Project\RMT-1040-glm-evidence\probe-matcher-extraction"
C:\Users\seanm\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe test-caller-model.py
```
Executed result: **21/21 passed** (full listing below). Every case is a scripted API transcript; **the mocked dispatch POST is recorded only inside `Api.dispatch()`, which the model reaches only through its common gate** — no counter is incremented anywhere else.

**REQUIRED ASSERTIONS → EXECUTED CASES (dispatch counts are actual `Api.dispatch` calls):**
1. *Eligible NONE and EXHAUSTED requests each dispatch exactly once* — t18 (NONE path: 1 POST, binds run 101 attempt 1), t6/t11 (EXHAUSTED path: 1 POST each).
2. *Refresh lanes and requester attempts > 1 dispatch zero times* — t15 (refresh: 0 POSTs in both report-failure and wait-and-bind variants), t7 (attempt 2 of run 7000: `REFUSE-REQUESTER-RERUN`, 0 POSTs on both an empty and a bindable transcript).
3. *Predating requesters, timestamp ties, stale authorization, malformed timestamps and incomplete histories refuse* — t8 (`REFUSE-REQUESTER-PREDATES-AUTHORIZATION`, 0 POSTs), t9 (tie consumes / tie does not qualify — both refused), t19 (`REFUSE-STALE-AUTHORIZATION` against the real #1066 history shape, 0 POSTs), t1–t3 (missing/invalid failure `updated_at`, invalid newest label timestamp), t10/t20 (incomplete requester history and incomplete event history).
4. *Another same-head requester at-or-after the authorization prevents dispatch, across all statuses* — t10 (pending, in_progress, cancelled, completed — each `REFUSE-AUTHORIZATION-CONSUMED`), t6 (NONE-path successor and third successor: 0 POSTs each).
5. *Accepted dispatch with lost response never causes a second POST, including from a queued successor* — t6 (A: 1 POST → bounded refusal; B and C: 0 POSTs) and t11 (delayed visibility: A recovers its own run by discovery and binds — still exactly 1 POST).
6. *A successful return_run_details response is validated and pinned* — t5 (response `workflow_run_id` 101 → fetched record validated through exact head/ref/PR/bot checks → pinned (101, attempt 1) → per-attempt jobs → BOUND; wrong-head identity in the response → `REFUSE-IDENTITY-UNRESOLVABLE`, never pinned).
7. *Terminal-first discovery can qualify the correct run* — t12 (`FOUND 101 success` before any PENDING → pins (101, attempt 1) → per-attempt jobs → binds; pre-boundary failures are history, not terminal).
8. *Product trust changes, attempt changes, missing records and missing per-attempt evidence refuse* — t13 (rerun `run_attempt` 1→2 → `REFUSE-ATTEMPT-CHANGED`; `triggering_actor` changed → record not trusted → pin refused; 404 → pin refused), t14 (jobs only for attempt 2 → `REFUSE-JOBS-NOT-PINNED-ATTEMPT`).
9. *Existing authoritative success is reusable after full identity and qualification checks* — t21 (`FOUND` → `_pin` full-record recheck → per-attempt jobs → bind with **0 POSTs**).

**MOCKED DISPATCH COUNTS AND SELECTED IDENTITIES (representative):** t5/t18: 1 POST each → bound identity (run 101, attempt 1); t6: exactly 1 POST total across A, B and C (A's), no bound identity (refusals); t7: 0 POSTs; t8: A 0 POSTs, B 1 POST → (101, 1); t11: 1 POST → (101, 1); t21: 0 POSTs → (101, 1). Every refusal case asserts its POST count explicitly.

**EVIDENCE CORRECTION (retained, restated):** the earlier printed-only probe outputs (`probe-round2.sh` / `probe-round2-results.log`, and the first-extraction round) are retained unchanged and are NOT transition proofs — their outcomes were printed descriptions and unconditional counters. All caller-behavior claims in this return are carried exclusively by `caller_model.py` + `test-caller-model.py` executed above.

**REMAINING ASSUMPTIONS AND UNTESTED BEHAVIOR (stated explicitly):**
1. `return_run_details: true` is stubbed per GitHub's documented dispatch API; qualification against the workflow's pinned API version (`2022-11-28`) is an implementation step. The lost-response fallback does not depend on the field.
2. The requester-run and issue-event history reads are modelled with a completeness flag; the implementation must realize the bounded pagination walk (and the same for product runs, which the current workflow reads with `per_page=100` — more than 100 trusted attempts at one head is not a realistic state, unchanged from the existing workflow).
3. Discovery-pin attribution rests on spend-once plus the trust filters (any new trusted attempt with `id > boundary` can only be the requester's own); no additional proof of ownership is modelled.
4. Timestamp comparisons parse GitHub-authored RFC3339 strings as UTC instants; exotic precision/leap-second variants are untested (ties are refused conservatively).
5. The prototype does not exercise the App publication path, the queue-exclusion guard, or the Product-side authentication step — they are untouched existing machinery, asserted by the existing contract tests, and the eventual implementation must re-execute the model's assertions as tests over the workflow's actual extracted shell/Python (the established harness), not over this Python model.
6. The model intentionally contains no window, no local consumption flag, and no automatic redispatch; its conservatism (any same-head requester run at-or-after the authorization consumes it, including one triggered by a different non-readiness label) is accepted design and must not be silently weakened during implementation.

**#1066 CONFIRMATION:** head `027c985e82d1d1b764a2f24eac6e6c2ed4ec2c71`, draft, auto-merge disabled, mergeStateStatus BLOCKED (checks/review only), no queue entries; the #1040 worktree is **clean at the identical SHA** (verified immediately before this return). #1040 remains open; D01–D03 closed; Checkpoint D has not passed; installation and rollback remain separately unresolved.

Please return your verdict and the precise next action — I will wait for Sean to relay your actual response.

CHECKPOINT D — AWAITING ASTRA REVIEW. Queue admission, merge, rollout and issue closure remain gated.
