# #1022 CORE_INTERACTION evidence — Round 6

**Tested code revision:** `d72345695d5308af147ea6ab8fcc032efdcb9c81` (branch `deepseek/1022-digital-thread-interaction`)

Temporary branch artifact; removed before any merge. Published here because the worker environment cannot upload
binaries to review conversations.

## Capture command (in `product/client`, isolated Windows worktree)

```
$env:AEROLINK_1022_EVIDENCE="<dir>"; $env:AEROLINK_1022_VIDEO="1"
npx playwright test --config=playwright.rendered.config.ts digital-thread-core-interaction.spec.ts
# -> 20 passed (1.1m)
```

Environment: Windows, headless Chromium, fixture-backed Vite dev server on a private port, per-test viewports
(1280×900 / 1100×900 / 1000×900 / 1440×1000), no browser zoom, default text settings.

## Screenshots — `screenshots/`

| File | Scenario |
|---|---|
| `network-hover-stationary.png` | Change network: pointer on a card, camera unmoved |
| `network-selected.png` | Change network: selected record with the tray open |
| `network-lane-scrolled-into-temporary-range.png` | Change network: lane scrolled past its ordinary bound |
| `inside-hover-stationary.png` | Inside a change: pointer on a card, camera unmoved |
| `inside-selected.png` | Inside a change: selected record |
| `artifact-hover-stationary.png` | Artifact thread: pointer on a card after the arrival selection is cleared |

## Clips — `clips/` (one per core test, in the order Playwright ran them)

| File | Test it records |
|---|---|
| `01-…cted-record-owns-its-thread….webm` | Inside a change: stationary hover, persistent selection |
| `02-…leaves-no-overlapping-cards….webm` | density change after manual exploration: no overlapping cards |
| `03-…its-explicit-reveal-action….webm` | dense thread: every record reachable |
| `04-…clear-does-not-snap-it-back….webm` | extended range → clear → cleanup → next drag |
| `05-…era-where-the-reader-saw-it….webm` | clearing during motion stops the camera where the reader saw it |
| `06-…same-scope-refresh-keeps-it….webm` | scope change resets navigation; same-scope refresh keeps it |
| `07-…rrival-selection-is-cleared….webm` | Artifact thread: stationary hover after clearing |
| `08-…ul-height-on-first-exposure….webm` | hidden lane: endpoint arrives at a useful height on first exposure |
| `09-…and-selection-is-persistent….webm` | the real card selects; hovering another card cannot replace it |
| `10-…rangement-without-animating….webm` | reduced motion: same arrangement, no transition |
| `11-…nd-without-a-camera-restore….webm` | hover exit: no popup, no camera restore |
| `12-…nt-and-selects-that-subject….webm` | hover→click promotion during an incoming reveal |
| `13-…it-where-the-reader-saw-it….webm` | relocated card keeps its position when it becomes the subject |
| `14-…e-camera-or-the-source-card….webm` | hover is stationary: camera and source card do not move |
| `15-…e-lane-and-keeps-it-visible….webm` | Arrow Down stays in-lane and lands on a visible card |
| `16-…from-the-displayed-position….webm` | a drag takes over an automatic camera move |
| `17-…es-horizontal-away-and-back….webm` | manual vertical exploration survives horizontal away/back |
| `18-…e-without-moving-the-camera….webm` | available space: automatic reveal without camera movement |
| `19-…when-the-reader-pans-to-it….webm` | hidden lane: endpoint reachable after panning |
| `20-…ces-the-old-subject-cleanly….webm` | new selection during cleanup replaces the old subject |

Filenames are Playwright's truncated test titles; the row above gives the full test name. A filename is not proof
of its scenario — the assertions in `tests/digital-thread-core-interaction.spec.ts` at the tested revision are.

## Qualification at this revision (implementer-run)

`npx tsc -b --pretty false` exit 0 · `npm run lint` exit 0 · `npm run build` exit 0 ·
`npx playwright test --config=playwright.logic.config.ts` 83 passed ·
`npx playwright test --config=playwright.rendered.config.ts` 66 passed ·
`npm run test:smoke` 5 passed (against the real API).

## Limits

Fixture-backed captures from the real shared canvas — not production and not owner visual acceptance. The
`change-network.html?case=scope` and `digital-thread-contract.html` fixtures are test-only harnesses; the latter
uses deliberately spaced rows that no production adapter emits, and is labelled as shared-canvas contract
coverage. Motion timing (A18) is now half-speed for automatic selection framing; owner acceptance of that
stillness/speed is pending at the visual gate.
