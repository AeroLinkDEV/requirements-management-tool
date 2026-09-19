# Architecture Direction

## Shape

AeroLink begins as a modular monolith: one deployable ASP.NET Core backend with explicit domain, infrastructure, and API boundaries, plus a React web client. This keeps controlled workflows transactional and understandable while leaving clean seams for later modules.

The ordered persistence phases and transaction/failure semantics are documented in [SAVE_BOUNDARY.md](SAVE_BOUNDARY.md).

## Technology decisions

- React and TypeScript for the browser client
- ASP.NET Core on .NET 10 LTS for the API
- Entity Framework Core for persistence
- PostgreSQL as the intended multi-user production database
- SQLite as a zero-administration local development provider only

These are implementation decisions, not changes to the authoritative product behavior defined by the root Markdown documents.

## Domain boundary

Lifecycle rules live in domain objects rather than controllers or UI code. The API requests an operation; the aggregate validates state, actor authority, revision behavior, and ordered review rules; persistence records the resulting state and audit events atomically.

Managed lifecycle documents follow the same boundary. PostgreSQL owns identity, formal revisions, build
selections, review steps, signatures, links, checkout sessions, hashes, and audit events. Binary DOCX/PDF
versions use the controlled evidence store. A small Windows connector is the sole Word boundary: it redeems a
one-use scoped grant, opens the exact source, maintains the exclusive lease, and returns a new immutable working
version or release candidate.

`SystemChangeRequest` is the shared System/Software change aggregate. Stable artifact identity (`SRCR-00001` or
`HLRCR-00001`) is distinct from revision display (`SRCR-00001.04`). Requirements referenced by a change request
retain stable identities and immutable revision identities.

Downstream engineering work and upward allocation are separate controls. Approval raises build-scoped consuming-
discipline assessments; prospective HLR/LLR changes carry exact proposed parent revision IDs in their review
snapshot. Baseline materialization alone creates the resulting immutable `AllocatedFrom` trace links.

The human Digital Thread projection walks only exact revisions in one materialized baseline. It follows the
selected requirement through its System/HLR/LLR ancestry or descendants, then joins exact procedure coverage,
the build-specific execution, linked checksummed evidence files, and the immutable software-build record. When
several confirmed procedures cover the exact requirement, it prefers one whose latest build-scoped execution
has linked evidence, then one with a result, then the controlled number as a stable tie-breaker. A free-text
evidence reference remains useful context but does not make the evidence stage complete. The general
relationship explorer remains available; the compact path is an assurance projection, not a new trace store.

Critical review mutations use an application service boundary between HTTP and the aggregates. TCR submit and
approve orchestration lives in `TestChangeReviewWorkflowService`: endpoint modules bind requests and perform
resource-entry checks, while the service resolves the active or frozen workflow, freezes authority provenance
and impact snapshots, transitions the aggregate, creates notifications and signatures, and owns the approval
transaction. `WorkflowAuthorityService` is the shared resolver for TCR and change-request paths; it is not
hosted by an endpoint module, so those paths cannot depend on one another for authority decisions. Aggregate
methods remain responsible for lifecycle invariants, and the service preserves the approval two-save ordering
needed before downstream Case assessment work is written.

Software-build identity is canonical: a release version such as `1.6` is represented by `SW-01.60`. The
historical `CandidateBaseline` and executable `SoftwareBuild` persistence records are implementation facets of
that one software build, not separate product concepts presented to the user.

## Code traceability boundary

GitLab is the source of truth for repositories, source code, branches, merge requests, review discussion, CI,
and commit content. AeroLink records contextual relationships separately from accepted implementation evidence.
Merge-request relationships identify the repository, numeric remote project and IID; file relationships pin
an immutable source snapshot, full SHA, path and optional line range. Targets are exact requirement revisions,
change-request revisions, typed requirement proposals or historical Problem Report snapshots. Adding links
does not amend signed target content. Withdrawal and re-add retain independently attributable history.

The current build source is an explicit versioned selector over immutable selection events and snapshots.
A friendly ref is resolved before confirmation; confirmation supplies the previewed SHA and expected source
and repository versions. A branch/tag move cannot silently substitute another commit. Selected-source browsing
checks snapshot/repository identity before returning bounded metadata, including cached observations.

Evidence acceptance is a separate command against an expected materialized campaign baseline and exact
required revision. `CodeTraceabilityProjection.RequiredAsync` remains the obligation authority. A new immutable
evidence set records one or more active `Implements` relationships, or a justified no-code decision. GitLab
acceptance observes each actual merged MR's merge/squash result and verifies ancestry to the selected source;
each file must be observed as a regular blob on a bounded exact-commit tree page. Provider failures are not
accepted evidence. No-code acceptance requires no repository or source selection.

An explicit versioned selector chooses the current set and names any superseded legacy record. Source changes
or candidate take-back can invalidate applicability without deleting the set or falling back to older evidence.
Readiness, Code, change/report projections and review manifests consume the same current-evidence projection.
Legacy `CodeTraceabilityRecord` rows remain readable historical facts; the old manual GitLab capture command
cannot bypass the new acceptance checks. A new format is frozen per campaign approval cycle, while existing
v1 serialization and historical signatures remain unchanged.

Project repository configuration persists separately from those evidence records and from the creation draft. A URL
starts unverified; an installation-scoped read-only GitLab probe can record the exact remote project identity.
The browser cannot supply connection verification, and edits or failed rechecks clear current verified status.
No database transaction spans the probe; optimistic configuration versions prevent stale observations from
overwriting a newer connection. Deferred setup remains pending and does not block unrelated engineering work.
GitLab commands require the current verified repository identity. Repository verification alone does not
establish implementation, merge or approval. Historical evidence is never rewritten or hidden when connection
configuration changes. Remote command preflight runs outside the transaction; the short shared project write
scope then rechecks authority, lifecycle, baseline, configuration, source and expected record versions before
atomically persisting the result. Configuration, source, campaign/reopen and evidence writers share that boundary.

The MR register groups and pages recorded build associations before bounded metadata decoration; discovery
separately includes unlinked remote MRs. Code Explorer traverses all exact-source file pages, with an optional
server-paged linked-file register. Artifact tabs use the same relationship store. Approval participants are
provider observations, never inferred from a merge, an empty approval list or satisfied rules. Display reads
use a bounded short-lived cache and disclose observation time; commands use fresh uncached reads.
Source text, diffs, discussions, webhook synchronization and runtime GitLab writes are outside this boundary.
AeroLink never clones a repository or approves a merge. Live synthetic qualification is distinct from local
mock-provider tests and must not fabricate remote identifiers, statuses or participants.

## Persistence

Repository interfaces are defined in the domain project and implemented in infrastructure. Provider choice is configuration-driven. PostgreSQL uses versioned EF migrations at application startup; SQLite remains isolated to tests and disposable local scenarios.

The change-request child graph is selected explicitly by caller through the load contract documented in
[CHANGE_REQUEST_LOADS.md](CHANGE_REQUEST_LOADS.md). This keeps read and command paths from purchasing all
controlled history by default while preserving a consistent snapshot for split collection loads.

`AeroLinkDbContext` exposes asynchronous persistence as its single supported write boundary. `SaveChangesAsync`
performs the provider reads and controlled preparation needed for aggregate child-state repair, versioning,
integrity checks, lifecycle events, and notification outbox rows before one EF write. The synchronous EF
overloads fail before tracker or provider mutation and are marked as compile-time errors for direct
`AeroLinkDbContext` callers; callers typed as `DbContext` receive the same runtime guard. Code that requests
`SaveChangesAsync(false)` owns the usual EF deferred `AcceptAllChanges` decision and must accept the tracked
states before beginning another logical unit of work.

Fresh installations contain no assumed program. The onboarding transaction creates the Program, its first Project/software product, and its initial release together. FMS records are optional demo data controlled by configuration and are disabled by default.

### Recoverable project creation and source inception

New-project creation is a durable, typed setup draft rather than a browser-only wizard. An AeroLink administrator
creates the draft; its creator and administrators may read, save, resume, and upload it, while only a current
AeroLink administrator may finalize it. The draft
allocates its internal backing Program, Project, initial release, and inception-baseline identities once, without
asking the user for a Program name or code. A numeric draft version is the optimistic concurrency token. The setup
state, current step, selected start kind, source choice, configuration, acceptance hashes, and finalization
operation/result are persisted so sign-out, API restart, duplicate submissions, and a lost HTTP response can be
recovered. A retry with the same operation key returns the committed result; a conflicting edit is rejected for
refresh rather than silently overwriting another answer.

The service keeps the setup draft and staged source package separate from a usable Project. Uploads are authenticated,
bounded to 50 MiB, read into the bounded staged payload, and SHA-256 verified before storage against the draft and
before a destination Project exists. Parser observations and server-derived mapping/reconciliation are durable
package state. Source configuration and reconciliation are typed and re-derived on the server; browser JSON is not
evidence that a mapping or gate passed. Parsing and upload streaming happen before finalization. Finalization uses a
short serializable local transaction for its required database reads, local validation/password confirmation,
materialization, and result recording; it never spans a user interaction or an external network call.

There are three inception boundaries:

- **Fresh** creates the necessary empty containers for the accepted effective ladder, review configuration, creator
  management access, and one new **IN WORK** build. It has no inherited FMS roster, leadership, requirements, cases,
  procedures, evidence, repository settings, or other project content.
- **AeroLink baseline** requires an exact source Project and CandidateBaseline whose state is Frozen or Released and
  whose materialized source manifest is present. Current source-project membership/access is checked when options
  are listed, when the source is captured, and when a resumed draft is read, configured, reconciled, or finalized.
  The selected source revision, state, relationships, parent IDs, source owners/authors, and evidence/execution
  facts remain source attribution. Target verification artifacts are unassigned until the new project manages them.
- **External baseline** accepts ReqIF, CSV, and XLSX through the existing parser/import foundations, then requires
  explicit category selection, object/attribute/relation mapping, exclusion reasons, dependency closure, and
  server reconciliation. ReqIF supports Requirements and explicit Traces; CSV and XLSX support Requirements. Native
  sources additionally support Cases, Procedures, and Evidence source facts and supported relationships. Unsupported,
  unmapped, or excluded content is reported rather than fabricated. The source package retains foreign identifiers,
  exact bytes/hash, parser observations, mapping, reconciliation manifest, and materialized source records.

Source acceptance is a separate immutable electronic signature. A current AeroLink administrator must confirm the
password; the signature binds the source hash, categories, mapping, reconciliation, accepted ladder,
manifest, target IDs, and canonical first-build identity. It records who accepted source provenance and the meaning
of that assertion. It does not assert that source approvals are new-project approvals, source executions occurred in
the target project, or source evidence is newly produced. The materialized Project exposes a provenance projection
with source identities, revisions, states, target links, acceptance person/time/meaning, and safe source snapshots;
storage keys are redacted. A later signature cannot replace the signature bound to the package assertion hash.

The accepted ladder becomes effective before source content can be materialized, and first-content persistence is
serialized against its configuration version. Structural ladder changes remain available only while the project has
no authored or inherited engineering content; empty containers do not count. All source consumers use the effective
ladder and supported capability profile. The first build uses the canonical `SW-NN.NN` identity and is explicitly
**IN WORK**; source baseline state remains historical and distinct. Visual build-lineage entry uses stable project,
build, lifecycle, and predecessor identities, with explicit user selection even when only one build exists.
The initial inherited build's living requirement documents read its completed setup's exact frozen inception
manifest plus approved project changes when there is no released predecessor. They label the basis as an accepted
source manifest and identify the initial materialized target baseline separately; source acceptance does not confer
new engineering approval. Independent fresh builds remain empty.

Repository setup is a project-scoped configuration seam: Configure later stays visibly Pending, while Connect now
records ConfiguredUnverified until an installation-approved read-only GitLab probe observes a remote identity.
Neither a URL nor a browser-supplied status is a verified connection, and repository setup does not assert merge, CI,
or implementation evidence. Standard email, managed Word, PDF/DOCX, and code-linkage capabilities retain their
existing lifecycle prerequisites; an empty project reports pending/empty state rather than fabricated readiness.

Enterprise authoring extends the existing requirement aggregate instead of replacing it. Stable artifacts and immutable requirement revisions remain authoritative; revision profiles add schema-bound rich content and classifications, specification nodes add reusable document placement, and comments/views/jobs preserve collaboration and high-volume operations as separate attributable records. Existing Projects are synchronized idempotently so the new workspace can be introduced without rewriting approved history.

CSV/XLSX interchange also has an older two-step preview/commit workflow. Files are size- and expansion-limited,
hashed, parsed into persisted validation results, and cannot create approved requirements directly. A successful
proposal commit creates a Draft change request containing proposed requirement changes. That ordinary proposal path
is separate from external inception: it does not create a new Project, does not stage a draft-owned inception package,
and does not satisfy source mapping, reconciliation, or source-acceptance gates.

## Security boundary

Identity now comes from a revocable authenticated server session. Passwords use salted PBKDF2 derivation, opaque session tokens are stored only as digests, material API actions derive the actor from the authenticated principal, Program memberships and roles constrain access, and SRCR/release approvals require password-confirmed immutable electronic signatures. Production deployment still requires TLS, enterprise identity federation/provisioning, configurable policy enforcement, privileged-access governance, audit export, and independent security review as defined in [SECURITY_AND_IDENTITY_MODEL.md](../../SECURITY_AND_IDENTITY_MODEL.md).

## Enterprise hardening boundary

The enterprise-hardening release adds versioned controlled files, structured and attachment-aware redlines, saved structured queries with stable links, durable background processing, multi-session edit detection, three-way merge records, integrity checkpoints, and an operator-facing control dashboard. These are separate attributable records around the authoritative requirement/SRCR/baseline aggregates; they do not create an alternate approval path or mutate approved history.

Files are streamed to protected local content-addressed storage, SHA-256 hashed, permission-checked through their Project and artifact, and retained across superseding versions. Background operations have idempotency keys, attempts, progress, final outcomes, and downloadable controlled output. Edit sessions capture a base snapshot and numeric concurrency version; collisions persist base/local/remote content and require an explicit resolution.

## Open Digital Thread boundary

The [bounded Digital Thread read contract](DIGITAL_THREAD_READS.md) describes typed frontier selection, explicit work limits, and the derived frozen-review adjacency lookup. Original snapshots remain the historical provenance authority.


AeroLink 2.0 introduces a separate machine-access boundary under `/api/v1`. Machine identities belong to exactly one Project, receive explicit scopes, and authenticate with one-time API keys whose secrets are never persisted. The first public resources expose cursor-paginated, ETag-bearing requirement reads and idempotent external-event ingestion without exposing internal tables or browser-session behavior.

Integration events and webhook deliveries are durable, separate records. Event creation and delivery creation share the application transaction; a hosted dispatcher claims due work with a conditional, expiring token, signs JSON envelopes with HMAC-SHA256, applies exponential retry, and retains bounded per-attempt outcomes for operator replay. Delivery is at-least-once: the stable event and delivery IDs are the receiver deduplication keys when a receiver accepts a request before local completion is recorded. Enabled-subscription and due-time filtering happens before the bounded dispatch batch, so disabled or future work cannot starve eligible work. Webhook signing secrets are protected through ASP.NET Core Data Protection and outbound targets fail closed against insecure or private destinations unless a development-only override is configured.

The Integration Command Center is the human control plane over these records. It shows scoped identities, endpoints, event activity, delivery health, replay actions, existing interchange history, and the ReqIF evolution path without creating an alternate approval path for requirements.

The connected foundation now includes the governed ReqIF profile, lifecycle transaction events, filtering and
conditional public reads/writes where documented, and monitored human control planes. Further connector depth
must start from a selected customer/vendor contract. Broad authorization or module rewrites are not standing
architecture work without a reproduced security/product defect; the current Program/resource enforcement and
server-authoritative actor boundary remain mandatory.
