# Controlled Document Publication Standard

## Purpose

AeroLink-generated DOCX and PDF files are professional controlled renderings of authoritative lifecycle records. They must be suitable for formal review, management presentation, configuration archives, and engineering use without implying an approval that the database does not contain.

The visual system uses the `compact_reference_guide` document preset with an editorial technical cover: US Letter, one-inch margins, Calibri typography, navy and restrained teal hierarchy, explicit running headers and footers, fixed-width document-control tables, and complete-record pagination.

## Required Cover Content

Every generated publication includes:

- product, program, project, and document type;
- controlled document title and subtitle;
- stable document or artifact number and exact revision;
- lifecycle status displayed prominently;
- target release and baseline context;
- the names, authority roles, identities, decisions, and dates of applicable approvers;
- a clear approval-pending statement when no completed approval exists; and
- a controlled-copy notice requiring manifest verification.

The platform never fabricates a signature, decision, person, or date. A person is shown as Approved only when an immutable approval step exists.

## Approval Basis

### change request publications

The cover and approval register use the ordered approvers from the latest review cycle for the exact change request revision. Pending and active reviewers remain visibly identified by their actual state. The review snapshot SHA-256 is included in Document Control.

### Requirement and test-document publications

The approval basis consists of named approvers from the exact approved change-request revisions selected into the document baseline, plus completed release approvals that existed at the publication generation time. The cover labels these roles as Change Authority or Release Authority so they are not misrepresented as handwritten document signatures.

## Document Control Front Matter

Page two contains:

- document type, number, revision, status, release, and baseline;
- preparation owner and deterministic generation timestamp;
- complete manifest SHA-256 and applicable baseline hashes;
- controlled-record count;
- approval-basis explanation;
- named approval register; and
- revision history.

The authority-and-use notice states that database records and hashes remain authoritative and that downloaded or printed copies require verification.

## Controlled Body Content

### change request

The body includes the Problem-Analysis-Solution case, proposed requirement changes with rationale and verification method, and append-only audit history.

### Requirement documents

Each exact requirement revision includes its stable identifier, level, normative statement, rationale, verification method, and source change request.

### Test-procedure documents

Each exact procedure revision includes its identifier, level, title, approval state, owner, objective, preconditions, procedure steps, and expected result.

## Rendering and Pagination Rules

- Cover, Document Control, and controlled body begin on separate pages.
- A controlled record's heading, primary content, and metadata remain together across page boundaries.
- Continued body pages are labeled explicitly.
- Running headers identify the product, document number, and revision.
- Footers identify status, manifest prefix, and page count.
- PDF and DOCX use the same publication data model and approval provenance.
- Generated files are snapshots; they never replace database authority.
