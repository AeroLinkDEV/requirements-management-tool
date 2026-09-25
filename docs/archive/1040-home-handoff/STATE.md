# #1040 — verified state at cloud hand-off (2026-09-25 ~07:15Z)

Every item below was verified by cloud Claude against GitHub or git at the time stated. Refresh GitHub
before relying on it. A local session should re-verify anything it acts on.

## Delivered and integrated (done)

| Item | Identity |
| --- | --- |
| Feature PR | #1066 **merged** (squash, via the protected merge queue) |
| Merge commit on `main` | `92ffbb6cefa58a36b9573ea59ceb155704c3538b`, tree `f67b4f9022b31704cc48578edb7c61c369cc78f5`, parent `33df25cde13b3889d59d571ef50244c423c7946a`. This is exactly the tested queue candidate. |
| PR head that was qualified | `49868301dd06e86da9075488316374163dc3db41` |
| Trusted requester | [36096616333](https://github.com/AeroLinkDEV/requirements-management-tool/actions/runs/36096616333): success. It dispatched a fresh bot-triggered Product run, because the head was new. |
| PR-head Product gate | [36096627604](https://github.com/AeroLinkDEV/requirements-management-tool/actions/runs/36096627604), attempt 1: success. Every job passed, including PostgreSQL required project-setup with none skipped. |
| Merge-queue Product gate | [36098866559](https://github.com/AeroLinkDEV/requirements-management-tool/actions/runs/36098866559), on candidate `92ffbb6c`: success |
| Migration shipped | `20260921140636_AddReleasePickerMembership` (additive: nullable column, sequence, two triggers; existing rows stay `NULL`) |
| Integration record | [#1040 comment 5827767275](https://github.com/AeroLinkDEV/requirements-management-tool/issues/1040#issuecomment-5827767275) |
| Separate finding | #1130: DataProtection key-folder race between parallel API test hosts; one cause of the old head's failed run |
| #1098 | Left **open** as a separate draft. Its disposition comment ([5827768680](https://github.com/AeroLinkDEV/requirements-management-tool/pull/1098#issuecomment-5827768680)) says #1040 does not depend on it and it must be sequenced with #1119. |

Main has since moved on: `05d85f7` (#1137) was the head when this hand-off was prepared. That is expected. The
deployed HOME revision must *contain* `92ffbb6c`; it need not equal it.

## Still open

1. **R12: deployed HOME acceptance.** Needs HOME (loopback API, read-only SQL on 54329, one browser look).
   #1040 stays open until this evidence is posted.
2. **Canonical Windows checkout recovery** (`C:\Sean Project\Requirements Management Tool`). Its local `main`
   is two accidental commits past the pre-incident state, and its repository-local identity is
   `rehearsal <rehearsal@invalid>`.

## Incident facts (verified in the cloud from the preserved bundle and GitHub)

| Fact | Value |
| --- | --- |
| Pre-incident local main | `77b96857868f35926cd439371af159f8af2230e2` (= #1105 squash; ancestor of protected main). Tree `d00fdd25367314188d28fa28863d13c0e8aac5c9`. `request-full-ci.yml` blob `d0e1776748026f59975dfc05eeb806163daaeb73`. |
| Accidental commits on local main | `a35b260b…` (exact revert of #1105) → `b6309fb0…` (appends `/* concurrent settings change */` to `request-full-ci.yml`). Incident tree `6db158346d6e454e84e747b3424e5dce70e86dfe`. |
| drillA2 | `bb8a6622…` on its own revert `5978b673…` (parent `77b96857`). Same tree as `b6309fb0`. |
| Incident bundle | Valid and **partial**: it requires `77b96857`. SHA-256 `70f10dc231d4cc129ae7d0e561b7ee36124cd4e0de71ba08b0579757ab76a5eb`. |
| Remote containment | None of the 109 remote branches contains any accidental commit, as of ~02:45Z. |
| Identity leak (**new**) | Two protected-main squash commits carry `Co-authored-by: rehearsal <rehearsal@invalid>`: `21e5095` (#1115, 02:34Z) and `33df25c` (#1134, 04:28Z). No main commit is authored or committed by `rehearsal`. Commits on those PR branches were made under the leaked local identity, from a worktree sharing the canonical repository's config. Protected history is not rewritten. |

## Cloud evidence in this folder

- `evidence/cloud-qualification/qual-027c985e/`: required PG runner at the old head. Infrastructure 11/11, API 11/11, none skipped.
- `evidence/cloud-qualification/qual-ed939376/`, full suites:
  - Domain 788/788.
  - Infrastructure 1148/79 skipped/1 failed.
  - API 1138/17 skipped/2 failed.
  - PG maintenance 14/14.
  - Required PG runner: Infrastructure 11/11, API 15/15.
  - SQL and plan evidence.
  - All three failures reproduce identically on the unchanged old head in the cloud sandbox (loopback SMTP and socket), and CI passed them.
- `evidence/cloud-qualification/qual-49868301/`, at the pushed head:
  - build;
  - focused SQLite 49/49;
  - required PG Infrastructure 11/11 and API 15/15;
  - contracts 38/38;
  - client lint, type-check and build;
  - browser 11/11 (`requalify.log`).
- `evidence/recovery-rehearsal/`: the recovery scripts rehearsed on three disposable replicas rebuilt from the bundle. Run 1 was 24/19 and exposed a script defect, since fixed. Run 2 was **43/0**. Runtime was pwsh 7.4 on Linux, so Windows PowerShell 5.1 was not exercised.
- `evidence/acceptance-script-test/`: `Test-1040-HomeAcceptance.ps1` run against a disposable PG 16 database migrated by the merged code.
  - Its read-only session was verified: row counts were unchanged afterwards.
  - Its FAIL lines were expected: no API was running, and the fixture contains post-upgrade rows.
- `evidence/CHECKPOINT-1-PACKET-historical.md`: the pre-implementation review and plan. Historical.
