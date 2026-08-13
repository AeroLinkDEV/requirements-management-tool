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

The browser handoff is a signed, five-minute `aerolink-connector-launch-v1` envelope rather than a caller-chosen
server URL. It binds the enrolled deployment and exact origin, Project, stable document, formal revision, mode,
one-use nonce, source attachment/size/SHA-256, and required OOXML profile. The connector verifies this before
any request, refuses redirects, and requires redemption to repeat the signed identity exactly. A connector
without an explicit active enrollment cannot contact even an HTTPS or loopback server. New files use a unique
deployment/Project/document/revision/session workspace and never truncate an unresolved workspace. Protected
local metadata contains only controlled identity, source hashes, lease state, and local evidence hashes—never a
browser credential or reusable connector token. A crash, offline interval, expired lease, or force unlock leaves
that workspace discoverable from the connector recovery center. Resume and discard require a fresh authenticated
browser action that returns a new signed, one-use command bound to the exact workspace and current source.
5. The formal revision scope is defined once when the revision starts. While Draft or Returned, an authorized
   owner or project authority may correct it through the explicit audited action with an optimistic-concurrency
   version and reason. It cannot be changed while in review or after release.
6. The owner builds an ordered, per-cycle route of two or more named stages, assigns a different active Program
   member to every stage, and classifies each as content **Review** or release **Approval** before submitting the
   exact current working attachment, formal-summary hash/version, and relationship manifest. Content reviews
   precede approvals and the final stage is an approval. The responsible owner, initiator, contributors, and any
   duplicate assignee are refused. Stage names, kinds, order, people, and authority evidence are frozen on the
   submitted cycle rather than inferred from fixed product slots.
7. A return preserves the completed review evidence and permits the owner to define a different ordered route
   for the next review cycle after correction.
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

## Bounded Project queries

The Project register returns at most 50 records by default and enforces a maximum of 100. Its opaque cursor is
bound to the authorized Project, filters, sort, direction, and first-page snapshot; changing any of those inputs
requires a new first-page request. Document number is the default stable order, with `updatedAt` available as an
explicit alternative. Search, lifecycle state, type, acronym, responsible owner, and steward predicates run in
the database before the page is selected. The dashboard applies the same Project-wide head predicates, so its
counts reconcile to the complete filtered register rather than only the visible page.

Long-lived evidence is not part of the current operational record query. Formal revisions, check-ins, review
steps, signatures, relationships, contributors, assignments, and audit events are available under
`/api/managed-documents/{documentId}/history/{surface}` as snapshot-bound pages. Relationship target lookup is
also cursor-paged and Project-scoped before materialization. New rows after the first request appear when the
operator refreshes from page one; they cannot shift records between pages in an existing snapshot. Invalid,
cross-filter, or oversized cursors fail closed with `400`. The browser shows the total register size and loads
additional records on request, while a direct document URL loads the current record independently of its page.

Production PostgreSQL indexes cover the Project/type/steward/register orders, document/state/revision heads,
review assignee/state, check-in time, attachment revision/logical version, relationship revision/time, and event
document/time paths. Qualification targets a maximum response page of 100 and verifies multi-page uniqueness,
filter binding, history isolation, dashboard reconciliation, payload bounds, and PostgreSQL query plans. SQLite
is retained only as a fast functional test provider; PostgreSQL is the authoritative performance provider.

The qualification budget is a maximum eight SQL commands and 256 KiB JSON for an inventory/dashboard request,
eight SQL commands and 128 KiB for one history page (including identity and Project-authorization reads), and two seconds of API latency for a 100-row page on the
reference local PostgreSQL installation. The 5,000-document/20,000-event fixture completed the selective
inventory plan in 1.7 ms and the indexed 51-row history plan in 0.1 ms; history used
`IX_managed_document_events_DocumentId_OccurredAt`. These database timings exclude HTTP and test-host startup.
Memory retained for a response is bounded by the requested page plus the current-head joins; histories and
relationship candidates are never accumulated server-side across pages.

## Lifecycle links

An authorized revision owner or Project configuration authority can link existing change requests, Problem
Reports, Test Change Requests, and builds from across the Project while a revision is Draft or Returned. The
browser supplies only the target type, target ID, and a typed meaning allowed for that target. AeroLink resolves
the canonical number, title, current state, Project, build provenance, and deep link server-side; browser labels
are never evidence. Ordinary engineers and global administrators without explicit Project document authority
cannot mutate the set.

The supported meanings are versioned and bounded: `MotivatedBy` or `ImplementsChange` for change requests,
`VerificationImpact` for TCRs, `AddressesProblem` or `AffectedBy` for Problem Reports, and `RelatedBuild` or
`AppliesToMilestone` for builds. Same-Project records from different builds are valid traceability and retain
their real build/version metadata, but never select document effectivity. Cross-Project and type/meaning
mismatches fail before mutation.

Every add, correction, or supersession is optimistic-concurrency checked. Correction retains the prior row,
reason, actor, time, and replacement identity rather than deleting evidence. In Review, Released, and
Superseded relationship sets are immutable. Submission serializes the active canonical links in a deterministic
order and binds that manifest hash into the technical review snapshot. Release-candidate hashing binds the same
submitted manifest into the final DOCX/PDF release manifest, so a relationship change after return necessarily
requires a new submission and signatures over a new snapshot.

## Desktop connector

Install once per Windows user with `INSTALL_AEROLINK_DOCUMENT_CONNECTOR.bat`. The installer requires no local
administrator rights and registers the `aerolink://` protocol for that user. Word must be installed only for
editing and final PDF production.

Connector trust controls are deliberately small: HTTPS is required for remote servers; loopback HTTP is allowed
for the local demonstration; launch tokens are one-use and short-lived; session access is scoped to one revision;
tokens stay in memory; stale-source check-ins fail without overwriting; macro-enabled files and non-DOCX sources
are rejected; and a final release is accepted only when its DOCX is free of Draft watermarks and Draft state
markings and its PDF is valid.

## Controlled file integrity

Every material managed-document read is fail-closed. AeroLink opens the exact retained object without write or
delete sharing, rejects reparse/symbolic-link traversal, verifies the recorded byte count, recomputes SHA-256 over
that same open handle, and only then resets and returns that handle. This applies to full and range downloads,
connector source delivery, successor transformation, review submission, release preparation, and final signature
of the candidate DOCX/PDF set. Range processing therefore cannot expose an unverified prefix.

The connector redemption binds the source attachment ID, size, and SHA-256. The Windows connector downloads to a
unique temporary file and independently checks both values before replacing its working copy or launching Word.
An incomplete or mismatched download is deleted and never opened.

Every accepted DOCX also passes the shared `aerolink-ooxml-safe-v1` profile in both the API and Windows connector.
The profile bounds the compressed package, entry count, per-part and aggregate expansion, compression ratio, XML
bytes/depth/nodes/attributes, media size/dimensions, and total processing time. It canonicalizes ZIP part names;
requires an unambiguous macro-free Word root and content-type manifest; resolves the complete internal
relationship graph; and rejects missing targets, cycles, active or embedded content, external templates/images,
unsafe external schemes, DTD/entity processing, and DDE/LINK/INCLUDE/DATABASE fields. Ordinary HTTPS/mailto
hyperlinks remain supported. Validation streams ZIP/XML content instead of materializing expanded parts.

## Local workspace recovery

Each connector launch has a unique workspace. The connector writes authenticated, Windows-user-protected
recovery metadata before source download and updates it atomically through download, editing, heartbeat,
finalization, conflict, and cleanup. Starting another launch never reuses or truncates a retained directory.
Heartbeats retry with bounded backoff; the workspace visibly becomes lease-at-risk before expiry, and finalization
pauses renewal so heartbeat and check-in cannot race each other. The server uses a 15-minute renewable lease.

Run the connector without an `aerolink://` argument to open its recovery center. Resume opens the canonical
Project document in the browser, where the user must authenticate again. AeroLink then rechecks Project access,
current revision responsibility or review authority, exact source attachment/hash, revision state, and current
checkout ownership. A current checkout is rotated into a new short-lived connector grant; a completed operation
returns signed cleanup evidence; abandoned work returns a signed discard command. A different current source,
advanced revision, authority loss, or competing checkout fails closed. Conflict work remains exportable but is
never uploaded automatically.

The connector uploads only a closed or saved Word document. Unsaved or externally locked files remain in place.
Draft check-in and release-candidate responses must repeat the exact accepted attachment IDs and hashes; cleanup
occurs only after those values match the retained local files and Word is closed. Successful or explicitly
discarded work is then removed. Source conflicts are marked for at least 90 days of operator retention, and
expired, abandoned, or force-unlocked work for at least 30 days; retention markers do not silently delete user
work. Legacy connector folders without authenticated recovery metadata are export-only.

New managed-document DOCX attachment rows retain the exact validation profile and accepted result. Historical
rows remain null rather than being retroactively claimed as validated, but every material read re-runs the
current profile after exact size/hash verification. Thus a restored or legacy package that matches its historical
hash but violates the safe profile is blocked operationally and never reaches Word, transformation, review,
release, or download. Profile rejection occurs before staging, so rejected uploads leave no attachment row or
evidence object.

A missing, unreadable, unsafe, size-changed, or hash-changed object creates one open critical operational alert,
one document event, and security-audit evidence. The affected formal revision is shown as integrity-blocked and
cannot be opened, submitted, transformed, or released. Repeated reads retain the original incident instead of
creating alert noise. Configuration Management, SQA, or Program authority may recover only bytes whose size and
SHA-256 exactly match the immutable attachment metadata. Existing altered bytes are moved into the evidence
quarantine before activation, the historical metadata and signatures remain unchanged, and recovery resolves the
incident with append-only audit evidence. The Project integrity-scan endpoint and periodic worker apply the same
verification contract to every managed-document attachment. These Project-document incidents do not implicitly
block an unrelated software build.

## Failure-atomic storage and recovery

Document creation, successor creation, connector check-in, and release-candidate preparation use one bounded
staging protocol. AeroLink validates authority, revision/session state, expected version, source hash, comments,
and file profile before reserving permanent object identities. It then records a durable operation key, payload
hash, Pending state, expected size/hash/object manifest, and planned response. Staged objects are promoted by
atomic same-volume moves inside the serializable metadata commit window. The operation becomes Available only
after every attachment row, lifecycle transition, check-in/event row, and object is committed.

The release DOCX and PDF are one candidate set with one operation ID and manifest; neither is exposed as a
candidate unless both metadata rows and both exact objects commit. Same-key/same-payload retries return the
original result and cannot create another working version or candidate set. Reusing the key for different
content or intent returns a conflict. Document creation requires the caller to supply that one-use operation
key; AeroLink never infers retry identity from document content, so two intentional documents with equal
business fields remain distinct when their keys differ.

Known failures roll back metadata and move any staged or promoted object into `_quarantine` before reporting the
operation RolledBack. The Project storage-reconciliation endpoint, also run by the periodic integrity worker,
fences Pending operations behind a conservative 30-minute lease so it cannot seize a live request. A known
failed request explicitly surrenders that lease; otherwise only expired operations are treated as interrupted.
Reconciliation is idempotent: a complete referenced set is verified and finalized; an unreferenced
set is quarantined and rolled back; a partial candidate/attachment set, dangling editable revision, missing file,
or hash mismatch opens a deduplicated critical operational alert and remains RepairRequired. Reports identify
operation IDs and quarantined keys. Operations and health use the configured evidence root and never infer a
software-build freeze.

Backup recovery is proven as an application outcome, not only as archive consistency. Restore first binds a
shadow database to its isolated evidence root, then a one-use loopback validation process downloads every
managed-document attachment through the normal integrity-verifying API and independently recomputes size and
SHA-256. Production database and evidence activation is reversible as one retained pair; AeroLink is not
restarted on a partial or failed activation.

An authorized responsible owner, Configuration Manager, Program Manager, or Project Engineering Lead may
withdraw only a Draft or Returned revision with its expected version and a reason. Withdrawal closes active
checkouts, revokes connector grants, retains and marks controlled attachments withdrawn, and appends document and
security-audit evidence. In Review, Released, and Superseded revisions remain immutable.

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

The controlled-relationship migration leaves historical labels and review/signature hashes exactly as stored.
It marks those rows `LegacyClientSupplied`, derives only Project and build provenance already provable through
existing foreign keys, and does not fabricate a relationship manifest for a review that never captured one.
New submissions use the versioned canonical relationship evidence contract.

## Stewardship, responsibility, and authorship

The document steward owns long-term Project accountability. The responsible revision owner coordinates the active formal revision. The stable record creator, revision initiator, accepted check-in contributors, and temporary checkout holder are separate identities and are displayed separately.

Creating a document or successor validates the selected assignee as an active account with current Program authoring authority or a valid delegation before any row or file is created. Global administrator status is not document-authoring authority, so an administrator must explicitly select an eligible Program author. Ordinary and privileged check-ins never transfer responsibility. Configuration Management, the Program Manager, or the Project Engineering Lead can explicitly reassign stewardship or Draft/Returned revision responsibility using a different eligible assignee, required reason, and expected version. AeroLink retains old/new assignee, assigning actor, reason, effective time, notification, and append-only document and security-audit evidence. Disabled or departed responsible owners appear in the authorized My Work recovery queue.

Submission freezes the attributable contributor set from accepted check-ins for that exact review cycle. A responsibility transfer therefore cannot make a prior content contributor independently eligible to review unchanged work. Legacy stewardship and responsibility preserve their respective retained owner fields; creator and initiator use the earliest retained check-in actor where available and fall back to those owner fields without changing historical check-in evidence. Where old review-cycle contributors can only be inferred from retained check-ins, their provenance is visibly `LegacyInferredFromRetainedCheckIns` rather than represented as contemporary proof.

## Review authority and exact decision intent

Each newly assigned review step records the required authority, authority actually exercised, exact direct
membership/delegation/standing-backup source row, workflow identity and version, assignment time, and policy.
Review-kind stages accept the configured reviewer and engineering-lead authorities. Approval-kind stages accept
SQA, Configuration Management, approval, or Program authority. Only the final Approval step may prepare and sign
the immutable release candidate. An explicitly identified administrator
substitution is retained as such; administrator privilege is never silently described as SQA. Future, expired,
or revoked delegations cannot create a review assignment.

The policy is `FrozenAtAssignment;ActiveAccountAtSigning`: authority is evaluated and frozen when the stage is
assigned, so later membership or delegation changes do not rewrite or invalidate that historical assignment.
The exact assignee must still have an active AeroLink account and confirm their password when signing. Signature
meaning and engineering rationale are separately required, bounded fields. The signature retains the exact
cycle, step, frozen authority source, workflow version, and submitted or release-candidate hash. Historical
steps and signatures for which those facts were never stored remain visibly `LegacyUnspecified` or blank; the
migration does not manufacture modern authority evidence.

Migration `20260813085734_AddManagedDocumentReviewRouting` records the explicit Review/Approval kind on every
step. Historical routes were created only by the former fixed endpoint: it always stored the last position as
the SQA/configuration release authorization and all preceding positions as technical review. The migration
preserves that known semantic by classifying only those last positions as Approval; it does not invent stage
names, assignees, authority, or decision evidence.

Submission and review commands carry the exact revision version, working attachment/hash, formal-summary
version/hash, relationship-manifest hash, cycle, step ID/version, submitted snapshot hash, and—at final
release—the exact DOCX/PDF candidate IDs and combined manifest. A caller-supplied one-use operation key makes a
same-intent retry return the original result while rejecting reuse for different intent. Stale tabs and
concurrent decisions return a stable conflict without a second signature or notification. Connector heartbeat
renews only the lease and no longer changes the finalize token, so a slow check-in or release upload is not made
stale merely by a healthy heartbeat; database concurrency still prevents renewal after finalization wins.
