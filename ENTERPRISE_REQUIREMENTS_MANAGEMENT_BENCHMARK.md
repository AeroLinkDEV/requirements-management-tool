# Enterprise Requirements Management Benchmark

**Benchmark date:** 2026-07-12  
**Status:** Product-direction baseline  
**Purpose:** Define the enterprise capabilities AeroLink must provide to be credible beside established requirements-management platforms without copying their unnecessary complexity.

> **Historical market benchmark.** This comparison and its gap analysis are a 2026-07-12 planning input,
> not the current implementation scorecard. Several listed gaps were delivered in later increments. Use
> [AeroLink 3 Implementation Status](AEROLINK_3_IMPLEMENTATION_STATUS.md) and the
> [current handoff](CURRENT_PRODUCT_HANDOFF_2026-08-03.md) for present claims and deliberate boundaries.

## Executive finding

AeroLink's strongest current capabilities are controlled change packages, attributable ordered approval, exact immutable baselines, release-readiness gates, version-aware lifecycle evidence, professional controlled publications, and a realistic large FMS dataset. Those foundations compare favorably with the governance story of mature tools.

The largest competitive gap is the daily requirements-engineering workspace. Established products make it efficient for thousands of users to structure, author, classify, review, search, filter, bulk-change, reuse, exchange, and report on very large specifications. AeroLink currently proves controlled lifecycle behavior but does not yet offer the configurable authoring, collaboration, interchange, product-line configuration, and operational surfaces expected of an enterprise requirements platform.

Enterprise parity therefore requires two things at once:

1. Preserve AeroLink's stronger-than-average assurance controls.
2. Add the high-throughput engineering capabilities people use every working day.

## Platforms reviewed

The comparison uses current first-party product pages and documentation. Marketing claims are treated as claims; documented product behavior is weighted more heavily.

| Platform | Distinctive strength relevant to AeroLink | Primary evidence |
|---|---|---|
| IBM Engineering Requirements Management DOORS Next | Deep configuration management with components, streams, baselines, change sets, global configurations, version-correct links, OSLC, ReqIF, custom artifact types/attributes, reporting, and suspect-link analysis | [DOORS Next overview](https://www.ibm.com/docs/en/engineering-lifecycle-management-suite/doors-next/7.1.0?topic=overview-doors-next), [configuration management](https://www.ibm.com/docs/en/engineering-lifecycle-management-suite/doors-next/7.1.0?topic=overview-configuration-management), [global configurations](https://www.ibm.com/docs/en/engineering-lifecycle-management-suite/lifecycle-management/7.1.0?topic=local-global-configuration) |
| Siemens Polarion ALM | Document-oriented authoring through LiveDocs, configurable workflows, unified requirements/test/issue lifecycle, automatic change control, traceability, dashboards, and audit-oriented reporting | [Polarion ALM](https://www.siemens.com/en-us/products/polarion/) |
| Jama Connect | Strong usability, online review experience, live traceability, reuse and synchronization, variant categories, baselines, test management, dashboards, custom reports, and partner exchange | [Jama feature guide](https://help.jamasoftware.com/ah/en/getting-to-know-jama-connect-features.html), [reuse and synchronization](https://help.jamasoftware.com/ah/en/getting-to-know-jama-connect-features/reuse-and-synchronization.html), [test management](https://www.jamasoftware.com/solutions/test-management/) |
| PTC Codebeamer | All-in-one requirements/risk/test ALM, document view, Review Hub, suspected-link processing, streams and merge, scalable product-line/variant management, ReqIF round trips, and PLM/digital-thread integration | [Codebeamer](https://www.ptc.com/en/products/codebeamer), [Codebeamer 3.2 help](https://support.ptc.com/help/codebeamer/r3.2/en/) |
| Perforce ALM | Integrated requirements, test, and issue management; configurable workflow automation; automatic trace matrices; impact analysis; reports; and pragmatic Jira/Jenkins integrations | [Perforce ALM](https://www.perforce.com/products/helix-alm), [requirements management](https://www.perforce.com/products/helix-requirements-management), [test management](https://help.perforce.com/helix-alm/helixalm/current/web/Content/User/ManagingTests.htm) |

No single reviewed product is the template for AeroLink. DOORS Next leads configuration depth, Polarion leads specification-as-document workflow, Jama emphasizes accessible collaboration and reuse, Codebeamer emphasizes integrated ALM and product lines, and Perforce emphasizes pragmatic requirements/test/defect workflow. AeroLink should combine the relevant strengths around its aerospace assurance core.

## Enterprise capability benchmark

Status meanings:

- **Strong:** implemented as a coherent, tested vertical slice.
- **Foundation:** implemented in part but not yet broad or configurable enough for enterprise use.
- **Planned:** explicitly defined but not materially implemented.
- **Gap:** not yet adequately defined or implemented.

| Capability | What mature platforms provide | AeroLink status | Required AeroLink outcome |
|---|---|---|---|
| Controlled identities and permissions | Enterprise authentication, groups, project roles, fine-grained action permissions, durable attribution | **Foundation** | Retain local secure identities; add configurable permission policies, OIDC/SAML federation, group mapping, SCIM provisioning, privileged-access reporting, and cross-program isolation tests |
| Configurable artifact model | Administrators define artifact types, fields, enumerations, requiredness, defaults, validation, relationships, and display rules | **Gap** | Program-controlled schemas for requirements, change requests, tests, risks, defects, and supporting artifacts without database/code changes |
| Specification authoring | Rich text, tables, equations, images, attachments, headings, outline numbering, reusable templates, document/module trees, drag/reorder, and inline editing | **Gap** | A fast document-centric authoring workspace that still stores stable artifact and revision identities |
| Organization and classification | Folders, modules/documents, collections, tags, categories, components, releases, ownership, and configurable hierarchy | **Foundation** | Configurable collections and specification trees with stable placement history, multi-classification, and permission-aware navigation |
| Collaboration | Comments, threaded discussions, mentions, notifications, decisions, review packages, participant progress, and stakeholder access | **Foundation** | Add review comments/dispositions, mentions, watchers, notifications, due dates, escalation, and guest/stakeholder review modes to existing ordered approval |
| Workflow and policy | Configurable states, transitions, guards, assignment rules, electronic signatures, escalations, and artifact-specific policies | **Foundation** | Retain hard assurance invariants while allowing administrators to configure permitted workflow overlays and independence policies |
| History and comparison | Complete item history, redlines, field-level differences, restoration or controlled supersession, project/baseline comparison | **Strong** | Add visual field/rich-text redlines, relationship diffs, and scalable comparison filters without weakening immutable approved history |
| Baselines and release configuration | Exact baselines, comparison, branching/streams, change sets, parallel development, component configurations, and version-correct links | **Strong baseline; gap in parallel configuration** | Extend candidate baselines into components, working streams, isolated change sets, controlled merge, and composite product configurations |
| Reuse and product variants | Libraries, reuse by reference, synchronization, branch/clone, divergence detection, propagation, applicability, and variant resolution | **Gap** | Reusable controlled libraries and explicit origin/synchronization records; never treat copy/paste as reuse |
| Traceability and link validity | Typed relationship rules, matrices, graph exploration, upstream/downstream coverage, suspect links, link validity, and impact analysis | **Foundation** | Complete suspect-link state machine, relationship-rule engine, validity decisions, scalable matrices, path queries, and full trace analytics |
| Verification and defects | Test plans, cases/procedures, suites, runs, environments, results, automation import, evidence, failures, and linked defects/issues | **Strong controlled evidence; partial planning/defects** | Add test plans/suites, step results, assignments, defect/PR workflow, automation adapters, and configurable release-quality gates |
| Search, filters, and views | Indexed full-text search, structured query builder, linked-item filters, saved personal/shared views, columns, grouping, sorting, and exports | **Foundation** | One permission-aware indexed search across all artifact types and history, plus advanced filters, saved views, shareable URLs, and bulk action previews |
| Dashboards, metrics, and reports | Role dashboards, trends, coverage, trace matrices, custom report builders, scheduled reports, audit packages, and drill-down | **Foundation** | Trusted metric contracts, saved dashboard layouts, report templates, scheduled controlled snapshots, portfolio views, and data exports |
| Import, export, and migration | Excel/CSV/Word import, preview/mapping/validation, ReqIF round trip, baseline-aware exchange, attachment handling, and import history | **Gap** | Build a governed job-based interchange center beginning with CSV/Excel preview and ReqIF 1.2 round trip |
| APIs and integrations | Supported REST APIs, webhooks/events, OSLC resources, Jira/ADO/PLM/model/test connectors, service identities, and integration monitoring | **Gap** | Versioned public API, webhooks/outbox, service accounts, idempotency, rate limits, OSLC RM, integration jobs, and health/replay tooling |
| Risk and compliance | Risk/hazard artifacts, FMEA or safety analysis links, compliance templates, evidence reports, and standard-specific process support | **Planned boundary** | Remain standards-informed first; later support configurable risk/compliance artifacts without claiming certification automatically |
| Enterprise operations | HA-ready deployment, backup/restore, disaster recovery, monitoring, audit export, retention, encryption, upgrade validation, and support diagnostics | **Planned** | Prove installation, migration, observability, backup/restore, recovery objectives, audit export, and secure upgrade/rollback procedures |
| Scale and concurrency | Responsive work with very large repositories, optimistic locking, background jobs, indexing, pagination, bulk operations, and measurable limits | **Foundation** | Set and test volume/service-level targets; virtualize large trees/tables; isolate long jobs; benchmark imports, trace queries, baselines, and 150-user concurrency |

## Non-negotiable enterprise table stakes

The following are required before AeroLink can credibly be called an enterprise requirements-management platform:

1. Configurable artifact types, fields, validation, and relationship rules.
2. A rich document/module authoring experience for large structured specifications.
3. Full-text and structured search with saved personal and shared views.
4. Bulk operations with preview, authorization, validation, atomicity, and audit results.
5. Review comments, dispositions, mentions, notifications, due dates, and electronic-signature evidence.
6. Exact baselines, visual comparison, suspect links, and version-correct traceability.
7. Governed import/export with mapping, preview, validation, provenance, and rollback or safe retry.
8. ReqIF 1.2 import/export for supply-chain exchange. OMG describes ReqIF as the open, non-proprietary exchange format used across RM and SysML tools: [OMG ReqIF](https://www.omg.org/reqif/).
9. A versioned public API, webhooks, integration identities, and monitored background jobs.
10. Enterprise identity federation and automated provisioning. OIDC/SAML plus SCIM is the standards-based target for authentication and lifecycle provisioning.
11. Backup, restore, observability, audit export, retention, and upgrade procedures proven by tests.
12. Published scale limits and repeatable performance/concurrency benchmarks.

## Deliberate AeroLink differentiators

AeroLink should not merely reach parity. It should be better in areas that matter to aerospace development assurance:

- **Change-authority-first requirements:** requirement revisions are approved through an explicit change request package containing the complete Problem, Analysis, Solution, and impact set.
- **Exact release evidence:** every baseline, build, document, test result, approval, and signature resolves to exact revisions and hashes.
- **Honest readiness:** dashboards expose blockers and provenance instead of presenting unqualified percentages.
- **Professional controlled outputs:** documents are generated from controlled records with named approvals, front matter, revision history, hashes, and reproducible source metadata.
- **Human authority:** automation may help prepare work, but approved state and verification determinations remain attributable human decisions.
- **Recoverable control:** correction creates explicit successor records; history is not silently rewritten.

## Enterprise parity program

### Wave 1 — Enterprise Requirements Workspace

This is the recommended next massive implementation. It closes the daily-use gap and supplies the model needed by later import, reporting, and reuse.

- Configurable artifact types, fields, enumerations, validation, and relationship rules.
- Requirement specifications/modules with hierarchical sections and stable requirement placement.
- Rich requirement content: formatted text, lists, tables, images, attachments, symbols, and controlled references.
- High-density tree/table/document modes with inline editing and virtualization.
- Comments, threads, mentions, watchers, decisions, and review dispositions.
- Advanced permission-aware search, query builder, saved views, configurable columns, grouping, and shareable filters.
- Bulk edit/move/link/classify operations with preview and atomic audit results.
- Visual revision redlines and relationship differences.
- Governed CSV/Excel import center with mapping templates, validation preview, error workbook, job history, and idempotent retry.

### Wave 2 — Open Digital Thread

- ReqIF 1.2 import/export and round-trip identity mapping.
- Versioned public REST API with pagination, conditional updates, idempotency, and service identities.
- Transactional event outbox, webhooks, delivery history, retry, replay, and dead-letter handling.
- OSLC Requirements Management provider/consumer support. The OASIS specification defines standard Requirement and RequirementCollection resources, query, creation, and lifecycle relationship vocabulary: [OSLC RM 2.1](https://docs.oasis-open.org/oslc-domains/oslc-rm/v2.1/cs01/part1-requirements-management-spec/oslc-rm-v2.1-cs01-part1-requirements-management-spec.html).
- Initial Jira/Azure DevOps and automated-test adapters based on real program demand.

### Wave 3 — Product-Line Configuration and Reuse

- Components, working streams, isolated change sets, controlled accept/merge, conflict resolution, and immutable baselines.
- Reusable governed libraries, reuse by reference, synchronized reuse, divergence detection, applicability decisions, and propagation review.
- Product variants and composite configurations that resolve every link to the correct endpoint revision.

### Wave 4 — Enterprise Operations and Identity Federation

- OIDC and SAML authentication, SCIM 2.0 provisioning, group-to-role mapping, service accounts, and break-glass governance.
- Deployment validation, TLS, secrets, encryption, backups, restore drills, RPO/RTO evidence, telemetry, alerting, retention, and security audit export.
- Performance laboratory for million-record repositories, large imports, deep trace graphs, background publications, and 150 concurrent users.

### Wave 5 — Risk, Compliance, and Portfolio Intelligence

- Configurable risk, hazard, mitigation, defect/PR, compliance, and assurance-case artifacts.
- Cross-program portfolio dashboards and controlled compliance evidence packages.
- Standards-specific templates only after subject-matter validation; no automatic certification claim.

## Acceptance gate for Wave 1

**Implementation checkpoint — 2026-07-12:** Wave 1 now includes controlled requirement authoring and impact analysis plus a unified enterprise-hardening layer. Engineers can version and integrity-check exact-revision files, inspect content/field/attachment redlines, build and share permission-aware saved queries, run idempotent downloadable background exports, and resolve retained three-way edit conflicts without silent overwrite. Managers and operators see repository, storage, job, concurrency, performance, and integrity-checkpoint signals in Enterprise Control. A dedicated PostgreSQL run materialized 50,000 mixed-level requirements in 24.7 seconds; the exact-current 100-row workspace query measured 150 ms warm p95. A 150-client, 1,200-operation mixed database workload completed with zero failures at 401.8 operations/second and 1,265 ms p95 against the 2,000 ms gate. Remaining depth is explicitly limited to embedded inline-image rendering, resumable import commit, production-topology browser load, and backup/restore drills before Wave 2 interchange/API work.

Wave 1 is complete only when a systems engineer can:

1. Create a Program-specific requirement type and fields without code or a database migration.
2. Import at least 10,000 mixed-level requirements through a previewed mapping job.
3. Organize them into a navigable specification tree without changing stable artifact identities.
4. Edit rich content and see an exact field/content redline.
5. Find requirements by full text, structured fields, state, links, baseline, author, and history.
6. Save and share a permission-safe view.
7. Bulk classify or link a selected set with preview and one attributable atomic outcome.
8. Discuss, mention, resolve, and formally disposition review comments.
9. Submit an exact specification subset into the existing controlled SRCR/approval/baseline workflow.
10. Demonstrate responsive paging/virtualization and safe concurrent edits at realistic scale.

## Product decision

AeroLink will pursue **enterprise parity in core requirements engineering and lifecycle assurance**, not indiscriminate parity with every ALM/PLM feature. Architecture management, source-code hosting, automated test execution, and broad project planning remain integration targets unless a later decision changes scope. This keeps the product focused while still making it a first-class enterprise system of record for requirements, change authority, verification evidence, traceability, and release assurance.
