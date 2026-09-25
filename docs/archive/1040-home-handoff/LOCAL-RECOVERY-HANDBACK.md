# #1040 — Windows canonical-checkout recovery hand-back (for Astra's review)

**Requested decision:** Astra reviews this bounded procedure. Sean runs the two read-only steps now (audit, Plan).
**Recover** and **Rollback** run only after Astra actually approves them. Nothing here has run on Sean's machine.
The cloud cannot reach it.

**Prepared by:** cloud Claude, 2026-09-25. No Windows path was accessed.

## 1. What the cloud verified independently (Linux, git 2.43)

| Fact | Evidence |
| --- | --- |
| The incident bundle is valid and **partial**. It requires `77b96857…`. Its heads are `main=b6309fb0…` and `drillA2=bb8a6622…`. SHA-256 `70f10dc2…ab76a5eb`. | `git bundle verify` / `list-heads` on the published copy |
| Local `main` = `77b96857` + `a35b260b` + `b6309fb0`. | Bundle object graph |
| The drillA2 branch is **its own** revert `5978b673` (parent `77b96857`) plus `bb8a6622`. These are not the same commits as main's. | Bundle object graph |
| Both revert commits have tree `1caaf3ee…`. That equals the tree of `77b96857~1` (#1101), so each is an exact revert of #1105. | `rev-parse ^{tree}` |
| `b6309fb0` and `bb8a6622` have the same tree, `6db15834…`. Beyond the revert, each only appends `/* concurrent settings change */` to `.github/workflows/request-full-ci.yml`. | `git show` |
| Pre-incident tree `d00fdd25…`. Pre-incident workflow blob `d0e17767…`. | `rev-parse` |
| `77b96857` is #1105's squash commit and an ancestor of protected main `0dddb029`. Main is 3 commits further on. | `merge-base --is-ancestor` |
| **None of the 109 remote branches** contains any of the four accidental commits (`a35b260b`, `b6309fb0`, `5978b673`, `bb8a6622`). No remote ref points at them. | Full fetch of `refs/heads/*` into a scratch bare repo, then `for-each-ref --contains` |
| Every #1066 and #1098 commit predates the incident (2026-09-24 21:03 −04:00) and is authored `seanmccarthyns`. | `git log` |

**Previously reported (not verifiable from the cloud):** that the working tree is clean, the HEAD reflog, the
repository-local `rehearsal` identity, and the worktree layout. These come from Astra's review and GLM's copied
files. The audit below re-observes them locally.

## 2. Important consequence to confirm locally

Repository-local config is **shared by every linked worktree** unless `extensions.worktreeConfig` is enabled.
If `RMT-1040-worktree` and `RMT-1040-ci-worktree` are linked worktrees of the canonical repository, any commit
made in them since the incident gets `rehearsal <rehearsal@invalid>` as its author. **Until the identity step is
approved, make no commits in any worktree of that repository.** The audit reports the layout (file 40) and any
rehearsal-authored commit on any ref (files 19b and 19c).

### 2a. Update 2026-09-25 ~05:35Z: the identity leak has already happened (verified on GitHub)

Two squash merges on protected `main` carry `Co-authored-by: rehearsal <rehearsal@invalid>`:

- `21e5095`: #1115, merged 02:34Z.
- `33df25c`: #1134, merged 04:28Z.

GitHub adds a co-author trailer for each commit author on the PR branch. So commits on those PR branches were
made under the rehearsal identity after the incident, from a worktree that shares the canonical repository's
local config.

No main commit is *authored* or *committed* by `rehearsal`. Protected history is not rewritten. This is
recorded rather than fixed.

**Stop making commits from any worktree of `C:\Sean Project\Requirements Management Tool` until the identity
step (`-UnsetRehearsalIdentity`) is approved and applied.** The audit's files 19b and 19c list every affected
local commit.

## 3. Step A: read-only audit (run now; no approval needed)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Audit-1040-CanonicalCheckout.ps1
```

- **What it does:**
  - Runs only Git reads, with `GIT_OPTIONAL_LOCKS=0` so `status` cannot rewrite the index.
  - Does no network access.
  - Writes only into a **new** directory, `C:\Sean Project\RMT-1040-glm-evidence\canonical-audit-<UTC>`. It refuses an existing directory or one inside the repository.
- **What it captures:**
  - HEAD and status.
  - Every ref.
  - The commits beyond `77b96857`.
  - Which refs contain each accidental commit.
  - All commits since 2026-09-24 20:30 −04:00 on any ref.
  - Rehearsal-authored and rehearsal-committed commits.
  - The HEAD reflog and every local branch's reflog.
  - Config with its origin and scope.
  - The `extensions.worktreeConfig` setting.
  - `core.hooksPath` and the hashes of active hooks.
  - Each worktree's HEAD, branch and status.
  - Workflow blob comparisons.
  - Verification of the incident bundle.
  - SHA256SUMS of the output.
- **Handling the output:** remote URLs have any embedded credentials masked. Review the output before relaying it.

## 4. Step B: Plan (run now; read-only)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Recover-1040-CanonicalCheckout.ps1 -Mode Plan -UnsetRehearsalIdentity
```

Plan checks every precondition and prints the exact commands. It writes and changes nothing. **Preconditions**
(any failure aborts before the first write):

- The path given is itself the top of the work tree, and it is the main worktree, not a linked one.
- `HEAD` is `refs/heads/main`, and `main` = `b6309fb0` with tree `6db15834`.
- `77b96857..main` is exactly `b6309fb0,a35b260b`.
- `drillA2` = `bb8a6622`.
- The working tree is clean, with no tracked changes and no untracked files.
- No merge, rebase, cherry-pick, revert or bisect is in progress, and no index or HEAD lock is held.
- `main` is not checked out in another worktree.
- No `refs/incident/1040/*` ref exists yet.
- When the identity step is requested, the local identity is exactly `rehearsal` / `rehearsal@invalid`.

## 5. Step C: Recover (**only after Astra approves**)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Recover-1040-CanonicalCheckout.ps1 -Mode Recover `
  -EvidenceDir 'C:\Sean Project\RMT-1040-glm-evidence\canonical-recovery-<UTC>' [-UnsetRehearsalIdentity] [-ArchiveDrillA2]
```

Recover takes these steps in order. Every Git call is `git -C <verified path>`.

1. **Preservation**, before any ref moves:
   - `git bundle create <evidence>\canonical-all-refs-pre-recovery.bundle --all`, then `bundle verify`. This bundle is **standalone**: it has no prerequisites and contains every ref.
   - A copy of `.git\config`.
   - Snapshots of refs, reflogs (HEAD, main, drillA2), status, the stash list and the local config with its origin.
   - Two **create-only** archive refs. Their `refs/incident/…` namespace is not pushed by a default or `--all` push:
     - `git update-ref refs/incident/1040/local-main-b6309fb0 b6309fb0 0000…`
     - `git update-ref refs/incident/1040/drillA2-bb8a6622 bb8a6622 0000…`
2. **Re-check** every precondition.
3. **Restore main:** `git reset --keep 77b96857…`. `--keep` refuses to discard local modifications.
4. **Verify:**
   - `HEAD` = `77b96857` with tree `d00fdd25`.
   - The tree is clean.
   - `request-full-ci.yml` has blob `d0e17767`.
   - Every branch other than main is unchanged.
   - The archive refs resolve.
5. **`-UnsetRehearsalIdentity`** (separate approval): unsets only the local `user.name` and `user.email`. It then records the effective identity and its origin, which may be global or none. **It sets no guessed value.** If the effective identity is absent or wrong, Sean chooses explicitly.
6. **`-ArchiveDrillA2`** (separate approval; not assumed):
   - Removes `refs/heads/drillA2` by compare-and-swap. Its commits stay reachable from the archive ref.
   - **Recommendation:** leave drillA2 in place unless Sean prefers it out of `git branch` listings. It is harmless while unpushed, and it is already preserved.

**Expected end state (incident undone):**
- `main` = `77b96857868f35926cd439371af159f8af2230e2`, with tree `d00fdd25367314188d28fa28863d13c0e8aac5c9`.
- The tree is clean.
- The #1105 content is present again, and the drill line is gone.
- drillA2 is unchanged, or archived if that step is approved.
- The local identity is removed only if that step is approved.
- The feature and requester branches and worktrees are untouched.
- Nothing is pushed.

## 6. Step D: update to current protected main (**separate decision**)

This step is separate from undoing the incident. `-FastForwardToRemoteMain`:

- Fetches only `+refs/heads/main:refs/remotes/origin/main`.
- Requires that `77b96857` is an ancestor of it.
- Runs `git merge --ff-only`.
- Records the resulting SHA. It was `0dddb029…` at handoff and may be newer now.

Doing it by hand afterwards is equivalent: `git -C <repo> fetch origin`, then `git -C <repo> merge --ff-only origin/main`.

## 7. Rollback

`-Mode Rollback -EvidenceDir <the Recover evidence dir>` does the following:

- Refuses unless `main` is exactly where Recover left it and the tree is clean.
- Runs `git reset --keep refs/incident/1040/local-main-b6309fb0`.
- Re-applies only what Recover changed, using `result.json`:
  - the rehearsal identity, and only if no local identity exists now;
  - drillA2, create-only.
- Verifies `main` = `b6309fb0` and tree `6db15834`.

**Last resort:** `git -C <repo> fetch <evidence>\canonical-all-refs-pre-recovery.bundle "refs/*:refs/restore/*"`. Then restore selectively after review. The config copy restores configuration. Git bundles do not carry reflogs or config.

## 8. Rehearsal evidence (cloud, disposable; not proof of Windows PowerShell 5.1 behavior)

`rehearse-1040-recovery.sh` builds three disposable replicas of the exact incident state. For each it:

- clones the verified source,
- checks out `77b96857`,
- fetches the published bundle,
- resets main to `b6309fb0` and creates drillA2 at `bb8a6622`,
- sets the local `rehearsal` identity,
- adds a linked feature worktree at `027c985e`,
- sets a synthetic global identity.

Every mutation targets a replica path that has been verified to be inside the run's own disposable root. A setup
failure exits before any recovery script runs. The source clone's refs, HEAD and config hash are compared before
and after.

- **Run 1 (`rehearsal-run-1.log`): 24 passed, 19 failed.** A PowerShell strict-mode defect made `-Mode Plan` and `Recover` throw on a returned empty list. Every refusal test still refused with no change, and the source was unchanged. The defect is fixed with `return ,$f` and `@(…)`.
- **Run 2 (`rehearsal-run-2.log`): 43 passed, 0 failed.** It covered:
  - that the audit is read-only, including index bytes;
  - that Plan is read-only;
  - seven refusal cases, each with zero change: untracked work, a subdirectory path, the linked-worktree path, HEAD not on main, an unexpected identity, an extra local commit beyond the incident, and an existing evidence directory;
  - Recover of main only, with the tree, #1105 content, workflow blob, drillA2, archive refs, identity, linked worktree, standalone bundle, config copy and checksums all verified;
  - Rollback;
  - Recover with every opt-in, which removed the identity (the linked worktree shares the fix), archived drillA2 and fast-forwarded to origin/main; then its Rollback;
  - the source clone unchanged.
- **Limits:** the runtime was pwsh 7.4.6 on Linux with git 2.43, so Windows PowerShell 5.1 and Windows paths were not exercised. Run Plan first; it is read-only and will surface any platform difference. `--path-format=absolute` needs Git 2.31 or later.

The inherited GLM rehearsal scripts were **not** executed, and they remain unsafe (Astra's finding).
