# Quality Attributes

These attributes define how trustworthy the product must become. Exact numeric service levels will be set during technical planning; the behavioral expectations apply now.

## Security and Access Control

The platform must:

- use authenticated identities and secure session handling;
- enforce least privilege by program, role, artifact type, workflow state, and action;
- prevent unauthorized cross-program discovery through search, links, reports, or attachments;
- protect credentials, tokens, sensitive configuration, stored data, backups, and data in transit;
- audit security-relevant and administrative actions;
- support timely account and session revocation; and
- allow future enterprise identity integration without weakening artifact attribution.

Security acceptance will include authorization-boundary, session, attachment, input-validation, dependency, configuration, and audit tests.

## Auditability and Non-Repudiation

Every material lifecycle and administrative action must be attributable to an identity and time. Audit records must be append-only through normal product interfaces and sufficiently detailed to explain relevant before/after state.

System administrators may operate storage and recovery mechanisms but must not be able to silently revise controlled application history. Privileged maintenance and recovery actions require separate operational logging and reconciliation.

## Data Integrity and Transactional Consistency

The product must prevent partial or contradictory controlled operations. Examples include:

- approval without the reviewed revision;
- baseline creation with an unresolved or unapproved member;
- a link referencing a missing revision;
- document metadata disagreeing with source baseline contents; or
- a completed test execution whose procedure revision or evidence changes silently.

Multi-record lifecycle transitions must be transactional or otherwise provide a provably consistent recovery path. Identifiers are never reused.

## Immutability and Historical Reconstruction

Approved revisions, released baselines, completed executions, approvals, and controlled output records must be immutable. The system must reconstruct the effective contents and relationships of historical baselines without applying later changes retroactively.

Corrections use successor revisions, amendments, executions, baselines, or outputs with explicit relationships to the original.

## Recoverability and Continuity

The on-premises product must support documented backup, restore, integrity verification, and disaster-recovery procedures covering structured data, attachments, generated outputs, identity/configuration data, and audit history.

Restore tests must demonstrate that:

- exact baselines and historical artifacts remain navigable;
- attachments and generated-output hashes still verify;
- identifier generation cannot collide or regress; and
- reconciliation after restore is visible rather than creating a silent alternate history.

Recovery-point and recovery-time objectives remain open until deployment needs are understood.

## Deterministic Document Generation

Controlled documents must be produced from exact baseline contents using versioned templates and generator behavior. Each output records source baseline, template revision, generator version, time, approval state, and cryptographic hash.

Byte-for-byte reproducibility is a desired direction but not yet assumed across operating systems, PDF engines, timestamps, or metadata. The project must decide whether “reproducible” means identical bytes or demonstrably identical controlled content with explained metadata differences.

Draft outputs must be unambiguously marked. Approved output generation must fail closed if required controlled inputs or provenance are missing.

## Performance and Concurrency

The production ambition is at least 150 concurrent authenticated users across multiple programs. This is a later production acceptance target, not an early prototype gate.

Technical planning must define representative data volumes and measurable latency targets for:

- common artifact reads and edits;
- identifier allocation and revision creation;
- review and approval actions;
- search and trace navigation;
- candidate-baseline construction and comparison;
- completeness/impact analysis; and
- background document generation and large reports.

Concurrency tests must prove that competing edits, approvals, selections, and identifier requests cannot corrupt or silently overwrite controlled data.

## Availability and Graceful Failure

The platform must fail visibly and safely. A failed transition, import, or generation job must not leave an item appearing approved, baselined, complete, or successfully generated when it is not.

Long-running generation and analysis work should not block ordinary interactive use. Operators and users need actionable status, retry behavior, correlation identifiers, and error history appropriate to their roles.

## Maintainability and Evolvability

The product should begin as a modular system with clear domain boundaries and controlled migrations.
This document deliberately does not mandate a language, framework, database, or deployment packaging;
the stack was chosen separately and is recorded in
[product/docs/ARCHITECTURE.md](../../product/docs/ARCHITECTURE.md).

Maintainability requires:

- automated unit, integration, end-to-end, migration, security, recovery, and performance tests;
- versioned data and document migrations with rollback/recovery strategy;
- documented operational procedures;
- observable background jobs and integrations; and
- the ability to add software-level artifact types without weakening system-level history.

## On-Premises Operability

The production platform must be deployable and supportable within customer-controlled infrastructure. Technical planning must cover installation, upgrades, configuration, identity, certificates, secrets, storage, backup, monitoring, logging, capacity, and offline/restricted-network operation as applicable.

An administration portal must support legitimate configuration and support activities without creating a path around controlled lifecycle rules.

## Usability and Human Error Prevention

The interface must make identity, revision, state, baseline, approval, applicability, and draft status visible. Destructive-looking actions must explain their actual controlled meaning. Users must be warned about suspect links, stale revisions, incomplete evidence, conflicts, and irreversible approval/baseline actions before commitment.

Bulk operations and imports require preview, validation, clear error reporting, and an auditable result.

## Accessibility

AeroLink targets **WCAG 2.2 Level AA**. This is a commitment, not an aspiration, and it is measured on rendered pixels rather than asserted:

- text contrast of at least 4.5:1, and 3:1 for large text (>=24px, or >=18.66px bold), per SC 1.4.3;
- interactive targets of at least 24x24 CSS pixels, per SC 2.5.8;
- a visible focus indicator on every interactive element;
- no information conveyed by colour alone; and
- a 12px floor for any text a person is expected to read.

Every criterion above is enforced across all routed surfaces, in both information densities, by `product/client/tests/design-system.spec.ts` and `product/client/tests/accessibility-contrast.spec.ts`. Elements whose effective background is a gradient or image cannot be resolved from computed style; the contrast audit counts and reports them rather than treating them as passes, and they require visual review.

Not yet covered by automated checks, and therefore not claimed: screen-reader semantics beyond roles and labels already in use, reflow at 320px (the client sets a 960px minimum width), and text spacing overrides per SC 1.4.12.

## Validation Strategy

Quality will be demonstrated with known reference projects and adverse scenarios, including:

- concurrent modification and approval attempts;
- unauthorized cross-program access;
- incomplete or conflicting SRCR selections;
- missing and suspect traces;
- failed imports and document jobs;
- backup and restoration;
- migration from earlier data versions;
- failed test followed by retest; and
- reconstruction of an old release after later changes.

No certification report or compliance decision may depend on uncontrolled AI output.

## Executable Release Gate

Mutation-oriented browser tests use a dedicated API port and disposable SQLite database; they never reuse the live PostgreSQL API. A release candidate must pass the domain and persistence tests, client lint, production build, complete Playwright suite, safe migration against a restored PostgreSQL copy, live migration, authenticated live smoke test, and Git diff review.

Operational recovery is exercised in layers: archive/manifest verification, isolated database and evidence restore, migration of the restored copy, then attended production recovery only when required. Restore scripts must constrain target names and paths, and they must never select the authoritative database through a default test invocation.
