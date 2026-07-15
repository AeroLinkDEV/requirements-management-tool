# Security and Production Hardening

This note records the production-safety decisions introduced after the initial AeroLink product review.

## Enforced in this change

- Production configuration defaults to PostgreSQL rather than silently creating a local SQLite database.
- The production connection string is intentionally empty and must be supplied by the deployment environment.
- Demo data and demo identities remain disabled.
- Secure cookies remain enabled.
- `AllowedHosts` is no longer unrestricted by default.
- An API regression test protects these defaults.

## Deployment requirements

A production deployment must provide:

- `Database__Provider=PostgreSql`
- `ConnectionStrings__AeroLink=<deployment secret>`
- explicit `AllowedHosts` values appropriate to the deployment
- a temporary `Identity__BootstrapSecret` for the one-time administrator bootstrap, removed immediately afterward

Do not place production credentials or bootstrap secrets in checked-in configuration.

## Follow-up hardening work

The following items should be delivered as separate, reviewable changes because they affect request semantics or broad authorization behavior:

1. Add explicit CSRF protection for every cookie-authenticated mutating endpoint, with browser integration tests.
2. Replace the route-string authorization middleware with explicit resource/program authorization policies or endpoint filters.
3. Add database- and storage-aware readiness checks while retaining a lightweight liveness endpoint.
4. Make CORS origins configuration-driven and fail closed outside Development.
5. Use a versioned SQLite migration path if SQLite remains a supported persistent deployment option; otherwise document it as disposable test/demo storage only.
6. Add automated accessibility checks and a keyboard-only critical journey.
7. Require the complete GitHub quality workflow as a protected-branch merge gate.

These items are intentionally not represented as completed by this document.
