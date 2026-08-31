# Digital Thread — approved visual prototype

This directory holds the **reference prototype** for the Digital Thread page, approved by the product owner
on 2026-08-31 after four rounds of review. It is design reference material, not product code: nothing here
builds, ships, or is imported by `product/`.

Its purpose is to make "implement it exactly as reviewed" enforceable. The implementer ports from working
code with the interaction rules already written down and checked, rather than reconstructing behaviour from
prose or screenshots.

## What is here

| Path | What it is |
| --- | --- |
| `prototype/Main.dc.html` | The approved prototype. Self-contained HTML + CSS + vanilla JS. All three views, every interaction rule, exact design tokens, and the sample data. **This is the fidelity contract.** |
| `prototype/DirectionA..D.dc.html` | The four exploratory directions shown in round 1. B (*Certified Flow*) was chosen; A, C and D are retained so the rejected options stay legible. |
| `prototype/canvas.json` | Multi-artboard layout for the review canvas, plus the reviewer notes captured during each round. |
| `prototype/checks.js` | Headless behavioural checks over `Main.dc.html` — see below. |
| `prototype/serve.js` | Minimal static server, for looking at the prototype in a browser. |
| `review/01..05-*.png` | Reviewer screenshots from prototype rounds 2 and 3, each showing a defect or question that changed the design. |
| `review/06..08-*.png` | The **shipped** Digital Thread page as it stands on `main` after #867, which this work replaces. |

## Running it

Open the prototype directly:

```bash
node design/digital-thread/prototype/serve.js
```

Then browse to `http://localhost:8791/prototype/Main.dc.html`. The file expects a `support.js` shim that the
review canvas supplies; opened bare it renders the artboard shell without the Design Component runtime, which
is enough to read the CSS but not to drive it. To drive it, use the published review canvas linked from the
implementing issue.

## The behavioural checks

`checks.js` runs `Main.dc.html`'s logic class against a DOM stub and asserts the invariants that matter:

```bash
node design/digital-thread/prototype/checks.js
```

It verifies the lane window fills the viewport, the zoom floor stops where everything is shown, the trace
walks the full directed web without leaking sideways, cross-lane sync moves the follower lanes, every change
request opens, and — the one that caught two real bugs — that no directly linked record ever ends up
underneath the detail panel, in any dock mode.

These are the behaviours to preserve when porting. They are not a substitute for the product's own tests;
port them into the real suite rather than depending on this file.

## If you are the agent implementing this

`prototype/Main.dc.html` in this repository is the canonical source. Read it here; do not depend on being
able to reach the review canvas.

The review canvas published during design review is the same content, but it is a private artifact belonging
to one account, it can be edited after the fact, and it is not version-controlled. It exists so a person can
*drive* the interaction. This file exists so an implementer can *read* it. If the two ever disagree, this file
wins, because this is the one that was committed alongside the issue.

There is nothing in the canvas that is not in this directory.

## Typography

The prototype loads Manrope and DM Sans from Google Fonts because a standalone artboard cannot use the
product's `@fontsource` imports. **That is a prototype concession, not a design decision** — the product
self-hosts both faces deliberately, for offline and restricted-network operation. Keep the `@fontsource`
imports when porting.

Identifiers use `ui-monospace, SFMono-Regular, Consolas, monospace`, matching `LifecycleExplorer.css`, the
existing Digital Thread surface. AeroLink ships no monospace webfont and none should be added.

## Known limits of the prototype

- Proposal content for 26 of the 31 change requests is generated from the request's own number. Five carry
  authored detail: `SRCR-00039`, `SRCR-00041`, `HLRCR-00127`, `LLRCR-00061`, `HLRTCCR-000003`. The prototype
  labels a generated one "illustrative content" in its breadcrumb.
- The record set is larger than the seeded FMS 1.6 dataset (31 change requests against the seeded 8), chosen
  so the crowding and density behaviour is visible at all.
- Edge routing crosses when a change fans out to three or more children in one lane.
- Identifiers follow the `PROJECT_STATE.md` grammar. `SYSTPCR` is confirmed correct and is being added to that
  document's controlled-identity table by PR #877.
