# AeroLink #1045 HOME acceptance - 2026-09-16

Operator: Codex/Astra, acting on the owner's explicit request to exercise AeroLink and close #1045 if acceptance succeeds. Account: AeroLink administrator. Credentials are excluded from this record.

## Revision and delivery

- Live loopback `/health/identity`: `b243a8bd998e472046fd67b150a7f46fd2a2a814`, HOME-PRODUCTION, HOME CANONICAL, HomeCanonical.
- This is the protected merge of PR #1056. Its reviewed head was `77fa9ee118d4cdab360fa38fab58a60e6312d418`; reviewed and merged trees are identical (`93807e3335111e7b0b5d8709c6136c9db8a2dabe`).
- Prior original implementation: PR #1049. Follow-up System wording and unfinished-setup discard: PR #1056.
- Exact-head hosted Full: run 35021465723. Trusted readiness: run 35021443080. Protected merge-group candidate: run 35027222480. These are retained qualification, not tests re-executed during HOME acceptance.
- No deployment, upgrade, restart, database maintenance, SQL, reset, reseed, or direct evidence-store mutation was performed in this session.

## Existing owner outcome - observed, not recreated

The recorded setup `e369b93f-5063-4ff6-84c2-dfd61719d0f3` already displays **Project created**. Its recorded last-save time is September 15, 2026, 1:45:59 PM. Consequently this session did not replay the owner's earlier repair or finalization, and does not claim fresh before/after repair evidence for that draft.

Verified through the live UI:

- Next Gen GPS / GPS 2.0 is present in authorized Projects discovery.
- Project `b93f52b6-cdb4-41b9-bda6-bf079fedc74f` has exactly one build: `0.01`, official name `SW-00.01`, **In Work**.
- Opening the project presents the Software Builds selector. Explicitly selecting SW-00.01 opens the working project context successfully.
- Effective configuration is active version 2: System verification disabled; High-Level and Low-Level software verification enabled with Case + Procedure. The configuration page reports Saved and activation readiness Ready. No configuration edit/save was performed.
- The unrelated existing **Untitled Project** saved setup was left unchanged.

## Live test setup - ordinary toggle, persistence and readiness

Created only one clearly named unfinished setup through the supported UI:

- Name: **Acceptance 1045 - 2026-09-16**
- Product: **Disposable setup validation**
- Draft: `318e6333-57c1-43f4-98f8-9c25f901be6b`
- No new active project or build was created.

Observed outcomes:

1. Turning System verification off shows the concise neutral **System test procedures: Off - Unsaved change** with Last saved: On. There is no separate System profile selector or software restoration explanation. High-Level and Low-Level remain Case + Procedure.
2. Turning it back on restores On, then turning it off again and saving preserves Off after reload. The software profiles remain unchanged.
3. Applying the appropriate review rules and explicitly accepting them leads to the ready final summary. The summary shows System disabled with no enabled verification artifacts and both software levels Case + Procedure.
4. Changing only High-Level to Case-only clears prior review acceptance. Before renewing it, Review and finish explains the mismatch and **Create Project is disabled**.
5. Updating the applicable rules and renewing acceptance restores readiness: System Off, High-Level Case-only, Low-Level Case + Procedure. This confirms System and software procedures are independent.
6. Save and exit preserves the test setup. The named discard confirmation identifies this setup and explains its scope. Cancel followed by reload leaves it present and unchanged.

## Final discard acceptance - completed after explicit owner approval

The owner replied **"I approve"** to the named discard confirmation. Only the agent-created setup above was discarded.

- The Projects list reported: **Discarded the unfinished setup "Acceptance 1045 - 2026-09-16". No Project, build or controlled record was deleted.** The test card disappeared.
- On the page opened before discard, Save and exit was refused with **This setup was discarded. It can no longer be saved or finalized.** It did not navigate away or report a successful save.
- Clicking Create Project on that stale page resolved to the terminal **This setup was discarded** screen, with no editing or creation controls. Reloading retained that terminal state.
- Reloading Projects and then signing out and signing in again still showed no test setup or test project. The unrelated Untitled Project draft retained its prior saved timestamp (September 16, 8:41:31 AM).
- Next Gen GPS still showed exactly one build, 0.01 / SW-00.01 / In Work.
- Discard is logical abandonment. The test draft's retained historical row is not claimed to have been physically purged.

Browser screenshot capture timed out for the final discard and stale-save views. Their actual accessibility-tree observations are retained in files 12-16; they are not presented as new screenshots. The seven earlier HOME screenshots remain available and unchanged.

**Disposition: HOME acceptance passed within the scope described here; closeout of #1045 is warranted.**

## Whole-issue criteria disposition

| Criteria | Evidence and disposition |
| --- | --- |
| I01-I03: coherent profiles, normalization, supported repair | Independently reviewed #1049 regressions and real-server owner-shape repair; live System toggle/save/reload and owner's effective configuration. Accepted. |
| I04-I06: rules, readiness and final summary | Retained implementation review plus live stale acceptance blocking and renewed acceptance/summary. Accepted. |
| I07-I08: finalization outcomes and recovery | Retained exact-head review and protected real-server failure/lost-response/lifecycle regression qualification; no fault injection on HOME. Accepted. |
| I09: native/external baseline paths | Retained real captured native baseline repair/invalidation/re-accept/materialization qualification and external source regressions. Not re-run on HOME. Accepted. |
| I10: atomicity and effective result | Retained transaction/provider qualification plus actual completed owner identities, effective ladder, authorized discovery and explicit build selection. Accepted. |
| I11: evidence separation | Prior red-first/disposable/API/provider evidence remains separately attributed. Live owner completion was already recorded; no fresh repair/create replay is claimed. Accepted. |
| I12: independent review and protected delivery | #1049 and #1056 independently reviewed and merged through protected candidate gates; installed b243a8bd verified. Accepted. |
| Owner follow-up: concise System feedback | Independently reviewed #1056, seven retained HOME screenshots and live normal toggle observations. Accepted. |
| Owner follow-up: discard unfinished setup | Retained authorization/audit/provider-race qualification plus this live named cancel/discard, stale save/finalize refusal, reload and new-sign-in proof. Accepted. |

No new implementation change was made during acceptance. #1054 remains separate and open.

## Evidence boundaries

This live session verifies the installed revision, completed owner result, effective owner configuration, authorized discovery, explicit build selection, normal System toggle wording/persistence, and review invalidation/renewal. It does not relabel prior disposable fault-injection, source-import, authorization or PostgreSQL race tests as HOME executions. Those remain covered by the independently reviewed implementation and protected qualification.

The original owner repair-to-creation journey was qualified earlier in disposable state; today's original draft was already completed. The owner reported that things looked good and requested this additional live acceptance and closeout. #1054 remains the separate portal-header narrow-width overflow issue.
