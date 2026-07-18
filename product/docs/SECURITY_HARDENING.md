# Security and Production Hardening

This note records the production-safety decisions introduced after the initial AeroLink product review.

## Enforced in this change

- Production configuration defaults to PostgreSQL rather than silently creating a local SQLite database.
- The production connection string is intentionally empty and must be supplied by the deployment environment.
- Demo data and demo identities remain disabled.
- Secure cookies remain enabled.
- `AllowedHosts` is no longer unrestricted by default.
- An API regression test protects these defaults.
- Browser-originated cookie-authenticated mutations require a signed token bound to the active server session. The React client acquires and applies the token automatically, and API tests prove unprotected browser mutations fail closed.
- CORS origins are configuration-driven and accept no cross-origin browser caller outside Development unless `Cors__AllowedOrigins` is explicitly supplied.
- `/health/live` remains lightweight while `/health/ready` proves database connectivity.
- Service API credentials are project-scoped, displayed once, SHA-256 hashed at rest, revocable, rate limited, and separated from browser sessions.
- Webhook signing secrets are encrypted through ASP.NET Core Data Protection. Outbound delivery blocks insecure and private targets by default.

## Deployment requirements

A production deployment must provide:

- `Database__Provider=PostgreSql`
- `ConnectionStrings__AeroLink=<deployment secret>`
- explicit `AllowedHosts` values appropriate to the deployment
- a temporary `Identity__BootstrapSecret` for the one-time administrator bootstrap, removed immediately afterward
- explicit `Cors__AllowedOrigins__<n>` entries for each trusted browser origin
- a shared, durable ASP.NET Core Data Protection key ring when more than one API instance is deployed

Do not place production credentials or bootstrap secrets in checked-in configuration.

## Follow-up hardening work

The following items should be delivered as separate, reviewable changes because they affect request semantics or broad authorization behavior:

1. Replace the remaining route-string authorization middleware with explicit resource/program authorization policies or endpoint filters.
2. Extend readiness beyond database connectivity to controlled file storage and required external dependencies.
3. Use a versioned SQLite migration path if SQLite remains a supported persistent deployment option; otherwise retain it strictly as disposable test/demo storage.
4. Add automated accessibility checks and a keyboard-only critical journey.
5. Require the complete GitHub quality workflow as a protected-branch merge gate.

These items are intentionally not represented as completed by this document.
