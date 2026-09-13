# #1022 C5 protected-run correction and renewed review

Head **6944761c490eb2c306544c8f56cc9fa825798fc9**, base **858791c6e12b316bb7ded56a2d17dab05e0b872b**, branch codex/1022-astra-implementation, owned worktree C:\Sean Project\RMT-1022-astra-implementation, PR#1031.

Only `product/client/tests/digital-thread-c5-acceptance.spec.ts` differs from reviewed8f77044e65fb474335b8f98f6f726d69ae0b69d7. Product source tree is identical at both heads: **3f92da8aaf88b056b26e0dc8aca9484a27af23f3**. Earlier review verdicts do not automatically approve the changed test head; renewed exact-head gates are requested.

## Failure retained and independently dispositioned

[Protected Product34730045022](https://github.com/AeroLinkDEV/requirements-management-tool/actions/runs/34730045022) failed at8f. Shard2 had2 deterministic C5 failures, both repeated on its automatic retry; its other172 tests passed. Other shards155/164/317 passed, production10 passed, client/script/metrics passed. The one skipped test is the existing opt-in `capture-overview.spec.ts` screenshot-generation tool, explicitly gated by CAPTURE=1; it is not a skipped acceptance journey. Auto-merge was disabled immediately and readiness removed before any edit. No candidate was merged.

Local exact8f C5 reproduction also returned12passed/2failed. Independent Terra review opened failure pixels/trace and confirmed two obsolete setup/oracles, no product defect:

1. The old test used Enter on an already-selected focal as toggle-clear, contrary to DEC-127's explicit clear contract. It also required86%Detailed to become81%Compact on reselection, encoding the superseded whole-story fitting model. The correction first proves Enter retains selection, then deliberately creates a different manual zoom, uses focused-canvas Escape, proves the exact displayed six-axis transform is retained, and reselects the exact focal. It requires the useful manual zoom/tier to remain, a camera distinct from initial landing, selected inspector, and ordinary pointer actionability. It does not merely substitute observed86 for expected81.
2. Inside's dense1280 arrival shows the upper part of SYSR-00076.02, but its center is below the usable canvas. The failure screenshot has a visible Show SYSR-00076.02 control. The corrected setup requires that control, clicks it normally, then clicks the card normally and asserts exact inspector identity before all unchanged Bottom/Right/Auto collision/control-separation assertions.

Intermediate correction reports remain adjacent: the first new camera assertion compared the entire style and incorrectly included a legitimately changing scene height; it was corrected to computed transform. The second retained the old81% oracle and exposed its superseded behavior after proper clearing. Final WIP C5 returned14/14. No timeout, retry, force-click, test removal or product change.

## Qualification and visual provenance

Current head rebuild PASS; lint and identity routing PASS (811 Full identities,89 Fast logic,46 Fast rendered,676 Full-only). New core42+C5 14 exact-head run: **56/56 PASS in2.0min**, no failures/skips/flaky, adjacent JSON, clean SHA recorded before/after. Current planner passes with no unknown paths. The previous [complete8f packet](https://github.com/AeroLinkDEV/requirements-management-tool/blob/abdfd4f25cf57707bdffc366ada6e55c47b074ac/docs/archive/1022-astra-implementation-20260912/final-gates-8f77044e/README.md) supplies all A01–A20 dispositions and actual57fixture/34core/21application images,42 motion recordings and89logic/85adapter/47page qualification for the **identical product source**, explicitly attributed to8f. Current56 recordings/C5 screenshots and decoded0.1s C5 sequences supplement A13/A14/A17/A20 and preserve old failure images. The implementing instance opened both current C5 contact sequences.

Reviewers must verify the product-tree equality and inspect changed test stimuli/oracles, current C5 pixels/motion, retained failures and exact-head report. Requested gates: CORE_INTERACTION round6 and CROSS_VIEW_VISUAL round3, followed by separate FINAL_HEAD round2. No PASS is inferred from unchanged product code or earlier results. Only after completed review may readiness be requested again; exact new native Full/queue qualification remains required.

No persistent PostgreSQL54329, product/.local or HOME/demo service was modified. Issue remains open. No original-dataset/HOME acceptance is claimed.
