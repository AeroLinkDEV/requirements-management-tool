(Retained copy of the proposal amendment submitted to Astra via Sean on 2026-09-23, resolving CI1040-P01–P03. No protected workflow code has been changed; the only new artifacts are offline disposable design probes retained under `probe-matcher-extraction/`.)

# AMENDMENT — Explicit same-head retry through the trusted Full-CI requester (resolves CI1040-P01, P02, P03)

The EXHAUSTED distinction, one-dispatch-outside-the-loop, preserved trust filters, retained failures, unchanged product code, and the two-file scope all stand as proposed. This amendment corrects the concurrency claim, supplies the freshness rule, states the installation route honestly, and completes the polling state machine and its testing.

## P01 — Freshness rule: a queued pre-failure request can never authorize a retry

**Accepted:** the concurrency argument was wrong. A requester queued in the concurrency group does not execute its matcher while the earlier requester owns the slot; on a same-head serial schedule {A dispatches retry 101; B queued; 101 fails; A exits; B starts and observes EXHAUSTED}, serialization alone lets B dispatch retry 102 with no fresh human request after 101 failed.

**Rule (all timestamps GitHub-authored, read from authenticated APIs; no local clock in any comparison):**

1. The matcher's `EXHAUSTED` line carries the newest failed attempt's `completed_at` from the runs API: `EXHAUSTED <id> <conclusion> <completed_at>`.
2. The requester fetches the pull request's authenticated issue-event history (`GET /repos/{o}/{r}/issues/{n}/events?per_page=100`) and takes the **latest `labeled` event with `label.name == 'ready-for-full-ci'`** as the request instant. `verify_live_pr` already requires the label to be currently present, so the latest labeled event is the currently active authorization (an unlabel/relabel pair leaves the later labeled event as the active one).
3. **Fresh:** `request_instant > newest_failure_completed_at` (both parsed as UTC instants) → the label lane may dispatch ONE retry. **Stale or unverifiable → conservative refusal**: exit 1 reporting the newest attempt's id and conclusion, no dispatch. Freshness is refused — never guessed — when: the events read fails; the retrieved window is ambiguous (the label is present but no `labeled` event appears in the retrieved window, or the oldest retrieved event is itself a matching label event, meaning the active authorization may lie beyond the window); either timestamp is missing or unparsable; or the request instant is not after the attempt's creation instant.
4. Scope: freshness gates ONLY the `EXHAUSTED` retry. The `NONE` first dispatch is unchanged (no failure to be stale against). Binding an existing completed success is unchanged (an already authoritative exact-head success can still be reused, per your wording).

**Effect on the counterexample schedule:** A labels at T_A > F0.completed_at → fresh → dispatches 101. B's labeled event T_B < 101.completed_at T_1 → B starts after A exits, reads EXHAUSTED 101 @ T_1, reads its own request instant T_B < T_1 → **stale → refusal, no 102**. A genuinely fresh re-label at T_2 > T_1 → fresh → one retry. The "report the failure before another deliberate retry" boundary now holds regardless of queueing.

**Behavioral test for the exact two-request schedule:** execute the workflow's real shell twice (A then B) against one mocked API transcript: A sees {failed F0, fresh event} → exactly one dispatch POST, exits after 101 is scripted to fail; B starts with its labeled-event instant before T_1 → zero further dispatch POSTs, exit 1 naming 101. Positive control: a third requester with event instant T_2 > T_1 → exactly one dispatch.

## P02 — Approval-kernel transition route (preparation ≠ authorization)

**Accepted, with the repository facts:** `request-full-ci.yml` is listed in `MAINTENANCE_KERNEL_PATHS` (`product/ci-metrics/lib/maintenance-preflight.mjs`, line 10 at `a3f29e9d`); the preflight refuses any kernel-path change with `separate-trust-root-bootstrap-required`; MERGING.md: "Any change to the runtime maintenance/merge-authority modules or the protected binding/readiness workflows is outside this routine path. Use a separately reviewed trust-root transition"; DEC-121/DEC-124 govern review and delegated approval.

**Stated explicitly: no currently qualified installation procedure applies.** The one-time trust-root bootstrap for kernel-path installation has not been performed; the live preflight refusal is the correct present state. This proposal does not perform, bypass, or self-authorize that bootstrap, does not remove the refusal, does not mint any check, and does not redesign merge authority.

**Proposed transition (offered for separate review; each phase gates the next):**

1. **Preparation (no authority):** one candidate branch, exactly two files (`request-full-ci.yml`, `full-ci-readiness-dispatch.test.mjs`). Extended contract tests green and twice-stable; planner-selected tests run.
2. **Qualification of the exact candidate (through current main machinery — no self-approval channel):** the requester always executes from the default branch, so the candidate PR's own readiness, dispatch and binding run **main's unmodified workflow code**; its required `Full Product evidence aggregate` and trusted binding are produced by pre-change code. The exact head SHA, tree identity and Product run id are recorded as the qualified candidate.
3. **Independent review and owner approval of live actions and restoration steps:** Astra's independent exact-candidate review of the diff, the qualification evidence and this procedure; the owner's approval of the live action below and of the restoration steps, with any delegated exact approval under DEC-124 recorded as delegated, never as independent human review.
4. **Installation (the only live action):** merge the exact qualified candidate to protected main through the ordinary merge queue with its ordinary required checks. The amended requester takes effect only for FUTURE label events. No other live action is proposed: no branch-protection, ruleset, environment, App, secret or required-check-publisher change of any kind; the kernel refusal in `maintenance-preflight.mjs` is not in the diff; `maintenance-preflight.mjs`, `detect-maintenance-candidate.mjs`, `merge-queue-binding.yml` and every `merge-authority` module are untouched.
5. **Preservation verification (captured before, re-verified after):** required publishers unchanged — `Trusted merge-queue binding` still published only by the Merge Authority App and the requester still mints no check of its own; required check names unchanged (`Full Product evidence aggregate` on PR suites; main's required set unchanged); the main-only secret boundary unchanged — no new secret, no new token, no new permission (the job keeps `actions: write`, `contents: read`, `pull-requests: read`; the retry dispatch POST uses the same job `GITHUB_TOKEN`; the `merge-authority` environment and its secret set untouched); ruleset 22306102's bypass list still empty and both environment policies unchanged — verified by owner inspection before/after. Any post-merge deviation is treated as an incident.
6. **Restoration:** an exact revert PR of the same two files — itself a kernel change under these same constraints, with reviewed exact revert evidence and no reuse of any earlier approval — restoring the pre-change contract-test set to green and verifying one readiness event behaves exactly as before. Failed run 35845728143, requester run 35845707428 and all artifacts are preserved in every branch of this plan.

**Credential isolation:** unchanged and additive-free — the amendment introduces no credential, App, environment or permission anywhere.

## P03 — Complete polling state machine; tests execute the workflow's actual matcher

**Accepted:** `known_attempt_id` existed only on the retry path, and the existing dispatch test stubs Python — it never executed the embedded matcher. Both corrected.

**State initialization (removes the unset-variable class):** `retrying=0; known_attempt_id=""; pinned_run_id=""` before the first decision.

**Matcher output contract (final):** `FOUND <id> completed success` (unchanged, lowest-id precedence); `EXHAUSTED <id> <conclusion> <completed_at>` (newest failed attempt); `PENDING <id>` (newest running attempt — extension enabling identity pinning); `NONE`.

**Complete decision table (initial decision → loop):**

| Initial observation | Behavior |
|---|---|
| `FOUND … success` | bind — unchanged (authoritative success reuse preserved) |
| `PENDING <id>` (label or refresh lane) | no dispatch; pin `pinned_run_id=<id>`; loop; when that attempt fails, loop `EXHAUSTED` exits 1 promptly naming id+conclusion |
| `NONE` (label lane) | dispatch first attempt; `retrying=1; known_attempt_id=""`; loop. Loop `NONE` → sleep (discovery); `PENDING <id>` → pin, sleep; `EXHAUSTED` → the dispatched attempt failed → exit 1 promptly; `FOUND success` → require id == pinned → bind |
| `EXHAUSTED <id> <c> <t>` (label lane, FRESH per P01) | `known_attempt_id=<id>; retrying=1`; dispatch ONE retry; loop. `EXHAUSTED` same id → retry not yet discoverable → sleep (this is the only place "waiting for a dispatched retry to become discoverable" exists, and it is distinct from ordinary waiting); `PENDING <id>` with id > known → pin, sleep; `EXHAUSTED` with id > known → the retry failed → exit 1 naming it; `FOUND success` → require id == pinned → bind |
| `EXHAUSTED …` (refresh or non-label lane) | exit 1 immediately naming id+conclusion — no dispatch, no timeout wait |

Promptness: every terminal path (initial-PENDING fails; NONE-dispatch fails; refresh observes another lane's failure; retry fails) exits at the first poll that observes the terminal conclusion — no path waits out the 70-minute budget, no path dispatches from inside the loop, no path references an unset variable. Once the intended new run is identified it is pinned and the final qualification (auth step, aggregate job) is required to be about that pinned run's id.

**Testing correction — the tests execute the workflow's actual code:**
- **Matcher tests:** extract the actual `find_product_run` shell+Python from the workflow (the same extraction technique the existing suite already uses for its shell fragments) and execute it with a mocked `curl` feeding scripted `workflow_runs` JSON. **Validated offline today** (disposable probe, retained at `probe-matcher-extraction/`): the current main matcher executes correctly under this harness and reproduces `NONE`; `PENDING`; `FOUND 200 completed success` (lowest-id precedence over 300); **`FOUND 100 completed failure` — the defect, demonstrated by the real code**; untrusted `triggering_actor` rows ignored; failed+running → `PENDING`. The amended matcher's tests assert the `EXHAUSTED id/conclusion/completed_at` and `PENDING id` contracts over the same harness, including foreign-head/foreign-PR/failed-authentication invisibility.
- **Shell-transition tests** (existing harness style, executing the workflow's real shell): every row of the decision table, the two-request A/B schedule with dispatch-POST counting (P01), the freshness refusals (failed read, ambiguous window, unparsable instant), prompt-exit assertions (terminal conclusion → exit at first observation, zero dispatches), and the pinned-run-identity requirement at binding.
- **Wording:** pending work prevents dispatch; an already authoritative exact-head success can still be reused.

**Non-goals restated (unchanged):** no test or check weakening; no application/journey timeout changes; no #1040 product code; no automatic repeat-until-green (every attempt after a failure requires a fresh, deliberate, provably-fresh label event, and every failure is reported first); no rebase or head change of #1066; HOME untouched; failed runs and artifacts preserved forever.
