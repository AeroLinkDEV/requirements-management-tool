# AeroLink Technical Overview

**Audience:** Software, systems, verification, configuration-management, and IT colleagues
**Status:** Current implementation brief, 10 August 2026

## System shape

AeroLink is a modular monolith: a React/TypeScript browser client calls an ASP.NET Core API on .NET 10. The API
uses Entity Framework Core for persistence. The normal local/on-premises database is PostgreSQL; SQLite is used
only for isolated automated tests. The application can be served as one production-shaped origin or as a Vite
development client plus API.

Domain objects enforce lifecycle rules. API endpoints authenticate the caller, validate Project/build scope and
role authority, invoke the domain operation, and save state plus audit/integration evidence in one transaction.
The client is explanatory rather than authoritative: released-build mutation protection and role separation are
also enforced server-side.

## Database and controlled records

PostgreSQL is the one real-life database. EF Core migrations version its schema and are applied at startup.
Stable artifacts (for example `SRCR-00076` or `LLR-000008`) are separate from immutable revisions (`.00`, `.01`).
A controlled requirement revision is never edited in place after approval; a change request proposes a new
revision, ordered review/electronic signatures approve it, and baseline materialization selects exact revision
IDs for a build.

The same stable-identity/immutable-revision pattern governs test procedures, executions/evidence, generated
documents, Word-authored managed documents, and code traceability. Procedure revisions are authorized only by
controlled Test Change Requests; released legacy predecessors that predate exact procedure manifests use the
explicit Configuration Manager bootstrap rather than a silent empty/current reconstruction. Problem Reports
remain controlled records but are **Project-scoped** rather than build-owned: target build is an explicit
attribute/filter (DEC-089). Managed documents separate the stable register identity from
formal `.00`/`.01` revisions and from retained working-file versions. PostgreSQL holds their lifecycle metadata;
the controlled evidence store holds checksummed DOCX/PDF bytes. Exact build-selection rows determine which
released revision applies to each build. Build 1.5 resolves to its released immutable baseline. Build 1.6 inherits that exact baseline until
its own candidate is materialized, while its changes, assessments, tests, and code mappings remain a separate
active build layer. Problem Reports stay in the Project-wide report database and carry target-build attribution
without being implicitly hidden by the active build. Foreign keys, unique constraints, concurrency tokens, immutable hashes, audit events, and
server-side build guards protect these relationships.

## Versioning and traceability

There are four complementary version controls. Git/GitHub versions the application source and documentation;
work is delivered on focused branches through pull requests and CI, never directly to `main`.
AeroLink artifact revisioning versions controlled engineering content. Candidate baselines select exact
approved revisions into a build; `/baselines` is the supported Configuration Management surface for candidate work and legacy procedure-manifest bootstrap. Generated DOCX/PDF outputs and release packages record their source baseline,
template revision, and content hashes.

Documentation Center adds one more controlled boundary without attempting to recreate Word in a browser. These
externally authored records are Project-wide aggregates: a stable document has one continuous formal revision
lineage, one current released head, and at most one active successor, independent of software-build lifecycle.
Build links are contextual traceability, while generated requirements and procedure publications remain
build-scoped and retain exact baseline/effectivity behavior. A
small per-user Windows connector redeems a one-use, short-lived grant for one document revision, downloads the
exact current DOCX, opens Microsoft Word, maintains an exclusive lease, and returns a new immutable working
version with a required comment. Draft DOCX files must retain the faint Draft watermark in every section. At the
final review step, Word creates a watermark-free DOCX and matching PDF labeled Release Candidate; AeroLink
rejects any candidate that still presents itself as Draft, then hashes the exact pair before the final
electronic signature releases it. Stale-source check-ins fail without overwriting.

Digital Thread follows exact baseline revisions across SYSR, HLR, LLR, procedure, execution/result, linked
checksummed evidence, and software build. Exact procedure views and search share one authoritative revision-title
projection, so discarded Retire proposal text cannot be searched as though it were controlled history and
release/build effectivity remains part of discovery. Modify/Retire authoring binds to the exact controlled
procedure carried by the target build; stale manifest/current-revision conflicts preserve authored prose but
clear target-dependent state and require explicit refresh/reselection rather than silent remapping.

When several confirmed procedures cover the same exact requirement, the Digital Thread prefers the build-scoped
run with linked evidence; a free-text reference remains context, not proof of an attached evidence file. GitLab remains authoritative for source code. AeroLink records immutable pointers from
an exact approved LLR revision to a GitLab merge request and merge commit SHA, or a justified `No code change
required` decision. That same exact-LLR scope drives the Code workspace, release-readiness gate, and signed
review manifest.

## Identity, review, and operation

Authentication uses server sessions with password hashing, revocation, Program membership, roles, delegations,
and password-confirmed electronic signatures. Authors cannot independently approve controlled work where role
separation is required. Review returns retain history; successor revisions supersede rather than overwrite.

The persistent PostgreSQL database, controlled evidence store, and runtime configuration are backed up together
into integrity-manifested archives. A Windows scheduled task runs the same verified backup engine daily by
default, with configurable time and retention. Restore validation is isolated so it cannot replace the live
database accidentally.

Quality gates include domain and API suites, client lint/type-check/build, Playwright role/workflow journeys,
production client hosting tests, PostgreSQL bootstrap/migration validation, and exact-merge requalification.
The FMS dataset is demonstration data; production demo seeding and demo accounts are disabled by configuration.

## Key boundaries

AeroLink governs lifecycle data and evidence; it does not execute tests, host source code, approve GitLab merges,
claim certification, or replace organizational process authority. External test systems and GitLab remain the
authoritative execution/code systems. AeroLink connects their evidence to the exact controlled engineering
revisions and build that a release decision relies on.
