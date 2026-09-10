# Merging into `main`

**Read before merging anything.** `main` is governed by the active AeroLink merge queue. Pull-request heads
must pass the trusted readiness flow before they can enter the queue, and GitHub then validates the exact
composed candidate before it can land. Do not restore the former strict-up-to-date treadmill alongside the
queue.

## Request Full CI only when the current SHA is merge-ready

Every pull-request SHA receives **advisory Fast feedback automatically**. Fast is for development feedback; it
is not merge authority and cannot satisfy branch protection.

When implementation and review are finished and the **current SHA is the one you intend to merge**, request
the existing Full Product gate by applying the repository label:

```bash
gh pr edit <number> --add-label ready-for-full-ci
```

The trusted default-branch requester authenticates the live pull request, exact head SHA, head ref,
same-repository origin and readiness label before dispatching Full validation. The event-captured base SHA is
the immutable comparison input for that run; `main` may advance without invalidating the unchanged head. Product's internal
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

The readiness publisher checks the complete current queue before dispatch and again before publishing. It
refuses a PR head that equals any current composed candidate, including another PR's candidate. Both paths
use the same required App context, so a second PR must not provide readiness for an existing queue commit.
An incomplete queue response also refuses. Historical queue membership alone does not permanently disqualify
a reopened head; current membership and the normal in-progress invalidator provide the boundary.

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
An initial run or rerun first replaces any earlier App success with an in-progress check, so old evidence
cannot remain authoritative while a newer Product attempt is active.
The ruleset also requires GitHub Actions' native `Full Product evidence aggregate`. GitHub moves that check
out of success as soon as a rerun is queued, covering the interval before `workflow_run` emits `in_progress`.
The native gate and the App binding are a required pair; neither is merge authority alone.

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

The repository moved to the `AeroLinkDEV` organization, and issue
[#549](https://github.com/AeroLinkDEV/requirements-management-tool/issues/549) / PR #911 established the
queue's repository-side authority. On 2026-09-04 the repository-scoped App was installed, its key was placed
in the main-only environment, and the active `main` ruleset replaced classic strict "require branches to be
up to date" status enforcement. The pull-request branch may be behind, but the exact composed candidate
cannot merge without passing both required checks.

The App-bound required check is pinned to the dedicated AeroLink Merge Authority App, not accepted by name
from any publisher. The co-required `Full Product evidence aggregate` is pinned to GitHub Actions. The App's
private key exists only in the `merge-authority` environment, whose deployment policy admits
`main` only. Pull-request readiness and merge-group binding run from protected default-branch definitions;
candidate code cannot mint that check. Changes to `.github/`, `product/test-planner/`, or
`product/ci-metrics/` deliberately refuse automatic queue binding and require an explicitly reviewed
authority-maintenance cutover.

## Prepare a maintenance review packet

The read-only preflight collects current GitHub PR, queue, run, native check publisher, complete protected
Git trees, ruleset and credential-environment metadata. Run it from a reviewed checkout, with an output path
outside `product/.local`:

```text
node product/ci-metrics/bin/prepare-authority-maintenance.mjs <pr-number> <product-run-id> <new-output.json>
node --test product/ci-metrics/tests/maintenance-preflight.test.mjs
```

The output includes the clean preparer commit/tree and a digest of that identity, the evidence and all
existing verifier refusals. The preparer's identity is checked again after collection. `REVIEW_REQUIRED` means
only that the packet can be reviewed; it cannot authorize an App check or a merge. `REFUSE` retains the
missing, failed, stale or mismatched evidence. A diagnostic run, an obsolete queue candidate or a PR-head
green check cannot replace the current composed candidate. Truncated responses and changes observed during
collection fail closed. The command does not execute candidate code, read candidate artifacts, approve an
environment, retrieve the App key or alter repository settings. Existing output files cannot be overwritten.

## Owner-reviewed maintenance binding

The binding workflow contains an opt-in maintenance path governed by
[DEC-121](../../DECISIONS_AND_OPEN_QUESTIONS.md#dec-121---protected-ci-maintenance-requires-exact-owner-approval-and-a-qualified-initial-installation).
Its initial activation is a separate trust-root
transition: the path cannot authorize installation or replacement of its own approval kernel. Activation
requires an explicitly reviewed procedure, exact candidate qualification, owner approval of live actions and
restoration steps, and verification of both unchanged required publishers and the main-only secret boundary.
An unconfigured environment is a refusal. Adding this code alone does not activate an approval environment.

After that activation, request maintenance with the `authority-maintenance-requested` PR label and ordinary
Full readiness. The label requests review; it grants no authority. When the PR is first in the queue and its
complete native Product proof succeeds, the protected-main binder compares the full protected Git trees.
Only the ordinary verifier's protected-surface refusal may proceed to review. Missing/failed jobs, a changed
approval kernel, a different publisher or an obsolete composition still refuse.

The binder writes the exact PR head, composed commit/tree, protected diff, main/preparer identity and Product
run/attempt into its run summary. It leaves the App check pending while the separate
`merge-authority-maintenance` environment waits for the owner. This environment must have exactly one required
reviewer, GitHub account `seanmccarthyns` (ID `295123958`), a branch-only `main` deployment policy and administrator
bypass disabled. It receives no App secret. Prevention of self-review is explicitly disabled because the owner
may also be the workflow initiator; this is a deliberate owner approval, not a claim of independent review.

Read the linked exact candidate diff and native evidence before approving **Review deployments**. The approval
comment must be exactly `APPROVE MAINTENANCE <digest>` using the summary's 64-character digest. An ordinary PR
comment, label, different reviewer, absent approval, rejected approval or mixed/duplicate approval history is
insufficient. Under the owner's standing delegation in
[DEC-124](../../DECISIONS_AND_OPEN_QUESTIONS.md#dec-124---standing-owner-delegation-for-ci-maintenance-approvals),
Codex may submit each exact approval on the owner's behalf after the required review and native qualification,
without asking the owner to confirm each digest again. Verify the current hosted packet and record the delegated
action; do not describe it as an independent human review. The delegation remains effective until revoked or
narrowed by the owner. The approval job executes no repository code and has no token permissions.

The final publisher runs from the same protected-main workflow SHA in the existing `merge-authority`
environment. It reads GitHub's authenticated approval history for this binding workflow and recollects live
PR/queue, native run/jobs/check publisher, Git trees, ruleset and both environment policies. A changed digest
refuses publication. Both the live Product attempt and binding workflow identity/status are checked once more
immediately before publishing. These reads cannot make the GitHub read-and-publish boundary atomic. The normal
in-progress invalidator and paired native check remain required throughout the wait.

GitHub approval history does not carry an attempt identifier. Maintenance therefore refuses reruns of the
binding workflow itself. If a run or candidate becomes stale, obtain a new Product completion and fresh binding
workflow/review; do not reuse a prior GitHub approval. A Product partial rerun may retain successful native jobs
through `filter=latest`, but its new attempt still needs a fresh digest and GitHub approval, which Codex may submit
under the standing delegation after renewed qualification. The existing queue timeout
continues to apply; approval does not extend it.

Any change to the runtime maintenance/merge-authority modules or the protected binding/readiness workflows is
outside this routine path. Use a separately reviewed trust-root transition; never remove a required check or
publish a fabricated success to make a refused maintenance PR merge. Rollback also requires reviewed exact
revert evidence; an earlier candidate's GitHub approval cannot be reused for a later revert. Standing delegation
does not waive technical refusals, required checks or the separately reviewed transition for kernel changes.

## Permanent maintenance evidence reader (#982 preparation)

Routine owner-reviewed maintenance uses a separate repository-scoped evidence App. Its installation is selected
to this repository only and its GitHub permission is **Administration: write** with the implicit Metadata read
permission; it receives no Checks or Contents permission. GitHub requires that Administration capability for a
ruleset read that includes the authoritative `bypass_actors` field. The capability is therefore physically
privileged even though the checked-in client exposes only one operation: an authenticated GET of the exact
`/repos/AeroLinkDEV/requirements-management-tool/rulesets/22306102` path. The protected action configuration pins
the App slug/client ID, expected App and installation IDs, owner, repository, and requested Administration-only
permission; an owner/JWT setup audit verifies the selected installation and exact permission grant. The
installation-token client then verifies the exact one-repository scope through the supported
`/installation/repositories?per_page=100` endpoint before that ruleset read. An installation token cannot call the
JWT-only installation-detail endpoint, so the client does not pretend to prove its own App ID or permission grant.
It rejects every other method, path, query, body or redirect.

The evidence App is separate from the existing Merge Authority App and its names are deliberate:
`MAINTENANCE_EVIDENCE_APP_CLIENT_ID`, `MAINTENANCE_EVIDENCE_APP_ID`,
`MAINTENANCE_EVIDENCE_INSTALLATION_ID`, `MAINTENANCE_EVIDENCE_APP_SLUG`, and
`MAINTENANCE_EVIDENCE_APP_PRIVATE_KEY`. The private key belongs
only in the existing main-only `merge-authority` environment. Every other GitHub read remains on the ordinary
read-only workflow token, and publishing the `Trusted merge-queue binding` check remains on the existing Merge
Authority token. The owner-review job has no token permissions or secret access.

The binding workflow first uses protected-main code to establish an exact completed queue candidate: the
ordinary verifier must refuse only the opted-in protected-surface change, the PR must carry the maintenance
request label, and no kernel path may change. Only that trusted step output can mint the evidence token. Ordinary
polling and ordinary merge-group candidates never mint it. After the owner review wait, the publisher repeats the
same current-candidate detector before minting a fresh evidence token. Missing configuration, unverified installation
identity/scope, an omitted or nonempty ruleset bypass list, or any changed native evidence remains a refusal.
The privileged response stays in process and is never written to a step summary, output, artifact, or check
message.

Creating/configuring the App, placing its key and immutable IDs in the main-only environment, and any one-time
trust-root bootstrap are separate operator actions. This implementation does not create an App, read a key,
change settings, install an App, or authorize its own rollout. The new reader and detector are kernel paths and
cannot self-authorize through the routine maintenance path.

## Related

- [Feedback time](BROWSER_AND_BACKEND_FEEDBACK_TIME.md) — **read before changing CI.** Where a pull request's
  wall clock actually goes, measured, and why shard counts are not the lever.
