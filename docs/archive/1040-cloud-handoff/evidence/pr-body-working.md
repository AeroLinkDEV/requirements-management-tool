## Problem

The Documentation Center **Build** relationship picker (`artifactType=Release`) must keep the same candidate membership while the operator loads successive pages. A build committed after page one could enter the continuation of an existing traversal (GitHub issue reference: AeroLinkDEV/requirements-management-tool#1040, R01 baseline reproduced on disposable PostgreSQL before implementation).

## Solution (approved at Checkpoints A/B with Astra; architecture unchanged)

- **PostgreSQL**: additive migration `20260921140636_AddReleasePickerMembership` — nullable `PickerInsertionOrdinal` bigint (no default, no identity — `NpgsqlValueGenerationStrategy.None`), sequence `aerolink_release_picker_ordinal_seq` (CACHE 1, NO CYCLE), and a fenced allocator trigger: supplied ordinals are rejected, `pg_advisory_xact_lock` per project is taken **before** `nextval`, and an immutability trigger rejects any ordinal change (`IS DISTINCT FROM`). Existing rows keep NULL as the documented legacy cohort.
- **SQLite**: `ReleasePickerSqliteGuard` installs, in one immediate transaction at host startup, a `PickerLegacyCohort` flag (first-install classification only, never reclassified) plus three triggers (reject supplied values, allocate MAX+1 per project via AFTER INSERT, absolute immutability permitting only the non-legacy NULL→value transition). No recursive-triggers dependency.
- **Reader**: the Release branch of link-options opens an explicitly Read Committed (SQLite: serializable immediate) request-scoped transaction, takes the same fence, captures `cutoff = MAX(ordinal) ?? 0`, filters membership **before** the unchanged canonical keyset and `Take(pageSize+1)`, and commits. Continuations replay the cutoff from a Release-specific v2 cursor (pre-decode 4096-character bound, required non-negative cutoff, old v1 tokens fail closed with `invalid_cursor`). Non-Release paging is untouched.
- **EF**: `ValueGeneratedOnAdd` + `AfterSaveBehavior.Ignore`; SQLite disables its RETURNING clause for this table only, so the allocated value is read back; lifecycle saves never write the column.
- **Docs**: the documented Release exception in `product/docs/MANAGED_DOCUMENTATION_CENTER.md` is replaced with the implemented guarantee, legacy-cohort meaning and provider/token limits.

## Qualification status (honest accounting)

- **Focused SQLite picker set: 14 tests (9 membership/access + 5 guard), all passing** — frozen membership, old-token/bound rejections, guard matrices, copied-host startup, EF readback/lifecycle, revocation and foreign-project refusals.
- **Required setup-PostgreSQL runner: Infrastructure 11/11, API 11/11 — none skipped** — including stale-snapshot writer schedules (both isolation levels), correlated fence-wait observation, reader cancellation cleanup, the unchanged 1.3-vs-1.30 canonical race, preceding-schema→upgrade with legacy-cohort preservation (releases, baselines, builds, campaigns, approvals, signatures), a database guard rejection matrix, a 601-candidate bounded continuation (page one 107 ms, continuation 38 ms) with actual-command EXPLAIN, and generated-SQL capture.
- **Browser journey** (`managed-documentation-center-picker-continuation.spec.ts`): test-owned Program/Project/document per attempt, 56-candidate picker (57 select options including the placeholder), frozen continuation across "Load more records" excluding a later build, page-two selection saved to the exact build (`/releases/{id}/command-center`), fresh traversal including it. Passed both `--repeat-each=2` repetitions and alongside the neighboring Documentation Center journeys (20 passed / 2 failed — the 2 are the pre-existing showcase spec failing identically under repetition on its own, independent of this branch; that spec is not repetition-safe by design).
- **Full API suite**: **no clean exact-final-head full-suite result exists yet** — the retained local history (1123/6 then 1128/1, the single failure since fixed and passing) plus one independent rebuild failure pre-dating the final fixes are documented; resource degradation during the long local runs remains an unconfirmed hypothesis. The authoritative broad-suite evidence is the GitHub Actions lane this PR runs at Checkpoint D.
- Generated contracts regenerated (byte-stable twice); layout guard passed.

Refs #1040 (issue stays open pending the gated C/D/E acceptance).


