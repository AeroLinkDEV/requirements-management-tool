# AeroLink product-definition documentation

This directory contains durable product-definition records: the long-lived intent, vocabulary, boundaries, capability contracts, workflow concepts, and design principles that remain useful across implementation cycles.

These documents are **not** the live backlog and they do not outrank current code or accepted decisions. For what AeroLink does now, start at [`../../PROJECT_STATE.md`](../../PROJECT_STATE.md). For accepted decisions and supersession history, use [`../../DECISIONS_AND_OPEN_QUESTIONS.md`](../../DECISIONS_AND_OPEN_QUESTIONS.md). Active work belongs in GitHub Issues.

## Core product definition

- [`PROJECT_VISION.md`](PROJECT_VISION.md) — long-term product vision.
- [`PROJECT_GOAL_Aerospace_Development_Assurance_Platform.md`](PROJECT_GOAL_Aerospace_Development_Assurance_Platform.md) — retained detailed statement of product intent.
- [`PRODUCT_PRINCIPLES.md`](PRODUCT_PRINCIPLES.md) — durable product principles.
- [`SCOPE_AND_BOUNDARIES.md`](SCOPE_AND_BOUNDARIES.md) — explicit included/excluded product scope.
- [`DOMAIN_MODEL_AND_GLOSSARY.md`](DOMAIN_MODEL_AND_GLOSSARY.md) — lifecycle vocabulary and domain concepts.
- [`FEATURE_CATALOG.md`](FEATURE_CATALOG.md) — stable capability inventory; not a status record.
- [`QUALITY_ATTRIBUTES.md`](QUALITY_ATTRIBUTES.md) — non-functional product goals and evidence posture.
- [`SYSTEM_LEVEL_WORKFLOW.md`](SYSTEM_LEVEL_WORKFLOW.md) — durable system-level lifecycle workflow definition.
- [`SECURITY_AND_IDENTITY_MODEL.md`](SECURITY_AND_IDENTITY_MODEL.md) — identity/security model and boundaries.

## Controlled output and traceability direction

- [`CONTROLLED_DOCUMENT_PUBLICATION_STANDARD.md`](CONTROLLED_DOCUMENT_PUBLICATION_STANDARD.md) — controlled publication principles.
- [`LLR_TO_CODE_TRACEABILITY_PROPOSAL.md`](LLR_TO_CODE_TRACEABILITY_PROPOSAL.md) — delivered LLR-to-code traceability direction and retained rationale.
- [`IDENTIFIERS_AND_REQUIREMENT_FIELDS_PROPOSAL.md`](IDENTIFIERS_AND_REQUIREMENT_FIELDS_PROPOSAL.md) — implemented identifier decisions plus retained field-policy rationale/proposals.

## Long-lived programme/design records

- [`AEROLINK_3_ENTERPRISE_LIFECYCLE_COMPLETION.md`](AEROLINK_3_ENTERPRISE_LIFECYCLE_COMPLETION.md) — long-lived enterprise lifecycle completion contract. Current delivery status is in `PROJECT_STATE.md`/GitHub, not this contract.
- [`DESIGN_VISION_AND_DASHBOARDS.md`](DESIGN_VISION_AND_DASHBOARDS.md) — north-star UX/design direction; mockups are guidance rather than authority to restore retired surfaces.

## Authority rule

A document can remain valuable here even when part of its implementation-status language is historical. Do not infer current routes, open issues, or delivered state from an old proposal/contract sentence. Current truth is `PROJECT_STATE.md` + current code/tests + accepted decisions + GitHub state.
