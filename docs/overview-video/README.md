# Overview video

A four-minute, silent, self-explaining overview of the product, for engineering leads and management. It
produces two deliverables from one source:

- `build/AeroLink-overview.mp4` — 1920×1080 H.264, ~10 MB. Attaches to email, embeds in PowerPoint, plays
  in Teams.
- `build/overview.html` — the same slides as a single self-contained page with pause and step controls, for
  sharing as a link.

Every screenshot in it is the real product, captured by driving the running application against the live FMS
showcase dataset. Nothing in the video is a mockup, and it makes no certification or qualification claim —
one slide is devoted to saying so, because that is the first thing the quality group will test.

## Revising it

**Edit `slides.js`. Nothing else.** Wording, ordering, how long a slide is held, which part of a screenshot
is shown, where the highlights sit — all of it is data in that one file, and it is commented.

```bash
cd docs/overview-video
node build.mjs --frames     # redraw what changed — about 2 seconds for a one-word edit
node build.mjs              # ...and produce the MP4 and the shareable page
```

The build hashes each slide's data together with the template and the screenshot it uses, so it re-renders
only the slides that actually changed. A single caption fix takes a couple of seconds; a full rebuild from
nothing takes about a minute.

| | |
|---|---|
| `node build.mjs` | render what changed, then encode both deliverables |
| `node build.mjs --frames` | render only — use this while iterating on wording |
| `node build.mjs --all` | ignore the cache and redraw everything |
| `node build.mjs --only 3,7` | redraw just these slides (0-based), then encode |

**To review without building anything**, serve the folder and open `template.html`. It plays through on its
own; arrow keys step, space pauses, `?still=7` freezes one slide.

```bash
cd docs/overview-video && python3 -m http.server 8899   # then open http://127.0.0.1:8899/template.html
```

## Common changes

**Change wording** — edit the `title`, `note`, or `chapter` string. Titles must fit two lines: roughly 64
characters. The heading band is clipped, so an over-long title is silently cut rather than allowed to bleed
onto the screenshot — if a title looks truncated, shorten it.

**Change how long a slide is held** — edit `seconds`. The video is currently 3:34; the whole deck is silent
and meant to be read, so budget roughly 15 seconds for a slide with two captions.

**Reorder or drop a slide** — move or delete its entry in the array. Nothing refers to slides by number.

**Move a highlight, or show a different part of a screenshot** — `crop` is `[x, y, width]` and each mark's
`box` is `[x, y, width, height]`, both in the pixels of the source PNG in `shots/`, which is 3200×1800.
Read the coordinate straight off the image. Crop width is the zoom control: smaller means bigger text and
less context, and 1750–2600 is the useful range. Height is derived so the image is never distorted.

**Add a slide** — copy an existing entry. Screenshot slides need `chapter`, `title`, `shot`, `crop` and one
or two `marks`. Text slides set `kind` to one of `title`, `questions`, `twoColumn`, `stats`, `close`.

## Refreshing the screenshots

When the interface changes, re-capture rather than editing images:

```bash
cd product/client && CAPTURE=1 npx playwright test capture-overview
```

That drives the real product and overwrites `shots/`. It is skipped without `CAPTURE=1`, so it never runs as
part of the ordinary journey suite. Afterwards run `node build.mjs --all`, because a new capture will have
moved things and every crop and highlight needs checking against the new images.

The capture viewport is 1600×900 at 2× device scale, producing 3200×1800 PNGs. **Do not change those
numbers** — every coordinate in `slides.js` is expressed in them.

## What is committed, and what is not

`shots/` is committed (about 3.8 MB) so that copy revisions need no running application. `build/` is not
committed — the MP4, the frames and the shareable page are all regenerable outputs, which is the same rule
the product itself applies to generated documents.

## Requirements

Node, a Chromium (the build looks for Playwright's, under `/opt/pw-browsers`), and an ffmpeg with H.264.
The build finds `imageio_ffmpeg`'s bundled binary if it is installed, otherwise falls back to `ffmpeg` on
`PATH`. Playwright's own bundled ffmpeg is **not** sufficient — it only encodes VP8.
