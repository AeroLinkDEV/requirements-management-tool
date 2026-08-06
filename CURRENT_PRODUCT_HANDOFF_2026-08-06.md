# AeroLink Product Handoff — 6 August 2026

## Current state

AeroLink remains a local/on-premises React, ASP.NET Core, EF Core, and PostgreSQL application. Build 1.5 is the
released read-only historical workspace; Build 1.6 is active development. Persistent engineering demonstration
records and the controlled evidence store must be preserved.

The latest increment adds a standalone **Documentation Center** for Word-authored lifecycle documents. It is
not the existing Generated Documents feature. Seven representative avionics documents demonstrate released,
Draft, in-review, and returned conditions. The center supports stable numbers, formal revisions, retained
working versions, build selections, exact downloads, direct links, lifecycle relationships, ordered independent
review, electronic signatures, immutable released DOCX/PDF pairs, hashes, audit history, and exclusive Word
checkout through a per-user Windows desktop connector.

Release preparation removes the watermark and changes visible Draft state labels to **Release Candidate** before
Word produces the exact DOCX/PDF pair. The API rejects a candidate that still presents itself as Draft; final
SQA authorization then releases the exact candidate hashes recorded by AeroLink.

## Important operating paths

- Production-shaped local start: `START_AEROLINK_PRODUCTION.bat`
- Development start: `START_AEROLINK.bat`
- Controlled stop: `STOP_AEROLINK.bat`
- One-time Word connector install: `INSTALL_AEROLINK_DOCUMENT_CONNECTOR.bat`
- Backup: `BACKUP_AEROLINK.bat`
- Full feature description: `product/docs/MANAGED_DOCUMENTATION_CENTER.md`
- Technical overview: `docs/AEROLINK_TECHNICAL_OVERVIEW.md` and matching DOCX

## Data and safety

PostgreSQL is the one real-life database. EF Core migrations update its schema without reset or reseed. Managed
document files are stored through the controlled evidence store, not Git. Daily backup archives include the
database, evidence, configuration, manifest, and integrity sidecar. Do not delete `product/.local`, reset the
database, or replace the evidence root during qualification.

## Delivery rules

Use focused `codex/*` branches and GitHub pull requests; never push directly to `main`. Validate build isolation,
role separation, exact persisted outcomes, direct links/refreshes, Word rendering, migrations, backups, and the
production-shaped launcher. GitHub is the application source of truth; GitLab remains the external source of
truth for implementation code linked to approved LLRs.
