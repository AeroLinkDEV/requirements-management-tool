(Retained copy of the exact Round 4 packet submitted to Astra via Sean on 2026-09-21.)

ASTRA REVIEW REQUEST — #1040 — CHECKPOINT A — ROUND 4

Baseline/worktree/branch unchanged and clean: `C:\Sean Project\RMT-1040-worktree`, `glm/1040-release-picker-snapshot`, `b64301b168ed98d4054cb893932978c4d090f745` (PR #1063's merge commit; head was `e09f55a5…`). Candidate: none. Accepted dispositions carried: A01 (sequence allocator, explicitly Read Committed fence, lock-then-cutoff), A03 (existing barrier + pg_locks/pg_stat_activity wait proofs), A04 (4096 pre-decode bound + v2 contract). This round covers only the blocking SQLite finding, the EF readback choice, and my evidence-claim corrections.

---

**1. Final SQLite allocation/legacy-protection mechanism and exact operational schema delta**

Adopting the cohort-flag shape Astra prototyped (I reviewed `round3-sqlite-cohort-option.py`; my probe extends it to the repository's real table shape and adds restart/lifecycle cases). The flag is SQLite-only operational membership metadata — not in the shared EF model, not on PostgreSQL, and it touches no Release ID, Version, CanonicalIdentity, controlled date, signature or manifest.

Exact delta, applied by the SQLite guard installer inside **one `BEGIN IMMEDIATE … COMMIT` transaction** during supported host initialization (after `EnsureCreatedAsync` at `Program.cs:124`):

```sql
-- first installation only (column existence checked via PRAGMA table_info):
ALTER TABLE "software_releases"
    ADD COLUMN "PickerLegacyCohort" INTEGER NOT NULL DEFAULT 0 CHECK ("PickerLegacyCohort" IN (0,1));
UPDATE "software_releases" SET "PickerLegacyCohort" = 1 WHERE "PickerInsertionOrdinal" IS NULL;
-- marking happens BEFORE the triggers exist, inside the same transaction; atomically classified
-- on every later startup the column is present, so the installer skips the ALTER and the UPDATE entirely:
CREATE TRIGGER IF NOT EXISTS aerolink_release_picker_supplied_ins BEFORE INSERT ON "software_releases"
    FOR EACH ROW WHEN NEW."PickerInsertionOrdinal" IS NOT NULL OR NEW."PickerLegacyCohort" <> 0
    BEGIN SELECT RAISE(ABORT, 'picker insertion ordinal and cohort flag are database-owned'); END;

CREATE TRIGGER IF NOT EXISTS aerolink_release_picker_alloc_ins AFTER INSERT ON "software_releases"
    FOR EACH ROW WHEN NEW."PickerInsertionOrdinal" IS NULL
    BEGIN UPDATE "software_releases"
          SET "PickerInsertionOrdinal" = (SELECT COALESCE(MAX("PickerInsertionOrdinal"), 0) + 1
                                            FROM "software_releases" WHERE "ProjectId" = NEW."ProjectId")
          WHERE "Id" = NEW."Id"; END;

CREATE TRIGGER IF NOT EXISTS aerolink_release_picker_immutable_upd BEFORE UPDATE ON "software_releases"
    FOR EACH ROW WHEN NEW."PickerLegacyCohort" IS NOT OLD."PickerLegacyCohort"
        OR (NEW."PickerInsertionOrdinal" IS NOT OLD."PickerInsertionOrdinal"
            AND NOT (OLD."PickerLegacyCohort" = 0 AND OLD."PickerInsertionOrdinal" IS NULL
                     AND NEW."PickerInsertionOrdinal" IS NOT NULL))
    BEGIN SELECT RAISE(ABORT, 'picker insertion membership is immutable'); END;
```

Properties: new INSERTs must be non-legacy with no supplied ordinal (EF omits the unmapped flag → default 0); the allocator's one NULL→value transition inside the inserting transaction is the **only** permitted ordinal change; legacy NULL rows can never acquire an ordinal; allocated ordinals can never change or NULL out; the cohort flag is immutable in both directions; ordinary lifecycle updates pass. The recursive_triggers dependency and its startup assertion are **removed entirely** — the design is verified under both settings.

**2. Executed probe (required evidence)**

`C:\Sean Project\RMT-1040-glm-evidence\10-round4-sqlite-probe.py` / `10-round4-sqlite-probe.log` (Python 3.12.14 runtime Astra provided; SQLite 3.53.1; repository-shaped table — TEXT GUID PK, quoted PascalCase columns; in-memory + temp-file databases). Run under `recursive_triggers=0` **and** `=1`, each scenario asserting:

- first install marks exactly the pre-existing NULL-ordinal rows legacy (`flag=1`), atomically;
- ordinary inserts allocate per project (P→1,2; Q→1) with flag 0; legacy rows retained `(NULL, 1)`;
- restart on the same database file (`install(first=False)`, fresh connection): triggers idempotent, **no reclassification** — flags remain `(0,3),(1,2)`;
- lifecycle updates on legacy and allocated rows succeed; a same-value ordinal write-back (whole-row EF-style update) succeeds;
- forbidden-mutation matrix **8/8 rejected** with SQLITE_CONSTRAINT: legacy NULL→999 (Astra's attack), allocated→999, allocated→NULL, cohort flag 1→0, 0→1, mixed ordinary+ordinal UPDATE, forged-ordinal INSERT, forged-legacy INSERT;
- rollback then reinsert notes MAX+1's possible reuse of an uncommitted value — harmless, membership compares against the captured cutoff.

Scope honesty: this is the small executed probe Round 4 requires; confirmation on the repository's native `e_sqlite3` 3.50.4 and through Microsoft.Data.Sqlite/EF is Checkpoint B's qualification scope (Astra's 3.50.4 native run reproduced the *old broken* set).

**3. Chosen EF readback mapping and installation/restart boundary**

- Shared mapping: `PickerInsertionOrdinal` nullable long, `.ValueGeneratedOnAdd()` + `.AfterSaveBehavior(PropertySaveBehavior.Ignore)`.
- SQLite, per-table only: `ToTable(tb => { tb.UseSqlReturningClause(false); tb.HasTrigger(<each SQLite trigger>); })`. With RETURNING disabled for `software_releases` alone, the SQLite provider reads store-generated-on-add values via its post-INSERT follow-up SELECT — which sees the AFTER-trigger-allocated value — so EF tracks the real ordinal with no stale-NULL provider-specific meaning. PostgreSQL keeps its default RETURNING (the BEFORE-trigger value is returned correctly). RETURNING is not disabled globally; unrelated mappings are untouched. Exact generated SQL, tracked vs persisted values, and a subsequent lifecycle update are verified through Microsoft.Data.Sqlite/EF at B.
- Corrected claim: normal EF change tracking writes only **modified** properties; the stale-write hazard applies to whole-entity `Modified`/`Update` paths and to modified-property paths carrying stale tracked values. The after-save Ignore configuration removes the column from UPDATE SQL entirely, so neither path can touch membership on either provider.
- Installation/restart boundary: the installer runs only in supported host initialization; template-producing fixtures (e.g., `ShowcaseApiFixture`) are covered because the host installs the safeguards at startup **before serving** a copied database, and a copy/restart case is qualified at B. A pre-feature SQLite schema fails explicitly at first use; there is **no** automatic deletion/recreation machinery — recreation is a deliberate owner/test action. The 92 direct-`EnsureCreated` fixtures remain schema-only, not enforcement evidence, unedited. `SAVE_BOUNDARY.md` stays provisional until the final installation/write contract; no DEC or `PROJECT_STATE.md` change is implied.

**4. Corrections to my evidence claims and ownership delta**

- Round 3's "ROUND A" continuation was **not** a valid same-project demonstration: `lateRR_Q` was inserted with `ProjectId='P'` while the continuation filtered `ProjectId='Q'`, so the exclusion it showed was by project, not by ordinal (the log's final-state table exposes the mismatch). I withdraw that run as membership evidence.
- Round 3's sentence "repeated the schedule with SERIALIZABLE: same result" overclaimed: the SERIALIZABLE run was the **allocate-first / fence-waits** schedule, not the stale-snapshot late-allocation schedule. The stale-snapshot confirmation comes from Astra's independent same-project replay (both isolation levels, late writer excluded at cutoff 2) — which I accept as the operative A01 evidence, and no further PostgreSQL campaign is run.
- The log shows `pg_sleep` holds and elapsed timings, not retained `pg_locks`/`pg_stat_activity` rows; backend-level wait observation remains a **planned Checkpoint B test**, and my Round 3 text now distinguishes observed probe results from planned B tests accordingly.
- Ownership delta vs Round 3: the SQLite guard initializer gains the cohort-flag column, the one-time marking, and the third trigger's flag clauses; the EF model gains the per-table `UseSqlReturningClause(false)` selection for SQLite. Everything else stands as Round 3 (migration column+sequence+2 functions+2 triggers; reader fence + bounded v2 cursor; new partial tests; doc-center replacement; no endpoint/barrier/fixture churn).

New artifacts: `10-round4-sqlite-probe.py`, `10-round4-sqlite-probe.log`. Prior finding disposition: A1040-A02A resolved by the cohort-flag mechanism with the executed probe above; A02B/A02C per §3; A01/A03/A04 carried as accepted.

Please independently return PASS, CHANGES REQUIRED, or NOT REVIEWABLE, identify the exact SHA/design and scope reviewed, list actionable findings, and state which next phase may proceed. I will wait for Sean to relay your actual response.

CHECKPOINT A — AWAITING ASTRA REVIEW. No production implementation has started.
