# #1040 — HOME rollout and deployed acceptance (Checkpoints D and E; local operator)

The cloud cannot reach HOME. This is the exact supported path, and the read-only acceptance a local
operator (Sean, or a local agent under Astra's review) runs after the #1066 squash merge landed on `main`.

**Merged:** PR #1066 → `main` at **`92ffbb6cefa58a36b9573ea59ceb155704c3538b`** (2026-09-25 ~06:12Z).
It is the exact merge-queue candidate. Its tree is `f67b4f9022b31704cc48578edb7c61c369cc78f5`, and its parent is
`33df25c`.

Evidence:
- Merge-group Product run [36098866559](https://github.com/AeroLinkDEV/requirements-management-tool/actions/runs/36098866559): success.
- PR-head Product run [36096627604](https://github.com/AeroLinkDEV/requirements-management-tool/actions/runs/36096627604): success.
- Trusted requester [36096616333](https://github.com/AeroLinkDEV/requirements-management-tool/actions/runs/36096616333): success, at PR head `49868301`.
**Nothing here writes to HOME data.** No insert, reset, reseed or truncate. Nothing here uses unmerged
tooling against HOME.

## What happens automatically after the merge (current `product/docs/OPERATIONS.md`)

1. Within 30 minutes, `AeroLinkProductionSourceReconcile` sees that `origin/main` moved. It then **inspects, stops, advances and restarts** the dedicated production source `C:\Sean Project\AeroLink Production`, which must be a clean canonical `main`.
2. Before the web server starts, the launcher runs `maintenance analyze`. The pending migration `20260921140636_AddReleasePickerMembership` gives exit code **10** (deterministic upgrade required). The launcher then:
   - takes a **verified backup**;
   - restores an **isolated copy** through `Restore-AeroLink.ps1`, with an isolated evidence root;
   - applies the upgrade to the copy;
   - proves the copy is current and can be served, in the read-only restore-validation boundary;
   - **only then** upgrades the real `aerolink` database.

   A failure at any earlier step leaves the persistent database and evidence untouched.
3. The migration is additive:
   - a nullable column;
   - a sequence;
   - two trigger functions and two triggers on `software_releases`.

   Existing rows keep `NULL`, which is the legacy cohort. No controlled column, manifest or signature is touched.

   The cloud's pg_dump/pg_restore round trip with the Restore-AeroLink flags confirmed that later backups and restores preserve ordinals, legacy NULLs, the sequence and the triggers.

If you'd rather deploy immediately than wait up to 30 minutes, the supported manual path is
`CONFIGURE_AEROLINK_PRODUCTION_SOURCE.bat Update`, which runs the same controller.

## Recovery if the upgrade refuses

- The launcher leaves production on the revision on disk, with the database untouched.
- The verified pre-upgrade backup is retained.
- Report the refusal text. **Do not** retry by hand, edit migration history, or point tooling at 54329.
- Rolling back the code is a normal revert PR through the queue. The migration's `Down()` exists, but it must **not** be applied to HOME without a separately reviewed operator decision. The new column and triggers are inert for older code, because older writers never supply the column and the trigger allocates.

## Checkpoint E: read-only acceptance to run and paste back

Run these after the production source reports the new revision.

1. **Deployed identity:**
   - `CONFIGURE_AEROLINK_PRODUCTION_SOURCE.bat Status`
   - `curl http://127.0.0.1:5080/health/identity`, which gives the source SHA; this endpoint is loopback-only.

   Record the deployed SHA and check that the #1066 merge commit is an ancestor. From the production source, run `git merge-base --is-ancestor 92ffbb6cefa58a36b9573ea59ceb155704c3538b HEAD`, which should exit 0. If later merges deployed too, record both SHAs; don't substitute one for the other.
2. **Migration applied through the supported path:**
   - `dotnet run --project product/src/AeroLink.Api -- maintenance analyze --json`, from the production source. It is read-only and should exit 0 (current).
   - The launcher and upgrade log lines for this start: backup, isolated copy, upgrade and serve proof.
3. **Health:** `curl http://127.0.0.1:5080/health/ready` returns 200.
4. **Schema and legacy cohort.** Run this read-only SQL against the HOME database on 54329, SELECT only:

   ```sql
   SELECT max("MigrationId") FROM "__EFMigrationsHistory";
   SELECT tgname FROM pg_trigger WHERE tgrelid = 'software_releases'::regclass AND NOT tgisinternal ORDER BY 1;
   SELECT count(*) AS total,
          count(*) FILTER (WHERE "PickerInsertionOrdinal" IS NULL) AS legacy_cohort
   FROM software_releases;
   ```

   Expect:
   - migration `20260921140636_AddReleasePickerMembership`;
   - the triggers `aerolink_release_picker_alloc_ins` and `aerolink_release_picker_immutable_upd`;
   - `legacy_cohort = total` for every row that existed before the upgrade.
5. **Picker behavior, read-only in the browser:**
   - Open an existing FMS document in the Documentation Center.
   - Open **+ Link artifact**, choose **Build**, and check that the existing builds appear in canonical order (e.g. 1.5 before 1.6), with correct Released / In-work labels.
   - If more than 50 builds exist, check that **Load more records** continues without duplicates.
   - **Do not save a link** unless you intend a real controlled change.

Mutation, concurrency and insert qualification stay on disposable state. They are already covered by the
required PostgreSQL runner and by CI.
