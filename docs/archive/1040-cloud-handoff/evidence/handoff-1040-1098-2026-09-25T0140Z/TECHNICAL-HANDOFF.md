# TECHNICAL-HANDOFF — #1040 implementation + #1098 requester maintenance

Worktree map:
- `C:\Sean Project\RMT-1040-worktree` — PR #1066 (#1040 implementation), head `027c985e82d1d1b764a2f24eac6e6c2ed4ec2c71`, clean, FROZEN (last Astra-reviewed head).
- `C:\Sean Project\RMT-1040-ci-worktree` — PR #1098 (requester maintenance), head `23bb25d987639ada3356a7003fa0380ba08e5042`, clean, `ready-for-full-ci` label present as evidence.
- Canonical `C:\Sean Project\Requirements Management Tool` — CONTAMINATED by the rehearsal incident (see START-HERE §0). Local main `b6309fb0…` contains two accidental commits; local author config is `rehearsal/rehearsal@invalid`; branch `drillA2` exists at `bb8a6622…`. Recovery NOT authorized or performed. `origin/main` = `0dddb029…`.

---

## PART A — #1040: frozen Release relationship-picker membership (PR #1066)

### A1. Problem
Before #1040, the Release relationship picker paginated candidates live: a Release created (or its cohort changed) after page 1 was captured could appear or vanish on page 2, and revoking access between pages could inject candidates the steward no longer may link. #1040 requires database-owned, project-fenced, insertion-ordinal-ordered membership frozen at the moment page 1 is captured, plus a page-one cutoff and continuation predicate that make every page of one selection observe one immutable cohort.

### A2. Mechanism (PostgreSQL)
1. **Sequence allocation + project fence.** Migration `20260921140636_AddReleasePickerMembership` (in `product/src/AeroLink.Infrastructure/Persistence/Migrations/`) adds `PickerInsertionOrdinal` (bigint) to `software_releases`, a dedicated sequence `aerolink_release_picker_ordinal_seq`, and a BEFORE INSERT trigger that takes `pg_advisory_xact_lock(hashtext('aerolink-release-picker:' || ProjectId))` BEFORE `nextval()`, so ordinals are allocated under a per-project fence (no cross-project contention, no same-project interleaving).
2. **Page-one cutoff capture + continuation predicate.** `ReleaseLinkQueryService` (in `product/src/AeroLink.Infrastructure/` — Release link-options query path) captures `cutoff = MAX(ordinal)` over the project on page 1 (Read Committed is sufficient; the MAX is taken inside the same request-scoped transaction) and every continuation page filters `ordinal <= cutoff` before keyset ordering, so pages 2..n observe exactly the frozen cohort.
3. **Legacy NULL cohort.** Pre-existing releases have NULL ordinals: they are treated as a legacy cohort that pages by the old ordering but is still cut off at page-1 time (a NULL-ordinal release created after page 1 never appears mid-selection). PostgreSQL side keeps NULLs; no backfill.
4. **EF configuration.** `PickerInsertionOrdinal` is configured `ValueGeneratedOnAdd` with `AfterSaveBehavior(PropertySaveBehavior.Ignore)` so EF never writes the column on UPDATE (readback only), preventing any save path from re-writing an allocated ordinal.
5. **Release-v2 cursor contract.** The Release picker's v2 cursor is release-specific (version + id + cutoff), never a raw offset; non-Release pickers keep their existing behavior unchanged.
6. **Concurrency/access/browser evidence.** Concurrency: two-session test showed a late INSERT excluded from an earlier selection's continuation; access revocation between pages excludes the revoked candidate. Browser: dedicated journey `managed-documentation-center-picker-continuation.spec.ts` (freezes membership across pages, links the exact selected build) — passed 6/6 locally at the candidate head and in CI on earlier rounds; it is independent of the intermittent `digital-thread-1046` failure (§B3).

### A3. SQLite (DEV/profile) guard
Because SQLite (DEV profile) lacks the trigger machinery, `ReleasePickerSqliteGuard` (in `product/src/AeroLink.Infrastructure/`) installs on first DEV use: the same `PickerInsertionOrdinal` column with a NOT NULL UNIQUE-ish discipline enforced by triggers (insert trigger assigns from a per-project MAX+1 within an immediate transaction; update trigger refuses ordinal changes), preserving the same frozen-page semantics. The guard is profile-conditional and does not run on PostgreSQL (trigger + sequence cover it).

### A4. Migration/history preservation
The migration is additive (table + sequence + trigger + NOT NULL backfill of existing rows via the sequence's first allocations is NOT performed — existing rows keep NULL = legacy cohort). No history table is rewritten; released/historical requirements and signatures untouched. Apply-once, never edited after merge.

### A5. Access/authorization
Relationship-candidate visibility remains governed by the existing project-scoped access rules (`ReleaseLinkQueryService` filters to candidates the steward may link; revoked access between pages excludes the candidate from later pages even within the frozen ordinal range).

### A6. Remaining work for #1040 (for the receiving agent)
- Checkpoint D completion: one exact-head Full Product qualification for PR #1066 at `027c985e…` (currently blocked by the intermittent browser journey §B3 and by PR #1098's failed gate — see PART B), then Astra's Checkpoint D review, merge, post-deployment verification on HOME (migration `20260921140636` applied, picker smoke, access smoke), and closure.
- Migration `20260921140636` applies at next deploy via the launcher's backup/isolated-copy/upgrade path; post-deploy HOME verification steps are listed in packet 23 (evidence dir).

---

## PART B — #1098: same-head retry for the trusted Full-CI requester (PR #1098)

### B1. Problem (why current main cannot dispatch another trusted attempt after this same head fails)
On current main, `request-full-ci.yml`'s `find_product_run` reports all-completed-unsuccessful trusted attempts as `FOUND <failed-run>`; only a `NONE` result reaches the dispatch branch. Consequences, live-demonstrated on PR #1098 (gate 36064279956 FAILED; requester 36064258664 correctly refused to bind):
- Once one trusted attempt at a given head finishes unsuccessfully, every later label event selects the failed run and errors — the head is permanently unbindable through the trusted path.
- A human "Re-run" cannot repair this: the matcher requires `actor/triggering_actor == github-actions[bot]`, and a human rerun inherits the human identity, so its evidence cannot bind.

### B2. Implemented change (PR #1098, two files)
1. **Matcher verbs:** all-completed-unsuccessful → `EXHAUSTED <newest-id> <conclusion> <updated_at>`; a still-running attempt → `PENDING <newest-unfinished-id>`; success → `FOUND <lowest-id>` (precedence unchanged); else `NONE`.
2. **Common authorization gate before EVERY dispatch POST (NONE included):** strict timestamps on every validated record; bounded 3-page pagination with `rel="next"` completeness for the issue-events and requester-run histories; response-shape validation per endpoint (bare arrays vs `workflow_runs` wrappers); mandatory requester identity (exact head SHA + `pull_request_target` event); first-attempt-only requesters (`GITHUB_RUN_ATTEMPT == 1`); self-postdating; spend-once consumption (any other same-head requester run at-or-after the label event consumes it; ties conservative; all statuses count); stale authorization refuses.
3. **Dispatch:** `return_run_details: true`; the returned `workflow_run_id` is fetched and validated through the exact head/ref/PR/bot checks before pinning `(id, attempt)`. A lost response or transport error (non-2xx/nonzero curl) is UNCERTAIN: boundary-filtered discovery only, bounded wait, refusal — never retransmission.
4. **Pinned qualification:** poll verdicts travel via a file; pin/poll modes in `check-product-run.py` (pin validates + reports ATTEMPT, completed failures never pinned; poll derives terminal states and refuses attempt changes); qualification evidence fetched from the pinned attempt (`/attempts/<n>/jobs`).
5. **Preserved:** live-PR auth (before dispatch and every poll), queue-exclusion guard (before dispatch and before App publication), Product authentication check, single-aggregate verification, App-published `Trusted merge-queue binding`, permissions, environment.

### B3. Remaining blockers
1. **Failed exact-head gate (unresolved):** the ONE authorized qualification run for this PR (gate 36064279956 at `23bb25d9…`) failed on `tests/digital-thread-1046-interaction.spec.ts:203` (browser journey, both attempts; screenshots show the selected card clipped at the lower canvas edge). Locally the same journey passed 6/6 and a 5-run geometry probe measured deterministic final placement with bottom overflow 236px — WITHDRAWN after Astra's coordinate correction: against correct viewport bounds (L280/T340/R1920/B584) the card fits with 16px bottom / 28px right clearance. Corrected classification: **existing intermittent failure of this journey on main** (merge-group 36049357299 failed it initially, passed on retry; push 36052413361 skipped browser journeys); underlying cause UNRESOLVED. The candidate's two files touch no client code.
2. **Kernel installation boundary:** `request-full-ci.yml` is in `MAINTENANCE_KERNEL_PATHS`; the routine-route preflight returns `separate-trust-root-bootstrap-required`. MERGING.md requires a separately reviewed trust-root transition. Proposal v3 (retained: `critical-evidence/45-trust-root-transition-proposal-v2.md`) supplies: qualification-before-installation ordering with owner-chosen routes; frozen-base/result-tree qualification commands; an EXECUTED isolated rehearsal (all drills passed); unconditional rollback qualification; verified temporary-authority removal; interrupted/concurrent-change handling. **The proposal is NOT approved; no installation route is established; merging the PR does not establish one.**
3. **Remaining coverage (stated, not claimed):** no executed time-ordered two-requester queued-successor schedule (the spend-once refusal is asserted as a static gate refusal); `return_run_details` live behavior against the pinned API version unqualified; predating/malformed-shape gate refusals covered at unit level only; Windows skips (10 POSIX-only scenarios) are a disclosed platform limitation.
