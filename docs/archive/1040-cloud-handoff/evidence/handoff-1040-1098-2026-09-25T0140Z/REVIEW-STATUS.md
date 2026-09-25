# REVIEW-STATUS — accepted / open / superseded findings and authorization boundaries

Provenance: every verdict below is Astra's, relayed verbatim by Sean. GLM never self-assigned a gate verdict.

## #1040 implementation (PR #1066)

| Checkpoint / finding | Status |
|---|---|
| Checkpoint A rounds 1–4 | PASS after corrections (SQLite cohort flag, sequence+fence design, cursor bound 640→4096, evidence corrections) |
| Checkpoint B rounds 1–4 | PASS after corrections (B01 host-before-seed, B02 SQLite installer, B04 guard matrix, B05 accounting) |
| Checkpoint C (I01–I06) | PASS at head `bf9dc3f0…` (I01 conflicts, I02 route manifest, I03 copied-host suite, I04 accounting, I05 contract inventory, I06 browser journey) |
| Checkpoint D | **NOT PASSED.** Blocked: exact-head Full Product gate failed on an unrelated intermittent browser journey; requester correctly refused binding. No retry authorized. |
| Post-deployment acceptance | Not started (requires merge; merge not authorized) |

## #1098 requester maintenance (PR #1098)

| Review | Verdict | Outcome |
|---|---|---|
| Design review (I02 proposal, rounds 1–2) | CHANGES REQUIRED → corrections | EXHAUSTED/PENDING verbs, common gate, spend-once design accepted; invalid routes (owner rerun, fresh-commit-as-install) withdrawn |
| Prototype qualification (23/23 + Astra's own probes) | PASS — DESIGN ONLY; fixture gaps later found and fixed (v5.1) | Offline model accepted as design evidence, not workflow qualification |
| Implementation v4 (candidate 2e6bb58c) | CHANGES REQUIRED — I07: OR-list suppressed errexit; stale success file could bind | Fixed in v4: explicit status capture, stale-file removal, REFUSED surfacing — Astra replayed both failure probes: now refuse |
| Implementation v4 test claims | CHANGES REQUIRED — the four post-pin regressions were never committed (helper exited before write) | Fixed in v5 (`bf9dc3f0`): regressions actually committed; Astra executed 32/0/0 on Linux |
| Implementation v5 (bf9dc3f0) | CHANGES REQUIRED — TEST FIXTURES ONLY: pinned-read-body never written; fixtures carried wrong head/ref identity | Fixed in v6 (`23bb25d9`): normalized bodies/leftover; precondition + emitted-body assertions; POSIX 32/32 re-executed |
| **Candidate v6 (23bb25d9)** | **PASS for implementation and local qualification** (Astra independently: Linux 32/0/0; digests match; worktrees clean) | Implementation accepted within stated limits |
| Exact-head Full Product qualification | FAILURE — reported before any retry; requester + aggregate refused correctly (fail-closed confirmed) | Blocking Checkpoint D |
| Transition proposal v1 (41b) | NOT APPROVED — owner bypass + admin merge unauthorized; #960 history misstated; ordering reversed; rehearsal/recovery/rollback incomplete | Superseded by v2/v3 annexes |
| Transition proposal v2/v3 annex | Reviewed; rehearsal script hollow drills exposed → rewritten + EXECUTED (all drills pass); history reconciled (#960 exception spent); limitation stated: no compliant installation route today | Remains OPEN — proposal only; installation/rollback unresolved |

## Superseded / disproven evidence claims (do not rely on these)

| Claim | Disposition |
|---|---|
| SQLite `recursive_triggers` OFF makes the allocator's internal UPDATE skip triggers | DISPROVEN by Astra (Checkpoint A round 3): recursive_triggers only gates same-trigger recursion; the cohort flag + absolute immutability design replaced it |
| "236.0px bottom overflow; card displaced right; journey passes only transiently" | WITHDRAWN — probe mixed coordinate systems; corrected bounds show containment HELD (16px bottom / 28px right clearance); local 6/6 passes were genuine |
| "Main's recent gates exercised this journey successfully" | CORRECTED — merge-group 36049357299 failed it initially (passed on retry); push 36052413361 skipped browser journeys |
| "No bootstrap has ever occurred" | CORRECTED — the trust-root bootstrap occurred for #960; its exception is SPENT; no currently valid installation authorization exists |
| "Owner rerun / fresh-commit escape" as qualification or installation routes | WITHDRAWN — human rerun cannot satisfy bot triggering_actor; fresh commit creates a new candidate, not an installation route |
| "All drills passed" (v1 rehearsal) | HOLLOW — interruption drill ran post-merge; abort of a nonexistent merge suppressed; missing-file/contract/YAML checks unimplemented; replaced by executed v2 drills with real preconditions |
| Packet-claimed post-pin regressions at 2e6bb58c | NOT COMMITTED at that sha (helper exited before write); actually committed at `bf9dc3f0` and executed 32/0/0 on POSIX |

## The new canonical-checkout incident (open)

| Item | Status |
|---|---|
| Cause | rehearsal-mirror.sh v1 continued executing git commands in the canonical checkout after its `cd "$CLONE"` failed (clone failed because the mirror lacked `main`) |
| Effects (Astra-verified) | Canonical local main advanced `77b96857…` → `b6309fb0…` via accidental commits `a35b260b…` (reverted unrelated #1105 change) and `b6309fb0…` ("drill C: concurrent settings change", modifying request-full-ci.yml); branch `drillA2` at `bb8a6622…`; repo-local author config `rehearsal/rehearsal@invalid` |
| Preservation | Astra's verified bundle + GLM's copy in `critical-evidence/incident-bundle/` (hashes in MANIFEST.csv) |
| Recovery | **NOT authorized, NOT performed.** Requires separately reviewed steps: reset local main to `77b96857…` (or forward-fix), delete/move `drillA2`, restore repo-local author config; verify `origin/main` (`0dddb029…`) untouched |

## Authorization boundaries at handoff (Astra's standing directives)

- No retry of the failed exact-head gate; no new label action; no direct dispatch; no head change on #1066/#1098; no ruleset change; no maintenance approval request; no queue admission; no merge; no HOME action; no #1040 closure.
- Installation and rollback of the requester change remain separately unresolved and require the reviewed trust-root transition; no route is approved.
- Persistent PostgreSQL (54329), `product/.local`, and original evidence must remain untouched.
