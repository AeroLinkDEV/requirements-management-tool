# Issue #1040: cloud takeover, Checkpoint 1 (takeover, evidence map, recovery hand-back, delivery route)

**From:** cloud Claude, 2026-09-25. **To:** Astra, relayed by Sean. **Status:** proposal awaiting Astra's actual verdict.

## 0. Requested decisions and exact next actions

1. **Delivery route (section 5):** approve updating **feature PR #1066 only** to a new head. The new head is current main merged in (no rebase) plus the reviewed fixes in section 4. It then goes through the existing trusted readiness requester. #1098 is **not** a dependency.
2. **Fix scope (section 4):** accept, amend or reject F1–F6. The one behavior change is F1, a bounded fence wait. Everything else is tests or docs.
3. **Local recovery (LOCAL-RECOVERY-HANDBACK.md):** review the procedure. Sean can run its two read-only steps (the audit and Plan) now. Recover or Rollback only after your approval.
4. **For Sean, a session constraint rather than an Astra decision:** this cloud session may push only to `claude/aerolink-1040-picker-completion-6mjzzg`. To keep PR #1066, Sean must explicitly allow me to push to `glm/1040-release-picker-snapshot`. Otherwise I open a successor PR from my branch and mark #1066 superseded. **Recommendation: allow the push to #1066's branch**, which keeps the PR history and the failed evidence attached to the old head.

**Nothing has been mutated:**
- On GitHub: no push, label, comment, rerun, dispatch, issue or queue action.
- On either PR head.
- On Windows or HOME.

## 1. Verified identities (cloud, 2026-09-25 ~02:45Z)

| Item | Value | How verified |
| --- | --- | --- |
| Protected main | `0dddb0291420ee3630314bba8b9a5a3e6d367e95` | `git ls-remote`, fetch |
| #1066 head | `027c985e82d1d1b764a2f24eac6e6c2ed4ec2c71` | Branch and `refs/pull/1066/head`. Draft, `ready-for-full-ci`, `mergeable_state: blocked`, merge-base `329f0757`, 20 commits behind main |
| #1098 head | `23bb25d987639ada3356a7003fa0380ba08e5042` | Base `bac43d09`. Draft, `ready-for-full-ci`. Exactly 2 files |
| Handoff package | `codex/1040-cloud-handoff` @ `4ec53fee` | `sha256sum --check SHA256SUMS`: exit 0, 958 entries |
| Cloud checkouts | Main clone at `0dddb029`, clean. Detached review worktree `/home/user/wt-1066` @ `027c985e`, clean. Scratch composition worktree (local-only merge `571a827`, never pushed) | `git status` |
| Other active work | #1119 (another Claude session, `ready-for-full-ci`) edits `request-full-ci.yml`, the same kernel file as #1098, and needs its own kernel transition. #1115 and #1116 are open product PRs | GitHub PR list |

## 2. Correction and evidence index

Labels:
- **V** = independently verified in the cloud.
- **R** = previously reported (Astra, per the package); not re-verified here.
- **S** = superseded or withdrawn.
- **P** = proposal.
- **U** = unknown.

| # | Claim | Label | Basis / consequence |
| --- | --- | --- | --- |
| 1 | Relabeling 027c985e cannot redispatch | **V** | `request-full-ci.yml` on main (`find_product_run`): when every trusted match is completed with no success, it reports `FOUND <lowest id> completed failure` and the step exits 1. It dispatches only on `NONE`. A **new head SHA** yields `NONE` and a fresh bot dispatch. |
| 2 | A human rerun cannot supply trusted evidence | **V** | The matcher requires `triggering_actor == github-actions[bot]`. **New observation (not a route):** a human rerun changes that run's `triggering_actor`, so the matcher then *ignores* the run entirely, and a later relabel would see `NONE` and dispatch anew. That hides failed evidence from the matcher. It is a trust-design gap belonging to the #1098/#1119 workstream and must not be used. |
| 3 | #1066 Product run 35845728143 failed for reasons unrelated to the feature | **V** (cause of the API race is inference) | Attempt 1 only. Failed jobs: `API test suite (1/3)` 107131625224, `Browser journeys (4/4)` 107131625496, and the aggregate. **API:** `ProjectPersonnelApiTests.The_holder_cannot_be_their_own_backup` got a 500 on `GET /api/auth/csrf` right after a **successful** login (correcting the handoff's "during login"). The server logged `KeyRingProvider … IOException … DataProtection-Keys\key-e233….xml … used by another process`. `AddDataProtection()` has no persistence override, and xUnit runs classes in parallel, so hosts share the runner's default key folder. The #1066 diff does not touch any of this, and #1066's own API tests were not in shard 1. **Browser:** `audit-value-containment.spec.ts:14`. First attempt stuck at "Authenticating…"; the browser login POST hung 48.6 s and returned 499. On retry, several requests hung together. That is the #939 sign-in-hang shape, also seen on main merge-group runs 35781896621 and 35803141183. The picker spec ran about 4 minutes later and only mutates its own `picker.author.<attempt>` user. |
| 4 | Main has fixed those failures | **V: false** | Main since `329f0757` adds #939 diagnostics only (bac43d0, 804a7b0, 0dddb02). Nothing touches DataProtection. |
| 5 | #1098 run 36064279956 failure | **V** | `Browser journeys (1/4)` job 107850582447: `digital-thread-1046-interaction.spec.ts:203` "recovered strip space…" failed on the first attempt and the retry. The same test failed both attempts in 35949958201 and failed-then-passed in main merge-group 36049357299. Cause unresolved (**U**). |
| 6 | Feature Checkpoint C passed at `bf9dc3f0` | **S / invalid** | `bf9dc3f0` exists only on #1098 and is not an ancestor of #1066. The IDs I01–I06 are #1098 defect IDs. Feature C was submitted at 6eb0a07 (R1), dafb08c (R2, with blocking findings C1040-C03/C04/C05) and e0509c0c (R3, "awaiting"). **No verbatim Astra C verdict is retained** (**U**). Even a C PASS at e0509c0c would not carry forward: the #1083 merge changed 15 other product files. **A fresh Checkpoint C is required.** |
| 7 | Checkpoints A/B passed | **R** (no SHA retained) | REVIEW-STATUS says "PASS after corrections". B rounds ran at ad83cc4 → d5fff26. |
| 8 | Picker content has been stable since the last feature reviews | **V** | The 7 picker source files are identical from ebf3f5d to 027c985e. The PG test file is identical from 6eb0a07; the SQLite, API and browser tests from 8b2df2e. No csproj or package changes since 599e742. |
| 9 | Required PG runner at 027c985e | **V** (new, cloud) | Disposable PostgreSQL 16.13 on 127.0.0.1:55433 (not 54329). Infrastructure 11/11 and API 11/11, **0 skipped**. HEAD `027c985e`, clean, identical before and after. TRX under `scratchpad/qual-027c985e/pg-required/`. **Limit:** CI uses PG 17. |
| 10 | GLM's c4 PG TRX came from 027c985e | **R/U** | Attributed by packet text and timestamps only; the TRX embeds no SHA. Superseded by #9. |
| 11 | Main composition | **V** | Trial merge of main into 027c985e has **no conflicts**. Main adds no migration and doesn't touch `AeroLinkDbContext`. `HasPendingModelChanges` passes. All three contract generators regenerate **byte-identical** on the composed tree, and 38/38 contract tests pass. The composed picker tests pass (SQLite 14/14). No protected CI path is in #1066 (`TRUSTED_SURFACE_PREFIXES` = `.github/`, `product/test-planner/`, `product/ci-metrics/`). |
| 12 | Main adds new build writers (e.g. #1105 FMS mock builds) | **V: false** | #1105 and #1107 are client-only; #1105's mock projects are hard-coded cards. #1103 *removes* `SecondShowcaseSeeder` from startup (moved to a test fixture). The PG trigger covers every insert path regardless. |
| 13 | ReleaseLinkQueryService, "SQLite lacks triggers", "ordinal = display order", "revocation filters" | **S** | As Astra corrected. Verified in source: the reader and fence are in `ManagedDocumentEndpoints.cs`; SQLite has three triggers plus the `PickerLegacyCohort` flag; `SoftwareReleaseOrderingQuery` sort key orders the list; revocation returns Forbid on both read and write. |
| 14 | Rehearsal v2 "all drills pass"; the transition proposal v3 is corrected | **S** (R: Astra) | The log ends "5 failure(s)"; the proposal still holds rejected logic. Not used. |
| 15 | Incident bundle and remote containment | **V** | Bundle valid and partial (requires `77b96857`). None of the 109 remote branches contains any accidental commit. Full detail in the hand-back. |
| 16 | #1098 local 32/0/0 POSIX | **R** | Astra's own execution. Not re-run here; not needed for #1040. |

## 3. Review of the feature at 027c985e

**Design conformance: verified in source, and no high-confidence correctness defect was found.**

- **Page one:**
  - Opens an explicit Read Committed transaction (SQLite: Serializable, i.e. `BEGIN IMMEDIATE`).
  - Takes `pg_advisory_xact_lock(hashtext('aerolink-release-picker:'||project))`. The key is identical in the trigger (`migration:32`) and the reader (`ManagedDocumentEndpoints.cs:911`).
  - Only then reads `MAX(ordinal)`. Under Read Committed that statement's snapshot is taken after the lock is granted.
  - Writers allocate `nextval` only while holding the same lock until commit. So any committed build is visible at the cutoff, and any later build receives a larger value.
- **Membership predicate:** `ordinal IS NULL OR ordinal <= cutoff` is applied before `WithSortKey`, the keyset and `Take(size+1)` (`:897-901`). Continuations hold no transaction.
- **Cursor:** Release v2 checks the raw 4096-character bound before decoding. It requires version, scope, filter, snapshot, cutoff ≥ 0, value ≤ 240 and tie-breaker "0". The filter key includes `canonical-release-order-v2`, so old v1 tokens fail with `invalid_cursor` ("Start again from the first page"). The non-Release branches are byte-identical in behavior.
- **Authorization:** `HasProjectAccessAsync` runs on every page. Relationship writes still re-check access and the target project.
- **PG triggers:** supplied non-NULL values are rejected (an explicit NULL is treated as omitted). Immutability uses `IS DISTINCT FROM`. The sequence is `START 1, CACHE 1, NO CYCLE`. Down() drops in the right order.
- **SQLite guard:** one immediate transaction; one-time classification keyed on the flag column; three triggers; installed after `EnsureCreated` and before every seeder.
- **EF:** `ValueGeneratedOnAdd` + `AfterSave Ignore`, with the Npgsql strategy set to `None`. SQLite disables `RETURNING` for this table only.
- **Backup/restore compatibility:** `Restore-AeroLink.ps1` runs `createdb` then a full `pg_restore --no-owner`. pg_dump puts CREATE TRIGGER in post-data, so restored rows keep their stored ordinals and legacy NULLs, and the sequence value is restored. *Not yet executed on this head* (see Q7).

**Findings:**

| ID | Severity | Finding | Evidence |
| --- | --- | --- | --- |
| F1 | Medium (R03 gap) | **The page-one fence wait has no explicit bound, and its failure is a 500.** The raw `DbCommand` (`:906-916`) inherits only the connection's 30 s command timeout, with no `lock_timeout`. Writers hold the fence for their whole transaction; `FmsShowcaseSeeder` deliberately waits up to 10 min on its own lock while building releases. A picker page one during such a write waits about 30 s and then fails with an unhandled `NpgsqlException`, not a recoverable response. R03 requires "bounded cancellation/timeout behavior". | Source |
| F2 | Low (evidence strength) | **The cancellation test does not prove server-side cancellation.** `Cancelled_page_one_releases_the_fence_without_stuck_writers` rolls back the blocking writer *before* counting fence locks. An uncancelled server wait would then be granted and finish, so a lock count of 0 is timing-dependent. | Test `:784-831` |
| F3 | Low (R02 gap) | **No test freezes membership with a search term.** Search binding is tested (`:153`), but no test inserts a matching build during a *searched* traversal. R02 says "with/without a search term". | Tests |
| F4 | Low (R05 gap) | **No PostgreSQL assertion of EF readback** (RETURNING populates the ordinal; lifecycle saves don't write it). Only SQLite covers it (`Release_ordinal_is_database_owned_across_ef_lifecycle_saves_on_sqlite`). | Tests |
| F5 | Low (R05 gap) | **Writer paths aren't asserted end to end.** Import acceptance, project setup and ordinary creation rely on the trigger plus structural reasoning. No test asserts that each path yields an ordinal on PG. | Tests |
| F6 | Low (docs) | **`MANAGED_DOCUMENTATION_CENTER.md` overstates and underspecifies.** It says Release has "the same … guarantee as other relationship targets", but the others filter only on PG. It omits: the provider behavior (PG advisory fence vs SQLite immediate write transaction), the wait bound and its response, the legacy-cohort meaning and that no cursor expiry exists. | Doc |
| N1 | Note | **SQLite page one takes a write lock** (`BEGIN IMMEDIATE`). SQLite is disposable test/browser state only; every supported operating mode uses PostgreSQL (OPERATIONS.md). Document it; no change. | Source, OPERATIONS.md |
| N2 | Note | **SQLite legacy classification is only proven on feature-schema files.** `EnsureCreated` never alters an existing file, so a pre-feature SQLite file fails at guard install ("no such column"). Not a supported upgrade path on any mode. Document it. | Source |
| N3 | Note | **Lock-key space.** The single-bigint advisory space is shared with `hashtext('aerolink-showcase-seed')`. A collision is possible in principle and would cause extra waits only. A writer inserting releases into two projects in one transaction could deadlock another writer doing the reverse; PostgreSQL detects that and aborts one. Page one takes only one lock and holds no row locks, so it cannot join a cycle. No absolute no-contention claim. | Source |
| N4 | Note | **EF `HasTrigger` metadata** lists `aerolink_release_picker_supplied_ins`, which exists only on SQLite (PG folds supplied-value rejection into the alloc function). Metadata only; no Npgsql behavior. Leave it. | Source |
| N5 | Note (R08) | **Database work grows with the project's release count** (the sort key is computed per row); only materialization is capped at pageSize+1. Retained plans came from un-ANALYZEd statistics (estimate rows=1). Recapture with ANALYZE on the new head. | Evidence |

## 4. Fix plan (subject to Astra; nothing implemented yet)

| ID | Change | Files | Proof |
| --- | --- | --- | --- |
| F1 | Inside the PG page-one transaction, before the fence, run `SET LOCAL lock_timeout = '5s'` (value open to Astra; `SET LOCAL` so it ends with the transaction). Map SQLSTATE `55P03` to **503** `{ code: "picker_busy", error: "…try again" }` with `Retry-After: 5`. Map client cancellation to the existing abort. SQLite keeps its busy-timeout behavior, which is documented. | `ManagedDocumentEndpoints.cs` | New PG test: hold the fence in a raw transaction; page one returns 503 in ≤ ~5 s plus slack; no waiter or lock leaks; after release, page one succeeds and the traversal includes the committed build. |
| F2 | Strengthen the cancellation test: after cancelling and **while the writer still holds the fence**, poll `pg_locks` for zero *ungranted* waiters on the project key. Then roll back and assert no lock and no row. | PG test file | The test fails if server-side cancel is missing. |
| F3 | Add a searched frozen traversal on PG and SQLite: late matching builds before and after the cursor are excluded, and a fresh searched traversal includes them. | PG test, `ReleasePickerMembershipApiTests.cs` | Tests |
| F4 | PG EF readback: an ordinal is present after `SaveChanges`; `MarkReleased` plus a save leaves it unchanged; an update never writes it. | PG test | Test |
| F5 | PG writer-path assertion: drive ordinary creation (`WorkspaceEndpoints`), baseline import acceptance (`BaselineImportEndpoints`) and project setup (`ProjectSetupService`) through their real endpoints/services, and assert every new row has an ordinal above the prior cutoff. Seeders and `Repositories.AddRangeAsync` are covered by the trigger (structural). | PG test | Test |
| F6 | Correct the guarantee text and add the provider, wait/timeout, legacy-cohort and no-expiry statements. | `MANAGED_DOCUMENTATION_CENTER.md` | Layout guard |
| C1 | Merge current `origin/main` into the branch (no rebase). Regenerate contracts from source twice; the second run must be identical. | Generated contracts only if changed | Generators, `node --test product/test-contracts/tests/*.test.mjs` |

**Deliberately not changed:**
- The design.
- The migration content.
- The SQLite write-lock behavior.
- The client's error display. The server message already says to start again; reopening the dialog starts a fresh traversal.
- Any CI or kernel path.

## 5. Delivery route recommendation

- **The exact blocker:** the frozen head 027c985e has a completed failed trusted Product run. Current main's requester makes that head permanently unbindable (identity #1 in section 2). Neither failure is attributable to the feature.
- **Which candidate changes:** #1066 only. The new head carries real reviewed content (section 4) plus current main. It is not an empty commit and it does not hide the failure. Run 35845728143 and its diagnostics stay attached to 027c985e.
- **How qualifying evidence is produced and accepted by the existing machinery:**
  1. Pushing to the branch fires `reset-full-ci-readiness.yml`, which removes the stale label.
  2. After Astra approves the final candidate (Checkpoint 2), the `ready-for-full-ci` label on the new SHA makes the requester find `NONE`, dispatch a fresh bot Product run, and bind it on success.
  3. The App publishes `Trusted merge-queue binding`.
  4. Auto-merge admits the PR to the queue. The merge-group Product run plus the protected binder validate the composed candidate.
  5. No `.github/`, planner or metrics path changes, so there is **no maintenance or kernel approval**, and DEC-121/124/135 are not engaged.
- **Review and authority required:**
  - Astra: this Checkpoint 1; Checkpoint 2 (final candidate and CI request); Checkpoint D (integration); Checkpoint E (deployed acceptance).
  - Sean: permission to push to `glm/1040-release-picker-snapshot`, or approval of a successor PR.
  - No new owner product or kernel decision is needed.
- **Residual risk, stated honestly:**
  - The #939 sign-in hang and the DataProtection race are pre-existing intermittent failures. If either hits the new head's single trusted Product attempt, that head is also unbindable under current main. I would report it and stop, with no retry.
  - Mitigations:
    - (a) File a separate issue for the DataProtection test-host race now; the fix is a per-factory key ring in test code, which is not a kernel path.
    - (b) The merged head includes main's #939 stall diagnostics, so a recurrence records what was waited on.
    - (c) A queue-stage flake is recoverable by re-queueing, because that produces a new composed SHA. That is not a same-head retry.
- **If a kernel transition were necessary:** it is not, for #1040.
- **#1098 disposition (separate):**
  - Keep it a separate draft.
  - It conflicts textually with #1119 on the same kernel file, and both need a separately reviewed trust-root transition. DEC-121's exception is spent, and #1119's text asks for a "one-time exception", which would be a new owner decision.
  - Recommend Astra/Sean decide one sequencing: fold #1098 into the #1119 transition, or rebase #1098 on #1119 after it lands.
  - Record the human-rerun observation (section 2 #2) in that workstream.
  - The stale `ready-for-full-ci` label on #1098 should be removed; that is a label action awaiting approval.
  - #1040 closure will not close or imply anything about #1098.

**What proceeds immediately under existing authority (read-only, or cloud disposable state only):**
- Local implementation of F1–F6 in an unpushed cloud worktree, if Astra prefers seeing a diff before approving the push.
- Pre-qualification on disposable PG and SQLite.
- The browser journey run.
- The pg_dump/pg_restore round-trip.
- Drafting Checkpoint D's HOME rollout plan.

## 6. Qualification plan for the new head (after approval)

| Step | Command / evidence |
| --- | --- |
| Q1 Identity | Record `git rev-parse HEAD` and `git status --porcelain` before and after every long run. Use `--no-build` only after a Release build of that exact HEAD. |
| Q2 Build | `dotnet build product/AeroLink.slnx -c Release` (.NET SDK 10.0.401) |
| Q3 Contracts | Run `generate-test-intent.mjs`, `generate-host-classification.mjs` and `generate-route-manifest.mjs` twice. The second run must diff empty. Then `node --test product/test-contracts/tests/*.test.mjs` |
| Q4 Planner | `node product/test-planner/tools/plan.mjs --base origin/main --head HEAD --json --dry-run`. At 027c985e it selected every area (broad validation forced by the host-classification contract). |
| Q5 Suites | Full `AeroLink.Api.Tests` (SQLite), Domain and Infrastructure with TRX. The 14+ picker SQLite set named explicitly. |
| Q6 PG | `Test-ProjectSetupPostgres.ps1 -NoBuild` against disposable PG 16 (CI has 17), with the new F1–F5 tests, 0 skipped. `AEROLINK_PICKER_SQL_EVIDENCE` recapture with ANALYZE'd statistics. |
| Q7 Restore | `pg_dump -Fc` of a migrated disposable database holding legacy and new rows → `createdb` + `pg_restore --no-owner` (the Restore-AeroLink flags). Then check: ordinals, NULL cohort and sequence continuity preserved; triggers active; a new insert gets ordinal > max; picker traversal identical. |
| Q8 Browser | Playwright `managed-documentation-center-picker-continuation.spec.ts` plus the neighbouring Documentation Center journeys, on Chromium, against a disposable SQLite host. |
| Q9 Docs | `pwsh product/scripts/Test-RepositoryLayout.ps1`, where it runs on Linux; otherwise record the limitation. |
| Q10 Matrix | R01–R12 matrix binding each criterion to exact-head TRX, logs, commands and provider. |

## 7. Evidence files (cloud scratchpad; to be attached or published as Astra directs)

- `deliverables/CHECKPOINT-1-PACKET.md` (this file)
- `deliverables/LOCAL-RECOVERY-HANDBACK.md`
- `deliverables/Audit-1040-CanonicalCheckout.ps1`
- `deliverables/Recover-1040-CanonicalCheckout.ps1`
- `deliverables/rehearse-1040-recovery.sh`
- `deliverables/rehearsal-run-1.log` (24/19; defect found)
- `deliverables/rehearsal-run-2.log` (43/0)
- `qual-027c985e/pg-required.log` and its two TRX files
- `diag/` (API and browser logs from run 35845728143). The raw Playwright archive under `diag/pw4/` may contain session data and must not be published.
