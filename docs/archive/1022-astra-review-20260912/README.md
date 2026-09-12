# Issue 1022: Astra review evidence, 2026-09-12

Historical review artifacts for [issue 1022](https://github.com/AeroLinkDEV/requirements-management-tool/issues/1022). The issue comment owns the findings and action plan. This evidence-only branch is not an implementation PR and should not be merged as the issue solution.

All screenshots and measurements here were acquired by Astra from DeepSeek's exact code revision `7956ad1c7a58be4532edf35cefabb0c70abe9b33` in an owned detached worktree. The publishing branch is based on main `858791c6e12b316bb7ded56a2d17dab05e0b872b`; that is publication provenance, not the tested code. No product source was modified for this audit.

- `artifact-native-action-obscured.png`: failure of the real application `digital-thread-page.spec.ts` native Artifact action journey. Disposable SQLite API on 5099, client on 5198; 1440x900. The bottom inspector intercepts the selected card's Open this change action. Full page suite: 40 passed, 1 failed. This is synthetic test data, not persistent HOME acceptance.
- `promotion-outside-usable-frame.png`: current-head core rendered promotion journey failure; expected top at least 112, observed 74.76891326904297. Its test helper also understates the actual product free-frame constraints.
- `network-selected.png`, `inside-selected.png`, `artifact-selected.png`: real component/adaptor fixtures at 1440x900. These isolate component behavior and do not claim the full application CSS context.
- `inside-true-unselected-hover.png`, `artifact-true-unselected-hover.png`: fixture captures after focusing the canvas and clearing selection, explicitly verifying zero selected cards and no inspector before hover. They correct the state classification of DeepSeek's earlier captures; they are not complete motion acceptance.
- `full-app-network-selected.png`: real application with disposable SQLite at 1440x900. Confirms the 9px inspector section headings with application styles present.
- `measurements.json`: displayed boxes, computed typography and selection/inspector state from the fixture audit. No credentials or persistent production data.
- `geometry-counterexample.json`: actual current `planReveal` output. A retained lane-1 linked card at row 8 is initially moved by -992 scene units to y=124. Increasing its measured height from 108 to 200 while retaining its placement overlaps the stationary row-2 resident by 36 units. This is a pure-helper counterexample, not a claim that this precise graph was reproduced in production.

Independent current-head checks: 83 logic tests passed; core rendered first run 18 passed / 2 failed (promotion plus inability to observe the motion precondition); the isolated motion takeover retry passed 1/1. Both outcomes are retained in the local audit; the retry does not establish why the first precondition failed. Client lint and production build passed, with warnings. No Full Product or merge-queue qualification was requested.

Original attachment provenance: ZIP SHA-256 `c8738c21a4623732baac2c54ae34fcbc1cf6dce57c4a78a6af61774320d4ab73`; embedded DOCX SHA-256 `bfed5645e92c6f86f5bda2d24200129591fbded3c145a1626f03b2753077d405`. Astra opened all six original PNGs and the six published Round 6 PNGs. Frames from four Round 6 videos were decoded and inspected; this is not a claim of watching all twenty recordings or final motion acceptance.
