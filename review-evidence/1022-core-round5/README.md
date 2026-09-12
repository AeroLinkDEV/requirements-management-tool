# #1022 CORE_INTERACTION evidence package — Round 5 draft

**Tested revision:** `0f19aed86fddd2d2ec826d779909358a09b1f999`
branch `deepseek/1022-digital-thread-interaction` (clean worktree at capture time)

**Command that produced everything here** (run in `product/client`, isolated worktree):

```
$env:AEROLINK_1022_EVIDENCE="...\round5-shots"; $env:AEROLINK_1022_VIDEO="1"
npx playwright test --config=playwright.rendered.config.ts digital-thread-core-interaction.spec.ts
# -> 9 passed (26.5s)
```

Capture environment: Windows, headless Chromium via Playwright, fixture-backed Vite dev server on a private
port, viewport set per test (1280×900 or 1100×900 or 1440×1000), no browser zoom, default text settings.

## Screenshots (`round5-shots/`)

| File | Scenario | Test |
|---|---|---|
| `network-hover-stationary.png` | Change network — pointer on a card, camera unmoved | hover emphasises without moving the camera |
| `network-selected.png` | Change network — selected record, tray open | the real card is the click target |
| `network-lane-scrolled-into-temporary-range.png` | Change network — lane scrolled past its ordinary bound | extended-range gesture proof |
| `inside-hover-stationary.png` | Inside a change — pointer on a card, camera unmoved | Inside hover is stationary |
| `inside-selected.png` | Inside a change — selected record | Inside hover is stationary |
| `artifact-hover-stationary.png` | Artifact thread — pointer on a card after the arrival selection is cleared | Artifact hover is stationary |

## Recordings (`round5-video/`)

One WebM per test, named after the Playwright test title:

- `…camera-or-the-source-card…` — hover stability, Change network
- `…without-a-camera-restore…` — hover exit, Change network
- `…and-selection-is-persistent…` — click selects; hovering another card cannot replace it
- `…its-explicit-reveal-action…` — dense thread reachability
- `…clear-does-not-snap-it-back…` — extended range → clear → cleanup → next drag
- `…record-owns-its-thread…` — Inside a change hover/selection
- `…arrival-selection-is-cleared…` — Artifact thread hover
- `…when-the-reader-pans-to-it…` — hidden-lane endpoint reachability
- `…horizontal-away-and-back…` — manual lane position retained across panning

## Limits

These are fixture-backed captures from the real shared canvas, not production or owner acceptance.
The screenshots show static states; the WebM clips show the gestures. Motion timing (A18) is not yet tuned.
Earlier captures at `ab2130dd` are stale and kept only as history.

## Not yet covered by this package

Relocated-subject and collapse/expansion regressions; same-subject promotion during incoming motion; new
selection during cleanup; interruption at start/middle/end including pointer-down-hold and nested actions;
reduced-motion equivalence; measured two-axis dock fit; A17 tray readability; A18 final timing.
