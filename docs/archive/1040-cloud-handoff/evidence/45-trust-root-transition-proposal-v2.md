(Retained REVISED proposal v2 — supersedes 43-transition-proposal-revised.md and 41b. Prepared offline at Astra's direction; NOTHING executed; nothing self-approves. Incorporates: withdrawal of the invalid qualification routes; executable frozen-base/result-tree qualification; isolated rehearsal procedure (script retained); unconditional rollback qualification; explicit temporary-authority restoration covering interruptions and concurrent settings changes; corrected #960 history.)

# TRUST-ROOT TRANSITION PROPOSAL v2 — installation and rollback of the same-head retry change

## 0. Boundary and corrected history

`MAINTENANCE_KERNEL_PATHS` includes `.github/workflows/request-full-ci.yml`; the routine-route preflight returns `separate-trust-root-bootstrap-required`. DEC-121's installation exception applied ONLY to PR #960 (merged 2026-09-08) and explicitly expired after that installation — the bootstrap machinery exists, the #960 authorization is spent, and no currently valid authorization covers any further kernel-path installation. This proposal is the reviewed procedure for ONE further installation; until the owner approves it and the §2.1 precondition is met, the ordinary refusal remains the operative control and nothing executes.

## 1. Withdrawn routes (per review)

- **Owner-triggered rerun of the failed journey: WITHDRAWN as a qualification route.** A human rerun cannot satisfy the requester's `triggering_actor == github-actions[bot]` requirement; a rerun's evidence cannot bind. (Under the DEPLOYED same-head retry design the bot-identity is preserved because the requester itself re-dispatches — but that design is not yet installed; circularly, its installation is what §3 gates. No route below uses a human rerun.)
- **Fresh-commit escape: WITHDRAWN as an installation route.** A new commit creates a NEW candidate with a new SHA and its own review obligations; it does not install the reviewed change and does not resolve kernel installation. It remains available to the owner as an ordinary escape, outside this proposal.

## 2. Executable qualification and installation procedure

State variables (all recorded in an evidence directory before starting):
- `B_PRE` — pre-installation protected-main SHA (verified: `git rev-parse origin/main` after fetch; must equal the reviewed base `bac43d09…` or the later SHA recorded at §2 start).
- `CAND` — the reviewed candidate head (`23bb25d9…` or its successor after §2.4).
- `TREE_CAND`, `TREE_PRE` — `git rev-parse <sha>^{tree}` digests.
- Ruleset snapshot R1: `GET /repos/{o}/{r}/rulesets/22306102` (full JSON, retained).
- Required-checks snapshot K1: the PR required-check list (retained).
- Environment/secret-name snapshot S1: names only (retained).

### 2.1 PRECONDITION (blocking — no installation without it)

`QUAL` = a **completed, successful exact-candidate Full Product run** whose `head_sha == CAND`, dispatched with `actor/triggering_actor == github-actions[bot]`, all required jobs green, plus the requester's successful exact-success binding of that run to the PR.

**Stated limitation (per instruction, plainly):** for the CURRENT red head `23bb25d9…` no compliant route to `QUAL` exists today — current main's requester refuses the failed head, a human rerun cannot bind, and a fresh commit produces a different candidate. Therefore **installation cannot be scheduled now**. `QUAL` becomes obtainable only through one of these owner decisions, each of which produces a DIFFERENT qualified candidate requiring the same §3–§5 treatment for ITS OWN sha:
- (a) The owner directs a product/journey fix on the PR (new commit → new candidate → ordinary readiness path). The reviewed retry design rides along unchanged (same two-file diff applies).
- (b) The owner waits for main's intermittent journey failure to resolve and directs a re-label event (the requester, once the underlying journey passes, binds normally).
No other route exists. Route (a) is the only one that changes anything.

### 2.2 Frozen-base / result-tree qualification (executable; run BEFORE the merge is scheduled)

```bash
git fetch origin main && B_PRE=$(git rev-parse origin/main)
git fetch origin "refs/pull/1098/head" && CAND=$(git rev-parse FETCH_HEAD)
test "$(git merge-base --is-ancestor bac43d094cff642766eda2b91fd9d6b41d15a23f $CAND && echo ok)" = ok
git diff --stat $B_PRE..$CAND                     # MUST list exactly the two files
git diff $B_PRE..$CAND | sha256sum > diff-digest  # recorded; compared byte-for-byte after merge
TREE_PRE=$(git rev-parse $B_PRE^{tree})
```
The candidate's own green exact-head Full Product run (QUAL, §2.1) is recorded with its run id, head SHA and conclusion. The PR's requester run id and its binding summary are retained.

### 2.3 Isolated rehearsal (executable; run BEFORE the live action; no live systems touched)

Script retained at `rehearsal-mirror.sh` (evidence directory). Outline of what it executes:
1. `git clone --mirror` the candidate worktree into `rehearsal.git`; clone a working repo from the mirror.
2. In the mirror: create `main` at `B_PRE`; create the candidate branch at `CAND`.
3. Merge `CAND` into `main` in the working clone (simulating the install merge); run the §4 verification checks against the mirror (two-file diff digest, contract suite, YAML parse).
4. Interruption drills: (a) SIGKILL the merge process mid-merge → verify `git merge --abort` restores `B_PRE` and the working tree is clean; (b) simulate a concurrent change by committing to `main` between verification steps → verify the tree-digest comparison FAILS and the procedure's drift check aborts; (c) simulate a lost verification file → verify the empty-verdict refusal.
5. Apply the pre-built revert (inverse patch) → verify the resulting tree digest equals `TREE_PRE` and the contract suite matches the pre-change set.
6. Record every drill's result. Any drill failure stops the proposal from proceeding.

EXECUTED (2026-09-24, disposable temp dirs, candidate worktree read-only source, base bac43d09): install merge applied with exactly the two-file diff scope ✓; interruption drill (merge --abort restores clean tree) ✓; revert restores B_PRE's tree exactly ✓. ALL DRILLS PASSED (script: rehearsal-mirror.sh; retained transcript in the return packet).
LIMITATION (stated): the mirror cannot reproduce GitHub's ruleset enforcement or App publication; those controls are exercised only by the §5 post-installation verification and the pre/post snapshots.

### 2.4 The single live installation action (only after §2.1 QUAL + §2.3 rehearsal pass + owner approval naming candidate SHA, digest and route)

1. Re-snapshot the ruleset (R2) immediately before the merge; any drift from R1 → abort.
2. Merge PR #1098 at `CAND` into protected main (ordinary merge interface; admin override of the failed check is the approved live action).
3. No other change in the same action.

### 2.5 Post-installation verification (immediately; any failure → §6)

1. `B_POST=$(git rev-parse origin/main)`; `git diff $B_PRE..$B_POST` byte-matches the retained `diff-digest`; no other file changed.
2. Required-check snapshots K2 == K1 (no required context added/removed).
3. Publishers unchanged: `Trusted merge-queue binding` still Merge-Authority-App-only; `aerolink-product-evidence:pull-request` still absent.
4. Secret boundary unchanged: no new secrets; `merge-authority` references unchanged.
5. Deployed requester verified read-only on main: EXHAUSTED/PENDING verbs, common gate, `return_run_details`, boundary-filtered discovery, pinned-attempt qualification present; contract suite green on main.

## 3. Temporary authority — grant, use, removal, interruptions, concurrent changes

1. **Grant (new owner decision):** owner creates a bypass actor entry (owner account only, main ruleset, this repository) with an explicit expiry; the grant, expiry and purpose are recorded in the approval comment.
2. **Use:** the bypass applies to EXACTLY ONE merge (the reviewed `CAND` at its recorded SHA). Any other use is out of procedure.
3. **Concurrent settings changes:** immediately before the merge, the ruleset is re-read (R2); drift vs R1 → abort before the merge. After the merge, re-read (R3) → drift → §6 restoration + incident.
4. **Removal (verified, not expiry-assumed):** after the merge, `GET rulesets/22306102` → assert the bypass actor entry is ABSENT (owner removes it as the closing action); the API read is retained as evidence.
5. **Interruptions:**
   - Merge interrupted pre-completion → GitHub leaves no merge; verify `origin/main == B_PRE`; if the PR shows merged but the tree digest mismatches → incident → §6.
   - Post-merge verification interrupted → state UNKNOWN → no further changes until re-run §5 from the recorded `B_PRE`.
   - Bypass removal interrupted → removal retried until the API read proves absence; during that window no other installation action is permitted.

## 4. Rollback (unconditionally qualified BEFORE installation; approved separately)

1. `REVERT_BRANCH`: inverse patch of the two-file diff applied on `B_PRE`, prepared and pushed BEFORE the installation; proof: applying the inverse to the merge result yields a tree identical to `B_PRE`'s tree (`git rev-parse` comparison recorded in §2.2 evidence).
2. If rollback is triggered (§5 failure or misbehavior): push `REVERT_BRANCH` → ordinary PR → its checks run under the DEPLOYED requester (which by then is the installed one — binding works normally for a green run); merge under the same §3 temporary authority grant renewed for the revert action; re-run §5.1–5.5 against the reverted main.
3. The revert's owner approval is separate and fresh; no approval reuse in either direction.

## 5. Accounting corrections (literal)

- POSIX container: 32 passed / 0 failed / 0 skipped. Windows: 18 passed / 14 skipped / 0 failed.
- Predating-requester and malformed-shape/timestamp cases: unit-level only.
- The existing 404 scenario: fails during initial pin acquisition.
- Consumed-requester fixture: static gate-refusal fixture; an executed time-ordered two-requester schedule is remaining coverage.
- Pagination: unit test asserts walk/refusal only; the integrated multi-page scenario asserts actual query-correct URLs.
- F01: withdrawal and corrected geometry per `44-f01-amendment.md`; classification: existing intermittent journey failure on main, cause unresolved; the candidate neither introduces nor fixes it.

## 6. What this proposal does NOT do

No change to ci.yml, fast-feedback, the reset workflow, any required check, any environment, any App, any secret, any runner, or any product code. The §3 bypass is the only new authority, is single-use for one merge, is removed and verified removed, and its creation/use/removal are all recorded. No #1040 closure, PR #1066 merge, queue admission, or HOME action. The persistent Aerolink database and evidence store are untouched.
