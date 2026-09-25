# START-HERE — GLM handoff for #1040 / PR #1066 / PR #1098

Package: `C:\Sean Project\RMT-1040-glm-evidence\handoff-1040-1098-2026-09-25T0140Z`
Prepared: 2026-09-25T01:40Z by GLM (outgoing agent). Read this file before any other work.

## 0. URGENT — canonical-checkout incident (read first)

During rehearsal development on 2026-09-24, GLM's `rehearsal-mirror.sh` v1 script failed at its `cd "$CLONE"` step (the clone had failed because the mirror lacked `main`) and CONTINUED executing git commands in the shell's current directory — the CANONICAL repository `C:\Sean Project\Requirements Management Tool`. This produced accidental commits on canonical local `main` and left repo-local author configuration changed.

Astra's independently verified observations (bundle copied to `critical-evidence/incident-bundle/`, hashes in MANIFEST.csv):

- Canonical local `main` BEFORE the rehearsal: `77b96857868f35926cd439371af159f8af2230e2`
- Canonical local `main` AFTER the unintended operations: `b6309fb01d46afe17e7aaa3e34fc0bc38be6e726`
- Accidental commits on main:
  - `a35b260b799cbfbea436ed5f63ff3c6ae1f23555` — reverted the unrelated #1105 product change
  - `b6309fb01d46afe17e7aaa3e34fc0bc38be6e726` — "drill C: concurrent settings change", modifying `request-full-ci.yml`
- Additional affected branch: `drillA2` at `bb8a6622a08742cc75f9879efb53ce8aa331b2fe`
- Repository-local author settings observed as `rehearsal / rehearsal@invalid`
- Astra preserved evidence and a verified Git bundle in the incident-bundle directory (originals also at `C:\Users\seanm\AppData\Local\Temp\astra-1098-isolation-incident-78a8f2c8606a413dbea66a7188d1e356`)

GLM's fresh READ-ONLY re-inspection (2026-09-25T01:40Z, no mutations performed) confirms:
- Canonical `HEAD` = `b6309fb01d46afe17e7aaa3e34fc0bc38be6e726`; working tree clean.
- `drillA2` still at `bb8a6622a08742cc75f9879efb53ce8aa331b2fe`.
- Repo-local `user.name=user.name=rehearsal` / `user.email=rehearsal@invalid` still set (global config remains seanmccarthyns).
- `main` reflog head: `b6309fb0 main@{0}: commit: drill C: concurrent settings change`; `a35b260b main@{1}: revert: Revert "Show the mock projects..."`.
- `origin/main` = `0dddb0291420ee3630314bba8b9a5a3e6d367e95` (canonical local main is AHEAD of origin due to a legitimate pull plus the two accidental commits).

**EXPLICIT STATEMENT: a clean working tree can still contain unintended COMMITTED changes — `git status` being clean does NOT mean main is safe. Recovery (removing `a35b260b`/`b6309fb0`, restoring `77b96857`, resetting the local author config, deleting/moving `drillA2`) has NOT been authorized and has NOT been performed. Do not push canonical local main anywhere. Recovery must be separately reviewed by Astra.**

## 1. Current situation

- **#1040** (frozen Release relationship-picker membership): implementation long complete and Astra-qualified through Checkpoint C; Checkpoint D (Full-CI qualification) is INCOMPLETE — see §3 and `EVIDENCE-LEDGER.md`.
- **PR #1066** (the #1040 implementation): draft, frozen at `027c985e82d1d1b764a2f24eac6e6c2ed4ec2c71`, worktree `C:\Sean Project\RMT-1040-worktree` (clean). Do not rebase or change the head.
- **PR #1098** (CI-maintenance: same-head retry for the trusted requester): draft at `23bb25d987639ada3356a7003fa0380ba08e5042`, worktree `C:\Sean Project\RMT-1040-ci-worktree` (clean), `ready-for-full-ci` label present as failure evidence. Implementation and local qualification received Astra PASS; the ONE authorized exact-head Full Product run FAILED on an unrelated, known-intermittent browser journey (§3). Per direction, reported before any retry; the label was left in place as evidence.
- **Kernel installation boundary:** PR #1098 modifies a protected workflow (`request-full-ci.yml` is in `MAINTENANCE_KERNEL_PATHS`). No approved installation route exists today; the revised (still unapproved) proposal is at `critical-evidence/45-trust-root-transition-proposal-v2.md`. Do not treat any proposal as authorization.

## 2. Takeover order (suggested)

1. Read `REVIEW-STATUS.md` (what Astra accepted, what remains open, what was superseded).
2. Read `TECHNICAL-HANDOFF.md` (both workstreams' mechanics, file locations, remaining blockers).
3. Read `EVIDENCE-LEDGER.md` (every material result with SHA, command, counts, artifact paths, provenance).
4. If resuming #1098 qualification: first obtain Astra's direction — the failed gate (36064279956) and requester refusal (36064258664) are preserved, and under current main's logic the head `23bb25d9…` is unbindable (any label re-event will select the failed run and refuse). No retry is authorized.
5. The canonical-incident recovery (§0) requires Astra-reviewed steps before execution; Astra's bundle contains `accidental-commits.bundle` and `accidental-main.patch` sufficient for exact restoration when authorized.

## 3. Hard boundaries observed at handoff time

- No CI retry, no label changes, no dispatch/rerun, no queue admission, no merge, no #1040 closure, no HOME action, no product/HOME qualification repeat was performed while packaging.
- Persistent PostgreSQL (port 54329), `product/.local`, and all original evidence remain untouched.
- Both worktrees verified clean at their documented heads (§1).
- Background process left running by GLM: a Vite dev server (PID 27364) listening on `[::1]:5199` serving `C:\Sean Project\RMT-1040-ci-worktree\product\client` — started for the F01 geometry probe; safe to stop, documented here for completeness.
