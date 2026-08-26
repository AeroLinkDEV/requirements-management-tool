# AeroLink product documentation

This directory contains documentation tightly coupled to the AeroLink application: architecture, operations, recovery, CI/testing, managed documentation, build-scoped behavior, scale/qualification foundations, and other implementation contracts.

For project/product-level orientation, use the repository-root [`PROJECT_STATE.md`](../../PROJECT_STATE.md) and the project documentation map at [`docs/README.md`](../../docs/README.md).

## Core references

- [`ARCHITECTURE.md`](ARCHITECTURE.md) — application architecture and boundaries.
- [`OPERATIONS.md`](OPERATIONS.md) — startup, diagnostics, backup, restore, migration and operational guidance.
- [`MERGING.md`](MERGING.md) — protected-branch/merge workflow.
- [`BROWSER_AND_BACKEND_FEEDBACK_TIME.md`](BROWSER_AND_BACKEND_FEEDBACK_TIME.md) — measured CI critical-path evidence and why current sharding/cadence exists.
- [`MANAGED_DOCUMENTATION_CENTER.md`](MANAGED_DOCUMENTATION_CENTER.md) — managed Word-document workflow.
- [`BUILD_SCOPED_WORKSPACES.md`](BUILD_SCOPED_WORKSPACES.md) — build-scoped workspace behavior.
- [`SCALE_FOUNDATION.md`](SCALE_FOUNDATION.md) — scale/performance foundation and evidence boundaries.
- [`API_TEST_INTENT_INVENTORY.md`](API_TEST_INTENT_INVENTORY.md) — API test-intent inventory.

Other files in this directory document focused implementation/qualification surfaces. They may describe a specific subsystem or measured checkpoint; the current code and accepted decisions remain authoritative when a historical implementation note has been superseded.

## Documentation authority

- Current product truth: [`../../PROJECT_STATE.md`](../../PROJECT_STATE.md)
- Accepted decisions: [`../../DECISIONS_AND_OPEN_QUESTIONS.md`](../../DECISIONS_AND_OPEN_QUESTIONS.md)
- Agent/repository safety: [`../../AGENTS.md`](../../AGENTS.md)
- Project history/lessons/reference/provenance: [`../../docs/`](../../docs/)
- Live backlog/findings: GitHub Issues

Do not create a dated restart handoff here as a second current-state system. Put active work in GitHub Issues and durable current architecture/operations knowledge in the appropriate maintained document.