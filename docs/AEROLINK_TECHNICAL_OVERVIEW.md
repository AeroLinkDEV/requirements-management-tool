# AeroLink Technical Overview

**Audience:** Software, systems, verification, configuration-management, and IT colleagues
**Status:** Current implementation brief, 2 August 2026

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
Stable artifacts (for example `SCR-00076` or `LLR-000008`) are separate from immutable revisions (`.00`, `.01`).
A controlled requirement revision is never edited in place after approval; a change request proposes a new
revision, ordered review/electronic signatures approve it, and baseline materialization selects exact revision
IDs for a build.

The same pattern governs test procedures, executions/evidence, controlled documents, Problem Reports, and code
traceability. Build 1.5 resolves to its released immutable baseline. Build 1.6 inherits that exact baseline until
its own candidate is materialized, while its changes, assessments, tests, PRs, and code mappings remain a
separate active layer. Foreign keys, unique constraints, concurrency tokens, immutable hashes, audit events, and
server-side build guards protect these relationships.

## Versioning and traceability

There are four complementary version controls. Git/GitHub versions the application source and documentation;
work is delivered on focused `codex/*` branches through pull requests and CI, never directly to `main`.
AeroLink artifact revisioning versions controlled engineering content. Candidate baselines select exact
approved revisions into a build. Generated DOCX/PDF outputs and release packages record their source baseline,
template revision, and content hashes.

Digital Thread follows exact baseline revisions across SYSR, HLR, LLR, procedure, execution/result, evidence,
and software build. GitLab remains authoritative for source code. AeroLink records only immutable pointers from
an exact approved LLR revision to a GitLab merge request and merge commit SHA, or a justified `No code change
required` decision.

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
