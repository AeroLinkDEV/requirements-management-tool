(Retained REVISED annex to packet 42 — supersedes 41b. Prepared at Astra's direction; NOT executed; nothing self-approves. History correction, qualification ordering, rehearsal, temporary-authority restoration, rollback qualification, and owner-decision identification are all addressed. Where a workable route does not exist, the limitation is stated.)

# REVISED TRUST-ROOT TRANSITION PROPOSAL — installation and rollback of the same-head retry change

## 0. Boundary and history correction

Current controls refuse this change through the routine maintenance route (`MAINTENANCE_KERNEL_PATHS` includes `.github/workflows/request-full-ci.yml`; preflight verdict `separate-trust-root-bootstrap-required`). MERGING.md requires a separately reviewed trust-root transition.

**History correction (as directed):** the repository's trust-root bootstrap DID occur once — DEC-121's installation exception applied ONLY to PR #960, which merged on 2026-09-08 and installed the owner-reviewed maintenance binding under an owner-approved exception that **explicitly expired after that installation**. My earlier statement that no bootstrap had ever occurred is withdrawn. The corrected present state is: the trust-root machinery was bootstrapped for #960, that exception is spent, and NO currently valid authorization exists for further kernel-path installations. Any new installation (including this one) requires a NEW reviewed authorization with its own qualification, owner approval, verification, and rollback commitments. This proposal is that reviewed procedure, offered for approval — it does not self-execute, and the ordinary kernel-path refusal remains the operative control until the owner approves a specific installation under it.

## 1. Qualified candidate (prepared; see the ordering rule in §3)

- PR #1098 draft head `23bb25d987639ada3356a7003fa0380ba08e5042` (worktree clean): two files — `.github/workflows/request-full-ci.yml` (accepted same-head retry design: EXHAUSTED/PENDING verbs, common authorization gate, spend-once requester consumption, uncertain-dispatch refusal, boundary-filtered discovery, pinned run+attempt qualification) and its contract test (32 tests; POSIX 32 passed / 0 skipped; Windows 18 passed / 14 disclosed POSIX-only skips / 0 failed).
- I07 workflow correction independently verified by Astra; design provenance: Astra-accepted v5 design + executable prototype + v5.1 fixture qualification.

## 2. ORDERING RULE — qualification precedes installation (F02 correction)

**Successful exact-candidate Full Product evidence is a PRECONDITION of installation, obtained BEFORE installation — never after, and never from the installed state.** The candidate currently has NO green exact-head Full Product run (gate 36064279956 failed on an unrelated browser journey; reported separately), and current main's requester logic makes the failed head unbindable. Installing first to obtain the missing qualification would reverse the required order and is excluded.

Therefore installation is gated on obtaining, by ONE of these owner-chosen routes (each explicitly an owner decision, none authorized by this proposal):
- **Route A — one-shot owner override on the failed check:** the owner (as the §2 bypass authority of the reviewed procedure) dismisses/re-runs the single failed `Browser journeys (1/4)` job on run 36064279956 once; if the re-run passes, the run becomes the green exact-candidate evidence and the head is qualified. If it fails again, the route ends and the failure is dispositioned as a product/journey defect (out of this change's scope, filed separately).
- **Route B — fresh-commit escape:** a new empty-test commit on the PR branch creates a new head; the normal requester path qualifies it (the old head's failed run no longer binds the new SHA). The reviewed diff is unchanged; the qualified SHA becomes the new candidate.
- **Route C — deferral:** installation is deferred until main's own future gate runs demonstrate the journey passing repeatedly, then §2–§5 proceed on the (rebased, re-qualified) candidate.

Only after a GREEN exact-candidate Full Product run exists does §4 (installation) become executable, and only with the owner approvals in §3.

## 3. Owner approvals required (each a genuinely NEW owner decision, explicitly identified)

1. **New temporary bypass authority** — creating a bypass actor entry (owner account only, this repository's main ruleset) is a NEW grant: the #960 exception expired and no active bypass exists. Time-boxed: the approval comment records an expiration timestamp, and §4.5 requires verified REMOVAL of the actor entry (not merely expiry) before the installation is declared complete. Temporary by construction; restoration is verified, not assumed.
2. **Admin merge of a kernel-path PR** — merging PR #1098 into protected main requires admin override of the failed/missing checks. This is the single live installation action.
3. **Acceptance of the qualification route** — Route A, B, or C above (each has different evidence semantics; the owner chooses).
4. **Authorization of the isolated rehearsal** (§5) as a valid substitute environment for the installation mechanics.
Each decision is recorded in the owner's approval comment with: procedure name, candidate SHA, this proposal's digest, route choice, and the restoration commitment.

## 4. Installation (single live action, only after §1–§3)

1. Merge PR #1098 at the qualified SHA into protected main via the ordinary merge interface under the §3 temporary bypass actor. The merge commit message records the procedure name, candidate digest, and route.
2. No other repository, ruleset, environment, App, secret, runner, or product change is made in the same action. Any additional detected change aborts §4 and triggers §6.
3. Immediately proceed to §5 verification. Failure of any item → §6 restoration.

## 5. Post-installation verification (all must hold; performed immediately)

1. Tree proof: pre-installation main SHA recorded; `git diff <pre-merge-main>..main` matches EXACTLY the reviewed two-file diff (byte-for-byte via `git diff --stat` + `git diff` comparison against the retained diff); no other path changed.
2. Required checks unchanged: `Full Product evidence aggregate` still required for PRs; main's required set identical (recorded before, compared after).
3. Publishers unchanged: `Trusted merge-queue binding` still published only through the Merge Authority App token path; `aerolink-product-evidence:pull-request` still absent.
4. Secret boundary unchanged: no new secrets; `merge-authority` environment and `MERGE_AUTHORITY_APP_PRIVATE_KEY` references unchanged; maintenance-evidence App untouched.
5. Ruleset restoration PROOF: the §3.1 bypass actor entry is REMOVED (checked via the ruleset API read, not assumed from expiry) and all other ruleset fields are identical to the pre-installation snapshot.
6. Contract suite on main: the deployed workflow's extracted fragments pass the committed contract suite (32-test file) on main.
Any failure → §6 immediately.

## 6. Rollback (independently qualified BEFORE installation is attempted)

1. The rollback is an exact revert of the two-file diff, prepared and PUSHED as a separate branch BEFORE installation, with its own qualification: contract suite green on the revert head, and (if achievable by the same §3 route) a green exact-head Full Product run on the revert PR. The revert's approval is a separate owner action; no approval reuse from the installation.
2. Execution: merge the revert through the same reviewed path; then re-run §5 items 1–6 against the reverted main ( publishers/protections/secret-boundary unchanged; contract tests on main match the pre-change set).
3. Interrupted/uncertain-operation handling: if the installation or restoration is interrupted with unknown state (e.g., merge applied but verification not run), the state is treated as UNKNOWN — no further changes until the tree is inspected and either §4 verification is completed or the §6 revert is executed and verified. Uncertainty is never resolved by assumption.
4. All evidence (pre-merge snapshot, merge commit, verification outputs, revert) is retained in the review evidence directory.

## 7. First authorized exercise (only after Astra's separate go following successful §4–§5)

One readiness-label event on PR #1066 (head `027c985e…`, the unbindable head): the deployed requester dispatches one exact-head Full Product run; success binds and completes Checkpoint D's qualification; failure is reported before any further attempt.

## 8. What this proposal does NOT do

No modification of ci.yml, fast-feedback, the reset workflow, any required check, any environment, any App, any secret, any runner, or any product code. The temporary bypass in §3.1 is the ONLY new authority, is limited to one owner actor on one repository's main ruleset, is removed and verified removed in §5.5, and its creation/expiry/restoration are all recorded. No #1040 closure, PR #1066 merge, queue admission, or HOME action is authorized or performed. The persistent Aerolink database and evidence store are untouched.
