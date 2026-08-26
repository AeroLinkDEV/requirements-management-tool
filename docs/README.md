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

The repository-hygiene program is organizing long-lived product-definition material under `docs/product-definition/`. Until that move is complete, some authoritative product-definition files remain at repository root. Follow links from `PROJECT_STATE.md`/README and do not infer authority from path alone during the transition.

### Reference, showcase, and provenance

The hygiene program is establishing:

- `docs/reference/` for durable reference/benchmark material;
- `docs/showcase/` for FMS showcase data/story material;
- `docs/provenance/` for source-material traceability and immutable original inputs.

Some of these files remain at repository root until their references and runtime dependencies have been audited.

### Historical records

[`archive/`](archive/README.md) contains dated handoffs, completed audits, superseded status snapshots, agent work logs, and other useful history removed from the repository front door. Archived content is evidence of what was believed/delivered at a point in time; it is **not** current product authority. The archive index records each document's former role, current authority, and why the historical record remains useful.

### Implementation and operations

Use [`../product/docs/`](../product/docs/) for documentation that is tightly coupled to the application, including architecture, operations/recovery, merging, CI feedback time, managed documentation, scale/testing, and other implementation contracts.

## Authority rule

When a historical document disagrees with current code/accepted decisions, do not “average” the two. Refresh current `main`, consult the decision log and current scoped issue, and treat the historical record as history.

## Where new information belongs

| Information | Home |
| --- | --- |
| What AeroLink does now | `PROJECT_STATE.md` |
| Why a product decision exists | `DECISIONS_AND_OPEN_QUESTIONS.md` |
| Active defect / finding / work item | GitHub Issue |
| Repository/agent safety | `AGENTS.md` |
| Durable lesson learned | `docs/ENGINEERING_LESSONS.md` |
| Major historical milestone | `docs/PROJECT_HISTORY.md` |
| Product/reference/showcase/provenance knowledge | `docs/` |
| App architecture/operations/testing | `product/docs/` |
| Dated handoff/audit/status snapshot | `docs/archive/` |

Do not create a new root-level dated handoff as a second current-state system.
