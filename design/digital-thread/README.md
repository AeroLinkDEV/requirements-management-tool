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
| `review/*.png` | Reviewer screenshots from rounds 2 and 3, each showing a defect or question that changed the design. |

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

## Known limits of the prototype

- Proposal content for 26 of the 31 change requests is generated from the request's own number. Five carry
  authored detail: `SRCR-00039`, `SRCR-00041`, `HLRCR-00127`, `LLRCR-00061`, `HLRTCCR-000003`. The prototype
  labels a generated one "illustrative content" in its breadcrumb.
- The record set is larger than the seeded FMS 1.6 dataset (31 change requests against the seeded 8), chosen
  so the crowding and density behaviour is visible at all.
- Edge routing crosses when a change fans out to three or more children in one lane.
- Identifiers follow the `PROJECT_STATE.md` grammar. `SYSTPCR` was confirmed by the product owner and is not
  yet recorded in that document's controlled-identity table.
