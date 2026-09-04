# Merging into `main`

**Read before merging anything.** How a pull request lands changed on 4 September 2026.

## Request Full CI only when the current SHA is merge-ready

Every pull-request SHA receives **advisory Fast feedback automatically**. Fast is for development feedback; it
is not merge authority and cannot satisfy branch protection.

When implementation and review are finished and the **current SHA is the one you intend to merge**, request
the existing Full Product gate by applying the repository label:

```bash
gh pr edit <number> --add-label ready-for-full-ci
```

The trusted default-branch requester authenticates the live pull request, exact head SHA, base SHA, head ref,
same-repository origin and readiness label before dispatching Full validation. Product's internal
`Full Product evidence aggregate` must pass, including Product's own readiness-input authentication. Only then
does the trusted requester complete the protected `Report what this run validated` context for that exact SHA.

Only after readiness is requested for the final SHA, ask GitHub to merge when ready:

```bash
gh pr merge <number> --squash --auto
```

After the trusted requester binds Product success, the AeroLink Merge Authority App publishes
`Trusted merge-queue binding` on the exact pull-request head. That App-bound check makes the pull request
eligible to enter the queue. If another commit is pushed, the trusted synchronize guard removes
`ready-for-full-ci`; the old Full result and App check belong to the old SHA and cannot authorize the new one.
Finish the fix, then request readiness again.

To disarm auto-merge:

```bash
gh pr merge <number> --disable-auto
```

## What the merge queue does

`main` does not require a pull-request branch to be manually rebased onto the latest base. Once the
pull-request head passes its App-bound readiness check, GitHub composes a temporary
`gh-readonly-queue/main/...` candidate from current `main`, the pull request, and any entries ahead of it.
The complete Product gate runs on that exact composed SHA. A protected-default-branch verifier independently
reads the completed run and publishes the App-bound `Trusted merge-queue binding` check only when every
authoritative gate succeeded and the candidate did not replace trusted CI/authority machinery.

Do **not** rebase merely because `main` advanced. That discards valid pull-request evidence and restarts the
pre-queue gate. Check live state before touching the branch:

```bash
gh pr view <number> --json mergeStateStatus --jq .mergeStateStatus
```

`BEHIND` is expected and the queue resolves it. `BLOCKED` normally means readiness evidence or another rule
has not passed yet. Rebase only to resolve a real merge conflict or another explicit queue failure that cannot
be regenerated against current `main`. If something has not landed when expected, inspect its pull-request
and merge-group runs rather than assuming it is still building.

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

## Merge-queue trust boundary

The repository moved to the `AeroLinkDEV` organization and issue
[#549](https://github.com/AeroLinkDEV/requirements-management-tool/issues/549) activated the queue. The queue
supersedes strict "require branches to be up to date" protection: the pull-request branch may be behind, but
the exact composed candidate cannot merge without passing the App-bound authority check.

The required check is pinned to the dedicated AeroLink Merge Authority App, not accepted by name from any
publisher. Its private key exists only in the `merge-authority` environment, whose deployment policy admits
`main` only. Pull-request readiness and merge-group binding run from protected default-branch definitions;
candidate code cannot mint that check. Changes to `.github/`, `product/test-planner/`, or
`product/ci-metrics/` deliberately refuse automatic queue binding and require an explicitly reviewed
authority-maintenance cutover.

## Related

- [Feedback time](BROWSER_AND_BACKEND_FEEDBACK_TIME.md) — **read before changing CI.** Where a pull request's
  wall clock actually goes, measured, and why shard counts are not the lever.
