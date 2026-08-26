# Identifiers and Requirement Fields Proposal

This document records the accepted requirement identifier/revision display and proposes the remaining mandatory-field policy.

> **Status, 2026-08-01:** the controlled identifier and appended revision display are implemented product
> contracts. The field-policy sections remain design rationale/proposals, not an active GitHub backlog or a
> claim that every listed field is enforced. Current terminology is in
> [Domain Model and Glossary](DOMAIN_MODEL_AND_GLOSSARY.md).

## Identifier Proposal

### Principle

Every controlled artifact receives one stable, globally unique base identifier. Revisions remain separate records internally, while the user-facing revision is appended to the base identifier.

### Display Format

Use a stable base identifier:

```text
<TYPE>-<8 digit global sequence>
```

Examples:

- `SRCR-00001049`
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
| System Change Request | `SRCR` |
| Software Change Request | `HLRCR`, `LLRCR` |
| System Requirement | `SYSR` |
| Software High-Level Requirement | `HLR` |
| Software Low-Level Requirement | `LLR` |
| Test Procedure | `TP` |
| Test Execution | `EXEC` |
| Problem Report | `PR` |
| Baseline | `BL` |
| Review | `REVW` |
| Generated Document | document-type-specific prefix |

The sequence is globally unique within the installation and is not restarted for each program. As accepted in DEC-036, each prefix maintains its own installation-wide sequence. The complete identifier must never be reused. The server assigns identifiers atomically; users cannot type or override authoritative numbers.

The system also maintains an internal machine identifier that is never shown as the business identifier. Users and documents cite the stable display identifier.

### Accepted Revision Display

Append a dot and a minimum two-digit revision suffix when identifying a specific revision:

```text
SYSR-00002375.04
SRCR-00001049.02
TP-00004502.06
```

The stable requirement identity remains `SYSR-00002375`; `SYSR-00002375.04` means revision 4 of that same requirement. Internally, identity and revision remain separate fields.

Use positive whole-number revisions for controlled artifact content and render values below 10 with a leading zero:

- Draft work begins as `Revision 1`.
- Requested changes before the SRCR has ever been approved keep the same SRCR revision number and create a new review cycle after resubmission.
- Any content change after SRCR approval creates the next SRCR revision number.
- Revision numbers are never reused or renumbered.
- The application retains every submitted review-cycle snapshot even when the unapproved SRCR returns to Draft and is edited at the same revision number.
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

A mandatory field is information required for the SRCR author to submit the complete SRCR package for review. Requirements do not enter an independent review/approval workflow; their proposed revisions are reviewed as part of the SRCR.

There are three useful categories:

1. **Platform-mandatory fields:** Required for every requirement in every program because identity, control, traceability, and audit would otherwise break.
2. **Program-mandatory fields:** Required by a particular program or process, configured through controlled administration.
3. **Optional fields:** Available when useful but not required for approval.

The SRCR author controls when drafting is complete enough to attempt submission. The system should then validate the SRCR package and clearly identify missing required information, while the author remains responsible for deciding that the technical content is ready.

## Proposed Platform-Mandatory Fields

### Created Automatically

- stable requirement identifier;
- revision number;
- program and project/software product;
- lifecycle state;
- created by and created time;
- last modified by and modified time; and
- complete audit history.

### Required in a Requirement Change Item Before SRCR Submission

- requirement statement;
- short title or summary;
- requirement type/level;
- owner;
- rationale or an explicit “not required” justification;
- verification method;
- derived/non-derived indicator;
- applicability or target release context;
- originating SRCR or approved source/change rationale; and
- any images/figures referenced by the statement stored as controlled revision content.

### Required Before Approval

- the enclosing SRCR has complete Problem, Analysis, and Solution content;
- all proposed requirement introductions, modifications, and retirements are identified;
- the SRCR author has selected and ordered every person whose approval is required;
- required links and impact information are present or explicitly justified; and
- the SRCR package has no unresolved submission-validation errors.

### Required Before SRCR Approval

- every author-selected approver approves the exact submitted snapshot in the defined sequence;
- every blocking review comment is dispositioned;
- the proposed requirement revisions remain exactly those reviewed within the SRCR;
- required impact analysis is complete; and
- the approval record identifies the exact SRCR revision and complete change package.

### Required Before Baseline Inclusion

- requirement revision authorized by an approved SRCR;
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
- A new revision must identify the SRCR or other approved change authority.
- Derived requirements require explicit rationale and the applicable review path.
- An SRCR cannot enter review with unresolved package-level required-field errors.
- Requirements are not approved independently; every author-selected approver approves the exact submitted snapshot of the SRCR revision containing the requirement changes.
- SRCR approval authorizes its proposed requirement revisions but does not automatically include them in a baseline.

## Remaining Decisions Requested

1. Confirm the proposed platform-mandatory requirement fields beyond the now-accepted generated identifier, revision, authenticated author, and software-derived indicator.
2. Identify fields that every current FMS requirement already contains and must preserve during import.
