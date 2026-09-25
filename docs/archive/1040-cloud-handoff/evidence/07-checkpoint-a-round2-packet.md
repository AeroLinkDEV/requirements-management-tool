(Retained copy of the exact Round 2 packet submitted to Astra via Sean on 2026-09-21.)

ASTRA REVIEW REQUEST — #1040 — CHECKPOINT A — ROUND 2

**Requested decision and next action:**
Review this focused design revision responding to A1040-A01…A04 and the six corrections. On PASS, the permitted next phase is implementation of the smallest approved vertical slice and Checkpoint B (core snapshot and migration proof). No production code, migration, or issue state has been touched since Round 1; baseline remains clean at the reviewed SHA.

**Repository, PR and worktree:**
Unchanged from Round 1: worktree `C:\Sean Project\RMT-1040-worktree`, branch `glm/1040-release-picker-snapshot`, clean, no PR. GitHub state re-verified this round only where Astra's corrections changed facts (#1041 resolved as independently verified CLOSED/COMPLETED by Astra; no new GitHub reads performed).

**Full baseline SHA / candidate SHA:**
Baseline `b64301b168ed98d4054cb893932978c4d090f745` — with Astra's correction 6 applied: this is **PR #1063's merge commit on protected main** (PR head was `e09f55a586433389e0bcda5c4566ea5a8e14a27f`); Round 1's phrase "equals merged PR #1063 head" was wrong and is withdrawn. Candidate SHA: none — no implementation diff exists.

**Design change since Round 1 — in one sentence:**
Allocation and fencing of the membership ordinal move from the application save pipeline into the **database itself** (the alternative Astra explicitly offered), because the correction work measured the application-side variant's true blast radius: **193 test files** (plus `ProgramRepository.AddAsync` and every fixture path) create releases through plain `SaveChangesAsync`, and adapting them or inventing scope exemptions would be exactly the invasive cross-cutting change Astra's confinement instruction forbids. The direction Astra sanctioned — immutable per-project insertion ordinal + shared serialization fence — is unchanged; only its enforcement layer moved below the application.

---

**A1040-A01 — DISPOSITION: superseded by database-owned allocation. Enforcement boundary stated exactly.**

The application-side save-scope contract is **withdrawn**. Measured fact behind the choice: `grep -rln "new SoftwareRelease(" product/tests` returns 193 files across Api.Tests/Infrastructure.Tests/Domain.Tests; `ProgramRepository.AddAsync` (Repositories.cs:165-171) does `AddRangeAsync(releases)` + `SaveChangesAsync` as a plain repository seam; `ManagedDocumentApiTests.BuildIdentity.cs:20-27` and every similar fixture would have needed either individual fence adaptation or a blanket test exemption. Both horns were unacceptable; database ownership removes the application contract entirely.

New enforcement design (all three properties database-owned — the null-check-alone claim is never made):

```sql
-- PostgreSQL (additive migration; quoted identifiers per repository conventions)
CREATE OR REPLACE FUNCTION aerolink_release_picker_alloc() RETURNS trigger AS $$
BEGIN
    IF NEW."PickerInsertionOrdinal" IS NOT NULL THEN
        RAISE EXCEPTION 'picker insertion ordinal is database-allocated and must not be supplied';
    END IF;
    PERFORM pg_advisory_xact_lock(hashtext('aerolink-release-picker:' || NEW."ProjectId"::text));
    SELECT COALESCE(MAX("PickerInsertionOrdinal"), 0) + 1 INTO NEW."PickerInsertionOrdinal"
        FROM "software_releases" WHERE "ProjectId" = NEW."ProjectId";
    RETURN NEW;
END; $$ LANGUAGE plpgsql;

CREATE TRIGGER aerolink_release_picker_alloc_ins BEFORE INSERT ON software_releases
    FOR EACH ROW EXECUTE FUNCTION aerolink_release_picker_alloc();

CREATE OR REPLACE FUNCTION aerolink_release_picker_immutable() RETURNS trigger AS $$
BEGIN RAISE EXCEPTION 'picker insertion ordinal is immutable'; END; $$ LANGUAGE plpgsql;

CREATE TRIGGER aerolink_release_picker_immutable_upd BEFORE UPDATE ON software_releases
    FOR EACH ROW WHEN (NEW."PickerInsertionOrdinal" IS DISTINCT FROM OLD."PickerInsertionOrdinal")
    EXECUTE FUNCTION aerolink_release_picker_immutable();
```

- **Supplied values are rejected outright**, not merely null-checked: any INSERT (EF, raw SQL, old binary, future tool) carrying an ordinal aborts; every accepted INSERT's ordinal is computed by the database. **Coverage is structural:** every INSERT into the table passes the trigger, so writer inventory is no longer the enforcement argument — it is only the qualification map. Application code cannot forge an ordinal (domain property has a private setter and the DB rejects supplied values).
- **Shared fence:** `pg_advisory_xact_lock` keyed per project, taken at INSERT time inside the writer's transaction (held until commit) and by the page-one reader before cutoff capture. It is shared across **all** writers — endpoints, imports, setup, seeders, fixtures, hypothetical raw-SQL writers — because it lives in the trigger, answering the "locking only one writer is not a shared fence" trap.
- **A01.1–A01.2 (scope evidence lifecycle):** no application scope exists to pass or clean up; the lock is transaction-scoped by construction (`_xact` advisory lock releases at commit/rollback automatically; nothing survives the request).
- **A01.3 (inception):** no special case exists. A new project's first release takes the same key, reads an empty committed set, and receives 1; concurrent creation of the *same* project is impossible (PK), and different projects use different keys. No "some project is Added" exemption exists because no exemption is needed.
- **A01.4 (batch/retry/false):** several releases in one save allocate consecutive values row-by-row under the held key; a second `SaveChanges` in the same transaction sees its own prior writes and continues MAX+1; a failed save that rolls back leaves no row, so a retry re-allocates the same value (harmless — see corrected allocation prose below); `SaveChangesAsync(false)` behaves identically (rows and ordinals persist; tracker state unchanged).
- **A01.5 (multi-project unit):** supported by construction — each row keys on its own project. Two concurrent multi-project batches inserting in opposite row orders could deadlock; PostgreSQL detects this and aborts one transaction (fail closed, retriable). No production writer and no known fixture spans projects in one release save today; this behavior is documented rather than refused, and will be stated in the B evidence.
- **Fixtures and `ProgramRepository.AddAsync`: deliberately untouched.** Plain `SaveChangesAsync` keeps working because the guard is in the database; `BuildIdentity.cs:20-27` needs no adaptation. This is the invariant being enforced everywhere, not bypassed.

**Corrected allocation prose (correction 2):** ordinals are allocated by the database as committed-maximum+1 at INSERT time under the per-project key. A rolled-back INSERT leaves no row, so the committed maximum does not advance and the same ordinal value can be allocated again later; reuse is harmless because no successful page-one boundary can ever have observed an uncommitted allocation. Empty project → 1; batch → consecutive values; overflow near `bigint` max fails closed at the database. Monotonicity of the committed maximum rests on committed release membership never being physically deleted (controlled-history invariant; every release reference is `Restrict`). No sequence/counter is added to preserve the Round-1 "permanent gaps" wording, which was wrong.

---

**A1040-A02 — DISPOSITION: SQLite guards installed by an idempotent supported-initialization path; qualified through the real startup route.**

Confirmed mechanism: `Program.cs:123-124` runs `MigrateAsync` (PostgreSQL) or `EnsureCreatedAsync` (SQLite), and `migrationBuilder.Sql` never executes on the EnsureCreated path. Plan:

- **PostgreSQL:** triggers ship in the additive migration (`Up` creates, `Down` drops) — the mandatory database guard evidence, proven by generated-SQL review, trigger-presence assertions, and required-suite execution.
- **SQLite:** a small persistence-layer initializer (e.g., `ReleasePickerSqliteGuard.EnsureInstalledAsync(db)`) invoked immediately after `EnsureCreatedAsync()` at `Program.cs:124` — i.e., inside the supported host startup that every real SQLite API/browser host and every `AeroLinkApiFactory`-based test goes through. Installation is idempotent (`CREATE TRIGGER IF NOT EXISTS`) so reused/copied SQLite databases re-initialize harmlessly, and a failed install fails host startup (fail closed).
- **SQLite trigger shape constraint, stated:** SQLite BEFORE INSERT triggers cannot assign `NEW` columns, so the SQLite allocation trigger uses the transactionally equivalent AFTER-form — the writing transaction is atomic, so no other transaction can observe the NULL window:

```sql
CREATE TRIGGER IF NOT EXISTS aerolink_release_picker_supplied_ins BEFORE INSERT ON software_releases
    FOR EACH ROW WHEN NEW."PickerInsertionOrdinal" IS NOT NULL
BEGIN SELECT RAISE(ABORT, 'picker insertion ordinal is database-allocated and must not be supplied'); END;

CREATE TRIGGER IF NOT EXISTS aerolink_release_picker_alloc_ins AFTER INSERT ON software_releases
    FOR EACH ROW WHEN NEW."PickerInsertionOrdinal" IS NULL
BEGIN UPDATE "software_releases"
      SET "PickerInsertionOrdinal" = (SELECT COALESCE(MAX("PickerInsertionOrdinal"), 0) + 1
                                        FROM "software_releases" WHERE "ProjectId" = NEW."ProjectId")
      WHERE "Id" = NEW."Id"; END;

CREATE TRIGGER IF NOT EXISTS aerolink_release_picker_immutable_upd BEFORE UPDATE ON software_releases
    FOR EACH ROW WHEN OLD."PickerInsertionOrdinal" IS NOT NULL
      AND (NEW."PickerInsertionOrdinal" IS NULL OR NEW."PickerInsertionOrdinal" <> OLD."PickerInsertionOrdinal")
BEGIN SELECT RAISE(ABORT, 'picker insertion ordinal is immutable'); END;
```

- **Ordinary lifecycle updates remain valid:** `MarkReleased`/state changes touch other columns and pass both providers' immutability predicates; the PG `IS DISTINCT FROM` and the SQLite `OLD IS NOT NULL AND (NEW IS NULL OR NEW <> OLD)` are null-safe in the stated directions (SQLite must permit NULL→value because that IS its allocation step).
- **Honest provider statement:** SQLite's reader fence is its immediate serializable write transaction (single-writer), which gives equivalent membership semantics with different contention characteristics; the *database-level guard* claim is made for PostgreSQL only, with SQLite behavior recorded separately, exactly as R09 requires. Qualification goes through the real startup/EnsureCreated route, including a second-host-start (reuse) case, not a constructed migration-only database.

---

**A1040-A03 — DISPOSITION: the fence moved below the application, so the endpoints Astra cited are deliberately not restructured; conflict semantics are preserved by construction; the barrier is reworked to prove real overlap.**

- **Revalidation question, answered honestly:** in this design the *writer* takes no application lock, so there is no post-wait revalidation point to add. The pre-INSERT reads in `WorkspaceEndpoints.cs:276-291` and `BaselineImportEndpoints.cs:352-361` keep exactly today's semantics — nothing moved, so no new staleness is introduced. The serialization point is the trigger during the INSERT itself, and the facts that must survive concurrency are enforced after it: canonical identity by the existing unique index `(ProjectId, CanonicalIdentity)` and the central validator; import state by `Accept`'s own guard on the entity inside the endpoint's existing explicit transaction, which the trigger's transaction-scoped lock joins without nesting (`AcquireAsync` is not used by writers at all in this design, so its active-transaction refusal never arises).
- **409 semantics preserved by construction:** under Read Committed the loser's pre-save validator still cannot see the winner's uncommitted row (no 400 `DomainException` path appears); the loser's INSERT proceeds through the trigger after the winner commits and fails at the unique index → `DbUpdateException.IsIdentityRace` → the existing 409 handlers (`WorkspaceEndpoints.cs:303`, `BaselineImportEndpoints.cs:384`) respond unchanged. One committed build (index), one controlled 409, zero partial import side effects (import mutation + release INSERT + `MarkReleased` commit or roll back atomically in the same transaction as today). No new exception type or error mapping is introduced.
- **Lock-and-revalidation timeline (writer/reader):** page one re-checks access (`HasProjectAccessAsync`, unchanged, live) → opens one short request-scoped transaction → takes the same per-project advisory key (this is what makes the fence shared) → reads `cutoff = MAX(ordinal) ?? 0` (committed rows only) → runs the bounded candidate query with `(Ordinal IS NULL OR Ordinal <= cutoff)` **before** the existing canonical keyset and `Take(pageSize+1)` → commits. A writer holding the key when the reader arrives finishes and commits first (the fence waits; cancellable via `RequestAborted`, bounded by the Npgsql command timeout — same policy as existing scope waits); a writer arriving after the reader's capture allocates cutoff+1 and can never enter that traversal, whatever its sort position. Uncommitted writers are invisible; commits after the boundary never leak.
- **Race-test rework that preserves the overlap proof:** request 2's INSERT command start is observed by the existing `DbCommandInterceptor` seam and **gates request 1's commit** via a `DbTransactionInterceptor` — so request 2 is provably inside its INSERT/trigger lock wait while request 1 is still uncommitted, then necessarily completes only after request 1's commit (advisory ordering). No sleeps; the old wait-for-two-INSERTs rendezvous (which deadlocks under any serializing fence, as flagged in Round 1) is replaced, while the assertions stand: two arrivals, exactly one success + one 409, single committed release `SW-01.30`, and the import-state branch assertions in `ProjectSetupPostgresQualificationTests.cs:82-103`.
- **Related observation, offered as out-of-scope:** the single-in-work-release precheck (`WorkspaceEndpoints.cs:281`) has a pre-existing TOCTOU (no supporting unique index); this task neither worsens nor fixes it — flagged for the backlog, not folded into #1040.

---

**A1040-A04 — DISPOSITION: Release-v2 cursor contract, bounded before decoding.**

Release gets its own decoder (`ManagedDocumentPaging.DecodeReleaseV2`); the shared v1 `Decode` and all non-Release behavior stay byte-identical. Contract:

- **Length bound first:** reject raw tokens longer than **640 characters before any base64 allocation** (arithmetic: JSON ≈ 90 syntax/field names + 11 scope + 64 filterKey + ~35 SnapshotAt + ≤240 Value + 1 TieBreaker + ≤20 CutoffOrdinal ≈ 460 chars → ≤ ~616 base64). The 240-char Value cap comfortably bounds the real SortKey ("0|SW-NN.NN|" + 40-char version + "!" + 36-char id ≈ 88).
- **Required fields, strict:** `Version == 2` (anything else → `invalid_cursor` — this rejects every pre-upgrade Release v1 token with the existing "Start again from the first page." recovery path), `Scope == "link-options"`, `FilterKey` equal to the request's computed key (which will bind `canonical-release-order-v2`, Project, normalized search, and order), `SnapshotAt <= UtcNow + 1min` (retained, informational).
- **Cutoff semantics:** `CutoffOrdinal` is a nullable long on the wire — **missing/null is invalid**; an explicitly present `0` is the legitimate legacy-only/empty-maximum boundary; negative values are invalid; values are bounded by `long`. `Value` must be non-empty and ≤240 chars; `TieBreaker` must parse as an integer and equal "0" for Release (keeping the pre-dispatch parse at `ManagedDocumentEndpoints.cs:827-828` valid). Encoding remains separate from authorization; page sizes 1–100 and the default of 50 are untouched.

---

**Remaining corrections — dispositions:**
1. Accepted: nullable legacy membership means "present when this feature's schema was installed" — no timestamp reconstruction, no controlled-history rewrite.
3. Accepted: cohesive new cases in a clearly named partial file under the existing `ProjectSetupPostgresQualificationTests` class; at Checkpoint B the passing, non-skipped TRX output will name each new test explicitly.
4. Accepted: `SoftwareReleaseOrderingQuery`, exact selected Release IDs, deep links and relationship meanings are preserved; the ordinal is a membership predicate only, never display order.
5. Accepted: no DEC number reserved, no unapproved design labeled accepted, no automatic `PROJECT_STATE.md` change. Docs plan: replace the `MANAGED_DOCUMENTATION_CENTER.md:98-103` exception with the implemented guarantee, the legacy-cohort meaning, and provider/token notes. `SAVE_BOUNDARY.md` now needs **no change**, because save requirements do not change under database-owned enforcement — I will confirm this against the actual implementation diff at Checkpoint B rather than assert it now.
6. Corrected as stated in the header.

**Mixed-version/rollout revision (material improvement over Round 1):** an old binary's INSERT omits the new column → `NEW."PickerInsertionOrdinal"` is NULL → the trigger allocates correctly. Old writers are **compatible, not fail-closed**, so schema-first deployment carries no availability window. Round 1's fail-closed mixed-version stance is withdrawn.

**Material scope delta since Round 1 — net reduction:**
Removed: application scope-evidence machinery; restructuring of `POST /api/releases`, import accept, and the second-showcase recovery path; adaptation of 193 fixture files and `ProgramRepository.AddAsync`; a new exception/error-mapping layer; the SAVE_BOUNDARY contract change; the mixed-version availability consequence. Added: two trigger functions + two triggers in the PG migration, three SQLite triggers + one idempotent init call at the EnsureCreated site, and one advisory-lock call in the Release page-one reader. Provider-specific SQL is confined to the migration and the SQLite initializer, and will carry the explicit PostgreSQL qualification AGENTS.md requires (generated SQL review, trigger-presence assertions, required-suite execution, query plans at B). Per-INSERT trigger cost is one indexed project-scoped MAX over a small per-project set; measured, not assumed, at B.

**R01–R12 plan:** unchanged in shape from Round 1 except: R03's barrier is the transaction-interceptor overlap design above; R05's "cannot bypass" evidence is trigger presence + coverage-by-construction plus endpoint/journey tests on both providers; R02/R04/R06–R12 as previously mapped. The Round-1 baseline reproduction stands as the defect evidence (Astra accepted it as sufficient; no new baseline runs performed).

**Artifacts opened/inspected this round:** `Repositories.cs:130-183` (`ProgramRepository.AddAsync`, repository seams), `Program.cs:95-150` (init block: Migrate/EnsureCreated + authority sequencing + seeding), `SoftwareReleaseIdentityAuthority.cs` (pre-INSERT authority behavior, unchanged), SQLite `EnsureCreated` site inventory across src/tests, fixture blast-radius count (193 files), and the four cited endpoint/save-pipeline regions from Round 1.

**Remaining uncertainty / decisions for Astra:** (1) approval of database-owned allocation/fencing as the A01 resolution; (2) the SQLite AFTER-INSERT allocation form (stated constraint: SQLite cannot assign NEW in BEFORE triggers) versus an application-side SQLite-only allocator — I recommend the trigger form for provider symmetry; (3) the 640-char cursor cap and 240-char Value cap; (4) confirmation that no `SAVE_BOUNDARY.md` change is required given the actual diff at B; (5) acknowledgment of the out-of-scope in-work TOCTOU observation. Prior finding IDs A1040-A01…A04 are dispositioned above; correction items 1–6 are dispositioned inline.

**Recommended verdict and why:** PASS for the revised design — it keeps the sanctioned ordinal+shared-fence direction, resolves A01 by eliminating the under-specified application contract in favor of the alternative Astra offered, answers A02 with a real installation path through the supported startup route, dissolves A03's revalidation/error-mapping risk by construction while replacing the barrier with a genuine overlap proof, and bounds the v2 cursor per A04 — with reduced scope and improved mixed-version behavior.

Please independently return PASS, CHANGES REQUIRED, or NOT REVIEWABLE, identify the exact SHA/design and scope reviewed, list actionable findings, and state which next phase may proceed. I will wait for Sean to relay your actual response.

CHECKPOINT A — AWAITING ASTRA REVIEW. No production implementation has started.
