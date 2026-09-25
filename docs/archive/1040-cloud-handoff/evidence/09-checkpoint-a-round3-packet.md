(Retained copy of the exact Round 3 packet submitted to Astra via Sean on 2026-09-21.)

ASTRA REVIEW REQUEST — #1040 — CHECKPOINT A — ROUND 3

**Requested decision and next action:**
Review this focused amendment responding to the four open findings, with isolated design probes I ran against the repaired PostgreSQL allocator. On PASS, the permitted next phase is implementation of the smallest approved vertical slice and Checkpoint B. No production code, migration, or issue state has been touched; the worktree remains clean at the reviewed baseline.

**Repository / worktree / SHA:**
Unchanged: worktree `C:\Sean Project\RMT-1040-worktree`, branch `glm/1040-release-picker-snapshot`, clean at `b64301b168ed98d4054cb893932978c4d090f745` (PR #1063's merge commit on protected main; PR head was `e09f55a586433389e0bcda5c4566ea5a8e14a27f`). Candidate: none. New isolated probe evidence: `C:\Sean Project\RMT-1040-glm-evidence\08-round3-pg-probe.log` (throwaway `postgres:17` container, port 54332, removed after the run; no persistent database involved).

---

**1. Repaired PostgreSQL allocator/fence and its old-snapshot timeline (A1040-A01 — FIXED, probe-verified)**

Adopting Astra's preferred revision. The allocation no longer reads a snapshot-dependent MAX:

```sql
-- additive migration, PostgreSQL
CREATE SEQUENCE aerolink_release_picker_ordinal_seq
    START WITH 1 INCREMENT BY 1 CACHE 1 NO CYCLE;

CREATE OR REPLACE FUNCTION aerolink_release_picker_alloc() RETURNS trigger AS $$
BEGIN
    IF NEW."PickerInsertionOrdinal" IS NOT NULL THEN
        RAISE EXCEPTION 'picker insertion ordinal is database-allocated and must not be supplied';
    END IF;
    PERFORM pg_advisory_xact_lock(hashtext('aerolink-release-picker:' || NEW."ProjectId"::text));
    NEW."PickerInsertionOrdinal" := nextval('aerolink_release_picker_ordinal_seq');
    RETURN NEW;
END; $$ LANGUAGE plpgsql;
-- trigger + immutability trigger unchanged from Round 2 (BEFORE INSERT / BEFORE UPDATE ... IS DISTINCT FROM)
-- Down: DROP TRIGGER/function/sequence
```

No `nextval` in a column default; no cached ranges, HiLo, or per-connection preallocation (`CACHE 1`); `NO CYCLE`. Per PostgreSQL's sequence documentation, `nextval` is non-transactional: it is unaffected by the caller's isolation level or MVCC snapshot and is never rolled back — so a stale-snapshot writer cannot allocate a value behind the committed frontier. The advisory xact lock orders each project's allocation against page-one's cutoff capture; the reader runs in an explicitly **Read Committed** transaction and reads the cutoff **after** the lock call succeeds, so its cutoff statement sees every allocation whose fence was released (i.e., every committed one).

**Old-snapshot timeline, walked against Astra's exact probe schedule** (probe-verified in `08-round3-pg-probe.log`):
1. Project P/Q has committed releases (probe: legacy NULL cohort + ordinal 3 committed on Q).
2. Writer A begins REPEATABLE READ and establishes its old snapshot (probe: snapshot max = 0 — even staler than Astra's schedule).
3. A separate READ COMMITTED writer inserts and commits ordinal 3.
4. Page one takes the advisory fence, captures **cutoff = 3**, commits.
5. Writer A now inserts a distinct release: it takes the same fence and allocates **nextval = 4** — not a stale maximum.
6. Writer A commits. A continuation at the captured cutoff 3 **excludes** the late writer (`lateRR_Q | 4` absent).
   Probe result line: `lateRR_Q allocated = 4`; continuation rows at cutoff 3 = `r20_Q | 3` only. **LEAK CLOSED.**
   I also repeated the schedule with writer A **SERIALIZABLE**: same result (global `nextval`, isolation-independent).

**Opposite fence order (fence waits for an uncommitted allocator):** probed with a SERIALIZABLE writer that allocated ordinal 8 and deferred its commit ~6 s while holding the fence: the page-one fence **waited** (entry 12:04:19.53Z, acquired 12:04:23.50Z), then captured **cutoff = 8** and the continuation **includes** the committed allocator. Membership is exactly "committed when the fence captured the cutoff" in both orders.

**Corrected allocation prose (supersedes Round 1 and Round 2 wording):** ordinals come from a single logged sequence; within a project they strictly increase in fenced insertion order but are **not consecutive** — gaps are permanent (a rolled-back INSERT's value is never reused — probe: value 7 allocated after 5 following a rollback) and cross-project interleaving is common. Both are harmless: membership compares per-row ordinals against a per-project captured cutoff, and no successful boundary can ever have observed an uncommitted allocation. The monotonicity assumption is replaced by the sequence's own monotonicity; the only required data assumption is that committed release membership is retained (controlled-history invariant, all references `Restrict`).

**Checkpoint B additions:** the stale-snapshot schedule (REPEATABLE READ and SERIALIZABLE writers), both fence orders with the `pg_locks`-observed wait, ordinary overlapping inserts, batch, rollback gap, reader cancellation, supplied-value/legacy/immutability rejection matrix, and mixed-isolation readers/writers — all through the real EF/provider stack (not this probe's raw SQL).

---

**2. SQLite legacy protection, initialization boundary, and the EF contract (A1040-A02)**

**A. Legacy protection — concrete fix: absolute immutability.** The SQLite immutability trigger becomes:

```sql
CREATE TRIGGER IF NOT EXISTS aerolink_release_picker_immutable_upd BEFORE UPDATE ON software_releases
    FOR EACH ROW WHEN NEW."PickerInsertionOrdinal" IS NOT OLD."PickerInsertionOrdinal"
BEGIN SELECT RAISE(ABORT, 'picker insertion ordinal is immutable'); END;
```

`IS NOT` is NULL-safe, so this rejects **all three** transitions: legacy NULL→value (Astra's `UPDATE … = 999` attack now aborts — the legacy cohort cannot lose membership), value→different-value, and value→NULL. The allocator still works because its UPDATE lives **inside the AFTER INSERT trigger body**, and SQLite fires triggers for statements inside trigger bodies only when `PRAGMA recursive_triggers = 1`; that pragma is off by default and Microsoft.Data.Sqlite does not enable it. The dependency is **checked, not assumed**: the guard initializer runs `PRAGMA recursive_triggers` at installation and fails host startup if it returns 1. Astra's in-memory probe methodology re-running these exact steps against this revised trigger set is part of the Checkpoint B evidence (I have no local sqlite3/python; the PG side I probed directly above, the SQLite side is stated + B-qualified).

**B. EF generated-value/readback contract — specified, provider-general.** Astra's RETURNING probe exposed a hazard that also affects PostgreSQL: with a plain mapped property, EF would write the tracked NULL back over the allocated value on the next lifecycle save (aborted by the immutability trigger — fail-closed, but ordinary saves would break). The model therefore configures:

- `b.Property(x => x.PickerInsertionOrdinal).ValueGeneratedOnAdd()` — EF omits the column from INSERT (sentinel null; old binaries do the same by omission) and reads the generated value back: Npgsql's RETURNING returns the BEFORE-trigger value (tracked correctly); SQLite's RETURNING returns NULL for an AFTER-trigger allocation, so the tracked in-memory value on SQLite is **null — documented, harmless**, because nothing consumes it in memory (the picker reads via server-side predicates) and the next rule makes it unreachable from writes.
- `.AfterSaveBehavior(PropertySaveBehavior.Ignore)` — EF excludes the column from **every UPDATE on both providers**, so no ordinary or lifecycle save can ever write a stale or null ordinal; `MarkReleased` and friends touch only their own columns and pass the immutability triggers.
- Per-table trigger declaration (`ToTable(t => t.HasTrigger(...))` for the SQLite trigger names) so the model records that the database modifies rows; the exact generated INSERT/UPDATE SQL for both providers is reviewed at B.
- B tests: plain `SaveChanges` insert (PG tracks the real value; SQLite tracks null — asserted and documented), a subsequent lifecycle save passes, failed-save/transaction retry behavior, and an honest note on `SaveChangesAsync(false)`: standard EF entity-state semantics apply and the column contract holds for any number of saves, but it is **not** claimed as retry coverage beyond that.

**C. Initialization boundary — narrowed to what is actually installed.** The supported prerequisite: the guard is installed by the **supported host initialization path** — `Program.cs:124` (PG via migration, SQLite via the idempotent guard initializer with the PRAGMA assertion). That path covers every real API/browser host and every `AeroLinkApiFactory`-based test host; the new required picker tests run through it. Enumerated honestly: **92 test files** create SQLite databases via direct `EnsureCreatedAsync` (e.g., `SoftwareReleaseOrderingQueryTests.cs:13-16`, `ShowcaseApiFixture.cs:36`, 90 others) — these are **schema-only fixtures, are not enforcement evidence, and are not edited**; the 193-file SoftwareRelease count from Round 2 was fixture churn, not initialization seams, and I withdraw any implication it was the latter. SQLite compatibility boundary, explicit: `EnsureCreated` + `CREATE TRIGGER IF NOT EXISTS` covers **fresh databases and same-schema restarts** (idempotent reinstall, second-host-start verified at B); it is **not** an old-schema migration mechanism — a pre-feature SQLite schema fails at first use against the new mapped column and is recreated; no SQLite upgrade path is claimed (SQLite is the functional test provider; PostgreSQL carries the real R04 migration/upgrade evidence).

---

**3. Corrected cursor bound and round-trip test plan (A1040-A04 — FIXED)**

The pre-decode raw-token bound is retained and raised **640 → 4096 characters** (Astra's candidate), accepting Astra's measured point: the historical fallback SortKey `1|<label>|<label>!<Guid>` with a 40-character U+00E9 label serializes to ~722 UTF-8 bytes / ~963 base64url characters, and escaping invalidates character-count arithmetic — 4096 covers the schema's true worst case (two 40-char labels, escaping, 20-digit cutoff) with margin. Everything else in the v2 contract is unchanged: explicit nonnegative cutoff including legitimate zero, missing/null cutoff invalid, required fields, exact scope/filter/order binding, old Release-v1 rejection with `invalid_cursor` + start-again, non-Release v1 untouched. Round-trip plan at B: production `Encode`→`DecodeReleaseV2` round-trips with maximum-supported historical labels (40-char, multi-byte/escaped), single-digit versions, canonical ties, and extreme cutoff values; an emitted valid cursor is accepted on the next page end-to-end; malformed, cross-filter, negative, missing-cutoff, and over-4096 inputs all receive `invalid_cursor`.

---

**4. Race-test synchronization — retained, with a corrected and decisive wait-proof (A1040-A03)**

Round 2's `DbTransactionInterceptor` proposal is **retracted**; no new interceptor framework is added. Astra's analysis is adopted: `ReleaseInsertBarrier.ReaderExecutingAsync` fires before the database executes the INSERT, so under in-trigger locking **both** requests reach and satisfy the two-arrival rendezvous before either takes the advisory lock — the existing barrier is **kept unchanged** (bounded by its 30 s `WaitAsync`) and still asserts arrivals = 2, exactly one success + one 409, one committed `SW-01.30` identity, and the correct import branch; Round 2's "preserved by construction" is replaced by these named tests and their actual results at B. For the test that specifically claims the fence **wait**, the evidence is a database-level signal, not a client-side arrival: during writer 1's held fence, a scoped connection polls `pg_locks` (`locktype='advisory'`, `granted=false`, matching key) and `pg_stat_activity.wait_event_type='Lock'` for writer 2's backend; writer 1 is released only after that ungranted-lock observation, with bounded polling and the observed lock/activity rows retained as diagnostics. (The Round-3 probe demonstrated the same signal shape by measuring the fence blocking ~3.97 s behind an uncommitted allocator.)

---

**5. Updated changed-file ownership and material scope delta**

Owned files: `ProgramRecord.cs` (new nullable property), `AeroLinkDbContext.cs` (mapping + EF value config + trigger declarations), one new additive migration (column + sequence + 2 functions + 2 triggers + Down), SQLite guard initializer + `Program.cs:124` call, `ManagedDocumentEndpoints.cs` (Release branch: fence + cutoff + membership predicate), `ManagedDocumentPaging.cs` (bounded Release-v2 decoder, 4096 cap), new partial test file(s) under `ProjectSetupPostgresQualificationTests` (+ Infrastructure-side where the fence unit tests live), `MANAGED_DOCUMENTATION_CENTER.md:98-103` replacement. **Not changed:** creation/import endpoints, `ReleaseInsertBarrier`, `ProjectControlledWriteScope`, `SoftwareReleaseOrderingQuery`, all 193 fixture files and 92 direct-context fixtures, no DEC entry, no `PROJECT_STATE.md` edit. **Delta vs Round 2:** + sequence DDL; + EF value configuration; + PRAGMA assertion in the initializer; cursor cap 640→4096; − the transaction-interceptor rework (retracted); expected `SAVE_BOUNDARY.md` outcome remains "no change" (model metadata + database guard, no save-phase requirement) — confirmed from the final diff at B before any doc edit.

**Dispositions carried:** legacy cohort = "present when this feature's schema was installed" (no timestamp reconstruction); canonical ordering, exact Release IDs, deep links and membership-before-`Take` query shape preserved; old writer binaries on a new PG schema write compatible rows (trigger allocates) while old **reader** binaries gain no guarantee (stated); opposite-order multi-project inserts may deadlock — PostgreSQL aborts one transaction with a deadlock error and the application surfaces the failure **without** automatic retry and without a generic retry framework; existing baseline reproduction remains the defect evidence, no repeated baseline campaign run.

**Remaining decisions for Astra:** (1) the sequence allocator as implemented above (probe-verified against your exact schedule); (2) the SQLite absolute-immutability + recursive-triggers dependency, startup-asserted via PRAGMA; (3) the EF `ValueGeneratedOnAdd` + `AfterSaveBehavior.Ignore` contract, including accepting SQLite's tracked-null readback as documented behavior; (4) the 4096-character cap; (5) the schema-only fixture classification (92 seams named, none edited); (6) `SAVE_BOUNDARY.md` no-change expectation. Prior finding IDs: A1040-A01 fixed (probe-verified), A02A/A02B/A02C resolved by §2, A03 resolved by §4 (retraction + pg_locks proof design), A04 fixed by §3.

**Recommended verdict and why:** PASS — the reproduced Round-2 allocator defect is closed by the sequence allocator and demonstrated against Astra's own schedule on PostgreSQL 17, the SQLite legacy hole is closed by an absolute immutability trigger with its one documented dependency startup-asserted, the EF contract is now explicit and provider-general, the cursor bound covers the measured worst case, and the race-test reasoning is corrected with a database-level wait proof — all within the sanctioned direction and with scope held or reduced.

Please independently return PASS, CHANGES REQUIRED, or NOT REVIEWABLE, identify the exact SHA/design and scope reviewed, list actionable findings, and state which next phase may proceed. I will wait for Sean to relay your actual response.

CHECKPOINT A — AWAITING ASTRA REVIEW. No production implementation has started.
