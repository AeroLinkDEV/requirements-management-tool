# #1022 CORE_INTERACTION evidence — tested revision 5f3642ca

**Tested code revision:** `5f3642ca28e90e4ad51b2210c59f29b47c08fc05`
branch `deepseek/1022-digital-thread-interaction`

This folder is a **temporary review artifact** on the feature branch; it is removed before any merge. Files are
published here because the worker environment cannot upload binaries to review conversations.

**Command that produced everything here** (run in `product/client`, isolated worktree):

```
$env:AEROLINK_1022_EVIDENCE="<dir>"; $env:AEROLINK_1022_VIDEO="1"
npx playwright test --config=playwright.rendered.config.ts digital-thread-core-interaction.spec.ts
# -> 15 passed (47.1s)
```

Environment: Windows, headless Chromium (Playwright), fixture-backed Vite dev server on a private port,
viewport per test (1280×900 / 1100×900 / 1440×1000), no browser zoom, default text settings.

## Screenshots — `screenshots/`

| File | Scenario | Test |
|---|---|---|
| `network-hover-stationary.png` | Change network — pointer on a card, camera unmoved | hover emphasises without moving the camera |
| `network-selected.png` | Change network — selected record, tray open | the real card is the click target |
| `network-lane-scrolled-into-temporary-range.png` | Change network — lane scrolled past its ordinary bound | extended-range gesture proof |
| `inside-hover-stationary.png` | Inside a change — pointer on a card, camera unmoved | Inside hover is stationary |
| `inside-selected.png` | Inside a change — selected record | Inside hover is stationary |
| `artifact-hover-stationary.png` | Artifact thread — pointer on a card after the arrival selection is cleared | Artifact hover is stationary |

## Clips — `clips/` (one per core test; filenames are Playwright's truncated test titles)

| File | Test it records |
|---|---|
| `digital-thread-core-intera-b9734-e-camera-or-the-source-card-rendered.webm` | hover is stationary: camera and source card do not move |
| `digital-thread-core-intera-8c7a5-nd-without-a-camera-restore-rendered.webm` | hover exit: no popup, no camera restore |
| `digital-thread-core-intera-3c22b-and-selection-is-persistent-rendered.webm` | the real card selects; hovering another card cannot replace it |
| `digital-thread-core-intera-1768a--its-explicit-reveal-action-rendered.webm` | dense thread: every record reachable (no-room fallback branch) |
| `digital-thread-core-intera-1bc39-clear-does-not-snap-it-back-rendered.webm` | extended range → clear → cleanup → next drag |
| `digital-thread-core-intera-02a97-cted-record-owns-its-thread-rendered.webm` | Inside a change: stationary hover, persistent selection |
| `digital-thread-core-intera-26826-rrival-selection-is-cleared-rendered.webm` | Artifact thread: stationary hover after clearing |
| `digital-thread-core-intera-fd06b--when-the-reader-pans-to-it-rendered.webm` | hidden lane: endpoint reachable after panning |
| `digital-thread-core-intera-f074a-es-horizontal-away-and-back-rendered.webm` | manual vertical exploration survives horizontal away/back |
| `digital-thread-core-intera-f0bee-e-without-moving-the-camera-rendered.webm` | available space: automatic reveal without camera movement |
| `digital-thread-core-intera-aad5a--it-where-the-reader-saw-it-rendered.webm` | a relocated card keeps its position when it becomes the subject |
| `digital-thread-core-intera-e8723-from-the-displayed-position-rendered.webm` | a drag takes over an automatic camera move |
| `digital-thread-core-intera-65b86-rangement-without-animating-rendered.webm` | reduced motion: same arrangement, no transition |
| `digital-thread-core-intera-aa262-nt-and-selects-that-subject-rendered.webm` | hover→click promotion during an incoming reveal |
| `digital-thread-core-intera-fda8e-ces-the-old-subject-cleanly-rendered.webm` | new selection during cleanup replaces the old subject |

## Limits

- Fixture-backed captures from the real shared canvas — not production, and not owner visual acceptance.
- Screenshots are static states; the clips are the gestures. Motion timing (A18) is deliberately untuned.
- A filename is not proof of its scenario; the assertions in `tests/digital-thread-core-interaction.spec.ts`
  at the tested revision are.
- Captures at earlier revisions are superseded and are not kept here, so nothing can be mistaken for evidence
  about a later change.

## Not covered by this package

Owner visual acceptance; final tray typography/spacing (A17); final easing tuning (A18); the full hosted journey
suite; and the planner-selected local smoke journeys, which have not been run yet.
