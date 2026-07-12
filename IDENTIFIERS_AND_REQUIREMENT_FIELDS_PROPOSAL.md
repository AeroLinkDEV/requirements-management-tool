# Identifiers and Requirement Fields Proposal

This proposal answers two product-definition questions: how controlled items should be numbered and what “mandatory requirement fields” means. It is a review proposal, not yet an accepted decision.

## Identifier Proposal

### Principle

Every controlled artifact receives one stable, globally unique identifier. Revisions are separate records and are never encoded as if they were new artifact identities.

### Display Format

Use:

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

The sequence is globally unique within the installation, not restarted for each program or artifact type. The type prefix improves recognition; the global sequence prevents two projects from creating confusingly identical numbers.

The system also maintains an internal machine identifier that is never shown as the business identifier. Users and documents cite the stable display identifier.

### Revision Format

Show revisions separately:

```text
SYSR-00002375 • Revision 4
SCR-00001049 • Revision 2
TP-00004502 • Revision 6
```

Use positive whole-number revisions for controlled artifact content:

- Draft work begins as `Revision 1`.
- Rework or an approved change creates the next revision number.
- Revision numbers are never reused or renumbered.
- The application may show draft iterations internally, but a reviewed snapshot remains immutable.
- The same artifact identifier remains stable across all revisions.

Avoid forms such as `SYSR000000001.001` as the primary identifier because they encourage users and integrations to treat each revision as a different requirement.

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

A mandatory field is information the system refuses to omit when a requirement reaches a defined lifecycle state. Not every field must be mandatory while drafting.

There are three useful categories:

1. **Platform-mandatory fields:** Required for every requirement in every program because identity, control, traceability, and audit would otherwise break.
2. **Program-mandatory fields:** Required by a particular program or process, configured through controlled administration.
3. **Optional fields:** Available when useful but not required for approval.

Requirements may become progressively stricter by state. A Draft can be incomplete; Ready for Review and Approved must satisfy stronger rules.

## Proposed Platform-Mandatory Fields

### Created Automatically

- stable requirement identifier;
- revision number;
- program and project/software product;
- lifecycle state;
- created by and created time;
- last modified by and modified time; and
- complete audit history.

### Required Before Review

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

- all required review assignments;
- all required review decisions;
- disposition of every blocking review comment;
- valid required upward/downward relationships or an approved justification;
- completed impact analysis;
- no unresolved validation errors; and
- approval record for the exact revision.

### Required Before Baseline Inclusion

- approved revision state;
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
- An artifact cannot enter review with unresolved required-field errors.
- An artifact cannot be approved until every required reviewer approves the exact revision.
- Approval does not automatically include the requirement in a baseline.

## Decisions Requested

1. Accept or adjust the `<TYPE>-<8 digit sequence>` format.
2. Confirm whether sequences must be globally unique across all artifact types or unique within each prefix.
3. Confirm whole-number artifact revisions or identify an existing revision convention that must be preserved.
4. Confirm the proposed platform-mandatory requirement fields.
5. Identify fields that every current FMS requirement already contains and must preserve during import.
