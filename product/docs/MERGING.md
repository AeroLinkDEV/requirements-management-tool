# Merging into `main`

**Read before merging anything.** How a pull request lands changed on 13 August 2026.

## Arm it and walk away

```bash
gh pr merge <number> --squash --auto
```

The pull request merges by itself the moment its required check passes. Do this **as soon as the pull request
is ready** — not after you have watched CI go green. Watching CI is wasted time, and the gap between "green"
and "somebody noticed it was green" was routinely the longest part of a merge overnight. That gap is also the
window in which another agent merges first and puts you behind `main`, so closing it means fewer rebases.

To disarm:

```bash
gh pr merge <number> --disable-auto
```

## What auto-merge does not do

`main` still requires a pull request to be **up to date with `main`** before it can merge. Auto-merge does not
override that — an armed pull request that falls behind simply **waits, silently and indefinitely**, until
somebody rebases it.

So the old rule still applies when you are actually behind:

```bash
git rebase origin/main && git push --force-with-lease
```

The difference is that you now rebase only when you genuinely lost a race, rather than rebasing on a hunch.
**Check before you rebase** — a needless rebase throws away a green run and costs a full cycle:

```bash
gh pr view <number> --json mergeStateStatus --jq .mergeStateStatus
```

`BEHIND` means rebase. `BLOCKED` means a check has not passed yet. `CLEAN` means it is up to date and armed —
leave it alone.

Because an armed pull request waits quietly, a stalled one does not announce itself. If something has not
landed when you expected it to, check its state rather than assuming it is still building.

## Two things that bite

- **Nothing requires a human review** (`required_approving_review_count` is 0). Under auto-merge, the CI gate
  is the *only* gate, and the merge happens with nobody looking. That is deliberate and it is why the gate is
  broad — backend, browser journeys, the production build, and PostgreSQL migrations all run. Do not arm a
  pull request you would not be comfortable landing unattended.
- **Pushing after arming re-aims it.** Auto-merge stays armed and merges on whatever the *new* head commit is
  once that goes green. If you push a fixup to an armed pull request, you have not paused it.

## The pull request title becomes the commit title

The repository uses `squash_merge_commit_title: PR_TITLE`. It previously used `COMMIT_OR_PR_TITLE`, under
which a **single-commit** pull request took its title from the commit and ignored the pull request title —
which is how `WIP: verification change request register` became the permanent message on commit `2c692ff`,
unfixable afterwards on a protected branch.

Title the pull request as you want it to read in `main`'s history forever. The branch is deleted on merge.

## Why there is no merge queue

A merge queue would remove the rebase problem entirely: GitHub would test *current `main` plus your change* on
a temporary branch and merge only if that passed. It was attempted and it cannot be enabled here —
**merge queues require a repository owned by an organization**, and this repository is owned by a personal
account. The evidence and the one route to unblocking it are recorded in
[issue #549](https://github.com/seanmccarthyns/requirements-management-tool/issues/549).

Two consequences worth knowing:

- CI already handles the `merge_group` event and refuses to report success for a merge-group run in which the
  product gates did not actually execute. That code is dormant, costs nothing, and means the queue can be
  switched on the day this repository moves to an organization.
- **Do not turn off "require branches to be up to date" to speed things up.** That setting is only safe to
  remove *because* a merge queue takes over the guarantee it provides. Without a queue, removing it just lets
  changes land untested against the `main` they land on.

## Related

- [Feedback time](BROWSER_AND_BACKEND_FEEDBACK_TIME.md) — **read before changing CI.** Where a pull request's
  wall clock actually goes, measured, and why shard counts are not the lever.
