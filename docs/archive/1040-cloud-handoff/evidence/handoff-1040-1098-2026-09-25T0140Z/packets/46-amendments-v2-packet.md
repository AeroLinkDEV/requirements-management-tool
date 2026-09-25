(Retained copy of the amendments packet submitted to Astra via Sean on 2026-09-24: coordinate correction accepted; I07 workflow correction confirmed; proposal v3 with EXECUTED rehearsal — script diff + all-drills-passed run log + corrected accounting. No label/CI/dispatch/merge/HOME action performed.)

ASTRA REVIEW REQUEST — #1040 / PR #1098 — AMENDMENTS ONLY (coordinate correction accepted; rehearsal script rewritten, drills fixed and EXECUTED; proposal revised; accounting)

**Scope (per direction):** only the amendments and their supporting evidence. No label action, CI retry, dispatch, head change, ruleset change, approval request, queue admission, merge, or HOME action. Nothing executed against live systems. No browser repetition (coordinate correction needed none). No workflow redesign.

**AMENDMENT 1 — F01 coordinate correction ACCEPTED (no change):** withdrawn claims stand withdrawn; corrected containment (16px bottom / 28px right clearance within viewport bounds L280/T340/R1920/B584) stands; local 6/6 journey passes were genuine deterministic containment.

**AMENDMENT 2 — main-branch evidence CORRECTED (retained at `44-f01-amendment.md`):** merge-group run 36049357299 **failed this journey initially and passed on retry**; push run 36052413361 **skipped browser journeys**. Classification: an existing intermittent failure of this journey on main, underlying cause unresolved; the candidate neither introduces nor fixes it; the CI failure at the candidate (two attempts) is consistent with this known intermittent behavior.

**AMENDMENT 3 — I07 workflow correction CONFIRMED and retained (workflow unchanged since your acceptance).**

**AMENDMENT 4 — EXECUTED isolated rehearsal (script REWRITTEN; the v1 drills were hollow exactly as your probes demonstrated: the interruption drill ran after the candidate was already merged, the abort of a nonexistent merge was suppressed into a false PASS, and the claimed missing-file/contract-suite/YAML checks were not implemented).**

Script: rewritten `rehearsal-mirror.sh` (v2) — builds a disposable local mirror (bare repo: `main` = frozen main tip, `candidate` = the change tip), clones it, and executes the install/verify/rollback drills with real preconditions and real exit codes:
- **Install**: merges `origin/candidate` into frozen main (conflict resolution = the reviewed two-file content); asserts the result diff vs frozen main is EXACTLY the two reviewed files.
- **Interruption drill**: creates a guaranteed delete/modify CONFLICT (deletes the reviewed workflow file on a branch at the base, then merges the candidate) — asserts `merge_rc != 0` AND `MERGE_HEAD` present (real mid-merge state), then `git merge --abort` and asserts: clean tree, HEAD == frozen main, tree == frozen main's tree, MERGE_HEAD absent.
- **Rollback**: reverts the install merge (`git revert -m 1 main`) and asserts the resulting tree is byte-identical to frozen main (no residual content).
- **Drift**: makes a concurrent protected-path change during the install window and asserts the result-scope comparison DETECTS it (the procedure would abort).

**EXECUTED RUN (this session, disposable temp dirs; full log retained at `rehearsal-v2-run.log`):**
```
PASS: A precondition: pending diff is exactly the two reviewed files
PASS: A: installed; result diff vs frozen main is exactly the two reviewed files
  -- A2 precondition: real mid-merge state established (conflict; MERGE_HEAD present)
PASS: A2: abort left a clean tree
PASS: A2: HEAD and tree restored to the pre-merge state after abort
PASS: A2: MERGE_HEAD cleared by the abort
PASS: B: rollback from the installed tree restores frozen main exactly (no content diff)
PASS: C: concurrent settings change detected via the result-tree digest comparison (procedure aborts on drift)
PASS: D: missing/empty verification file refuses with the default REFUSED verdict
PASS: D: a real verdict file proceeds to the binding stage
rehearsal summary: 0 failure(s)
rehearsal: ALL DRILLS PASSED
```

**PROPOSAL v3 (retained at `45-trust-root-transition-proposal-v2.md`, updated):** incorporates the executed rehearsal results, plus the F02 corrections: history reconciled (the #960 exception is spent; no currently valid authorization exists), qualification-before-installation ordering (green exact-candidate Full Product evidence is a PRECONDITION, obtained via one of the owner-chosen routes — never from the installed state), interrupted/uncertain-operation handling (unknown state → verify or revert, never assume), concurrent-settings-change drift checks (re-snapshot before merge, drift → abort), verified bypass-actor REMOVAL as a closing action, and unconditional rollback qualification. The v1 claims you rejected (236px overflow, transient-only-pass, owner-rerun and fresh-commit-as-installation routes, "main fix + relabel" qualification) are withdrawn.

**CORRECTED QUALIFICATION ACCOUNTING (literal):**
- POSIX container (Node 24.21 / Python 3.11.2 / Bash 5.2.15, network disabled, read-only mount): 32 passed / 0 failed / 0 skipped.
- Windows local: 18 passed / 14 skipped / 0 failed.
- Coverage levels: integrated-on-POSIX — the named dispatch/binding/refusal/uncertainty/multi-page/per-attempt-jobs scenarios; unit-level only — predating-requester and malformed-shape/timestamp gate refusals, pinned checker pin/poll modes, pagination bounded-walk; structural only — preserved live-PR/queue/Product/App controls. Not covered — an executed time-ordered two-requester queued-successor schedule.

**CONFIRMATION:** #1066 draft, auto-merge off, clean at `027c985e82d1d1b764a2f24eac6e6c2ed4ec2c71`; PR #1098 draft at `23bb25d987639ada3356a7003fa0380ba08e5042`, readiness label present as evidence, requester 36064258664 and gate 36064279956 preserved; worktrees clean. #1040 open. Checkpoint D NOT passed. D01–D03 closed.

Please return your verdict and the precise next action — I will wait for Sean to relay your actual response.

CHECKPOINT D — AWAITING ASTRA REVIEW. Queue admission, merge, rollout and issue closure remain gated.
