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

Final discard and stale-page disposition: pending action-time owner confirmation.

## Evidence boundaries

This live session verifies the installed revision, completed owner result, effective owner configuration, authorized discovery, explicit build selection, normal System toggle wording/persistence, and review invalidation/renewal. It does not relabel prior disposable fault-injection, source-import, authorization or PostgreSQL race tests as HOME executions. Those remain covered by the independently reviewed implementation and protected qualification.

The original owner repair-to-creation journey was qualified earlier in disposable state; today's original draft was already completed. The owner reported that things looked good and requested this additional live acceptance and closeout. #1054 remains the separate portal-header narrow-width overflow issue.
