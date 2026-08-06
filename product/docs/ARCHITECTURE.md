# Architecture Direction

## Shape

AeroLink begins as a modular monolith: one deployable ASP.NET Core backend with explicit domain, infrastructure, and API boundaries, plus a React web client. This keeps controlled workflows transactional and understandable while leaving clean seams for later modules.

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

Software-build identity is canonical: a release version such as `1.6` is represented by `SW-01.60`. The
historical `CandidateBaseline` and executable `SoftwareBuild` persistence records are implementation facets of
that one software build, not separate product concepts presented to the user.

## Code traceability boundary

GitLab is the source of truth for repositories, source code, branches, merge requests, review discussion, CI,
and commit content. AeroLink's `CodeTraceabilityRecord` is an immutable lifecycle pointer: Project, build,
exact LLR artifact/revision, GitLab repository/MR reference and URL, merge commit SHA/time, or an attributable
`No code change required` rationale. A uniqueness constraint prevents competing mappings for the same exact
LLR revision in one build. The Code center and release readiness use the same required-LLR projection; exact
mappings are included in the signed review manifest. Released-build mutation protection applies server-side
at the endpoint even when a caller does not supply browser workspace context.

The first integration is manual evidence capture and a small, conspicuously labelled FMS demonstration set.
Webhook synchronization, GitLab project allow-lists, CI state, many-to-many MR/LLR mapping, and automated
commit-in-build proof remain later integration depth. AeroLink never clones a repository or approves a merge.

## Persistence

Repository interfaces are defined in the domain project and implemented in infrastructure. Provider choice is configuration-driven. PostgreSQL uses versioned EF migrations at application startup; SQLite remains isolated to tests and disposable local scenarios.

Fresh installations contain no assumed program. The onboarding transaction creates the Program, its first Project/software product, and its initial release together. FMS records are optional demo data controlled by configuration and are disabled by default.

Enterprise authoring extends the existing requirement aggregate instead of replacing it. Stable artifacts and immutable requirement revisions remain authoritative; revision profiles add schema-bound rich content and classifications, specification nodes add reusable document placement, and comments/views/jobs preserve collaboration and high-volume operations as separate attributable records. Existing Projects are synchronized idempotently so the new workspace can be introduced without rewriting approved history.

CSV/XLSX interchange is a two-step preview/commit workflow. Files are size- and expansion-limited, hashed, parsed into persisted validation results, and cannot create approved requirements directly. A successful commit creates a Draft change request containing the proposed requirement changes, preserving the established review and baseline authority boundary.

## Security boundary

Identity now comes from a revocable authenticated server session. Passwords use salted PBKDF2 derivation, opaque session tokens are stored only as digests, material API actions derive the actor from the authenticated principal, Program memberships and roles constrain access, and SRCR/release approvals require password-confirmed immutable electronic signatures. Production deployment still requires TLS, enterprise identity federation/provisioning, configurable policy enforcement, privileged-access governance, audit export, and independent security review as defined in [SECURITY_AND_IDENTITY_MODEL.md](../../SECURITY_AND_IDENTITY_MODEL.md).

## Enterprise hardening boundary

The enterprise-hardening release adds versioned controlled files, structured and attachment-aware redlines, saved structured queries with stable links, durable background processing, multi-session edit detection, three-way merge records, integrity checkpoints, and an operator-facing control dashboard. These are separate attributable records around the authoritative requirement/SRCR/baseline aggregates; they do not create an alternate approval path or mutate approved history.

Files are streamed to protected local content-addressed storage, SHA-256 hashed, permission-checked through their Project and artifact, and retained across superseding versions. Background operations have idempotency keys, attempts, progress, final outcomes, and downloadable controlled output. Edit sessions capture a base snapshot and numeric concurrency version; collisions persist base/local/remote content and require an explicit resolution.

## Open Digital Thread boundary

AeroLink 2.0 introduces a separate machine-access boundary under `/api/v1`. Machine identities belong to exactly one Project, receive explicit scopes, and authenticate with one-time API keys whose secrets are never persisted. The first public resources expose cursor-paginated, ETag-bearing requirement reads and idempotent external-event ingestion without exposing internal tables or browser-session behavior.

Integration events and webhook deliveries are durable, separate records. Event creation and delivery creation share the application transaction; a hosted dispatcher signs JSON envelopes with HMAC-SHA256, applies exponential retry, and retains delivered, retry-scheduled, and dead-letter outcomes for operator replay. Webhook signing secrets are protected through ASP.NET Core Data Protection and outbound targets fail closed against insecure or private destinations unless a development-only override is configured.

The Integration Command Center is the human control plane over these records. It shows scoped identities, endpoints, event activity, delivery health, replay actions, existing interchange history, and the ReqIF evolution path without creating an alternate approval path for requirements.

The connected foundation now includes the governed ReqIF profile, lifecycle transaction events, filtering and
conditional public reads/writes where documented, and monitored human control planes. Further connector depth
must start from a selected customer/vendor contract. Broad authorization or module rewrites are not standing
architecture work without a reproduced security/product defect; the current Program/resource enforcement and
server-authoritative actor boundary remain mandatory.
