# Enterprise Identity Persistence and Administration Increment

## Objective

Turn the Workstream 4 identity mapping foundation into a durable, auditable administration capability without yet claiming federated sign-in or SCIM completion.

## Required production changes

### Persistence

Add `ExternalIdentityProvider` and `ExternalGroupRoleMapping` to `AeroLinkDbContext` and configure:

- table names `external_identity_providers` and `external_group_role_mappings`
- required, bounded strings for keys, issuer, claims and actor identifiers
- string conversions for protocol and role enums
- unique provider key
- unique provider issuer
- unique mapping tuple `(ProviderId, ExternalGroup, ProgramId, Role)`
- foreign keys from mappings to providers and programs with restrictive deletion
- indexes supporting enabled-provider lookup and Program-scoped authorization resolution

### Migration

Create an additive EF Core migration and update the model snapshot. The migration must:

- create both tables without modifying existing identity data
- create all unique constraints and foreign keys
- support PostgreSQL and the repository's test provider
- include migration smoke coverage from the current `main` schema

### Administration service

Add a server-authoritative service that supports:

- list/get/create/update identity providers
- enable and disable providers
- list/create/enable/disable group-role mappings
- duplicate detection with deterministic conflict results
- fail-closed mapping resolution
- Program isolation
- security audit events for every accepted or rejected administrative mutation

The service must not expose secrets, protocol tokens, assertion contents or raw directory payloads.

### API

Add administrator-only endpoints under `/api/admin/identity` with validation and stable status semantics:

- `GET /providers`
- `POST /providers`
- `PUT /providers/{id}`
- `POST /providers/{id}/enable`
- `POST /providers/{id}/disable`
- `GET /providers/{id}/mappings`
- `POST /providers/{id}/mappings`
- `POST /mappings/{id}/enable`
- `POST /mappings/{id}/disable`

All endpoints must use the existing authenticated actor and IP audit conventions. Do not trust client-supplied actor identifiers.

## Acceptance evidence

Tests must prove:

1. providers and mappings survive database recreation and reload;
2. provider keys and issuers cannot be duplicated;
3. mapping tuples cannot be duplicated;
4. disabled providers and disabled mappings never grant authority;
5. mappings cannot grant authority across providers or Programs;
6. malformed group input fails closed;
7. every successful and rejected administrative mutation produces a security audit event;
8. non-administrators receive forbidden responses;
9. the migration upgrades the current schema without data loss;
10. the full product quality gate remains green.

## Explicit non-claims

This increment does not complete OIDC login, SAML login, logout propagation, SCIM provisioning, MFA enforcement, step-up authentication, service accounts, break-glass access, provider health monitoring or the identity administration UI.

## Merge rule

Merge only after the Product quality gate succeeds and the PR remains additive and migration-safe.
