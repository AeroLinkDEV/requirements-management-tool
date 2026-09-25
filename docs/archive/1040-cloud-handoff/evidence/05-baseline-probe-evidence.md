# RMT-1040 baseline reproduction — disposable PostgreSQL (checkpoint A / R01 precursor)

Result: **DEFECT REPRODUCED.** Final probe run: `Total tests: 1, Passed: 1`, exit code 0, ~16.5 s
(log: `03-baseline-probe-run.log`; throwaway source retained as `06-baseline-probe-source.cs`;
removed from the worktree after the run — `git status` clean at b64301b1).

Conditions:
- Disposable docker container `rmt1040-baseline-pg`, image `postgres:17`, published `127.0.0.1:54331`
  (persistent `127.0.0.1:54329/aerolink` NOT used; the probe additionally refuses port 54329/non-loopback).
- Per-run throwaway database `aerolink_1040_probe_<guid>`, migrated via `db.Database.MigrateAsync()`,
  dropped `WITH (FORCE)` in `finally`. Real `AeroLinkApiFactory` API host, real HTTP requests, no route mocks.
- Probe ran against exact worktree HEAD b64301b168ed98d4054cb893932978c4d090f745, unmodified product code.

Observed sequence (final green run):
1. Seed: program RMT1040PROBE + project + releases 1.0 (released), 1.5 (in work, predecessor 1.0), 2.0 (released).
2. `GET /api/managed-documents/link-options?projectId=…&artifactType=Release&pageSize=2` →
   page one = [BUILD-1.0 `07934342-7723-4eb7-a156-c0b35158173b` (Released), BUILD-1.5 (In work)],
   `hasMore=true`, v1 cursor with `Value="0|SW-01.50|1.5!928bd374-…"`, `TieBreaker="0"`.
3. Two builds committed AFTER page one: BUILD-1.2 `21db1215-4847-48c9-98ca-78eded3f603c` (sorts BEFORE the
   cursor) and BUILD-9.9 `2ac6f90d-1476-4b04-a9a2-e2c3d9678e79` (sorts AFTER the cursor).
4. Continuation `GET …&cursor=<page-one cursor>` → page two = [BUILD-2.0, **BUILD-9.9**].
   `Assert.Contains(afterCursorId)` PASSED: **the build committed after page one entered the continuation
   of the original traversal.** `Assert.DoesNotContain(beforeCursorId)` PASSED (the before-cursor build is
   excluded by the existing after-key keyset predicate — it is a fresh-traversal-only record today).
5. Fresh `pageSize=50` traversal returned all 5 builds in canonical order (1.0, 1.2, 1.5, 2.0, 9.9) —
   `Assert.Equal(5, …)` PASSED. A restarted traversal does include the new builds (current live behavior,
   which the frozen-membership design must preserve for NEW traversals).

Honest run history (all three runs against identical code; failures were probe-fixture bugs, product behavior
was identical and defect-consistent in every run):
- Run 1: fixture seeded only 2 originals → page one not short (`hasMore=false`, `nextCursor=null`);
  probe crashed on `EscapeDataString(null)`. No product assertion involved.
- Run 2: fixture corrected; leak assertions PASSED; probe's fresh-traversal URL duplicated `pageSize`
  (400) — probe bug, after the leak evidence was already captured.
- Run 3 (final): all assertions passed, 1/1 green, exit 0.

This is diagnosis-only evidence. The probe asserted the CURRENT (defective) behavior; the retained source is
the seed for the R01/R02 baseline assertion that the implementation phase will invert into the frozen-membership
contract. No production code, migration, or committed test was added to the branch.
