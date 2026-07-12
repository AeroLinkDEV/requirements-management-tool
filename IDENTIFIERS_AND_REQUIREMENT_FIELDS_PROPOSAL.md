# Identifiers and Requirement Fields Proposal

This document records the accepted requirement identifier/revision display and proposes the remaining mandatory-field policy.

## Identifier Proposal

### Principle

Every controlled artifact receives one stable, globally unique base identifier. Revisions remain separate records internally, while the user-facing revision is appended to the base identifier.

### Display Format

Use a stable base identifier:

```text
<TYPE>-<8 digit global sequence>
```

Examples:

- `SCR-00001049`
- `SYSR-00002375`
- `HLR-00003142`
- `LLR-00006721`
- `TP-00004502`
- `EXEC-00008821`
- `PR-00002844`
- `BL-00000217`

Recommended type prefixes:

| Artifact | Prefix |
| --- | --- |
| System Change Request | `SCR` |
| Software Change Request | `SWCR` |
| System Requirement | `SYSR` |
| Software High-Level Requirement | `HLR` |
| Software Low-Level Requirement | `LLR` |
| Test Procedure | `TP` |
| Test Execution | `EXEC` |
| Problem Report | `PR` |
| Baseline | `BL` |
| Review | `REVW` |
| Generated Document | document-type-specific prefix |

The sequence is globally unique within the installation and is not restarted for each program. Whether separate artifact types share one numeric sequence or maintain separate per-prefix sequences remains open. The complete identifier must never be reused.

The system also maintains an internal machine identifier that is never shown as the business identifier. Users and documents cite the stable display identifier.

### Accepted Revision Display

Append a dot and a minimum two-digit revision suffix when identifying a specific revision:

```text
SYSR-00002375.04
SCR-00001049.02
TP-00004502.06
```

The stable requirement identity remains `SYSR-00002375`; `SYSR-00002375.04` means revision 4 of that same requirement. Internally, identity and revision remain separate fields.

Use positive whole-number revisions for controlled artifact content and render values below 10 with a leading zero:

- Draft work begins as `Revision 1`.
- Requested changes before the SCR has ever been approved keep the same SCR revision number and create a new review cycle after resubmission.
- Any content change after SCR approval creates the next SCR revision number.
- Revision numbers are never reused or renumbered.
- The application retains every submitted review-cycle snapshot even when the unapproved SCR returns to Draft and is edited at the same revision number.
- The same base artifact identifier remains stable across all revisions.
- Revisions above 99 expand naturally, such as `.100`; revision numbers are not limited to two digits.

Interfaces, links, documents, imports, and exports must distinguish the stable base ID from the revision-qualified display value even though users commonly cite the combined form.

### Release and Document Naming

Software releases retain their business version:

- `FMS Software Version 3.2`
- `FMS Software Version 3.3`

Baselines use a stable baseline ID plus a readable name:

```text
BL-00000217
FMS Software 3.3 Released Baseline
```

Generated documents use a controlled document identifier and revision separate from the source baseline. For example:

```text
Document: SYSRD-FMS
Document Revision: 12
Source Baseline: BL-00000217
Software Release: FMS 3.3
```

The exact document-number convention may follow existing organizational policy.

### Why This Proposal

- Stable IDs remain easy to cite in reviews, documents, PRs, emails, and meetings.
- The type is recognizable without opening the record.
- Revision history remains explicit and unambiguous.
- Program transfers or reorganizations do not require renumbering.
- IDs do not reveal sensitive program names.
- Eight digits provide ample space without producing excessively long identifiers.

## What “Mandatory Requirement Fields” Means

A mandatory field is information required for the SCR author to submit the complete SCR package for review. Requirements do not enter an independent review/approval workflow; their proposed revisions are reviewed as part of the SCR.

There are three useful categories:

1. **Platform-mandatory fields:** Required for every requirement in every program because identity, control, traceability, and audit would otherwise break.
2. **Program-mandatory fields:** Required by a particular program or process, configured through controlled administration.
3. **Optional fields:** Available when useful but not required for approval.

The SCR author controls when drafting is complete enough to attempt submission. The system should then validate the SCR package and clearly identify missing required information, while the author remains responsible for deciding that the technical content is ready.

## Proposed Platform-Mandatory Fields

### Created Automatically

- stable requirement identifier;
- revision number;
- program and project/software product;
- lifecycle state;
- created by and created time;
- last modified by and modified time; and
- complete audit history.

### Required in a Requirement Change Item Before SCR Submission

- requirement statement;
- short title or summary;
- requirement type/level;
- owner;
- rationale or an explicit “not required” justification;
- verification method;
- derived/non-derived indicator;
- applicability or target release context;
- originating SCR or approved source/change rationale; and
- any images/figures referenced by the statement stored as controlled revision content.

### Required Before Approval

- the enclosing SCR has complete Problem, Analysis, and Solution content;
- all proposed requirement introductions, modifications, and retirements are identified;
- the SCR author has selected every person whose approval is required;
- required links and impact information are present or explicitly justified; and
- the SCR package has no unresolved submission-validation errors.

### Required Before SCR Approval

- every author-selected approver approves the exact submitted snapshot of the SCR revision;
- every blocking review comment is dispositioned;
- the proposed requirement revisions remain exactly those reviewed within the SCR;
- required impact analysis is complete; and
- the approval record identifies the exact SCR revision and complete change package.

### Required Before Baseline Inclusion

- requirement revision authorized by an approved SCR;
- applicability to the candidate release/configuration;
- resolved selection conflicts;
- acceptable trace/suspect status according to the baseline policy; and
- exact inclusion relationship to the candidate baseline.

## Proposed Program-Configurable Fields

These may be mandatory for some projects but should not be hard-coded as universally required until confirmed:

- safety classification or criticality;
- requirement category or functional area;
- source/customer reference;
- interface reference;
- allocation target;
- verification level or independence requirement;
- verification-method details;
- operational mode or phase;
- configuration/variant applicability;
- priority;
- assumptions and constraints;
- standards-compliance tags;
- cybersecurity classification;
- export/data classification; and
- custom program attributes.

## Proposed Requirement Validation Rules

- A requirement statement cannot be empty.
- A requirement must be testable/verifiable or carry an approved rationale explaining why a non-test method applies.
- Controlled figures referenced by the statement must belong to the same revision.
- An approved revision cannot be edited in place.
- A new revision must identify the SCR or other approved change authority.
- Derived requirements require explicit rationale and the applicable review path.
- An SCR cannot enter review with unresolved package-level required-field errors.
- Requirements are not approved independently; every author-selected approver approves the exact submitted snapshot of the SCR revision containing the requirement changes.
- SCR approval authorizes its proposed requirement revisions but does not automatically include them in a baseline.

## Decisions Requested

1. Confirm whether numeric sequences must be global across all artifact types or unique within each prefix.
2. Confirm the proposed platform-mandatory requirement fields.
3. Identify fields that every current FMS requirement already contains and must preserve during import.
