# #1022 — handoff (owner asked for a clean transfer)

**Read this first. Everything below is at the exact revisions named, and nothing is merged.**

## Revisions and state

| Item | Value |
|---|---|
| Branch | `deepseek/1022-digital-thread-interaction` (pushed, worktree clean) |
| Current head | `6dde569fe262f802a011670fe06fa17c6a5c626d` — a **WIP** commit on top of the reviewed set |
| Reviewed set (Round 6 submission) | tested code `d72345695d5308af147ea6ab8fcc032efdcb9c81`, evidence commit `0779e35f074b7732350d17ffe0007441bad39fac` |
| Base | `ab8cd8f1ce31d707fa6b1f55914fe51b88cbce29` |
| Round 6 packet | https://github.com/AeroLinkDEV/requirements-management-tool/issues/1022#issuecomment-5646207970 |
| Page-level regression addendum | https://github.com/AeroLinkDEV/requirements-management-tool/issues/1022#issuecomment-5646410364 |

Nothing is merged. No Full CI arming, no queue entry, no issue closure, no production/demo/database/evidence-store
operations. The persistent PostgreSQL instance (54329), `product/.local` and HOME production are untouched.

## What the WIP commit changes (and why)

Two edits, both about *when the reveal plan is invalidated* and *what "usable frame" means*:

1. `product/client/src/DigitalThreadCanvas.tsx` — the plan's invalidation key is now `relationships | tier | a
   coarsely rounded usable window`, and a window change clears `delivered`/`visited` (never the reader-owned
   `frozen` lanes). Per-card measured heights are deliberately **not** in the key.
2. `product/client/tests/digital-thread-core-interaction.spec.ts` — a `usableFrame()` helper measures the real
   usable top from the toolbar instead of assuming `canvasTop + 40`, and the promotion test uses it.

Why: a page-level journey (below) proved that per-card height wobble was re-planning the board on nearly every
paint, leaving a selected card's own action button never stable enough to click. Moving the invalidation to the
window fixes that, and the window is what actually changes when the tray opens.

## Current verification (measured, this exact head)

```
npx playwright test --config=playwright.logic.config.ts                    -> 83 passed
npx playwright test --config=playwright.rendered.config.ts                 -> 19 passed, 1 failed
npx playwright test tests/digital-thread-page.spec.ts                      -> 40 passed, 1 failed (at d7234569)
npx playwright test tests/digital-thread-page.spec.ts -g "Artifact card…"  -> PASSES with the WIP (2.9 m)
```

**The one failure, precisely:** `clicking during an incoming reveal keeps the arrangement and selects that
subject` (`tests/digital-thread-core-interaction.spec.ts`, change-network `?case=reveal`). After hover→click
promotion, the linked card arrives at screen **y = 74.77** while the *real* usable top (toolbar bottom + heading
offset + 8, measured by the new helper) is **112** — i.e. the card sits ~37 px **above** the usable area. The
fixture preconditions, the in-flight-reveal sampling, the selection assertions and the lens-floor check all pass;
only the vertical containment fails.

Working hypothesis for whoever picks this up: the placement was computed against the pre-tray window and is not
being re-placed after the tray's camera move, because the lane is treated as settled (or the window key rounds
to the same bucket). A likely-correct fix is to *validate a retained placement against the current window before
keeping it* — dropping or re-placing any delta whose card is no longer fully inside — rather than relying on the
key alone. That also matches what Astra asked for in the Round 5 review ("validate retained placements").

## What is already implemented and proven (do not redo)

- **Persistent selection vs unselected hover**: hover never replaces or previews a selected thread; the floating
  "Click to pin story" target and its saved-camera restore are removed; the real card is the click target.
- **Stationary hover**: no camera, density or layout movement on hover, in all three views.
- **Lane-local reveal**: only out-of-view linked cards are displaced, in their own lane, never above the lane
  origin; the search includes usable space beyond the lane's previous content (Astra's short-lane counterexample
  reproduced failing at 132, now placing inside [300,610]); dense lanes fall back to directional cue + the
  labelled `Show <ID>` action.
- **Extended range**: the ordinary bound is derived from real geometry (−1920), the reader's gesture crosses it
  (strict assertion), a linked witness is readable there, clearing does not snap, and the next drag follows the
  reader.
- **Gesture defect fixed**: after the first drag, Chrome's native drag was cancelling every later gesture (1
  move, 0 pointerups), so later lane drags moved one step and never completed. Fixed with a narrow `onDragStart`
  block + `user-select`/`-webkit-user-drag`; the harness was proven innocent on a plain page.
- **Takeover**: displayed-transform sampling, proven motion in progress, pointer-down hold within 3 px, full
  200 px travel from the frozen position, no continuation.
- **Clear**: Escape freezes the displayed camera before releasing the selection; clear-during-motion proven by
  remaining travel after the clear.
- **Lifecycle**: promotion during an in-flight reveal (frame-sampled with a click marker); new selection while a
  displaced card is genuinely retiring; density change after manual exploration leaves no overlapping cards;
  scope change starts a fresh navigation context (product fix) while a same-scope refresh keeps the reader's
  position; reduced motion reaches the same arrangement with no transition; Arrow Down stays in-lane and lands
  on a visible card.
- **Hidden-lane first exposure**: spaced-row contract fixture (`tests/fixtures/digital-thread-contract.html|tsx`,
  labelled as shared-canvas contract coverage) — endpoint at y = 0 while its lane is off-screen, readable on
  arrival, no Show click, camera moved only by the reader.
- **A17/A18**: tray type at the 12 px floor with height returned in padding; half-speed automatic selection
  framing (.8 s) with dock/inset re-frames unchanged (.4 s).

## Attempts that were ruled out (evidence in the issue)

- **Measured two-axis dock recovery** (feasibility from the selected card's rectangle): the panel moves *after*
  the measurement and ended up covering the record it moved to protect (page-level journey caught it). Backed
  out; the reviewed behaviour is the bottom fallback, scoped to the situation that reported the shortfall.
- **A18 scoped slow class**: toggling the transition duration mid-animation destabilised the same journey.
  Backed out; final timing belongs to the visual slice, and the repo's existing journeys sample at 600–700 ms
  assuming the .4 s ease.
- **Fine-grained invalidation keys** (per-card heights, band height, window alone): see the WIP description.

## Reproducing everything

```
# in <worktree>/product/client
npm ci --prefer-offline                 # ~2 s from cache
npx tsc -b --pretty false
npm run lint ; npm run build
npx playwright test --config=playwright.logic.config.ts
npx playwright test --config=playwright.rendered.config.ts
npx playwright test tests/digital-thread-page.spec.ts      # full app + real API, ~7 min

# evidence capture (opt-in; writes nothing during ordinary runs)
$env:AEROLINK_1022_EVIDENCE="<dir>"; $env:AEROLINK_1022_VIDEO="1"
npx playwright test --config=playwright.rendered.config.ts digital-thread-core-interaction.spec.ts

# gesture diagnostics inside the canvas (gated; no console noise otherwise)
$env:AEROLINK_1022_DIAG="1"
```

Fixtures used by the core lanes: `tests/fixtures/change-network.html?case={hover,dense,reveal,scope}`,
`tests/fixtures/inside-change.html`, `tests/fixtures/artifact-thread.html`,
`tests/fixtures/digital-thread-contract.html` (spaced rows; contract coverage only).

## Suggested order of work for the next agent

1. Fix the promotion placement against the real usable frame (validate retained placements; see hypothesis).
2. Re-run the promotion test **and** the page-level artifact journey together — they pulled in opposite
   directions twice, so neither alone is sufficient evidence.
3. Run the full local qualification: `tsc`, lint, build, logic, rendered, `npm run test:smoke`, and the full
   `tests/digital-thread-page.spec.ts`.
4. Refresh the evidence folder at the new head (the current one is tied to `d7234569` and is stale).
5. Submit CORE_INTERACTION / Round 6 revision 2 to ChatGPT Astra with the packet structure used previously
   (see the archived packets in this issue), then stop for review.

## Safety notes

- The `review-evidence/` folders are **temporary branch artifacts** and must be removed before any merge.
- Do not reset/seed the persistent database or touch `product/.local`.
- The `?case=scope` and `digital-thread-contract` fixtures are test-only harnesses; the latter uses rows no
  production adapter emits and is labelled as such.
