# Current product and repository handoff - 2026-08-06

This is the current restart point for AeroLink. It supersedes the 5 August handoff, which remains a
historical delivery record. [PROJECT_STATE.md](PROJECT_STATE.md) is the canonical product description and
[FEATURE_CATALOG.md](FEATURE_CATALOG.md) is the stable capability inventory.

## Repository checkpoint

- Repository: `seanmccarthyns/requirements-management-tool`.
- Delivery rule unchanged: focused `codex/*` branch, pull request, required Product Quality Gate, squash
  merge. Never push implementation directly to `main`.
- Build 1.5 is the released, read-only historical workspace; Build 1.6 is active development.
- The persistent PostgreSQL database remains the sole real-life database. Nothing in these increments reset,
  reseeded, or destructively migrated it.
- Five additive migrations support the latest test-procedure and managed-document capabilities:
  `20260806002614_AddTestProcedureChanges`, `20260806020541_AddTestProcedureBaseline`,
  `20260806021511_AddTestProcedureChangeTitle`, `20260806024333_KeyTestChangeRequestExclusivityByRevision`,
  and `20260806032704_AddManagedDocumentationCenter`.
- The controlled evidence store remains outside Git under `product/.local` and must be preserved with the
  database.

## What was delivered

### Test procedures handled exactly as requirements are (#350, #351, #353, DEC-097)

The mirror is literal, not thematic:

| Requirements | Test procedures |
| --- | --- |
| `RequirementRevision.SourceChangeRequestId` | `TestProcedureRevision.SourceTestChangeRequestId` |
| `RequirementRevision.EffectiveBaselineId` | `TestProcedureRevision.EffectiveBaselineId` |
| `BaselineRequirementSelection` | `BaselineTestProcedureSelection` |
| `BaselineChangeRequestSelection` | `BaselineTestChangeRequestSelection` |
| `CandidateBaseline.RequirementsHash` | `CandidateBaseline.TestProceduresHash` |
| `RequirementBaselineMaterializer` | `TestProcedureBaselineMaterializer` |

Two places the mirror is deliberately inexact, both about sequencing: a package may be selected into a
baseline after the freeze, because procedures are written against requirements the freeze has already fixed;
and `MarkReleased` does not require a procedure manifest, because every build released so far has none and
gating would make them retrospectively invalid.

### A procedure covers requirements at its own level (#356, DEC-098)

A coverage link crossing a level is refused, and a procedure's number must agree with its level. This removes
the root cause of a System change request raising work in the HLR queue. Live data showed 0 of 1,251 links
crossing a level and 0 of 516 procedures disagreeing with their prefix.

### The testing surface is the requirements surface (#357, #359, DEC-099)

A test assessment row now follows the requirements-card design with one `Open assessment` control in every
state. Its drawer holds the conclusion, lifecycle actions, and per-requirement decisions. Each SYSTCR, HLR TCR,
and LLR TCR opens in its own workspace, where procedures can be introduced, modified, or retired.

### Approved procedure work reaches a build (#358)

`GET`/`POST`/`DELETE /api/baselines/{id}/test-change-requests` and
`POST /api/baselines/{id}/materialize-test-procedures` carry approved procedure work into a candidate build.

### Controlled Word Documentation Center (#355)

The standalone **Documentation Center** governs Word-authored lifecycle documents without replacing Microsoft
Word as the editor. Seven representative avionics documents demonstrate released, Draft, in-review, and
returned conditions: PSAC, SDP, SVP, SCMP, SQAP, SAS, and ICD.

The center provides stable document numbers, formal revisions, retained working versions, exact downloads,
direct links, build selections, lifecycle relationships, exclusive per-user checkout, ordered independent
review, electronic signatures, immutable released DOCX/PDF pairs, hashes, and audit history. Build 1.5 remains
read-only while Build 1.6 supports active document work.

The per-user Windows connector registers the `aerolink://` protocol, downloads the controlled Word working
copy, keeps the lease alive, and checks the saved file back into AeroLink. Every Draft section carries a faint
Draft watermark. Release preparation removes the watermark and changes visible Draft labels to **Release
Candidate** before Word produces the exact DOCX/PDF pair; the API rejects a candidate that still presents
itself as Draft. Final SQA authorization releases the exact recorded hashes.

## Defects found and corrected during delivery

- `StartNextRevision` originally collided with a unique index that omitted the revision. The index now includes
  `Revision`, matching the change-request model.
- The test-procedure materializer originally had no caller. It now has controlled API and UI entry points; see
  [LES-006](../../DECISIONS_AND_OPEN_QUESTIONS.md#les-006---a-capability-with-no-caller-is-not-delivered).
- A procedure-authoring dialog taller than the viewport did not scroll. Real-screen testing found and corrected
  it.
- Managed-document review steps were initially updated instead of inserted in PostgreSQL, an author could also
  appear as final approver in showcase data, and released signatures could appear under the wrong Draft. These
  were corrected with persistence, role-separation, and exact-revision regression coverage.
- Documentation Center styles initially leaked three shared class names and used labels below the 12 px
  readability floor. Production chunk testing found and corrected the isolation and typography.

## Open product questions and limitations

- Ownership of a folded-in change-request claim across a TCR revision remains intentionally unresolved. A
  successor currently refuses to start while its predecessor owns those claims rather than silently covering
  less work.
- Issue #332 Phase 3, the DOORS/ReqIF parser, still waits on a real extract.
- No existing build carries a procedure manifest because all existing builds predate the mechanism.
- GitLab remains the external source of truth for implementation code linked to approved LLR revisions.

## Important operating paths

- Production-shaped local start: `START_AEROLINK_PRODUCTION.bat`
- Development start: `START_AEROLINK.bat`
- Controlled stop: `STOP_AEROLINK.bat`
- One-time Word connector install: `INSTALL_AEROLINK_DOCUMENT_CONNECTOR.bat`
- Backup: `BACKUP_AEROLINK.bat`
- Documentation Center guide: `product/docs/MANAGED_DOCUMENTATION_CENTER.md`
- Technical overview: `docs/AEROLINK_TECHNICAL_OVERVIEW.md` and matching DOCX

The API applies pending migrations at startup. Do not delete `product/.local`, reset the database, or replace
the evidence root during qualification.
