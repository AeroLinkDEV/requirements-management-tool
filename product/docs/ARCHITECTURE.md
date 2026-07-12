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

Repository interfaces are defined in the domain project and implemented in infrastructure. Provider choice is configuration-driven. `EnsureCreated` is acceptable during this early local phase; versioned EF migrations replace it before shared environments or production data.

Fresh installations contain no assumed program. The onboarding transaction creates the Program, its first Project/software product, and its initial release together. FMS records are optional demo data controlled by configuration and are disabled by default.

## Security boundary

Actor identifiers currently enter through development request bodies to exercise authorization rules. This is deliberately not production authentication. Before multi-user use, identity must come from an authenticated server-side principal, roles and program membership must be enforced, and audit events must use that trusted identity.

## Next implementation increment

1. Add API integration tests around persistence and workflows.
2. Add authenticated identity and role/program authorization design.
3. Build SCR authoring and ordered review screens against the API.
4. Add EF migrations and a local PostgreSQL development option.
5. Implement candidate-baseline assembly from approved SCR revisions.
