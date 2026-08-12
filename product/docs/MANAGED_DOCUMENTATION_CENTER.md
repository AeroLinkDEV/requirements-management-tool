# Managed Documentation Center

## Purpose and boundary

Documentation Center controls lifecycle documents whose authored source is normally Microsoft Word. AeroLink is
the system of record for identity, one continuous Project-wide formal revision lineage, file history, checkout, review,
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

1. An authorized engineer registers a Project document or starts its one active successor revision. No software
   build is required, and released-build state does not make the document read-only.
2. AeroLink creates a faintly watermarked **Draft** DOCX. The word Draft is a state label, not a separate acronym.
3. The owner selects **Open in Word**. The desktop connector obtains a short-lived, one-use grant, downloads the
   exact current source, holds an exclusive renewable checkout, and opens Word.
4. The owner checks the Word file back in with a required working-version comment or discards the checkout.
   Check-in never changes the formal revision scope or revision owner. Every accepted check-in retains its actor,
   time, base attachment/hash, resulting attachment/hash, superseded version, connector session when known, and
   operation identifier as immutable evidence.
5. The formal revision scope is defined once when the revision starts. While Draft or Returned, an authorized
   owner or project authority may correct it through the explicit audited action with an optimistic-concurrency
   version and reason. It cannot be changed while in review or after release.
6. The owner submits the exact current working attachment plus the exact formal-summary hash and version to an independent technical reviewer and a different final SQA
   or configuration approver. The owner cannot fill either role.
7. A return preserves the completed review evidence and creates a new review cycle after correction.
   The next accepted working version records its check-in comment and, separately, the return-resolution note;
   the original reviewer rationale remains on the immutable review step.
8. At the final step, the connector removes the Draft watermark, relabels visible Draft state markers as
   **Release Candidate**, and creates the exact DOCX/PDF pair. The API rejects any candidate that retains a
   Draft watermark or visible Draft state marker. The final electronic signature releases that immutable pair
   and records its combined manifest hash.

Each stable Project document has one current released head and at most one active Draft, returned, or in-review
successor. Starting `.01` uses the verified immutable released DOCX for `.00`; starting `.02` uses `.01` in the
same way. AeroLink records the parent revision, released attachment, SHA-256, and transformation profile.
Missing, corrupt, or ambiguous parent evidence fails closed. Build and release links are optional contextual
traceability only: changing, releasing, or switching a software build never selects, duplicates, freezes, or
hides these records. Generated requirements and procedure publications remain build-scoped.

Project search and My Work use the formal revision scope when describing a managed-document revision. A
selected software build may narrow genuinely build-owned records, but it never hides or relabels Project-wide
document work.

## Lifecycle links

An in-work revision can select and link existing change requests, Problem Reports, Test Change Requests, and
builds from across the Project. AeroLink validates that the selected record belongs to the same Project, stores the relationship and
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

The migration from the former build-scoped model copies prior target-build and build-selection rows into
`managed_document_build_provenance` before removing lifecycle ownership from revisions. Those rows are retained
for historical explanation only and never drive current effectivity. Existing attachment IDs, hashes,
signatures, actors, timestamps, review history, and audit events are not rewritten. If legacy data contains more
than one released head or more than one active successor, the API reports reconciliation required and refuses to
silently choose a branch. A legacy successor retains its formal parent revision identity, but its source
attachment/hash remain unset and its transformation profile is `legacy-working-source-unverified-v1`: the former
implementation copied a working attachment, so migration must not falsely claim the released DOCX was its source.

The formal-summary migration preserves the latest value of the legacy `ChangeSummary` column and records its
SHA-256, but marks it `LegacyAmbiguousLatestValue`: the former check-in path overwrote that field, so migration
cannot truthfully claim that the retained text was the original formal revision scope. Every historical working
attachment is recovered as check-in evidence using its existing attachment ID, version, actor, timestamp, base
link, and hashes. Legacy connector session and operation-token values that were never stored remain explicitly
unknown rather than being fabricated.

## Stewardship, responsibility, and authorship

The document steward owns long-term Project accountability. The responsible revision owner coordinates the active formal revision. The stable record creator, revision initiator, accepted check-in contributors, and temporary checkout holder are separate identities and are displayed separately.

Creating a document or successor validates the selected assignee as an active account with current Program authoring authority or a valid delegation before any row or file is created. Global administrator status is not document-authoring authority, so an administrator must explicitly select an eligible Program author. Ordinary and privileged check-ins never transfer responsibility. Configuration Management, the Program Manager, or the Project Engineering Lead can explicitly reassign stewardship or Draft/Returned revision responsibility using a different eligible assignee, required reason, and expected version. AeroLink retains old/new assignee, assigning actor, reason, effective time, notification, and append-only document and security-audit evidence. Disabled or departed responsible owners appear in the authorized My Work recovery queue.

Submission freezes the attributable contributor set from accepted check-ins for that exact review cycle. A responsibility transfer therefore cannot make a prior content contributor independently eligible to review unchanged work. Legacy stewardship and responsibility preserve their respective retained owner fields; creator and initiator use the earliest retained check-in actor where available and fall back to those owner fields without changing historical check-in evidence. Where old review-cycle contributors can only be inferred from retained check-ins, their provenance is visibly `LegacyInferredFromRetainedCheckIns` rather than represented as contemporary proof.
