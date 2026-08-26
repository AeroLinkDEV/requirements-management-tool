# AeroLink current product handoff — 10 August 2026

This is the current restart point after the August procedure-control remediation and close-out sequence. It supersedes the 9 August handoff **as current operating guidance**. Older dated handoffs remain historical records and must not be rewritten to look current.

A dated SHA in this file is an audited checkpoint, not a permanent claim about the live branch. Always refresh GitHub before starting work. GitHub `main` is the source of truth.

## Audited clean product checkpoint

Immediately before this documentation-only closeout:

- repository: `seanmccarthyns/requirements-management-tool`;
- protected branch: `main`;
- audited product checkpoint: `af8760a6ad17b6266a770fb8c0beb2b67eaf3c90`;
- checkpoint commit: **Complete controlled procedure stale-selection handling (#367)**;
- required branch check: **Report what this run validated**;
- open pull requests: **0** after superseded draft PR #443 was closed;
- current open product issues: **#332 only** — controlled materialization/qualification of an imported legacy baseline.

The documentation closeout itself advances `main` without changing product behavior. Do not treat the product checkpoint above as a promise that live `main` will never move.

## Latest completed remediation sequence

| Issue | Merged PR | Result | Squash commit |
| --- | --- | --- | --- |
| #442 | #444 | Procedure-title search now uses the authoritative exact revision-title projection | `abb72eb672a097e8a1ed19de035bee6f4d88a7d3` |
| #365 | #438 | Superseded TCR history/current-work presentation and exact successor navigation completed | `a2bda231ba30b9cceca04f25a29360f1e3dbfc77` |
| #364 | #440 | Explicit legacy procedure-manifest bootstrap and exact predecessor carry-forward completed | `c0b020b4df6e0fb9d9daa72c3c02fd40fea132b9` |
| #367 | #445 | Controlled Modify/Retire target selection, lifecycle state, server validation, and stale-selection conflict handling completed | `af8760a6ad17b6266a770fb8c0beb2b67eaf3c90` |

PR #443 was an older draft for #442. It was not merged; #444 was the qualified replacement. #443 is closed as superseded so the repository does not carry an orphan draft that looks like unfinished product work.

## What now works

### Exact procedure titles and search (#442)

Exact procedure-revision views and search now share one authority. List search, universal procedure search, universal execution search, and the Modify/Retire target picker match the same exact title the result displays. A Retire decision preserves the predecessor title as controlled history: discarded/forged retirement title text does not become searchable evidence. Legacy revisions use their deterministic compatibility label rather than pretending today's mutable catalog title was historically recorded. Release/build effectivity remains part of search authority, including execution search.

### Superseded TCR presentation (#365)

Revising approved test work leaves the predecessor as immutable controlled history and makes the successor the active work item. Historical/deep-linked predecessors show their Superseded relationship and route to the exact successor, including cross-release successors and folded automatic assessments. Superseded predecessors are not offered for new baseline selection and remain authoritatively refused if a caller tries to select one directly.

### First exact legacy procedure manifest (#364)

A Configuration Manager now has an explicit operation to establish the first exact procedure manifest for a released predecessor whose controlled procedures predate build-scoped manifests. It is deliberately described as a **legacy migration/configuration snapshot**, not reconstructed historical precision.

The operation previews the exact candidate set and deterministic hash, requires that exact preview at commit, refuses inventory drift without partial writes, is idempotent for the same snapshot, and records actor, time, source rule, count, and hash in `LegacyProcedureManifestBootstrapped`. It preserves existing procedure identity, revisions, authorship, approvals and coverage; it does not infer or rewrite coverage. A genuinely empty legacy inventory can establish an exact empty manifest.

Normal successor materialization no longer treats a missing predecessor manifest as an empty product. It requires the exact predecessor procedure manifest, carries that set forward, and then applies approved selected Introduce/Modify/Retire decisions.

The supported Configuration Management route is `/baselines` (**Candidate Baselines**). The old `/release-planning` route remains retired.

### Controlled Modify/Retire targets and stale selection (#367)

Modify and Retire authoring uses controlled procedure identity and displays canonical number, authoritative revision title, current revision, lifecycle state, and current coverage. Server validation remains authoritative for project, discipline/level, build effectivity and permitted revision progression.

Three stale build/revision conditions are explicit `409 Conflict` responses:

- `procedure_not_carried_by_build`;
- `procedure_manifest_revision_missing`;
- `procedure_revision_not_next_for_build`.

On one of those conflicts the proposal remains open and authored engineering text is preserved, but stale target-dependent identity/coverage state is cleared and the picker reloads. The engineer must explicitly reselect the controlled target. AeroLink does **not** silently remap a stale request to a newer revision. Unknown, cross-project and wrong-level requests remain ordinary validation failures rather than being mislabeled as concurrency conflicts.

## What is still intentionally incomplete or constrained

- **#332 is the only open product issue.** The five-gate imported-baseline workflow and provenance model exist, but Accept does not yet materialize the imported requirements as the complete immutable controlled baseline, exact membership and source-identity relationships required by the issue. Representative extract/parser qualification is also still required before claiming an existing program can be brought in end to end.
- **Legacy procedure history is not fabricated.** The bootstrap establishes one attributable migration snapshot. It does not claim to reconstruct the exact procedure manifest of every historical build.
- **Stale controlled identities are not repaired silently.** Conflicts require refresh/reselection so the user sees and accepts the current controlled record.
- Deployment-owned limitations remain deployment-owned: real SMTP relay qualification, TLS/reverse proxy, protected off-device backup storage, monitoring/alerting, customer RPO/RTO/SLOs, identity-provider contracts, and independent security review are not made complete by repository code alone. See `PROJECT_STATE.md`.
- The published scale claim remains what was actually measured; do not restate database-client evidence as 150 rendered production browser users.
- No certification, compliance, or tool-qualification claim is made.

## Qualification evidence and observations

The final #367 Product Quality Gate ran on exact PR head `65d5c72ab5fc0f24bfd3c898827efc472a3c0726` as Actions run **31436826622** (run sequence 839) and completed successfully: backend/client, production-build browser, both browser shards, and the required aggregate reporter were green. PostgreSQL migration/bootstrap was truthfully skipped by the classifier because #367 had no persistence/migration surface.

The #364 qualification exposed several dormant assumptions and one provider-specific production-browser failure; those were corrected before merge. On its final exact head, one browser shard then hit a `page.goBack()`/drawer timing failure that had passed on the immediately preceding head. Re-running the failed jobs on the **identical commit** passed both shards and the aggregate gate. That is evidence of a test flake, not permission to merge a red head: identical-head evidence was required and the merge waited for a fully green required aggregate.

No persistent engineering database operation was needed for this remediation sequence; validation used disposable test/CI infrastructure where database coverage applied.

## Lessons from this sequence

- **Qualify the exact head you merge.** A green older head or locally reasoned equivalence is not merge authority. Refresh `main`, inspect the exact diff, review the exact head, and require its aggregate gate.
- **Provider-sensitive query logic belongs on the correct side of the SQL boundary.** The newly exposed predecessor-baseline route failed under SQLite when display/order expressions were pushed into translation. Project SQL-safe primitives first; format and order provider-sensitive display values in memory.
- **Test the DOM contract the browser actually exposes.** A native `<option>` can exist and be selectable while Playwright considers it not visible. Use an existence/count assertion for option membership rather than `toBeVisible()`.
- **An abandoned PR is still repository state.** Replacement PR #444 did not make draft #443 disappear. Clean closeout includes closing superseded drafts and stating why they were not merged.
- **Temporary repair mechanics must not become product history.** Branch-only repair/self-deleting workflows are acceptable only as tooling; the reviewed final branch/tree must contain no temporary workflow or script and should be cleaned back to intentional product/test/document changes.
- **A same-head retry can classify a flake; it cannot excuse one.** Only retry the identical commit, preserve the first failure as evidence, and do not merge until the required final aggregate is green.
- **Current documentation is part of the product control surface.** A finished issue left listed as open sends the next engineer or model toward duplicate work. Historical handoffs stay historical; present-tense authority must be reconciled when the product state changes.

## Safe restart sequence

1. Fetch GitHub and confirm the exact live `main` head and protection before branching.
2. Read `PROJECT_STATE.md`, then this handoff.
3. Treat older dated handoffs and audit reports as historical evidence, not current backlog authority.
4. Refresh open issues from GitHub. At this checkpoint #332 is the only open product issue.
5. For the next broad product review, reproduce findings against current `main`, search for duplicates, raise focused issues with acceptance criteria, and let each corrective branch prove its own exact-head gate.
6. Never weaken repository governance or mutate protected persistent engineering data simply to make a test or merge convenient.
