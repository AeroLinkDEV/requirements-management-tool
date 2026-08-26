# AeroLink documentation map

This directory contains durable project/product knowledge that should remain useful across implementation cycles. It is intentionally separate from `product/docs/`, which contains documentation tightly coupled to application architecture, operation, testing, and deployment.

## Start here

For the current product, do **not** begin with a dated handoff or old audit report.

1. [`../PROJECT_STATE.md`](../PROJECT_STATE.md) — current product truth.
2. [`../DECISIONS_AND_OPEN_QUESTIONS.md`](../DECISIONS_AND_OPEN_QUESTIONS.md) — accepted product decisions and unresolved questions.
3. [`../AGENTS.md`](../AGENTS.md) — repository operating/safety contract for coding agents.
4. GitHub Issues — live backlog, active findings and implementation contracts.

## Documentation taxonomy

### Current product state

`PROJECT_STATE.md` at repository root is the single living product-level snapshot. It is deliberately concise and should be refreshed when the supported architecture or major product boundary changes.

### Product decisions

`DECISIONS_AND_OPEN_QUESTIONS.md` at repository root is append-only authority for accepted product decisions. If a decision changes, add a superseding decision rather than rewriting history.

### Project history

[`PROJECT_HISTORY.md`](PROJECT_HISTORY.md) records major milestones and architectural transitions: how AeroLink reached its current shape without forcing readers through every dated handoff.

### Engineering lessons

[`ENGINEERING_LESSONS.md`](ENGINEERING_LESSONS.md) captures durable lessons that have already cost time or defects to learn. These are not release notes; they are guidance intended to prevent repeated mistakes.

### Durable product definition

[`product-definition/`](product-definition/README.md) contains long-lived product intent, principles, scope/boundaries, domain vocabulary, capability contracts, workflow definitions, security/identity direction, controlled-publication policy, and design direction.

Its contents can remain authoritative as product-definition records without being live status records. Current delivered behavior still comes from `PROJECT_STATE.md`, accepted decisions, current code/tests, and GitHub state.

### Reference material

[`reference/`](reference/README.md) contains durable technical overview, historical market/enterprise benchmark material, and retained roadmap/phase evidence. These records are useful context but are not the live backlog.

### Showcase material

[`showcase/`](showcase/README.md) contains the maintained synthetic FMS demonstration guidance and dataset/release-campaign contracts. The retired static-prototype story and completed historical usability reports live in the archive.

### Provenance

[`provenance/`](provenance/README.md) contains source-material traceability and the byte-preserved original Word inputs under `provenance/original-inputs/`. Original inputs are historical provenance, not current product specifications.

### Historical records

[`archive/`](archive/README.md) contains dated handoffs, completed audits, superseded status snapshots, agent work logs, retired showcase records, completed acceptance/increment reports, and other useful history removed from the repository front door. Archived content is evidence of what was believed/delivered at a point in time; it is **not** current product authority.

### Visual reference

- [`mockups/`](mockups/) — retained visual/mockup reference material.
- [`overview-video/`](overview-video/) — overview-video assets/material.

### Implementation and operations

Use [`../product/docs/`](../product/docs/) for documentation that is tightly coupled to the application, including architecture, operations/recovery, merging, CI feedback time, managed documentation, scale/testing, and other implementation contracts.

`REMOTE_DEMO_OPERATOR.md` remains at the top of `docs/` as a stable operator-facing path tied to the protected
remote-demo launchers. The #783 launcher audit confirmed that preserving this established path is safer than a
cosmetic move.

## Authority rule

When a historical document disagrees with current code/accepted decisions, do not “average” the two. Refresh current `main`, consult the decision log and current scoped issue, and treat the historical record as history.

## Repository-layout guard

Run `powershell.exe -NoProfile -ExecutionPolicy Bypass -File product/scripts/Test-RepositoryLayout.ps1` after
documentation changes. It enforces the five canonical root files, the explicit root compatibility/community-file
allow-list, the documentation taxonomy, the 15 stable Windows launcher paths, and relative links in the maintained
current-document set. Link checking URI-decodes local targets and ignores external, mail, data, and anchor-only links.
The historical files under `docs/archive/` are intentionally outside that maintained-content link sweep except for
`docs/archive/README.md`; the archive index may link to individual historical records. Current `README.md` and
`PROJECT_STATE.md` may link to the archive index but must not present an individual archived handoff or audit as
current authority.

Do not add a new root narrative Markdown file by analogy with an existing compatibility shim. New root narrative is
permitted only for an explicit current-authority or compatibility reason, with the guard allow-list and this taxonomy
remaining truthful. Active findings belong in GitHub Issues; accepted long-lived decisions belong in the append-only
decision log; current product truth belongs in `PROJECT_STATE.md`; durable lessons/history/product-definition/reference/
showcase/provenance belong in their existing `docs/*` homes; implementation and operator documentation belongs in
`product/docs/`. Historical records remain discoverable in `docs/archive/` but are never current authority.

## Where new information belongs

| Information | Home |
| --- | --- |
| What AeroLink does now | `PROJECT_STATE.md` |
| Why a product decision exists | `DECISIONS_AND_OPEN_QUESTIONS.md` |
| Active defect / finding / work item | GitHub Issue |
| Repository/agent safety | `AGENTS.md` |
| Durable lesson learned | `docs/ENGINEERING_LESSONS.md` |
| Major historical milestone | `docs/PROJECT_HISTORY.md` |
| Durable product definition | `docs/product-definition/` |
| Technical/market/roadmap reference | `docs/reference/` |
| Synthetic demonstration guidance/data | `docs/showcase/` |
| Original source inputs / intent trace | `docs/provenance/` |
| App architecture/operations/testing | `product/docs/` |
| Dated handoff/audit/status/retired report | `docs/archive/` |

Do not create a new root-level dated handoff as a second current-state system.
