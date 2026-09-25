(Retained copy of the CI-maintenance proposal submitted to Astra via Sean on 2026-09-23. Per the Round 4 direction, this proposal is RETURNED FOR REVIEW BEFORE ANY protected workflow code is changed; no branch, PR, or edit to `.github/**` has been made.)

# PROPOSAL — Explicit same-head retry through the trusted Full-CI requester

**Scope:** retry-orchestration correction only. Two files: `.github/workflows/request-full-ci.yml` (protected main) and its contract test `product/test-planner/tests/full-ci-readiness-dispatch.test.mjs`. No other workflow, no required-check change, no application timeout change, no `ci.yml` change, no #1040 product code.

## 1. The defect, stated precisely

In the current `find_product_run` matcher, when every matching trusted attempt has completed and none succeeded, the branch

```python
elif matches and all(run.get("status") == "completed" for run in matches):
    run = min(matches, key=lambda item: item["id"])
    print(f"FOUND {run['id']} completed {run.get('conclusion')}")
```

returns `FOUND … completed failure`. The shell case binds that `run_id` and falls into the poll loop, whose `FOUND` branch then exits 1 ("Exact-head Product run … completed with conclusion=failure"). The dispatch path is reachable **only** from the `NONE` branch. Consequence (observed on #1066): once a trusted attempt has finished unsuccessfully, `find_product_run` can never again return `NONE`, so no label event can ever dispatch a new attempt at that SHA — the head is permanently unbindable through the trusted path, and a fresh commit was the only escape. A human "Re-run" of the failed run is not a substitute: the re-run inherits `triggering_actor=<human>`, so the matcher's trust filters (`actor` and `triggering_actor` both `github-actions[bot]`) would exclude it, and the bound evidence would not carry the trusted attribution.

## 2. Proposed mechanism (all four matcher verbs preserved)

**Matcher change (one branch):** the all-completed-unsuccessful branch emits a distinct verb instead of a false `FOUND`:

```python
elif matches and all(run.get("status") == "completed" for run in matches):
    run = max(matches, key=lambda item: item["id"])
    print(f"EXHAUSTED {run['id']} completed {run.get('conclusion')}")
```

`EXHAUSTED <id> <conclusion>` = "all matching trusted attempts so far finished unsuccessfully; `<id>` is the NEWEST attempt, `<conclusion>` its outcome." `FOUND … success`, `PENDING`, and `NONE` are unchanged. Trust filters, head/ref/PR association filters, and the success branch's lowest-id determinism are unchanged.

**Caller change (initial decision):**
- `FOUND` (success) → bind — unchanged.
- `PENDING` → wait — unchanged (a running attempt prevents any dispatch).
- `NONE` → dispatch once, label lane only — unchanged.
- `EXHAUSTED` → **new**: if `REQUEST_LABEL == 'ready-for-full-ci'` (the dispatch lane only), record `known_attempt_id=<id>`, dispatch **one** new trusted attempt through the byte-identical existing dispatch body (same endpoint, same inputs: exact `pull_request_number`, `pull_request_base_sha`, `pull_request_head_sha`, `full_diagnostics=false`, `GITHUB_TOKEN` so `actor`/`triggering_actor` remain `github-actions[bot]`), set `retrying=1`, and enter the existing wait loop. Otherwise (refresh lane / any other label) do NOT dispatch: exit 1 reporting the existing failed conclusion — exactly today's behavior for refreshes.

**Caller change (in-loop `EXHAUSTED` handling — the discovery-delay answer):** GitHub's dispatch response carries no run id, and the runs list propagates with delay, so after a retry dispatch the matcher may keep reporting `EXHAUSTED` with the SAME `known_attempt_id` for a short window. The loop therefore distinguishes by id:
- `EXHAUSTED` with id `> known_attempt_id` → the new attempt completed unsuccessfully → exit 1 naming that newest attempt's id and conclusion (fail-closed; no further dispatch inside this run).
- `EXHAUSTED` with id `== known_attempt_id` → the new attempt is not discoverable yet → sleep 10 and continue (bounded by the existing 420×10 s poll; `timeout-minutes: 75` unchanged — a retry is dispatched within seconds of the initial decision, so effectively the full observed gate envelope, 54.4 m max, remains covered).
- `NONE|PENDING` → sleep 10 and continue — unchanged. `FOUND` → unchanged (wait for completion; bind on success).

**At-most-once semantics:** the dispatch decision exists only at the initial decision point, never inside the loop — one new attempt per readiness event, never a poll-driven retry loop. Across events, the existing concurrency group (`full-ci-request-<PR>-<head sha>-dispatch`, `cancel-in-progress: false`) serializes same-head label events: a second event queued behind a running attempt observes `PENDING` and waits rather than dispatching. A further attempt after a failed retry therefore requires a fresh, deliberate human readiness-label event — and every failed attempt is reported (requester exit 1, `Full Product evidence aggregate` refusal, no App check run) before any such event.

**Everything else byte-identical:** `verify_live_pr` before dispatch and on every poll; queue-exclusion guard (`verify-readiness-target.mjs`, re-checked before App publication); the Product-side label-dispatch authentication check; single-aggregate verification (`accepted_names`); App-published `Trusted merge-queue binding` check; required `Full Product evidence aggregate` job and its refusal wording; permissions; environment `merge-authority`.

## 3. Coverage of the six required points

1. **Explicit readiness request may dispatch one new attempt when all matching trusted attempts finished unsuccessfully** — the `EXHAUSTED` branch, dispatch lane only.
2. **A running matching attempt prevents duplicate dispatch** — `PENDING` never dispatches, at the initial decision and in the loop; the concurrency group serializes overlapping label events.
3. **After dispatch, the requester waits for the new attempt rather than selecting the previous failed run** — `retrying`/`known_attempt_id` bookkeeping: same-id `EXHAUSTED` = "not discoverable yet, keep waiting"; a strictly greater id is the only accepted failure signal.
4. **Existing authentication, queue exclusion, Product authentication, required-job verification and App publication remain enforced** — untouched; asserted by the retained regex/behavioral contract tests.
5. **Regression tests** — extend `full-ci-readiness-dispatch.test.mjs`, which already executes the workflow's real shell/Python with mocked `curl`/`python` (no network):
   - *failed→retry*: initial decision with matcher output `EXHAUSTED <id> completed failure`, label `ready-for-full-ci` → exactly one dispatch call, `known_attempt_id` recorded, loop entered; same input with a non-readiness label → no dispatch, exit 1 reporting the conclusion.
   - *pending→no duplicate*: `PENDING` at the initial decision and in the loop → no dispatch, keeps polling.
   - *new-attempt discovery delay*: scripted in-loop sequence `EXHAUSTED(same id)` ×N → `PENDING` → `FOUND … success` → binds; asserts no exit and no second dispatch during the delay.
   - *changed head*: retained exact-head/ref/PR filter assertions, plus the bookkeeping is per-invocation (no `known_attempt_id` reuse across heads); existing head-moved/foreign-repository refusals unchanged.
   - *untrusted runs*: retained `actor`/`triggering_actor` bot filters; new matcher-level test that a completed-unsuccessful run failing any trust/association filter is invisible (can neither trigger nor suppress a retry).
   - *failed aggregate*: retained `pr-product-aggregate` refusal test (non-success prerequisite → exit 1, "refusing PR Product authority").
   - Plus matcher-level Python tests over scripted `workflow_runs` JSON: all-failed → `EXHAUSTED` with newest id/conclusion; success present → `FOUND` lowest id; any running → `PENDING`; empty → `NONE`.
6. **Preserve every earlier failure and artifact** — no run deletion, no check minting, no protection change; failed run 35845728143, requester run 35845707428, their logs, TRX and browser diagnostics stay exactly as retained.

## 4. Explicit non-goals (per the Round 4 direction)

No weakening of any test or required check; no increase of any application or journey timeout (the browser splash/`expect` timeouts are untouched); no change to #1040 product code; no automatic repeat-until-green (each new attempt requires a deliberate fresh label event, and each failure is reported first); no rebase of #1066 and no change to its head; HOME untouched.

## 5. Sequencing after review

1. Astra reviews/approves this proposal (no code changed yet).
2. One focused maintenance branch/PR (`ci/request-full-ci-same-head-retry` or similar) with exactly the two files above; contract tests extended per §3.5 and run twice-stable; the PR itself proceeds through normal required checks.
3. After the maintenance PR merges to protected main (protected path "reviewed and available"), request **ONE** fresh Full Product attempt at unchanged head `027c985e82d1d1b764a2f24eac6e6c2ed4ec2c71` via the authorized readiness-label action.
4. Any further failure of that attempt is reported with its evidence before any further retry.

## 6. Correction of record carried from the Round 4 review

Browser shard 4/4 actuals are **303 passed, 2 skipped, 1 failed** (my "307 specs passed" misread the reporter's highest index as a count). First attempt: the page remained on the login form (snapshot shows "Welcome back" sign-in; "Authenticating…" in progress) — it never reached "Establishing your secure session"; Retry #1 reached "Establishing your secure session"; neither reached the audit-layout assertion. Root causes remain unresolved; runner load is a hypothesis; main's unrelated timing failure is context, not proof of cause here.
