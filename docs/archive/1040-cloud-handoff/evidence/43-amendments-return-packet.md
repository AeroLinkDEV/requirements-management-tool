(Retained copy of the amendments packet submitted to Astra via Sean on 2026-09-24: F01 coordinate amendment, revised proposal v2 with EXECUTED isolated rehearsal, corrected accounting.)

ASTRA REVIEW REQUEST — #1040 / PR #1098 — AMENDMENTS ONLY (coordinate correction, proposal v2, accounting)

**Scope of this return (per direction):** only the amendments and their supporting evidence. No browser repetition was performed (the coordinate correction needed none); no label action, CI retry, dispatch, head change, ruleset change, approval request, queue admission, merge, or HOME action. Nothing was executed against live systems.

**AMENDMENT 1 — F01 geometry and main-evidence corrected (retained at `44-f01-amendment.md`):**
- WITHDRAWN: "236.0px bottom overflow on every run", "card displaced right of the box", and "the journey passes only transiently". Cause: the probe compared viewport-absolute card coordinates against a canvas-content box with the canvas origin omitted.
- CORRECTED (Astra's verified viewport bounds: left 280, top 340, right 1920, bottom 584; card rect: left 1674.15, top 362.15, right 1892, bottom 568): containment **HELD** on every probe run — bottom clearance 16px, right clearance 28px. The local 6/6 journey passes were genuine, deterministic containment.
- Main evidence corrected: merge-group run 36049357299 **failed this journey initially and passed on retry**; push run 36052413361 **skipped browser journeys**. Classification: an existing intermittent failure of this journey on main, underlying cause unresolved; the candidate neither introduces nor fixes it. The CI failure at the candidate (two attempts) is consistent with this known intermittent behavior.

**AMENDMENT 2 — transition proposal v2 (retained at `45-trust-root-transition-proposal-v2.md`; supersedes 41b):**
- Withdrawn routes, per review: owner-triggered rerun (cannot satisfy `triggering_actor == github-actions[bot]`; rerun evidence cannot bind) and fresh-commit escape (creates a NEW candidate; does not install the reviewed change — it remains an ordinary owner escape outside this proposal).
- **Stated limitation, plainly:** for the current red head `23bb25d9…` there is NO compliant route to a green exact-candidate Full Product run today, and therefore NO workable installation route until one of the owner decisions in §2.1/§3 produces one. Installation cannot be scheduled now; the ordinary refusal remains the operative control.
- Executable procedure now supplied (§2.2–§4 + retained script `rehearsal-mirror.sh`, EXECUTED): frozen-base/result-tree qualification commands (B_PRE/CAND/tree digests, two-file diff digest recorded and byte-compared after merge); isolated rehearsal in a disposable mirror — **EXECUTED this session, all drills passed**: install merge with exactly the two-file diff scope ✓; interruption drill (merge --abort restores a clean tree) ✓; unconditional rollback qualification (pre-built revert restores B_PRE's tree exactly) ✓. Full procedure text with interruption and concurrent-settings-change handling (re-snapshot before merge, drift → abort; removal verified by API read, not expiry; uncertain state treated as UNKNOWN — verify or revert, never assume) is in the retained file.
- New owner decisions explicitly identified: (1) creating a new time-boxed bypass actor (the #960 exception is spent); (2) admin-merging a kernel-path PR; (3) choosing the qualification route; (4) authorizing the rehearsal as valid. History reconciled: the bootstrap DID occur for #960; that exception is spent; this proposal does not self-execute and the routine refusal remains the operative control.

**CORRECTED QUALIFICATION ACCOUNTING (literal):**
- POSIX container (Node 24.21 / Python 3.11.2 / Bash 5.2.15, network disabled, read-only mount): 32 passed / 0 failed / 0 skipped.
- Windows local: 18 passed / 14 skipped / 0 failed.
- Coverage levels: integrated-on-POSIX — the named dispatch/binding/refusal/uncertainty/multi-page/per-attempt-jobs scenarios; unit-level only — predating-requester and malformed-shape/timestamp gate refusals, pinned checker pin/poll modes, pagination bounded-walk (URL forms asserted in the integrated multi-page scenario); structural only — preserved live-PR/queue/Product/App controls. Not covered — an executed time-ordered two-requester queued-successor schedule.

**CONFIRMATION:** #1066 draft, auto-merge off, clean at `027c985e82d1d1b764a2f24eac6e6c2ed4ec2c71`; PR #1098 draft at `23bb25d987639ada3356a7003fa0380ba08e5042`, readiness label present as evidence, requester 36064258664 and gate 36064279956 preserved; worktrees clean. #1040 open. Checkpoint D NOT passed. D01–D03 closed.

Please return your verdict and the precise next action — I will wait for Sean to relay your actual response.

CHECKPOINT D — AWAITING ASTRA REVIEW. Queue admission, merge, rollout and issue closure remain gated.
