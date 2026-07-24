# Enterprise Identity Persistence and Administration Increment

## Objective

Turn the Workstream 4 identity mapping foundation into a durable, auditable administration capability without claiming federated sign-in or SCIM completion.

## Persistence design

This increment uses an additive EF Core migration for schema ownership and a small provider-neutral relational service over the existing `AeroLinkDbContext` connection for runtime access. This is intentional: the capability needs two simple tables and a handful of exact queries, and does not justify another repository layer or a broader identity framework.

The schema provides:

- `external_identity_providers` and `external_group_role_mappings` tables;
- bounded required strings for provider keys, issuer anchors, claims, groups and actor identifiers;
- string storage for protocol and role values;
- unique provider key and issuer constraints;
- a unique `(provider_id, external_group, program_id, role)` authority tuple;
- restrictive foreign keys from mappings to providers and Programs;
- indexes for provider and Program-scoped lookup.

The migration is additive, preserves existing identity data, and must apply successfully on PostgreSQL and the repository's SQLite test path.

## Administration service

The server-authoritative service supports only the operations needed by this increment:

- list and create providers;
- enable or disable providers;
- list and create Program-scoped group-role mappings;
- enable or disable mappings;
- deterministic duplicate and missing-reference results;
- fail-closed role resolution using provider identity, issuer, groups and Program scope;
- security audit events for successful and rejected mutations.

It does not store or expose protocol secrets, tokens, assertions or raw directory payloads.

## API

Administrator-only endpoints are exposed under `/api/admin/external-identity`:

- `GET /providers`
- `POST /providers`
- `POST /providers/{id}/enabled`
- `GET /mappings?providerId={providerId}&programId={programId}`
- `POST /mappings`
- `POST /mappings/{id}/enabled`
- `POST /resolve`

The authenticated server-side user and connection IP are used for audit attribution. Client-supplied actor identifiers are not accepted.

## Acceptance evidence

Tests must prove:

1. providers and mappings survive database reload;
2. provider keys and issuers cannot be duplicated;
3. mapping authority tuples cannot be duplicated;
4. disabled providers and disabled mappings never grant authority;
5. mappings cannot grant authority across providers or Programs;
6. malformed group input fails closed;
7. successful and rejected administrative mutations produce security audit evidence;
8. non-administrators receive forbidden responses;
9. the migration applies on PostgreSQL without disrupting secure bootstrap;
10. the full Product quality gate succeeds.

## Explicit non-claims

This increment does not complete OIDC login, SAML login, logout propagation, SCIM provisioning, MFA enforcement, step-up authentication, service accounts, break-glass access, provider health monitoring or an identity administration UI.

## Merge rule

Merge only after the Product quality gate succeeds and the PR remains additive, auditable and migration-safe.