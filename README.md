# Aerospace Development Assurance Platform

> **New here — human or model? Read [PROJECT_STATE.md](PROJECT_STATE.md) first.** It records what
> exists today, what is deliberately excluded, where delivery stands, and the known limitations. The
> documents indexed below are durable definitions and historical records; `PROJECT_STATE.md` describes
> the present.

This repository contains the working, local/on-premises AeroLink Aerospace Development Assurance Platform and its authoritative product-definition records. AeroLink manages controlled system and software requirements, change, review, traceability, verification evidence, immutable baselines, builds, documents, and release campaigns without claiming certification or tool qualification.

The production-oriented application uses React/TypeScript, ASP.NET Core, Entity Framework, and PostgreSQL. Its FMS workspace retains released version 1.5 as immutable history and version 1.6 as an explicitly user-controlled in-work successor.

Release evolution is user-controlled: authorized users plan an in-work successor (for example 1.6), approve the exact SCR/SWCR revisions they intend to include, assemble a candidate over an exact materialized predecessor baseline, complete verification and release approvals, and only then release it. The tool never auto-creates or auto-approves 1.6, 1.7, or later product baselines.

## One-click local startup

Three launchers, for three different purposes.

**Showing AeroLink to somebody:** double-click [`START_AEROLINK_PRODUCTION.bat`](START_AEROLINK_PRODUCTION.bat). It builds the website and serves it from the API on one origin at `http://127.0.0.1:5080`, which is how an on-premises install runs and the only path that exercises the built client.

**Letting colleagues open it from their own machines:** double-click [`START_AEROLINK_SHARED.bat`](START_AEROLINK_SHARED.bat). Same build, listening on every network interface instead of loopback, and it prints the `http://<this-machine>:5080` address to hand out. Windows Firewall drops inbound connections on that port until an administrator allows it in, once per machine; the launcher checks and prints the command if it is missing. Sharing is opt-in because the same run prints a known administrator password, loads demonstration data, and carries everything over plain HTTP.

**Working on AeroLink:** double-click [`START_AEROLINK.bat`](START_AEROLINK.bat). It starts or verifies PostgreSQL, the API, and the Vite development server; waits for the API to report the database reachable; opens `http://127.0.0.1:5173`; and writes diagnostic logs under `product/.local/logs/`.

All three are safe to run again while AeroLink is already running. Note that PostgreSQL must be installed once first — `product\scripts\Setup-Postgres.ps1`, described in [the product README](product/README.md).

Run [`BACKUP_AEROLINK.bat`](BACKUP_AEROLINK.bat) manually or through Windows Task Scheduler for a complete local backup. It captures PostgreSQL, controlled evidence, and runtime configuration into an integrity-manifested archive under `product/.local/backups/`, with 30-day retention by default. Production IT must copy these archives to protected storage and periodically prove restore.

Operational shortcuts are also provided for [stopping AeroLink](STOP_AEROLINK.bat), [diagnostics](AEROLINK_DIAGNOSTICS.bat), [backup verification](VERIFY_AEROLINK_BACKUP.bat), and isolated [restore validation](RESTORE_AEROLINK.bat). The safety model and production recovery procedure are documented in [Operations and recovery](product/docs/OPERATIONS.md).

## First Product Slice

The first usable vertical slice is system-level:

> SCR -> system requirement revisions -> review and approval -> baseline -> SYSRD -> system test procedure -> externally produced results and evidence -> traceability

This slice proved controlled change, immutable history, exact baselines, controlled document generation, verification evidence, and an end-to-end audit story. Implementation has now expanded through software HLRs, LLRs, SWCRs, typed traceability, and build-specific verification.

## The application

The [AeroLink product application](product/README.md) is the single software artifact in this
repository. It provides an ASP.NET Core API, executable domain rules, PostgreSQL persistence, isolated
SQLite tests, automated domain and browser tests, and a React application connected to live lifecycle
data.

A separate Phase 0.5 static-data prototype under `showcase/` was retired on 2026-07-24 once the
application surpassed it in both capability and visual maturity. See DEC-046. Its design intent
survives in the north-star mockups under `design/mockups` and in
[DESIGN_VISION_AND_DASHBOARDS.md](DESIGN_VISION_AND_DASHBOARDS.md); its narrative survives in
[SHOWCASE_STORY_FMS_3_3.md](SHOWCASE_STORY_FMS_3_3.md), retained as a historical record. Live
demonstrations use the `FMSLIVE` dataset described in
[FMS_LIVE_SHOWCASE_DATASET.md](FMS_LIVE_SHOWCASE_DATASET.md).

## Authoritative Documents

| Document | Purpose |
| --- | --- |
| [Project state](PROJECT_STATE.md) | **Start here.** What exists today, what is excluded, delivery status, known limitations |
| [Project vision](PROJECT_VISION.md) | Problem, audience, value, ambition, and success definition |
| [Scope and boundaries](SCOPE_AND_BOUNDARIES.md) | Current, future, and excluded capabilities |
| [Domain model and glossary](DOMAIN_MODEL_AND_GLOSSARY.md) | Shared vocabulary and lifecycle concepts |
| [Product principles](PRODUCT_PRINCIPLES.md) | Non-negotiable behavioral rules |
| [Design vision and dashboards](DESIGN_VISION_AND_DASHBOARDS.md) | North-star mockups, role-aware dashboards, trusted metrics, and showcase direction |
| [FMS 3.3 showcase story](SHOWCASE_STORY_FMS_3_3.md) | Historical Phase 0.5 narrative; superseded for live use by the FMS live dataset |
| [FMS live showcase dataset](FMS_LIVE_SHOWCASE_DATASET.md) | Deterministic released 1.5 lifecycle baseline and active 1.6 development program |
| [FMS 1.6 release campaign](FMS_1_6_RELEASE_CAMPAIGN.md) | Governed change, verification, evidence, readiness, review, and release workflow |
| [Controlled document publication standard](CONTROLLED_DOCUMENT_PUBLICATION_STANDARD.md) | Professional covers, approval provenance, front matter, body content, and rendering rules |
| [Security, identity, and electronic approval model](SECURITY_AND_IDENTITY_MODEL.md) | Authenticated users, Program roles, secure sessions, delegations, e-signatures, and security auditing |
| [Enterprise requirements-management benchmark](ENTERPRISE_REQUIREMENTS_MANAGEMENT_BENCHMARK.md) | Current market benchmark, AeroLink gap analysis, enterprise table stakes, and prioritized parity program |
| [Identifiers and requirement fields proposal](IDENTIFIERS_AND_REQUIREMENT_FIELDS_PROPOSAL.md) | Proposed numbering, revision display, and mandatory-field policy |
| [System-level workflow](SYSTEM_LEVEL_WORKFLOW.md) | Decision-complete first-slice behavior and paper scenarios |
| [Feature catalog](FEATURE_CATALOG.md) | Stable, phased capability inventory |
| [Release roadmap](RELEASE_ROADMAP.md) | Incremental delivery strategy and exit criteria |
| [Massive enterprise update report](MASSIVE_ENTERPRISE_UPDATE_REPORT.md) | Implemented scope, validation evidence, limitations, and next update |
| [Showcase and usability refresh](SHOWCASE_USABILITY_REFRESH_REPORT.md) | Readable design system, simplified shell, progressive disclosure, critical-surface redesign, and visual validation evidence |
| [Operations and recovery](product/docs/OPERATIONS.md) | Startup, stop, diagnostics, backup verification, isolated restore, and production recovery |
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

## Documentation baseline gate — met

Phase 0 established the product-definition baseline and is complete. Implementation began afterwards
and has since delivered the system-level slice, the software level, and the enterprise maturity work
described in [PROJECT_STATE.md](PROJECT_STATE.md).

The gate that was met, retained because it still describes what a good documentation change looks
like:

1. Every substantive source statement is accepted, deferred, excluded, recorded as a decision or assumption, or tracked as an open question.
2. The eight paper scenarios in [SYSTEM_LEVEL_WORKFLOW.md](SYSTEM_LEVEL_WORKFLOW.md) have unambiguous actors, inputs, state changes, outputs, and retained history.
3. Terminology and feature identifiers are consistent across the document set.
4. High-impact open questions required for the first product slice are resolved.
5. The documentation is reviewed and approved as the starting product-definition baseline.

Some open questions in [DECISIONS_AND_OPEN_QUESTIONS.md](DECISIONS_AND_OPEN_QUESTIONS.md) remain
recorded as open. Where implementation has since answered one in practice, close it with a decision
record rather than editing the question away.
