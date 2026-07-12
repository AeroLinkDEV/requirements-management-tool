# Aerospace Development Assurance Platform

This repository is the documentation-first foundation for a future secure, on-premises platform that manages controlled aerospace system and software lifecycle data.

The product definition reached its initial working baseline and implementation has begun. The repository now contains both the preserved documentation foundation and an early production-oriented application skeleton.

## First Product Slice

The first usable vertical slice is system-level:

> SCR -> system requirement revisions -> review and approval -> baseline -> SYSRD -> system test procedure -> externally produced results and evidence -> traceability

This slice proved controlled change, immutable history, exact baselines, controlled document generation, verification evidence, and an end-to-end audit story. Implementation has now expanded through software HLRs, LLRs, SWCRs, typed traceability, and build-specific verification.

## Interactive Showcase

The local [AeroLink FMS 3.3 showcase](showcase/README.md) implements the Phase 0.5 fictional walkthrough defined in [SHOWCASE_STORY_FMS_3_3.md](SHOWCASE_STORY_FMS_3_3.md). It is an interactive design-validation prototype, not production software.

## Real Product

The [AeroLink product application](product/README.md) is separate from the original visual showcase. It currently provides an ASP.NET Core API, executable domain rules, PostgreSQL local persistence, isolated SQLite tests, automated domain and browser tests, and a React application connected to live lifecycle data.

## Authoritative Documents

| Document | Purpose |
| --- | --- |
| [Project vision](PROJECT_VISION.md) | Problem, audience, value, ambition, and success definition |
| [Scope and boundaries](SCOPE_AND_BOUNDARIES.md) | Current, future, and excluded capabilities |
| [Domain model and glossary](DOMAIN_MODEL_AND_GLOSSARY.md) | Shared vocabulary and lifecycle concepts |
| [Product principles](PRODUCT_PRINCIPLES.md) | Non-negotiable behavioral rules |
| [Design vision and dashboards](DESIGN_VISION_AND_DASHBOARDS.md) | North-star mockups, role-aware dashboards, trusted metrics, and showcase direction |
| [FMS 3.3 showcase story](SHOWCASE_STORY_FMS_3_3.md) | Canonical fictional data and walkthrough for the interactive concept |
| [FMS live showcase dataset](FMS_LIVE_SHOWCASE_DATASET.md) | Deterministic released 1.5 lifecycle baseline and active 1.6 development program |
| [Identifiers and requirement fields proposal](IDENTIFIERS_AND_REQUIREMENT_FIELDS_PROPOSAL.md) | Proposed numbering, revision display, and mandatory-field policy |
| [System-level workflow](SYSTEM_LEVEL_WORKFLOW.md) | Decision-complete first-slice behavior and paper scenarios |
| [Feature catalog](FEATURE_CATALOG.md) | Stable, phased capability inventory |
| [Release roadmap](RELEASE_ROADMAP.md) | Incremental delivery strategy and exit criteria |
| [Quality attributes](QUALITY_ATTRIBUTES.md) | Security, integrity, operations, and production targets |
| [Decisions and open questions](DECISIONS_AND_OPEN_QUESTIONS.md) | Accepted decisions, assumptions, and unresolved choices |
| [Source material traceability](SOURCE_MATERIAL_TRACEABILITY.md) | Disposition of the original Word and Markdown inputs |
| [Project goal](PROJECT_GOAL_Aerospace_Development_Assurance_Platform.md) | Concise, reconciled statement of the goal |

## Working Conventions

- Markdown in Git is authoritative. Generated Word or PDF copies are snapshots, not source records.
- Product decisions are recorded in [DECISIONS_AND_OPEN_QUESTIONS.md](DECISIONS_AND_OPEN_QUESTIONS.md); they are not silently changed in another document.
- New capabilities receive a stable feature identifier in [FEATURE_CATALOG.md](FEATURE_CATALOG.md).
- Terms use the definitions in [DOMAIN_MODEL_AND_GLOSSARY.md](DOMAIN_MODEL_AND_GLOSSARY.md).
- `SYSRD` means System Requirements Document; `SWRD` means Software Requirements Document.
- Requirements use normative language deliberately: **must** is mandatory, **should** is preferred, and **may** is optional.
- Standards references inform terminology and expected rigor but do not constitute a compliance or certification claim.
- Source Word documents remain unmodified in the repository root for provenance during this initial consolidation.

## Documentation Baseline Gate

Phase 0 is ready to baseline when:

1. Every substantive source statement is accepted, deferred, excluded, recorded as a decision or assumption, or tracked as an open question.
2. The eight paper scenarios in [SYSTEM_LEVEL_WORKFLOW.md](SYSTEM_LEVEL_WORKFLOW.md) have unambiguous actors, inputs, state changes, outputs, and retained history.
3. Terminology and feature identifiers are consistent across the document set.
4. High-impact open questions required for the first product slice are resolved.
5. The documentation is reviewed and approved as the starting product-definition baseline.
