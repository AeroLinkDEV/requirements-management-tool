(Retained copy of the packet submitted to Astra via Sean on 2026-09-24: the ONE authorized exact-head qualification request failed on an unrelated browser journey; reporting before any retry per direction.)

ASTRA REVIEW REQUEST — #1040 / PR #1098 — EXACT-HEAD QUALIFICATION RESULT: FAILURE REPORTED BEFORE ANY RETRY

**Requested decision and next action:**
The ONE authorized exact-head Full Product qualification request for PR #1098 was performed and FAILED on an unrelated browser journey. Per direction, this report comes before any retry, head change, or other dispatch route. No readiness-label removal/re-add, no new commit, no live dispatch beyond the authorized one, no queue admission, no merge, no installation, no HOME action, no #1066 change.

**EXECUTION (exact identifiers):**
- Readiness label applied once at 2026-09-24T21:53Z to PR #1098 (head verified exactly `23bb25d987639ada3356a7003fa0380ba08e5042` immediately before; worktree clean; draft; auto-merge off; no other labels).
- Trusted requester run (protected main's `Request full merge validation`, event pull_request_target): **36064258664**.
- Exact-head Product run dispatched by it: **Product quality gate 36064279956** (workflow_dispatch bound to `23bb25d987639ada3356a7003fa0380ba08e5042`, comparison base per PR).
- Result: **Product gate conclusion FAILURE**; requester run failure at step "Authenticate live ready PR, dispatch once, and bind exact Product success" (exact-success binding refused — correct fail-closed); PR check "Full Product evidence aggregate" FAILURE (`These gates did not pass: Browser journeys (1/4)`).
- Retained artifacts: `ci-gate-36064279956/playwright-diagnostics-1-1.zip` (failure context, screenshots, traces for both attempts of the failing journey) and `playwright-diagnostics-1.zip` (shard 1 diagnostics).

**FAILURE DETAIL:**
- Failing journey: `tests/digital-thread-1046-interaction.spec.ts:203` — "recovered strip space fits the selected card without an unnecessary bottom-dock change" — `expect(received).toBe(expected)` — failed on BOTH the initial attempt and retry #1 (artifacts: `test-failed-1.png`, `error-context.md`, `trace.zip` per attempt).
- Everything else in the gate passed: all API/Domain/Infrastructure shards, client build, operator/recovery script contracts, PostgreSQL smoke, Browser journeys shards 2–4, production-build journeys, CI metrics.
- The failing journey is a client visual/layout spec. The candidate's two files (.github workflow + test-planner contract test) touch no client code; this journey is independent of the change.

**OBSERVATION (noted, not acted on):** this failure now live-demonstrates, on the PR itself, the unbindable-head defect the candidate fixes: under current main's requester logic, every future label event on this exact head will select the failed run and refuse — the only main-logic escape is a new commit. Under the accepted same-head retry design, the bounded retry path would exist. Per direction I have NOT retried, re-labeled, or re-dispatched; the label remains on the PR as evidence.

**STATE:** head unchanged `23bb25d987639ada3356a7003fa0380ba08e5042` (worktree clean, verified); PR #1098 draft, auto-merge off, `ready-for-full-ci` label present (as applied), requester failed, aggregate failed, no queue entry; #1066 draft/clean at `027c985e82d1d1b764a2f24eac6e6c2ed4ec2c71`, untouched; #1040 open; Checkpoint D NOT passed; D01–D03 closed; installation and rollback remain separately unresolved.

**Awainting your direction:** whether to (a) treat this as the environmental single-journey flake class previously documented and authorize one bounded same-head retry under the accepted design (which requires the separately reviewed installation first), (b) direct another prescribed route, or (c) otherwise direct. The separately reviewed executable trust-root installation AND rollback proposal you authorized preparing is drafted and retained at `41b-trust-root-transition-proposal.md` (annex to this packet) for your review — it does not self-execute and changes nothing until separately approved.

Please return your verdict and the precise next action — I will wait for Sean to relay your actual response.

CHECKPOINT D — AWAITING ASTRA REVIEW. Queue admission, merge, rollout and issue closure remain gated.
