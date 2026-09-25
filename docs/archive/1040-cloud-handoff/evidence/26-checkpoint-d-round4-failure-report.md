(Retained copy of the exact failure report submitted to Astra via Sean on 2026-09-23, per the contract that gate failures are evidence until dispositioned and reported before any rerun.)

ASTRA REVIEW REQUEST — #1040 — CHECKPOINT D — ROUND 4 (EXACT-HEAD FULL-CI EVIDENCE: GATE FAILED — REPORTING BEFORE ANY RERUN)

**Requested decision and next action:**
The authorized readiness-label action was executed exactly as directed and the trusted requester dispatched Full Product validation at the exact head. **The gate FAILED on two leaf jobs unrelated to the candidate** (full attribution below). Per the standing contract I am reporting the failure before rerunning or changing anything. Precise next action requested: authorize ONE re-dispatch of Full Product validation at the SAME head `027c985e` (no code change, no rebase, no branch update — per your own direction), or prescribe another path. No re-dispatch has been attempted. PR #1066 remains draft, out of the queue; the `ready-for-full-ci` label remains in the state your authorization left it.

**Authorization executed verbatim:**
1. Head confirmed still exactly `027c985e82d1d1b764a2f24eac6e6c2ed4ec2c71` (worktree clean) immediately before labeling.
2. `gh pr edit 1066 --repo AeroLinkDEV/requirements-management-tool --add-label ready-for-full-ci` executed (2026-09-23 ~09:54Z).
3. Trusted requester run **35845707428** ("Request full merge validation") dispatched; it ran the Full Product gate as run **35845728143** ("Product quality gate"), `workflow_dispatch`, bound to head `027c985e82d1d1b764a2f24eac6e6c2ed4ec2c71` (verified in each job's checkout log).

**Gate result — 35845728143, started 09:55:00Z, completed ~10:19Z:**
- 15 component jobs succeeded — including **all picker-scoped work**: API test suites 2/3 and 3/3, Browser journeys 1/4–3/4, "Browser journeys on the production build", Domain, Infrastructure, PostgreSQL migrations and secure bootstrap, client lint/build, operator/script contracts, CI metrics tooling.
- **Leaf failure 1 — API test suite (1/3): exactly ONE test failed** — `AeroLink.Api.Tests.ProjectPersonnelApiTests.The_holder_cannot_be_their_own_backup` (6.9s, 09:58:23Z). The client-side error is a login POST returning 500; the server-side log in the same run shows the 500's mechanism directly: `CryptographicException: An error occurred while trying to encrypt the provided data` → `IOException: The process cannot access the file 'C:\Users\runneradmin\AppData\Local\ASP.NET\DataProtection-Keys\key-e2332d6f-….xml' because it is being used by another process` — **two concurrent test hosts on the runner contending over the shared ASP.NET DataProtection key ring** during session/cookie encryption. Failure stack: `SecurityBoundaryTests.AuthorizeMutationsAsync` (line 574) ← `ProjectPersonnelApiTests.SignInAsync`. TRX retained (`ci-full-head-027c985e/api1/shard.trx`).
- **Leaf failure 2 — Browser journeys (4/4): exactly ONE spec failed** — `tests/audit-value-containment.spec.ts:14` ("a long unbroken audit value wraps instead of taking the page sideways"): `expect(getByRole('heading', { name: 'Projects', exact: true })).toBeVisible()` timed out at 15s — **the page was still on the "Establishing your secure session" splash** ("Confirming identity, authority, and active program context…") when the assertion expired, on BOTH the initial attempt and Retry #1. 307 specs passed in the same shard — including **the #1040 picker continuation journey itself (`ok 156 — "the build picker freezes membership across pages and links the exact selected build"`)**. Error contexts + traces retained (`ci-full-head-027c985e/browser4/`).
- "Full Product evidence aggregate" failed as a consequence of those two; the trusted requester run **35845707428 failed at its binding step ("Report what this run validated") and published NO trusted success binding** — correct fail-closed behavior.

**Disposition offered (not asserted as established — offered for your confirmation):** both failures are runner-environment sensitivity, not candidate regressions:
1. **The diff touches neither failing file.** `ProjectPersonnelApiTests.cs` and `audit-value-containment.spec.ts` are byte-identical across baseline `b64301b1`, merge-base `329f0757`, HEAD `027c985e`, and current main `a3f29e9d` (git diff empty on all three comparisons).
2. **Both pass locally at the exact SHA**: the API test passed 1/1 locally (binaries built at `027c985e` by the earlier required-PG-gate run, tree clean since — `personnel-repro.trx`); the browser spec passed 1/1 locally under the same default dev-journeys config (3.5s). Local runs are single-host, so neither the key-ring contention nor the loaded-runner session delay applies.
3. **Main's own gate exhibited the same sensitivity this window**: run 35800767807 at main `1474c945` (2026-09-23 00:09Z) FAILED on a different timing-sensitive API test (`CodeEvidenceAcceptanceRaceTests.Deferred_provider_preflight_rechecks_source_review_and_authority`, 49s, shard 3/3); main's next gate run 35806797664 at `a3f29e9d` (01:33Z) PASSED with **no fix commit for that test in between** (`a3f29e9d` is DEC-134, a decision-record change). Identical test code, green on re-run.
4. In the same failed run, every picker-scoped job passed and the picker journey passed inside the very shard that failed on the unrelated audit spec.
5. The one API failure's mechanism is directly evidenced in the run's log (DataProtection key-file contention), not inferred from convenience.

**Accounting corrections — accepted, applied herewith:**
- `retry-validation.json`'s `dedicatedSha=20242739…` records the dedicated-source identity read back from the INSTALLATION at retry time; `3eafdea3` is the clean dedicated-source CHECKOUT the driver executable ran from. My Round-3 packet's phrase "dedicated source read at 20242739" conflated the two — corrected here: driver from checkout `3eafdea3`; installed-source readback `20242739…`.
- `BB767004…` and `F3BB2735…` are both FINAL hosted receipt hashes — Recovery and Reconcile respectively (not "Preflight vs final" as my packet said).
- Confirmed as disclosed: the authoritative summaries contain the actual ending observations; the wrapper's zero counter does not.

**State:** HEAD `027c985e` (unchanged, clean, pushed); PR #1066 draft, MERGEABLE, auto-merge off, no queue entries, label `ready-for-full-ci` applied (your authorized state); #1040 open; HOME healthy at `a3f29e9d`/Current; nothing on the installation, the persistent database, or the evidence store was touched by any of today's actions.

Please return your verdict on the disposition and the precise next action — I will wait for Sean to relay your actual response before any re-dispatch, queue admission, or merge.

CHECKPOINT D — AWAITING ASTRA REVIEW. Queue admission, merge, rollout and issue closure remain gated.
