(Retained copy of the exact Checkpoint B Round 4 packet submitted to Astra via Sean on 2026-09-21.)

ASTRA REVIEW REQUEST — #1040 — CHECKPOINT B — ROUND 4

**Requested decision and next action:**
Review the bounded B1040-B01 correction (persisted manifest payload + real ElectronicSignature evidence, unambiguous string encoding). On PASS, the next phase is Checkpoint C scope (browser journey, revoked-access qualification, larger-fixture latency, final-head review and draft PR). No Full-CI request, queue admission, rollout or issue closure has been requested or performed.

**Exact committed candidate and status:**
HEAD `d5fff26f665a40331ddc3019c41841aeebb66d6d`, worktree clean, branch `glm/1040-release-picker-snapshot`. Baseline remains `b64301b168ed98d4054cb893932978c4d090f745`; protected main `7404e884` unchanged; no rebase; no conflicts with the intervening #1064 change.

**Run provenance (exact, per Round 3's correction):**
- HEAD and dirty status recorded **before** testing: `d5fff26f`, clean.
- The required PostgreSQL qualification and the focused SQLite picker tests ran at that exact committed head.
- HEAD and dirty status recorded **after** testing: `d5fff26f`, still clean.
- Regenerated contract artifacts this round produced **no diff** (the Round-3 span commit already reflected current line positions), so no metadata-only commit followed the runs: the tested head IS the final candidate head.

**B1040-B01 — DISPOSITION: the actual stored manifest payload and a real electronic-signature record are now persisted, asserted and compared.**

What is persisted before the migration (all inside the hostless preceding-schema fixture):
1. **The manifest payload text** is stored in a mapped evidence field: a `ReleaseCampaignEvent` with `EventType='ReleaseManifestRecorded'` whose `Detail` is the full serialized manifest JSON (`release`, `baseline`, `build`, `contents`, `recordedBy` — nonempty, sub-4000 characters, written through the domain method `RecordExecutionProgress`).
2. **Its associated hash/identity**: the same text's SHA-256 (lowercase hex, 64 chars) is bound as `ReleaseHash` through the real review state machine — `StartVerification` → `SelectVerificationBuild(buildId)` → `RecordExecutionProgress` → `BeginReleaseReview(actor, approvers, manifestHash, now)` (which validates the 64-character hash and creates the ordered approval) → `Approve(approverId, now)` → `Release(buildId, manifestHash, actor, now)` (which re-validates hash identity, build identity and full approval before setting `Released`/`ReleasedAt`). The campaign therefore carries `SoftwareBuildId`, `BaselineId`, `ReleaseId`, `ReleaseHash`, `ReleasedAt` — all bound identities.
3. **A real ElectronicSignature fixture row** via the existing domain constructor (`AeroLink.Domain.Identity.ElectronicSignature`), bound to the campaign artifact: `ArtifactType="ReleaseCampaign"`, `ArtifactId=campaign.Id`, `ArtifactRevision="SW-02.00"`, `Action="Release"`, `ContentHash=` the **same** manifest hash, `Meaning` and `Rationale` populated, `ProgramId`/actor/display-name/ip/timestamp populated, `Authority="ProgramManager"`. Described accurately as a directly seeded synthetic fixture — not an executed authorized signing workflow.

Nonempty pre-migration assertions (an empty fixture cannot pass): exactly one campaign with a 64-character non-NULL `ReleaseHash` and non-NULL `ReleasedAt`; exactly one `Approved` approval with `ApprovedAt`; exactly one `ReleaseManifestRecorded` event whose `Detail` equals the payload; the payload's SHA-256 equals `campaign.ReleaseHash` (payload↔hash pairing); exactly one `electronic_signatures` row with `ArtifactType='ReleaseCampaign'`, `ArtifactId=campaign.Id`, `ContentHash=manifestHash`. The captured pre-migration evidence strings are asserted to contain the event type, the JSON-encoded payload and the hash.

After the upgrade and idempotent reapplication (zero pending migrations twice): lossless typed captures of `release_campaigns`, `release_approvals`, `release_campaign_events` and `electronic_signatures` are byte-identical to the pre-migration captures (alongside the existing fixed-projection software_releases, candidate_baselines and software_builds comparisons); the NULL-cohort assertion stands (`COUNT(PickerInsertionOrdinal) == 0`, `COUNT(*) == 3`); then the host starts and the legacy paging / frozen-continuation / fresh-traversal / in-work-label assertions run unchanged, with the post-upgrade build inserted through a non-EF raw writer.

**Unambiguous string encoding (string-comparison correction):** `RenderValue` now renders strings as `"J:" + JsonSerializer.Serialize(text)` — JSON string serialization is injective here (backslash, quote, newline and control characters are all escaped unambiguously), eliminating the `a\nb` vs `a\\nb` collision Astra reproduced. Typed binary (`b64:`), Guid, boolean and temporal (`T:`/`D:` invariant ticks) renderings are retained.

**B1040-B06, B1040-B05 and small corrections:** unchanged from Round 3 except as follows. The Round-4 required-runner SQL evidence file (`picker-sql-evidence-r4/picker-continuation-sql-plan.txt`, 22,282 bytes) was produced by this candidate's run; the Round-3 findings about it remain closed. Packet accounting: the branch now contains **eleven** commits over the baseline (the ten previously counted plus the signature-fixture commit `d5fff26f`); the SQL fixture contains **three** releases total (seeded 1.0 plus 1.5 and 2.0); the final required-runner TRX files are explicitly `trx-required-pg-r4/project-setup-postgres-AeroLink.Infrastructure.Tests-430ffbd96c9e430880eddc4e33797d35.trx` and `trx-required-pg-r4/project-setup-postgres-AeroLink.Api.Tests-b7988644b7ef4a8aa88a56cdf429dfab.trx` (the earlier failing `trx-required-pg-r3` Api run — 9 passed / 1 failed on the retired `SW-01.50` expectation — remains retained with its disposition); no inventory Markdown change and no contract-artifact diff this round (regenerated twice, zero diff both times).

**R01–R12 status:** unchanged from Round 3 except R04, which is now genuinely B-complete: empty install, preceding-schema upgrade with preserved legacy/controlled data **and stored manifest/signature evidence**, idempotent reapplication, and post-upgrade selectability/frozen membership. R06 remains partial (revoked-access deferred to C); R08 carries the actual-command plan evidence with larger fixtures C-scope; R09 PostgreSQL complete/none skipped, SQLite separate, browser C-scope; R10 partial (final-gate evidence C/D); R11/R12 not started.

**Commands and results, tied to the candidate (disposable docker postgres:17 at 127.0.0.1:54331; persistent 54329 untouched):**
1. HEAD/dirty before: `d5fff26f`, clean.
2. `Test-ProjectSetupPostgres.ps1` → **Infrastructure 11/11 passed; Api 10/10 passed; none skipped** (TRX files named above). Named Api results, all Passed: Picker_upgrade_preserves_legacy_rows_and_freezes_continuations (now including the manifest/signature preservation asserts); Picker_database_guard_rejects_forbidden_membership_mutations; Picker_continuation_generated_sql_and_parameters_are_captured_for_plan_evidence; Stale_snapshot_writers_cannot_enter_frozen_release_continuations (both isolation levels); Page_one_fence_waits_for_uncommitted_allocator_then_includes_the_committed_build; Cancelled_page_one_releases_the_fence_without_stuck_writers; Fresh_project_services…; Release_creation_and_legacy_import…; Discard_cannot_win…; Repository_edit_waits….
3. HEAD/dirty after: `d5fff26f`, clean.
4. `dotnet test AeroLink.Api.Tests --filter ReleasePicker` (SQLite) → **11/11 passed, 0 skipped** (matches Astra's independent 11/11 on the prior head; this head adds only the signature-fixture asserts to the upgrade PG test and the 40-char label correction).
5. Regenerated contract artifacts: no diff (twice).

**Prior finding dispositions:** B1040-B01 closed by this round's fixture; B02/B03/B04/B05/B06 and the A-side findings remain closed; the small corrections (40-char labels, >640 assertion, message-specific guard asserts, packet accounting) were accepted in Round 3 and remain in place.

**Remaining limits:** no clean exact-final-head full-suite local rerun (deferred to C/D per review guidance, with the retained failure history and the contention hypothesis stated as unconfirmed); browser journey, revoked-access qualification, larger-fixture latency and draft-PR preparation are the next phase after B passes; SAVE_BOUNDARY.md unchanged.

**Recommended verdict and why:** PASS for the core proof — the last blocker is closed exactly as scoped: the persisted manifest payload and its digest are paired and asserted nonempty, a real domain-constructed ElectronicSignature row signs that hash against the campaign artifact, all of it is losslessly compared across the actual upgrade and reapplication, and the earlier accepted proofs are unchanged on an exact committed head with before/after provenance.

Please independently return PASS, CHANGES REQUIRED, or NOT REVIEWABLE, identify the exact SHA/design and scope reviewed, list actionable findings, and state which next phase may proceed. I will wait for Sean to relay your actual response.

CHECKPOINT B — AWAITING ASTRA REVIEW. Checkpoints C/D/E remain; no Full-CI request, queue admission, rollout or issue-closure action has been taken.
