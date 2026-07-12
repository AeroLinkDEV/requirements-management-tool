# Project Goal: Aerospace Development Assurance Platform

Build a secure, web-based, on-premises platform that manages controlled aerospace system and software lifecycle data across multiple programs.

The platform will become the authoritative source for versioned artifacts, controlled changes, reviews, approvals, baselines, verification evidence, typed traceability, audit history, and generated lifecycle documents. It is informed by ARP4754 and DO-178 concepts but does not initially claim compliance, certification suitability, or tool qualification.

## First Usable Product Slice

Prove one complete system-level chain:

> SCR -> system requirement revisions -> review and approval -> baseline -> SYSRD -> system test procedure -> externally produced results and evidence -> traceability

This slice must demonstrate that users can:

1. create and revise system requirements without reusing identities or overwriting history;
2. introduce, modify, or retire requirements through reviewed and approved SCRs;
3. defer an SCR or select approved SCRs for a target release;
4. construct and approve an immutable successor baseline from exact revisions;
5. generate a draft-watermarked or approved SYSRD from a named baseline;
6. create, revise, review, and approve reusable system test procedures;
7. enter or import externally produced executions, configurations, outcomes, and evidence;
8. preserve failures, amendments, PR references, and retests without rewriting history;
9. identify missing, suspect, failed, incomplete, and unpassed trace chains; and
10. prove who changed, reviewed, approved, selected, imported, executed, or generated every controlled item.

## Product Direction

After the system-level model is proven, the platform may extend to:

- HLRs, LLRs, derived requirements, SWCRs, SWRDs, and software verification;
- complete PR lifecycle and PR-driven impact analysis;
- broader configuration, release, enterprise-identity, test, Git/build-reference, and external-system integrations; and
- optional locally hosted AI suggestions under explicit qualified human control.

Plans and standards management, software architecture/code management, automated test execution, tool qualification, objective-by-objective compliance management, and AI are outside the first slice.

## Core Rules

- Structured artifact records and baselines are authoritative; documents are generated outputs.
- Stable artifact identity and revision identity are separate.
- Approved history and released baselines are immutable.
- Requirement “deletion” is controlled retirement from future effective baselines, never historical erasure.
- Artifact approval and baseline inclusion are separate decisions.
- Trace links are typed, directional where appropriate, version-aware, reviewable, and auditable.
- A test procedure and an execution of that procedure are distinct controlled records.
- Controlled documents identify exact inputs, template/generator revisions, approval state, and integrity hash.
- The product must prevent, detect, expose, and support recovery from errors rather than claim infallibility.

## Current Phase

The project is in documentation and domain validation. No application code, technical architecture, database schema, or UI design should begin until the foundational documents and high-impact open questions have been reviewed and baselined.

Start with [README.md](README.md), then review [SYSTEM_LEVEL_WORKFLOW.md](SYSTEM_LEVEL_WORKFLOW.md), [FEATURE_CATALOG.md](FEATURE_CATALOG.md), and [DECISIONS_AND_OPEN_QUESTIONS.md](DECISIONS_AND_OPEN_QUESTIONS.md).
