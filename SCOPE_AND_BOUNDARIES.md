# Scope and Boundaries

## Scope Model

Capabilities are divided into four categories: Phase 0 documentation, the first system-level product slice, later product capabilities, and explicit near-term exclusions. A later capability is not implicitly authorized for the first slice.

## Phase 0: Current Work

The current deliverable is a reviewed, internally consistent Markdown product-definition baseline covering:

- vision and outcomes;
- scope and exclusions;
- domain language and behavioral principles;
- system-level lifecycle behavior;
- phased features and quality targets;
- accepted decisions, assumptions, and open questions; and
- traceability back to the supplied source documents.

Phase 0 contains no application code, database schema, user-interface design, or technology-stack decision.

## First Product Slice: System Level

The first usable slice includes:

- multiple programs and program-scoped access;
- stable, globally unique system requirement identities and immutable revision history;
- requirements containing formatted text and controlled images or figures;
- SCRs that introduce, modify, or retire one or more requirements;
- review comments, dispositions, rejection, rework, approval, and attribution;
- target-release assignment and controlled deferral of SCRs;
- selection of approved SCRs for a candidate baseline;
- exact baselines and baseline comparison;
- draft and approved SYSRD generation;
- versioned system test procedures and reusable links to system requirements;
- recorded or imported test executions, results, configurations, and evidence;
- pass, fail, and not-applicable outcomes, with additional operational states defined before implementation;
- links from relevant artifacts to PR references where PR integration is available;
- system traceability navigation and controlled reporting;
- roles, permissions, administration, audit history, backup, and recovery appropriate to the slice.

Automated execution of tests is outside this slice. Tests run in external environments; the platform controls procedures and captures or imports results and evidence.

## Later Product Capabilities

The planned product direction preserves space for:

- software HLRs and LLRs, including derived requirements;
- SWCRs and controlled SWRD generation;
- HLR, LLR, integration, and robustness verification artifacts;
- fuller PR lifecycle and PR-driven impact analysis;
- deeper release and configuration management;
- links to interfaces or upstream system artifacts;
- links to source components, Git commits, builds, and releases without necessarily storing source code;
- enterprise identity integration;
- external test-tool and data integrations;
- controlled planning and standards records if later justified;
- objective-oriented compliance evidence; and
- optional locally hosted AI assistance under human control.

These items require separate scope decisions and must not silently enter the initial slice.

## Explicit Near-Term Exclusions

The following are not part of Phase 0 or the first product slice:

- managing certification plans, development plans, or engineering standards;
- authoring or managing software architecture, design, source code, compilers, linkers, or builds;
- hosting Git repositories;
- automated test execution or control of test benches;
- tool qualification;
- claims of ARP4754 or DO-178 compliance or certification suitability;
- certification-authority liaison and objective-by-objective compliance management;
- automated decisions based on AI; and
- any AI integration in the initial implementation.

## Boundary Clarifications

- **Documents:** The platform generates documents from controlled artifact data; uploaded Word or PDF files are not the authoritative requirement database.
- **Deletion:** A user may retire a requirement from future effective baselines through an approved change. The platform must not physically erase its identity, revisions, links, rationale, or audit history.
- **Approval and baselines:** Approval of an artifact revision does not automatically place it in a release baseline. Baseline inclusion is a distinct controlled decision.
- **Testing:** A test procedure is a reusable controlled definition. A test execution is a historical occurrence using a specific procedure revision and configuration.
- **Standards:** Standards inform product language and rigor. Program-specific processes remain authoritative for actual certification use.
- **PRs:** PRs are strategically important but the complete PR workflow is later scope. The initial model may retain external PR identifiers and links so the future relationship is not blocked.

Scope changes must be recorded in [DECISIONS_AND_OPEN_QUESTIONS.md](DECISIONS_AND_OPEN_QUESTIONS.md) and reflected in [FEATURE_CATALOG.md](FEATURE_CATALOG.md).
