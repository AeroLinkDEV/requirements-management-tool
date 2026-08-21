# Project Configuration — design mockups

Mockups for [#697](https://github.com/seanmccarthyns/requirements-management-tool/issues/697), which
makes a project's requirement ladder, its per-step obligations and its assurance posture
configurable instead of hard-coded.

**These files are the specification, not a picture of one.** Each `.dc.html` carries the exact
colours, type sizes, spacing, radii and copy intended for the real page, as inline styles — read
them directly rather than reading a screenshot of them. Every value was lifted from the running
client (`product/client/src/index.css` and `ApprovalConfigurationCenter.css`), so a faithful
implementation is a matter of using the same tokens rather than matching by eye.

A rendered, pannable version for humans:
**https://claude.ai/code/artifact/0307deb6-cc2f-4e0c-9e90-2de3387b5c71**

## The artboards

| File | Shows |
| --- | --- |
| `Main.dc.html` | Project Configuration landing page for FMS: section rail, ladder summary, assurance and policy cards |
| `Ladder.dc.html` | The ladder section in full — every step expanded to its obligations, and why it cannot be changed |
| `Variants.dc.html` | Two other projects: `[System, LowLevel]`, and one with a Customer step above System |
| `Inception.dc.html` | The one editable state — choosing the ladder at project creation, with the level catalogue |
| `Assurance.dc.html` | DAL posture: recommendation per lever, one tightened, one relaxed with a recorded rationale |
| `Approvals.dc.html` | Today's Approval Configuration as a section nested inside the new page |

`canvas.json` positions them and carries three explanatory notes.

## Design decisions embedded in these files

Worth knowing before implementing, because each is deliberate and each is arguable:

- **A step that arrives from outside is drawn with a dashed border**, and the obligations it lacks
  are struck through rather than hidden. A reader should be able to see what a step *does not*
  have — that is the point of a configurable ladder.
- **Where the assurance posture owns a lever, the page that would otherwise own it defers and says
  so.** On `Approvals.dc.html`, "approver may be the author" reads *Refused — set by the assurance
  posture* rather than appearing as a local toggle that would silently lose. If that precedent is
  accepted it applies wherever two configuration surfaces overlap.
- **The frozen state is explained, not merely enforced.** `Ladder.dc.html` says why the ladder
  cannot change and what would break, rather than showing a disabled control with no account of
  itself.
- **FMS's configuration describes FMS accurately.** Nothing on these screens is a placeholder
  standing in for a value the product does not really hold — see the issue's *"Demonstrating this
  on the live FMS project"*.

## Tokens used

From `product/client/src/index.css`, unchanged:

```
fonts    Manrope (display, 500/600/700) · DM Sans (body, 400/500/600/700)
type     12 / 13 / 14 / 15 / 17 / 22 / 26 / 32
ink      #142139  #42526a  #606c7e
line     #dce3eb        surface #ffffff        surface-subtle #f6f8fb        canvas #f3f6f9
teal     #176f68  #23877d  #dff3ee
alarm    bg #fce9eb  border #eccfd3  text #8f2f3d
radii    8 (controls) · 12 (cards) · 16 (hero) · 999 (pills)
control  40px height (comfortable) · 36px small
layout   1240px max width, 26px 28px 64px padding, 288px + 1fr section grid
```

The mockups link Manrope and DM Sans from Google Fonts because an artboard has no access to the
app's self-hosted `@fontsource` packages. **The real page must keep using the self-hosted faces** —
AeroLink is an on-premises product and must render on a disconnected network, which is why
`index.css` self-hosts them in the first place.

## Rebuilding the viewable canvas

The published canvas is generated from these sources; the generated file is not committed because
it is ~2.2 MB of editor payload rather than design. To regenerate and republish it, use the
`design` skill with these files as input.

## What these mockups do not settle

Nothing here is clickable, and no interaction has been tested against a real path through the page.
The ladder editor's drag-to-reorder, the add-a-step flow and the deviation-recording flow are drawn
as end states only. Treat them as the shape of the design, not as a validated interaction.
