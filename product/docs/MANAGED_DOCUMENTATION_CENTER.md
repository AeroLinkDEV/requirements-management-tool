# Managed Documentation Center

## Purpose and boundary

Documentation Center controls lifecycle documents whose authored source is normally Microsoft Word. AeroLink is
the system of record for identity, formal revision, build applicability, file history, checkout, review,
electronic signatures, released DOCX/PDF renditions, hashes, links, and audit evidence. Word remains the authoring
tool; AeroLink is not intended to reproduce Word editing in the browser.

This is distinct from **Generated Documents**, where AeroLink renders requirements and test-procedure data into
deterministic publications.

## Current document set

The FMS demonstration project contains seven representative avionics records:

| Acronym | Document type |
| --- | --- |
| PSAC | Plan for Software Aspects of Certification |
| SDP | Software Development Plan |
| SVP | Software Verification Plan |
| SCMP | Software Configuration Management Plan |
| SQAP | Software Quality Assurance Plan |
| SAS | Software Accomplishment Summary |
| ICD | Interface Control Document |

Each record has a stable number such as `SDP-000001`. Formal controlled revisions use `.00`, `.01`, and so on.
Working-file versions are retained inside a formal revision and do not create new formal revision numbers.

## Lifecycle

1. An authorized engineer registers a document or starts the next revision for one active build.
2. AeroLink creates a faintly watermarked **Draft** DOCX. The word Draft is a state label, not a separate acronym.
3. The owner selects **Open in Word**. The desktop connector obtains a short-lived, one-use grant, downloads the
   exact current source, holds an exclusive renewable checkout, and opens Word.
4. The owner checks the Word file back in with a required comment or discards the checkout. Every checked-in
   working version and SHA-256 hash remains retrievable.
5. The owner submits the exact current snapshot to an independent technical reviewer and a different final SQA
   or configuration approver. The owner cannot fill either role.
6. A return preserves the completed review evidence and creates a new review cycle after correction.
7. At the final step, the connector removes the Draft watermark, relabels visible Draft state markers as
   **Release Candidate**, and creates the exact DOCX/PDF pair. The API rejects any candidate that retains a
   Draft watermark or visible Draft state marker. The final electronic signature releases that immutable pair
   and records its combined manifest hash.

Released versions are immutable and can be carried into later builds. Build 1.5 shows only its released
selection and is read-only. Build 1.6 may show the carried released revision beside one active Draft, returned,
or in-review successor.

## Lifecycle links

An in-work revision can select and link existing change requests, Problem Reports, Test Change Requests, and
builds. AeroLink validates that the selected record belongs to the same Project, stores the relationship and
actor, and retains it with the exact document revision.

## Desktop connector

Install once per Windows user with `INSTALL_AEROLINK_DOCUMENT_CONNECTOR.bat`. The installer requires no local
administrator rights and registers the `aerolink://` protocol for that user. Word must be installed only for
editing and final PDF production.

Connector trust controls are deliberately small: HTTPS is required for remote servers; loopback HTTP is allowed
for the local demonstration; launch tokens are one-use and short-lived; session access is scoped to one revision;
tokens stay in memory; stale-source check-ins fail without overwriting; macro-enabled files and non-DOCX sources
are rejected; and a final release is accepted only when its DOCX is free of Draft watermarks and Draft state
markings and its PDF is valid.

## Operational notes

Managed document binaries use the existing controlled evidence store and are included with the PostgreSQL
database and runtime configuration in `BACKUP_AEROLINK.bat`. The authoritative metadata remains in PostgreSQL;
copying the evidence folder without its matching database is not a complete backup.
