# Integrity Problem Report source packages

This is the implementation and operator contract for the bounded first slice of
[#1186](https://github.com/AeroLinkDEV/requirements-management-tool/issues/1186), under
[DEC-145](../../DECISIONS_AND_OPEN_QUESTIONS.md#dec-145---integrity-package-acceptance-and-preserved-source-access).
The issue remains the live authority for outstanding research, customer qualification and wider migration.

## Two pieces and the qualification boundary

The Java 17 extractor lives in [product/tools/integrity-extractor](../tools/integrity-extractor/README.md).
It runs outside AeroLink and constructs read-only PTC command-API requests. It enumerates an
administrator-accepted inventory, captures typed field values, history, annotations, references and
current attachment bytes, checks inventory/configuration/items/bytes again, then seals a ZIP.
The checkpoint directory belongs to that capture and is bound to its configuration.
Resumption checks retained bytes and re-reads source items and attachment bytes before sealing.

**No customer Integrity server has been qualified.** Compiling against the published SDK is not proof
of server/API compatibility or response shape. A real adapter uses a customer-qualified response-field
profile. Qualification binds the SDK jar and response-profile hashes. Unrecognized value types, absent required fields, command errors and changed source data fail
capture. SDK date objects retain their epoch-millisecond representation and source display string;
the adapter does not pretend that those restore the source's original date-only/zone semantics.
They remain uninterpreted source values unless a future qualified decoder establishes their meaning.

The current live adapter cannot retrieve historical/replaced attachment bytes. An administrator
inventory that finds such bytes requires an adapter extension and new qualification, not an exclusion.
A qualification declaration is accepted only when it identifies the same source instance/server version,
an evidence reference, accepted-by identity, a capability-matrix reference, and demonstrated absence of
missing historical attachment bytes. It is an operator assertion backed by external evidence, not a
cryptographic certificate. Hashes prove byte consistency relative to the acquired package, not authenticity
or completeness of data hidden from the extraction account. See #1186 sections 4–6 and 13.

## Package version 1

The ZIP contains `manifest.json`, exactly one schema, users and capabilities JSON member, item JSON
members, and attributed attachment byte members. Every non-manifest member has a path, kind, byte length
and SHA-256 in the manifest. The root is SHA-256 of the exact UTF-8 manifest bytes and is recorded in a
separate `.manifest.sha256` acquisition sidecar. Keep a trusted independent copy of that hash.

The manifest identifies a stable source-instance UUID, source name, server version, query and capture
time. Scope names projects, item types, exact live decimal item IDs, accepted inventory and source-freeze
references, full audit history, attachment depth, and relationship preservation without following links.
Versioned document traversal and wider native artifact migration are not part of this profile.
Source item IDs remain numeric SDK identities; title/custom field values are not substituted as identity.
Custom Unicode values retain their original text and are not uppercased or normalized.

Only `complete-within-scope` packages without extraction findings are accepted. Every exclusion or loss
requires a later owner decision. `unproven` packages are refused; `fixture` requires the explicit
`IntegrityImport:AllowFixtures` configuration on a disposable host (default false).
The normal route accepts `qualified` packages with the qualification evidence retained inside the package.

Limits: 50 MiB compressed, 100 MiB expanded, 10,000 archive members, JSON depth 48 and 2,000 fields per
item. Paths must be canonical relative ASCII file paths. Links, duplicate/colliding paths, duplicate
JSON properties, missing/unlisted members, hash/size mismatches and unsupported versions are refused.
Archive members are read in memory; none is extracted to a filesystem path or fetched from a URL.

## Review and acceptance

In a project's Problem Reports register choose **Import from Integrity…**, upload the package, paste the
independently acquired root hash, select fields and map source values. Source instance plus numeric item ID
is the identity; the displayed source system is `Integrity:<source-instance-UUID>`.
Already imported identities are skipped without changing existing reports. Mapping differs from the old
CSV/XLSX path: exact source field names and mapping keys are retained, with no implicit Unicode folding.

The preview accounts for every item, field, history entry, annotation, attachment, relationship and
package member. Inspect mapped content, people, category, state, build, dates and findings. Missing required
mappings block the whole batch. Source relationships stay directed/exact source references; mapping a
target build is an explicit native field mapping, not importing a source effectivity fact.
Source-closed reports retain DEC-139's read-only **Closed in source** behavior.

Date representation and interpretation are separate. Original raw values stay in the package and
reconciliation. ISO date-only and local/unknown-zone values have no instant. An explicit-offset ISO value
normalizes to the same UTC instant only when exactly representable to one microsecond. Unreadable and
unrepresentable values remain preserved with a finding. No display format supplies a rounding rule.

Rich content enters AeroLink's non-executable content model. XHTML conversion is bounded to 200,000
source characters and depth 32; unsupported markup/formatting produces a finding while its original
payload remains preserved. No external URL is fetched. Package-backed `mks:///` images must resolve
unambiguously to an attributed PNG or JPEG accepted by AeroLink's existing bounded image validator. Preview image
bytes are limited to 8 MiB total. Source images retain restricted access after import; ordinary
document exports do not embed their restricted bytes.

Password confirmation accepts the exact preview, including outcomes and findings, package root, mapping,
project and resolved destination identities. Commit rebuilds the preview under the provider's project
write lock and refuses changed intent. It stages and verifies files first, then commits reports, source
identities, immutable reconciliation, the importer's signature and blob references together.
Source signatures create no native signatures; the sole new signature accepts the import.

Keep the operation identity when retrying an uncertain response. The same accepted operation and content
returns its original receipt after access/password checks; changed content under that identity is refused.
A definitively rolled-back storage attempt needs a new review/operation. Clicking **Review updated
mapping** supplies that new identity. Do not delete evidence to clear a pending operation.

## Preservation, reconciliation and recovery

The original package is a controlled attachment. Special package/image artifact types are withheld from
generic attachment downloads. Dedicated retrieval and inline images require project access, Problem
Reports enabled and Configuration Manager, Program Manager or Administrator authority. Source extraction
permissions confer no AeroLink permissions.

**Preserved imports** provides the exact acquired package and signed reconciliation/receipt.
For every real migration, compare the durable receipt and report identities to its own manifest,
review the native imported revision against the mapped fields, and inspect all findings and source
outcomes. Record the human reconciliation in the migration's controlled evidence. Fixture results do
not replace this per-migration review.

File publication uses the existing managed storage journal and verified evidence store. Pending leases
fence active work; failures reconcile durable attachment references before retaining or quarantining bytes.
An uncertain database commit must never trigger blind deletion. Use the existing controlled storage
reconciliation and backup/restore procedures in [OPERATIONS.md](OPERATIONS.md), preserving both the
database and evidence store. The normal developer database on port 54329 is never a rehearsal target.

## Qualification evidence

- Infrastructure tests run the actual Java extractor against independently authored source fixtures and
  assert source values/bytes, manifest refusal and date meaning.
- Hosted API tests exercise preview, password confirmation, stale intent, retry, restricted retrieval,
  source/native separation and two interruption windows.
- PostgreSQL qualification migrates a new disposable database and forces overlapping imports through a
  held provider lock, asserting one batch and an exact microsecond instant.
- The browser journey verifies the actual package review, raster preview and invalidation after mapping
  changes. It does not submit an import into shared showcase data.

The related CSV/XLSX issues #1193, #1194, #1198 and #1199 are separate. This package path does not certify
those readers or repair earlier imports. Live SDK/server extraction, historical-byte support, customer
scope/permissions and an actual cutover remain tracked on #1186.

Primary API references: PTC's
[viewissue command](https://support.ptc.com/help/windchillrvs/r13.2.0.0/en/IntegrityHelp/int-man_pages/im_ref/im_viewissue.html)
and [extractattachments command](https://support.ptc.com/help/rvs/r13.4.0.0/ko/IntegrityHelp/int-man_pages/im_ref/im_extractattachments.html).
Documented options still require target-server demonstration.
