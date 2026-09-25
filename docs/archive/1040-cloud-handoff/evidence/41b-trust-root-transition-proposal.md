(Retained draft annex to packet 41 — prepared at Astra's authorization, NOT executed, NOT self-approving. Installation of any protected-workflow change requires the separately reviewed trust-root transition per DEC-121/MERGING.md; this proposal is the concrete form offered for that review.)

# TRUST-ROOT TRANSITION PROPOSAL — installation and rollback of the same-head retry change (PR #1098)

## 0. Boundary statement

Current controls refuse this change through the routine maintenance route (`MAINTENANCE_KERNEL_PATHS` includes `.github/workflows/request-full-ci.yml`; preflight verdict `separate-trust-root-bootstrap-required`). MERGING.md requires a "separately reviewed trust-root transition" with: explicitly reviewed procedure, exact candidate qualification, owner approval of live actions and restoration steps, and verification of unchanged required publishers and the main-only secret boundary. No trust-root bootstrap has ever been performed on this repository; therefore NOTHING below executes until the owner approves this proposal as that reviewed procedure AND performs the owner-side bootstrap actions it names. Every step that would change repository state is explicit; nothing self-approves.

## 1. Qualified candidate (already prepared, pending your review)

- PR #1098 draft head `23bb25d987639ada3356a7003fa0380ba08e5042` (worktree `RMT-1040-ci-worktree`, clean): two files — `.github/workflows/request-full-ci.yml` (same-head retry: EXHAUSTED/PENDING verbs, common authorization gate, spend-once requester-run consumption, uncertain-dispatch refusal, boundary-filtered discovery, pinned run+attempt qualification via `/attempts/<n>/jobs`) and its contract test (32 tests; 32 pass / 14 skip / 0 fail on POSIX per environment, Windows 18/14/0).
- Design provenance: Astra-accepted v5 design + executable prototype (23/23) + v5.1 fixture qualification (32/32 POSIX).

## 2. Owner-side bootstrap actions (DEC-121 "reviewed procedure", performed once by the owner, before ANY installation)

1. Owner creates the protected-branch ruleset exception ONCE for this specific reviewed procedure: a bypass actor entry naming ONLY the owner account, scoped to this repository's main ruleset, with an expiration date recorded in this proposal's approval comment. (No other ruleset change; bypass list remains otherwise empty; the change is recorded and reversible by the same owner action.)
2. Owner records in the approval comment: procedure name (`trust-root-transition-request-full-ci-same-head-retry`), qualified candidate SHA, this proposal's retained file digest, and the restoration commit reference from §5.
3. Owner performs no secret, App, environment, or permission change of any kind. The main-only `merge-authority` environment, the maintenance-evidence App, and every required publisher remain untouched (verified in §4).

If the owner declines these bootstrap actions, installation does not occur and this proposal ends; the repository continues under current controls (the head stays unbindable; a fresh-commit escape remains available to the owner at any time through ordinary review).

## 3. Installation (single live action, executed by the owner or by delegated automation only AFTER owner approval in §2)

1. The qualified PR #1098 branch is updated from the reviewed head (force-push prohibited; the reviewed SHA must match) and the PR is marked ready-for-review.
2. The PR is merged into protected main by the owner via the ordinary merge interface (admin merge permitted ONLY under the §2 bypass actor and ONLY for this reviewed SHA; the merge commit message records the procedure name and digest).
3. Immediately after merge, the owner runs the verification in §4. If any check fails, §5 rollback executes.

## 4. Post-installation verification (all must hold within the first hour)

1. `git log main` shows exactly the reviewed merge; `git diff <pre-merge main>..main -- .github/workflows/request-full-ci.yml product/test-planner/tests/full-ci-readiness-dispatch.test.mjs` matches the reviewed diff; NO other file changed.
2. Required checks unchanged: `Full Product evidence aggregate` still required on PRs; main's checks unchanged; no new or removed required context.
3. Publishers unchanged: the `Trusted merge-queue binding` check is still published only via the Merge Authority App token path; `aerolink-product-evidence:pull-request` remains absent from the workflow.
4. Secret boundary unchanged: `secrets.MERGE_AUTHORITY_APP_PRIVATE_KEY` references unchanged; no new secret introduced; `merge-authority` environment policy untouched.
5. Ruleset: bypass list contains only the §2 owner exception; all other protections identical.
6. The candidate requester's behavior verified read-only: `request-full-ci.yml` on main now contains the EXHAUSTED/PENDING verbs, the common gate, `return_run_details`, boundary-filtered discovery, and pinned-attempt qualification (contract suite on main passes).

## 5. Restoration (rollback)

1. Trigger: any §4 check fails, or the deployed retry path misbehaves on its first authorized exercise.
2. Action: exact revert of the merge commit (single revert commit, no fix-forward), pushed through the same §2 bypass with a fresh owner approval naming the revert SHA; verified by re-running §4 items 2–5 against the reverted main.
3. The reverted state equals the pre-installation main byte-for-byte for the two files; contract tests on main return to the pre-change set.

## 6. First authorized exercise (after Astra's separate go)

One readiness-label event on PR #1066 (head `027c985e...`, the unbindable head this change exists to recover): the deployed requester dispatches one exact-head Full Product run; on success it binds; on failure the head refuses again and the failure is reported before any further attempt. #1040's Checkpoint D qualification completes on that evidence.

## 7. What this proposal does NOT do

It does not modify ci.yml, the fast-feedback workflow, the reset workflow, any required check, any branch-protection rule beyond the §2 bypass exception, any environment, any App, any secret, any runner, or any product code. It does not authorize #1040 closure, PR #1066 merge, queue admission, or HOME action. It does not touch the persistent Aerolink database or evidence store.
