# EVIDENCE-LEDGER — executed results and provenance

Provenance codes: [A] Astra independently verified/executed; [G] GLM executed (GLM-authored evidence, may be independently re-executed); [P] proposal/assertion only (not executed or not yet approved). All artifact paths absolute. Superseded claims are listed in REVIEW-STATUS.md.

## Incident (canonical checkout) — see START-HERE §0

| Result | Provenance | Evidence |
|---|---|---|
| Canonical main before rehearsal `77b96857…`; after `b6309fb0…`; accidental commits `a35b260b…` (reverted #1105) and `b6309fb0…` ("drill C"); drillA2 branch `bb8a6622…`; local author `rehearsal/rehearsal@invalid` | [A] Astra verified; bundle retained | `critical-evidence/incident-bundle/` (accidental-commits.bundle SHA-256 70f10dc2…; accidental-main.patch ab5b562f…; canonical-head/log/reflog/author files) and originals at `C:\Users\seanm\AppData\Local\Temp\astra-1098-isolation-incident-78a8f2c8606a413dbea66a7188d1e356\` |
| GLM read-only re-inspection 2026-09-25T01:40Z: HEAD `b6309fb0…` ✓; drillA2 ✓; local author config unchanged ✓; reflog head matches; origin/main `0dddb029…` | [G] GLM read-only | This ledger; no repair performed |

## #1098 implementation + local qualification (head 23bb25d9…)

| Result | Provenance | Evidence |
|---|---|---|
| Windows requester suite: 18 passed / 14 skipped / 0 failed (10 POSIX-only integrated scenarios + 4 post-pin regressions skipped on win32) | [G] GLM executed; [A] Astra independently re-ran 18/0/0 and additionally 20 pass / 7 fail in a Linux container exposing harness defects (later fixed) | `node --test product/test-planner/tests/full-ci-readiness-dispatch.test.mjs` with `AEROLINK_TEST_PYTHON` set; log `dt1046-local/` era superseded by final run (see 46 packet) |
| POSIX requester suite (Linux container node:24, network disabled, read-only mount): 32 passed / 0 failed / 0 skipped — includes the four post-pin regressions | [A] Astra independently executed 32/0/0 at 23bb25d9 and bf9dc3f0 | Command: `docker run --rm --network none -v <worktree>:/work:ro -w /work -e AEROLINK_TEST_PYTHON=/usr/bin/python3 node:24 bash -c "node --test product/test-planner/tests/full-ci-readiness-dispatch.test.mjs"` |
| I07 workflow correction (explicit status handling; stale-file removal; REFUSED surfacing) | [A] Astra replayed both original failure probes against the fix: both now refuse | Workflow file at PR #1098 head; probes described in Astra's v4 review |
| All planner test files (15 files) green on Windows | [G] GLM executed | `node --test product/test-planner/tests/*.test.mjs` per-file counts in packet 39 |
| Workflow YAML parse-validated | [G] GLM executed | pyyaml --target install + safe_load (validation only) |

## #1098 exact-head Full Product qualification — FAILED (preserved)

| Item | Value | Provenance |
|---|---|---|
| Readiness label applied once | 2026-09-24T21:53Z | [G] GLM executed under Astra authorization |
| Trusted requester run | 36064258664 — FAILURE at "Authenticate live ready PR, dispatch once, and bind exact Product success" (correct fail-closed on the failed Product run) | [A] Astra verified refusal |
| Exact-head Product gate | 36064279956 at `23bb25d9…` — FAILURE | [A] Astra verified |
| Failing journey | `tests/digital-thread-1046-interaction.spec.ts:203` — failed initial + retry #1 (`expect(received).toBe(expected)`); everything else in the gate passed | [A] Astra confirmed both attempts; artifacts: `ci-gate-36064279956/playwright-diagnostics-1-1.zip` (context/screens/traces per attempt) |
| Aggregate refusal | "Full Product evidence aggregate" FAILURE — `These gates did not pass: Browser journeys (1/4)` | [A] Astra verified |

## Browser journey disposition (digital-thread-1046:203)

| Result | Provenance |
|---|---|
| Candidate local journey: 6 passed / 0 failed (bounded, --repeat-each=6, headless, stated count) | [G] GLM executed; count and every result reported |
| CI at candidate: failed initial + retry #1 | [A] Astra verified |
| Main merge-group 36049357299: failed this journey initially, passed on retry (context only — not proof of cause) | [A] Astra corrected/verified |
| Main push 36052413361: browser journeys SKIPPED | [A] Astra corrected/verified |
| Classification: existing intermittent failure on main; underlying cause UNRESOLVED; probe geometry claims (236px overflow, right displacement, transient-only-pass) WITHDRAWN (probe mixed coordinate systems) | [A] Astra correction; [G] withdrawal accepted |
| Retained artifacts: `C:\Sean Project\RMT-1040-glm-evidence\ci-gate-36064279956\playwright-diagnostics-1-1.zip`; local bounded runs under `C:\Sean Project\RMT-1040-glm-evidence\dt1046-local\` | [G] GLM retained |

## Rehearsal + proposal (offline preparation; nothing executed against live systems)

| Item | Provenance |
|---|---|
| Rehearsal v2 script EXECUTED (disposable temp mirror; no live systems): install merge two-file scope ✓; interruption drill (delete/modify conflict, MERGE_HEAD present, abort restores) ✓; rollback from installed tree restores frozen main tree exactly ✓; drift detected via tree digest ✓; missing/empty verdict guard refuses ✓ | [G] GLM executed; log `rehearsal-v2-run.log`; script `rehearsal-mirror.sh` |
| Proposal v3 (qualification-before-installation ordering; frozen-base/result-tree commands; temporary-authority grant/use/removal incl. interruptions and concurrent-change drift checks; unconditional rollback qualification) | [P] Proposal only — NOT approved, NOT executed |
| Kernel installation boundary: routine-route refusal stands (`separate-trust-root-bootstrap-required`); #960's exception spent | [A] Astra verified (kernel-refusal tests 2/2; history correction) |

## #1066 / #1040 status (frozen at handoff)

| Item | Value | Provenance |
|---|---|---|
| PR #1066 head `027c985e…`, draft, worktree clean | verified read-only at packaging | [G] GLM verified |
| Checkpoint A/B/C | Astra PASSed (see REVIEW-STATUS.md) | [A] Astra |
| Checkpoint D | NOT passed — blocked on exact-head qualification | [A] Astra |
| Failed evidence preserved: gate 36064279956, requester 36064258664, original probe logs | retained, unmodified | [A]+[G] |
