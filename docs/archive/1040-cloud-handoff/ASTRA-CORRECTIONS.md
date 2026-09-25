# Corrections established during handoff review

The retained GLM material contains both useful evidence and material false statements. Copies preserve that history. This correction index must be read before relying on their prose.

| Claim/problem | Verified correction and consequence |
| --- | --- |
| The 28-file handoff is fully hash-verified | All 28 files existed; the nine incident copies matched their originals. The overall CSV has placeholder hashes, zero sizes and a malformed row. Use this publication's manifest and SHA256SUMS for the derived export. |
| Rehearsal v2 passed all drills in isolation | The actual retained log ends with **five failures**, including failed clone/cd and invalid refs. It is incident evidence, not installation qualification. |
| The rewritten script is safe to run | Its setup and cd failures can continue into the caller's checkout; it still contains automatic `--theirs` conflict handling. Do not run it. |
| Transition proposal v3 fully fixed earlier findings | The copied `45-trust-root-transition-proposal-v2.md` still contains rejected logic. It suggests waiting for main then relabeling the old failed head, compares current-main directly to an older candidate rather than the actual merge result, and depends on the installed requester for rollback. Its digest is `3E295EA18E1E05798B6193593AD3D47FB395DFF9F4EE756B88F1B479486A897F`. It is not an approved route. |
| Feature Checkpoint C passed at bf9dc3f0 | That SHA belongs to #1098's test corrections, not feature #1066. Locate actual review/qualification provenance; do not transfer a review across workstreams. |
| ReleaseLinkQueryService implements the picker | That class is absent at the feature head. The reader/fence helpers are in `ManagedDocumentEndpoints.cs`. |
| SQLite lacks triggers / has a NOT NULL UNIQUE-like ordinal | SQLite uses three installed triggers plus a separate operational cohort flag. The shared ordinal remains nullable for legacy rows. |
| The insertion ordinal defines display order | It defines membership. Existing canonical ordering and exact-ID tie-breaking define display order. |
| Revocation simply excludes a candidate | The qualified revoked-project-access case denies the continuation and relationship write. Authorization remains live. |
| No cross-project contention is possible | The global sequence is shared; advisory-key hashing can collide. Do not make an absolute no-contention claim. |
| Legacy NULL is a creation-time reconstruction | It is the documented pre-feature schema cohort. New guarded inserts acquire ordinals. The membership predicate itself always includes legacy NULLs. |
| Local geometry proves 236px overflow or transient-only passing | That comparison omitted the canvas origin. Correct viewport bounds were left 280, top 340, right 1920, bottom 584; the measured card fit with 16px bottom and 28px right clearance. |
| Main had clean native browser proof for this journey | Merge-group 36049357299 failed it initially and passed its retry. Push 36052413361 skipped browser journeys. The cause of the intermittent failure remains unresolved. |
| A clean canonical Git status means safe main | The unwanted changes are committed. Local main was two accidental commits beyond the pre-incident local state and diverged from current remote main. |

## Local incident identity

- Canonical path: `C:\Sean Project\Requirements Management Tool`.
- Pre-incident local main: `77b96857868f35926cd439371af159f8af2230e2`.
- Accidental revert of unrelated #1105: `a35b260b799cbfbea436ed5f63ff3c6ae1f23555`.
- Accidental workflow drill commit/current canonical HEAD: `b6309fb01d46afe17e7aaa3e34fc0bc38be6e726`.
- Additional branch `drillA2`: `bb8a6622a08742cc75f9879efb53ce8aa331b2fe`.
- Repository-local author configuration: `rehearsal` / `rehearsal@invalid`; the original local values are not established.
- The valid preservation bundle requires prerequisite `77b96857868f35926cd439371af159f8af2230e2`. It is not a full standalone repository/configuration backup.

No recovery was performed during review or cloud publication. No accidental branch was pushed as a branch. The bundle is retained as data only. [Local hand-back](LOCAL-OPERATOR-HANDBACK.md) specifies the review boundary.

## Accepted evidence and remaining limits

The #1098 v6 implementation had independent Astra POSIX execution: 32 passed, zero failed/skipped at `23bb25d9`. Its local fixture audit and log are in [astra-evidence/astra-1098-23bb25d9-review](astra-evidence/astra-1098-23bb25d9-review/). Windows 18-pass/14-skip is separate platform-limited evidence. This is local qualification, not hosted Full Product success or installation authorization.

The time-ordered two-requester queued-successor schedule is still not executed in the integrated workflow harness; static consumption refusal is not that schedule. Live `return_run_details` behavior against the pinned API version remains unqualified. Unit, integrated and structural coverage must remain distinguished.
