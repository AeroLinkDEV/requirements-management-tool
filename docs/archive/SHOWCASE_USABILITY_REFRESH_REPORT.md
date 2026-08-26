# AeroLink Showcase & Usability Refresh

Status: delivered and validated on 2026-07-13.

> **Historical snapshot.** This records the visual refresh of the **product application** — it is not
> about the retired `showcase/` prototype, despite the name. "Showcase-critical surfaces" here means the
> product screens used in demonstrations. The design system it introduced is still in force; the test
> counts and validation results were true on that date and are not maintained.
>
> **Navigation and dashboard details below are also historical.** PRs #166–#168 later introduced Projects
> and Software Builds landing pages, a three-way System/Software/Verification Command Center, and deliberately
> hid Problem Reports, Product Versions, Candidate Baselines and the Change Request Software Builds view.
> Current product truth is in [PROJECT_STATE.md](PROJECT_STATE.md); the former
> [2026-08-10 handoff](docs/archive/CURRENT_PRODUCT_HANDOFF_2026-08-10.md) is historical restart context.

## Outcome

AeroLink now presents its existing lifecycle depth with a readable visual hierarchy instead of treating maximum information density as the default. The refresh changes presentation and navigation without weakening controlled workflows, immutable history, authority checks, or artifact provenance.

The separately proposed guided **Start Showcase** journey was deliberately excluded at the product owner's request. All other approved visual-cleanup work is included.

## Shared visual system

- Added application-wide typography, color, spacing, radius, surface, focus, and motion tokens.
- Set ordinary content and controls on a 14-16 px scale, with 12 px as the minimum for meaningful secondary metadata on the showcase-critical surfaces.
- Increased control heights and interaction targets, improved focus visibility, and retained reduced-motion support.
- Standardized page headings, explanatory text, surfaces, borders, cards, table rows, and status treatments.

## Application shell and navigation

- Repaired the global context bar. It is now a single 48 px horizontal row rather than a vertically stacked banner.
- Long Program and Project names truncate instead of expanding the shell.
- Context now exposes the active release state and a clearly named copy-link action.
- Increased sidebar labels to 12-14 px and navigation targets to approximately 40 px.
- Kept Command Center and My Work permanently available while collapsing discipline groups that are not relevant to the current page.
- Removed the sticky-footer overlap that obscured the last navigation group.
- Fixed global search so it is mounted and functional on Command Center as well as routed workspaces.

## Showcase-critical surfaces

### Command Center

- Replaced generic workspace-readiness content with release-specific review, draft, and readiness attention when lifecycle data exists.
- Limited the change flow to the five most recent records and added an explicit complete-history action.
- Enlarged release, inventory, metric, activity, and attention typography.

### Change Requests

- Replaced the mixed-purpose `Artifact & Build Explorer` title with scope-specific `System Change Requests` or `Software Change Requests` headings.
- Kept SRCR, requirement, and build history as clear tabs.
- Shows `Record Software Build` only when Software Builds is the active job.
- Increased table headings, row content, states, timestamps, filters, and build-provenance content to readable sizes.

### Requirements Workspace

- Reduced the header to one `Workspace tools` disclosure rather than four competing actions.
- Added accessible labels to table/document view controls.
- Collapsed saved views until requested while preserving immediate specification navigation.
- Enlarged filters, table rows, metadata, tags, paging, inspector content, discussions, modals, and governed bulk/import controls.
- Preserved the high-density repository capability through internal scrolling rather than shrinking text.

### Traceability and Verification

- Changed Traceability to a readable single-column requirement scan.
- Each requirement now summarizes its lineage and verification counts; detailed relationships, procedures, executions, and evidence expand on demand or automatically for a focused search.
- Verification now presents Requirement Coverage, Test Procedures, and Execution History as three focused tabs instead of simultaneous competing panels.
- Recording a result moves to execution history; users can return to coverage to confirm the recalculated state.

### Release Campaign

- Foregrounds unresolved release gates and moves completed-gate evidence into a disclosure.
- Keeps the release execution workbench, completed impact dispositions, campaign history, and release comparison available on demand.
- Preserves verification-build and ordered-approval controls in the decision rail.
- Applies the readable scale to the expanded execution workbench as well as the primary release page.

## Executable usability gate

`product/client/tests/showcase-usability.spec.ts` proves the visual contract at 1440 x 900:

- no document-level horizontal overflow on the checked showcase surfaces;
- context bar at or below 50 px with horizontal breadcrumb flow;
- no visible meaningful text below 12 px on the checked Change Request surface;
- build creation appears only in the Builds job;
- Requirements tools and saved information use progressive disclosure;
- Traceability records are collapsed until requested;
- Verification exposes one active work panel;
- completed release gates and the execution workbench are disclosed on demand.

## Final validation

- Domain tests: 30 passed.
- Infrastructure tests: 16 passed.
- Client lint: passed.
- TypeScript and production Vite build: passed.
- Playwright: 12 passed, including the new showcase-usability journey.
- Live diagnostics: PostgreSQL, API, authentication, client, 17 migrations, disk, backup recency, and evidence storage all passed.

The live local site remains available at `http://127.0.0.1:5173`.
