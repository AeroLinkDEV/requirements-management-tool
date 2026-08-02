# Enterprise Identity Persistence and Administration Increment

> **Historical increment record.** This file documents the persistence foundation delivered at that point.
> Later work added explicit Program role, session, and delegation lifecycles. See
> [Security and Identity Model](../SECURITY_AND_IDENTITY_MODEL.md) and the
> [current handoff](../CURRENT_PRODUCT_HANDOFF_2026-08-02.md); federated sign-in and SCIM remain deployment/provider work.

## Objective

Turn the Workstream 4 identity mapping foundation into a durable, auditable administration capability without claiming federated sign-in or SCIM completion.

## Persistence design

Both records are mapped in `AeroLinkDbContext` and their schema is owned by a generated additive EF Core
migration. The model must own them rather than a hand-written migration alone, because the product creates
its schema two different ways: `Database.Migrate()` on PostgreSQL, which applies only migrations Entity
Framework can discover by attribute, and `EnsureCreated()` elsewhere, which builds purely from the model.
A table that is absent from the model is therefore absent from every local, test and development database
regardless of what any migration file says.

The schema provides:

- `external_identity_providers` and `external_group_role_mappings` tables;
- bounded required strings for provider keys, issuer anchors, claims, groups and actor identifiers, with
  the same bounds enforced in the domain constructors so over-long input is a validation failure rather
  than a database error;
- string storage for protocol and role values;
- unique provider key and issuer constraints;
- a unique `(ProviderId, ExternalGroup, ProgramId, Role)` authority tuple;
- restrictive foreign keys from mappings to providers and Programs;
- indexes for provider and Program-scoped lookup.

Issuer anchors are canonicalized once, on the way in: scheme and host are lower-cased, a default port is
dropped, and a trailing separator is removed, while the path stays case-sensitive per RFC 3986. Query and
fragment are rejected. This makes the unique issuer index a real control — the same anchor written in an
equivalent form cannot become a second trusted provider — and gives configuration and authentication a
single comparison to share.

The migration is additive, preserves existing identity data, and applies successfully on PostgreSQL and the
repository's SQLite path.

## Administration service

The server-authoritative service supports only the operations needed by this increment:

- list and create providers;
- enable or disable providers;
- list and create Program-scoped group-role mappings;
- enable or disable mappings;
- deterministic duplicate and missing-reference results;
- fail-closed role resolution using provider identity, issuer, groups and Program scope;
- security audit events for successful and rejected mutations, and for every resolution outcome.

Each mutation and its audit event are saved in one unit of work, so an authority change cannot be committed
without its evidence. Authority decisions are delegated to the domain records rather than reimplemented in
the service, so there is exactly one issuer comparison and one group comparison in the product.

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
2. provider keys and issuers cannot be duplicated, including when the same anchor is written in an
   equivalent form;
3. mapping authority tuples cannot be duplicated;
4. disabled providers and disabled mappings never grant authority;
5. mappings cannot grant authority across providers or Programs;
6. malformed group input, look-alike issuers and over-long input fail closed, and over-long input is
   reported as validation rather than conflict;
7. successful and rejected administrative mutations, and both resolution outcomes, produce security audit
   evidence that discloses no secret;
8. non-administrators receive forbidden responses and change no state;
9. every migration in the assembly is discoverable by Entity Framework, and the model carries no change
   that is missing a migration;
10. an administrator completes a provider, mapping and resolution round trip through the HTTP API against
    the schema the product creates for itself — not a fixture-local one;
11. the migration applies on PostgreSQL, and the migrated tables serve that same round trip in the
    PostgreSQL smoke job without disrupting secure bootstrap;
12. the full Product quality gate succeeds.

Evidence 9 through 11 exist because the first attempt at this increment passed a green gate while the
capability was unreachable: the tables were created by no code path the running product uses. A gate that
never calls an endpoint proves nothing about that endpoint.

## Explicit non-claims

This increment does not complete OIDC login, SAML login, logout propagation, SCIM provisioning, MFA enforcement, step-up authentication, service accounts, break-glass access, provider health monitoring or an identity administration UI.

None of those are in progress. They were deferred by explicit decision on 2026-07-24; the reason, the resume
trigger and the order to resume in are recorded in the Workstream 4 decision record in
`AEROLINK_3_ENTERPRISE_LIFECYCLE_COMPLETION.md`. This document describes a delivered increment, not a
staging post in active work.

`POST /resolve` is an administrator-only diagnostic that answers what a set of directory groups *would*
grant. Nothing authenticates through it yet; no login path consumes it.

The provider record deliberately carries no client identifier, discovery or JWKS endpoint, redirect URI or
allowed audience, because nothing yet performs a protocol exchange. Runtime OIDC federation will need those
fields and a protected home for a client secret, and should add them as its own additive migration.

An issuer anchor may still be plain `http`, which is retained from the original foundation for local test
identity providers. Runtime federation should refuse a non-HTTPS anchor outside development.

## Merge rule

Merge only after the Product quality gate succeeds and the PR remains additive, auditable and migration-safe.
