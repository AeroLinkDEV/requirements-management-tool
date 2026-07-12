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

The first aggregate is `SystemChangeRequest`. Stable artifact identity (`SCR-00000001`) is distinct from revision display (`SCR-00000001.04`). Requirements referenced by an SCR follow the same identity model.

## Persistence

Repository interfaces are defined in the domain project and implemented in infrastructure. Provider choice is configuration-driven. PostgreSQL uses versioned EF migrations at application startup; SQLite remains isolated to tests and disposable local scenarios.

Fresh installations contain no assumed program. The onboarding transaction creates the Program, its first Project/software product, and its initial release together. FMS records are optional demo data controlled by configuration and are disabled by default.

Enterprise authoring extends the existing requirement aggregate instead of replacing it. Stable artifacts and immutable requirement revisions remain authoritative; revision profiles add schema-bound rich content and classifications, specification nodes add reusable document placement, and comments/views/jobs preserve collaboration and high-volume operations as separate attributable records. Existing Projects are synchronized idempotently so the new workspace can be introduced without rewriting approved history.

CSV/XLSX interchange is a two-step preview/commit workflow. Files are size- and expansion-limited, hashed, parsed into persisted validation results, and cannot create approved requirements directly. A successful commit creates a Draft SCR/SWCR containing the proposed requirement changes, preserving the established review and baseline authority boundary.

## Security boundary

Identity now comes from a revocable authenticated server session. Passwords use salted PBKDF2 derivation, opaque session tokens are stored only as digests, material API actions derive the actor from the authenticated principal, Program memberships and roles constrain access, and SCR/release approvals require password-confirmed immutable electronic signatures. Production deployment still requires TLS, enterprise identity federation/provisioning, configurable policy enforcement, privileged-access governance, audit export, and independent security review as defined in [SECURITY_AND_IDENTITY_MODEL.md](../../SECURITY_AND_IDENTITY_MODEL.md).

## Next implementation increment

Wave 1 now has an integrated production-shaped foundation: schemas, specification hierarchy, revision profiles, discovery, saved views, discussions, redlines, bulk jobs, and governed CSV/XLSX onboarding all operate against persisted data. The next increment completes the remaining Wave 1 acceptance depth:

1. Add sanitized rich-text editing, attachments, tables/images, keyboard authoring, and optimistic concurrency for draft content.
2. Expand discussions into threads, formal dispositions, watchers, assignments, due dates, and notification delivery.
3. Add a structured query builder, configurable columns/grouping, shareable URLs, and a dedicated permission-aware search index.
4. Add reusable import mappings, downloadable error workbooks, job history/retry, export, and measured 10,000-requirement performance before ReqIF and product-line configuration work.
