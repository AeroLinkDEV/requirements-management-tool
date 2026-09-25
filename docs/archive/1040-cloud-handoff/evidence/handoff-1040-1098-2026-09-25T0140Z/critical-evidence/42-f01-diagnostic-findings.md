(Retained copy of the packet submitted to Astra via Sean on 2026-09-24: F01 diagnostic findings, revised transition proposal, corrected accounting. No label/CI/dispatch/merge/HOME action taken.)

ASTRA REVIEW REQUEST — #1040 / PR #1098 — F01 DIAGNOSTIC FINDINGS, REVISED TRANSITION PROPOSAL, CORRECTED ACCOUNTING

**Requested decision and next action:**
Per the precise next action: (1) both PR heads and all failed evidence are preserved; (2) the single browser failure was investigated locally with disposable state, reproducing the exact viewport and fixture at the candidate and comparing against the unchanged base, with card/canvas geometry, framing state and traces retained; (3) the transition annex is revised offline per F02's five deficiencies, with the #960 history reconciled and every genuinely new owner decision identified; (4) the qualification accounting is corrected. No readiness-label action, no CI retry, no direct dispatch, no head change, no ruleset exception, no maintenance approval, no queue admission, no merge, no HOME action was performed.

**PRESERVATION:** PR #1098 head `23bb25d987639ada3356a7003fa0380ba08e5042` (draft, `ready-for-full-ci` label still present as failure evidence, auto-merge off); Product run 36064279956 (failure) and requester run 36064258664 (refusal) retained unmodified with artifacts (`ci-gate-36064279956/playwright-diagnostics-1-1.zip`: both attempts' error-context, screenshots, traces); #1066 clean at `027c985e82d1d1b764a2f24eac6e6c2ed4ec2c71`.

**F01 — DIAGNOSTIC FINDINGS (full detail retained in `42-f01-diagnostic-findings.md`):**

Environment/identity proofs:
- Client code is byte-identical between base `bac43d09`, candidate `23bb25d9`, and PR head `bf9dc3f0` (`git diff` empty on `product/client` for both ranges). The journey outcome cannot differ candidate-vs-base by code.
- Main's own recent gates on `0dddb029` (push 36052413361 and merge_group 36049357299, both success) exercised the same journey family on effectively identical client code.

Bounded journey reproduction at the candidate (stated count, every result):
- `npx playwright test tests/digital-thread-1046-interaction.spec.ts -g "recovered strip space" --repeat-each=6` (repo defaults; headless; the case sets viewport 1920×1000 then 1920×830): **6 passed / 0 failed**. The journey's own containment assertion (`selectedFits`, expect.poll, 15s) succeeded on every local attempt; CI failed initial + retry.

Standalone geometry probe (5 runs, deterministic measurements; probe source + per-run JSON + screenshots retained in `dt1046-local/`):
- selected `proc-4`; panel `dtnPanel dtnPanel-bottom`; box `{x:0, y:88, width:1640, height:244}`; card rect `{top:362.15, bottom:568, left:1674.15, right:1892}`.
- **Deterministic containment violation on every run**: bottom overflow 236.0px; card displaced right of the box (left/right ✗). Matches the CI screenshots (card clipped at the lower canvas edge).

Probe-vs-journey discrepancy (documented, not hidden): the journey passes locally because its in-page `expect.poll` succeeds the moment containment holds **transiently** during the dock-recovery animation, while the final resting placement violates it; on CI both attempts timed out without ever achieving transient containment. Classification: order/timing-sensitive layout behavior in the fixture page — a product-journey/fixture behavior question, NOT caused by the candidate (client code identical to base and to main's recent green runs). No assertion was weakened, no timeout increased, no product code changed; the probe is standalone in the evidence directory.

**F02 — REVISED TRANSITION PROPOSAL (retained at `43-transition-proposal-revised.md`; summary):**
- History reconciled: the trust-root bootstrap DID occur for PR #960 (merged 2026-09-08) under DEC-121's installation exception, which applied ONLY to #960 and explicitly expired after that installation. My "no bootstrap has ever occurred" statement is withdrawn; corrected present state: the #960 exception is spent and NO currently valid authorization exists for further kernel-path installations — the operative control remains the routine-route refusal.
- Ordering corrected: **successful exact-candidate Full Product evidence is a PRECONDITION obtained BEFORE installation** — installing first to obtain the missing qualification is explicitly excluded. Three owner-chosen qualification routes are identified (one-shot owner re-run of the failed unrelated journey; fresh-commit escape; deferral), each an explicit owner decision. The current candidate is RED; installation cannot proceed until a green exact-candidate run exists by one of these routes or another owner-directed route.
- Added: exact base/result-tree proof (pre-merge main SHA + byte-comparison of the merged diff against the reviewed diff); isolated rehearsal requirement (installation + rollback mechanics rehearsed in a disposable mirror before the live action); temporary-authority restoration as VERIFIED REMOVAL of the bypass actor (not expiry); independently qualified rollback (revert branch prepared, pushed, and qualified BEFORE installation, with its own owner approval — no reuse); interrupted/uncertain-operation handling (unknown state treated as UNKNOWN — verify or revert, never assume).
- New owner decisions explicitly identified: (1) creating a new time-boxed bypass actor (the #960 exception is spent); (2) admin merge of a kernel-path PR; (3) choosing the qualification route; (4) authorizing the rehearsal as valid.
- Limitation stated plainly: if the owner will not grant the new temporary authority, there is NO workable installation route today — the head stays unbindable and the owner's available escape remains a fresh commit through ordinary review. No admin merge is proposed outside this reviewed path.

**CORRECTED QUALIFICATION ACCOUNTING:**
- POSIX container (network disabled, read-only mount, Node 24.21 / Python 3.11.2 / Bash 5.2.15): **32 passed / 0 failed / 0 skipped.**
- Windows local (Git Bash + real Windows Python): **18 passed / 14 skipped / 0 failed** (skips = POSIX-only integrated scenarios + the four new post-pin regressions; accurately disclosed platform limitation, not qualification).
- Coverage levels, literally: integrated-on-POSIX — dispatch/binding/refusal/uncertainty/multi-page/per-attempt-jobs scenarios as named; unit-level only — predating-requester and malformed-shape/timestamp gate refusals, pinned checker pin/poll modes, pagination bounded-walk; structural only — preserved live-PR/queue/Product/App controls. Not covered — an executed time-ordered two-requester queued-successor schedule (the spend-once refusal is asserted as a static gate refusal; the schedule itself is remaining coverage); the pagination UNIT test asserts walk/refusal only (actual requested URLs are asserted by the integrated multi-page scenario).

**CONFIRMATION:** #1066 draft, auto-merge off, clean at `027c985e82d1d1b764a2f24eac6e6c2ed4ec2c71`; PR #1098 draft at `23bb25d987639ada3356a7003fa0380ba08e5042` with the readiness label present as evidence; worktrees clean. #1040 open. Checkpoint D NOT passed. D01–D03 closed. No readiness-label action, CI retry, direct dispatch, head change, ruleset exception, maintenance approval, queue admission, merge, or HOME action performed.

Please return your verdict and the precise next action — I will wait for Sean to relay your actual response.

CHECKPOINT D — AWAITING ASTRA REVIEW. Queue admission, merge, rollout and issue closure remain gated.
