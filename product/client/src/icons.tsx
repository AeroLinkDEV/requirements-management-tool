import type { ReactElement } from "react"

/**
 * AeroLink's navigation icon set (#902).
 *
 * The contract every icon in the shell/navigation follows:
 *
 * - **Local and self-hosted.** Inline SVG React components in this repository; no runtime icon font, no
 *   CDN, no third-party icon package. AeroLink runs disconnected and must render identically when it is.
 * - **One geometry.** Every icon is drawn on a shared 16×16 viewBox with consistent stroke width, round
 *   caps and joins, and `currentColor` — so size comes from the slot and color comes from the UI state
 *   around it (rest, hover, focus, active), never from the glyph itself. Fills are used only where a
 *   solid mark is the point (the brand triangle, small dots) and always `currentColor` too.
 * - **Decorative by default.** The component renders `aria-hidden` and `focusable="false"`. Icons are
 *   supplementary to readable text labels; navigation meaning must never depend on icon shape alone.
 *   Any icon-only control must carry its own accessible name.
 * - **Sizing and alignment belong to the slot.** The navigation's icon slot keeps its fixed width so
 *   labels align across entries, and centers a block-level svg; this component only draws. Icons render
 *   identically at every supported desktop width and in both workspace densities, because neither the
 *   viewBox nor the slot is density-dependent.
 * - **One icon, one meaning.** Names are the navigation's semantics, not the Unicode glyphs they replace:
 *   two entries that mean different things get different icons even when the old glyph was reused
 *   (generated documents vs the Documentation Center, requirements explorer vs procedure explorer,
 *   Digital Thread vs Integration Center).
 */

export type IconName =
  | "home"
  | "myWork"
  | "teamWork"
  | "changeRequests"
  | "requirements"
  | "documents"
  | "library"
  | "verification"
  | "testChangeRequests"
  | "procedureExplorer"
  | "testResults"
  | "code"
  | "problemReports"
  | "release"
  | "baselines"
  | "digitalThread"
  | "peopleAuthority"
  | "workflow"
  | "integrations"
  | "operations"
  | "coverage"
  | "search"
  | "brandMark"

const shapes: Record<IconName, ReactElement> = {
  home: <path d="M2.5 8 8 2.8 13.5 8M4.2 6.8v6.7h7.6V6.8" />,
  myWork: (
    <>
      <circle cx="8" cy="8" r="5.6" />
      <circle cx="8" cy="8" r="1.5" fill="currentColor" stroke="none" />
    </>
  ),
  teamWork: (
    <>
      <circle cx="5.6" cy="5.6" r="2.1" />
      <path d="M2.1 13.2c0-2 1.6-3.5 3.5-3.5s3.5 1.5 3.5 3.5" />
      <circle cx="11.2" cy="6.1" r="1.8" />
      <path d="M10.6 9.6c1.9-.2 3.5 1.1 3.5 3.2" />
    </>
  ),
  changeRequests: <path d="M8 2.4 13.6 8 8 13.6 2.4 8Z" />,
  requirements: <path d="M3 4.4h10M3 8h10M3 11.6h6.5" />,
  documents: (
    <>
      <path d="M4 2.5h5.2l3.3 3.3v7.7H4Z" />
      <path d="M9.2 2.5v3.3h3.3M6.2 8.6h3.6M6.2 11h3.6" />
    </>
  ),
  library: (
    <>
      <path d="M8 4.6C7 3.5 5.1 3.1 3.2 3.3v9.2c1.9-.2 3.8.2 4.8 1.3 1-1.1 2.9-1.5 4.8-1.3V3.3c-1.9-.2-3.8.2-4.8 1.3Z" />
      <path d="M8 4.6v9.2" />
    </>
  ),
  testChangeRequests: (
    <>
      <rect x="2.5" y="2.5" width="8" height="8" rx="1" />
      <rect x="5.5" y="5.5" width="8" height="8" rx="1" />
    </>
  ),
  procedureExplorer: (
    <>
      <rect x="3.4" y="3.4" width="9.2" height="10.2" rx="1" />
      <path d="M5.8 3.4h4.4v1.7H5.8ZM5.8 8h4.4M5.8 10.6h4.4" />
    </>
  ),
  testResults: <path d="M3 13.4V9.2M6.4 13.4V5.4M9.8 13.4V7.6M13.2 13.4V3.4" />,
  code: (
    <path d="M5.9 3.4C4.4 3.4 4.5 5 4.5 6.1c0 1.2-.4 1.8-1.4 1.9 1 .1 1.4.7 1.4 1.9 0 1.1-.1 2.7 1.4 2.7M10.1 3.4c1.5 0 1.4 1.6 1.4 2.7 0 1.2.4 1.8 1.4 1.9-1 .1-1.4.7-1.4 1.9 0 1.1.1 2.7-1.4 2.7" />
  ),
  problemReports: (
    <>
      <circle cx="8" cy="8" r="5.8" />
      <path d="M8 4.7v3.8" />
      <circle cx="8" cy="11.2" r=".9" fill="currentColor" stroke="none" />
    </>
  ),
  release: (
    <>
      <path d="M4 2.6v11" />
      <path d="M4 3.4h6.9L9.1 5.7 10.9 8H4Z" />
    </>
  ),
  baselines: (
    <>
      <path d="M8 2.6 13.8 5.4 8 8.2 2.2 5.4Z" />
      <path d="M2.2 8.6l5.8 2.8 5.8-2.8M2.2 11.6l5.8 2.8 5.8-2.8" />
    </>
  ),
  digitalThread: (
    <>
      <circle cx="3.6" cy="12.4" r="1.6" />
      <circle cx="8" cy="4" r="1.6" />
      <circle cx="12.4" cy="10" r="1.6" />
      <path d="M4.9 11 7 5.5M9.3 5.4l2 3.2" />
    </>
  ),
  peopleAuthority: (
    <>
      <circle cx="6.3" cy="5.6" r="2.2" />
      <path d="M2.6 13.2c0-2 1.7-3.5 3.7-3.5 1.1 0 2.1.5 2.8 1.3" />
      <circle cx="12" cy="4.6" r="1.9" />
      <path d="M12 3.7v1.8M11.1 4.6h1.8" />
    </>
  ),
  workflow: <path d="M2.6 4.2 6.4 8l-3.8 3.8M8.6 4.2 12.4 8l-3.8 3.8" />,
  integrations: (
    <>
      <path d="M6.6 9.4 9.4 6.6" />
      <path d="M7.2 4.6 8.6 3.2a2.4 2.4 0 0 1 3.4 3.4L10.6 8M8.8 11.4 7.4 12.8A2.4 2.4 0 0 1 4 9.4L5.4 8" />
    </>
  ),
  operations: (
    <>
      <path d="M2.6 12.2a5.4 5.4 0 1 1 10.8 0" />
      <path d="M8 12.2 10.7 8.7" />
      <circle cx="8" cy="12.2" r=".9" fill="currentColor" stroke="none" />
    </>
  ),
  coverage: (
    <>
      <rect x="2.6" y="2.6" width="10.8" height="10.8" rx="1" />
      <path d="M8 2.6v10.8M2.6 8h10.8" />
    </>
  ),
  search: (
    <>
      <circle cx="7" cy="7" r="4.3" />
      <path d="M10.3 10.3 13.6 13.6" />
    </>
  ),
  brandMark: <path d="M8 2.4 14 13.4H2Z" fill="currentColor" stroke="none" />,
  verification: (
    <>
      <path d="M8 2.2 12.9 4v4.1c0 3.1-2 5.2-4.9 6.1-2.9-.9-4.9-3-4.9-6.1V4Z" />
      <path d="M5.9 8l1.5 1.5 2.7-3" />
    </>
  ),
}

/** Guards rendering when a slot carries either an icon name or ordinary text (an acronym, or a glyph kept
 * by an older localStorage record): text falls through and renders as itself. */
export const isIconName = (value: string): value is IconName =>
  Object.prototype.hasOwnProperty.call(shapes, value)

export const Icon = ({ name }: { name: IconName }) => (
  <svg
    viewBox="0 0 16 16"
    width="15"
    height="15"
    fill="none"
    stroke="currentColor"
    strokeWidth={1.4}
    strokeLinecap="round"
    strokeLinejoin="round"
    aria-hidden="true"
    focusable="false"
  >
    {shapes[name]}
  </svg>
)
