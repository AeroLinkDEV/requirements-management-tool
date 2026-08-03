# Scope and Boundaries

## Scope Model

Capabilities are divided into four categories: Phase 0 documentation, the first system-level product slice, later product capabilities, and explicit near-term exclusions. A later capability is not implicitly authorized for the first slice.

## Phase 0: Complete

Phase 0 delivered a reviewed, internally consistent Markdown product-definition baseline covering:

- vision and outcomes;
- scope and exclusions;
- domain language and behavioral principles;
- system-level lifecycle behavior;
- phased features and quality targets;
- accepted decisions, assumptions, and open questions; and
- traceability back to the supplied source documents.

Phase 0 itself contained no application code, database schema, user-interface design, or
technology-stack decision. Those followed after the baseline was approved, and the stack is now
recorded in [product/docs/ARCHITECTURE.md](product/docs/ARCHITECTURE.md).

## First Product Slice: System Level — delivered

This slice is implemented and proven end to end; implementation has since extended through the
software level and the enterprise maturity program. The list below remains the definition of the
slice's scope. It included:

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

Several entries below have since been delivered — software HLRs and LLRs, SWCRs and SWRD generation,
software verification artifacts, deeper release and configuration management, and external
integrations. Enterprise identity integration is partially delivered with its remainder formally
deferred (see the Workstream 4 decision record in
[AEROLINK_3_ENTERPRISE_LIFECYCLE_COMPLETION.md](AEROLINK_3_ENTERPRISE_LIFECYCLE_COMPLETION.md)).
Current status always lives in [PROJECT_STATE.md](PROJECT_STATE.md), not here.

The planned product direction preserved space for:

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
- **PRs:** The product provides the agreed Draft-to-independent-SQA-closure lifecycle, progressive rich fields,
  Unknown/No/Yes impact decisions, AND filters, internal history, and controlled PR links from SCRs, SWCRs,
  and System/HLR/LLR TCRs. Approved engineering changes appear as corrective actions and selected
  closure-supporting test results provide verification evidence. Optional classifications, attachments,
  containment/preventive action, saved views, and external issue integration remain later scope.
- **Code:** GitLab owns source, MRs, review, and commit content. AeroLink stores immutable build-scoped pointers
  from exact approved LLR revisions to GitLab merges, or a justified no-code disposition. Repository browsing,
  code editing/review, branch management, and GitLab enforcement remain out of scope.

Scope changes must be recorded in [DECISIONS_AND_OPEN_QUESTIONS.md](DECISIONS_AND_OPEN_QUESTIONS.md) and reflected in [FEATURE_CATALOG.md](FEATURE_CATALOG.md).
