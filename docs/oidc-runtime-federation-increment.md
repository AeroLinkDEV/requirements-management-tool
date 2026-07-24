# OIDC Runtime Federation Increment

## Objective

Connect the persisted external identity provider and Program-scoped group-role mapping foundation to an executable OpenID Connect sign-in path without weakening AeroLink's existing local-session, account-state, MFA, audit, or Program authorization controls.

## Scope

This increment shall:

- expose enabled OIDC providers for sign-in discovery;
- use authorization code with PKCE and signed, expiring, single-use state and nonce;
- validate exact issuer, configured audience, signature, nonce and expiry;
- require a stable non-empty subject claim;
- bind an external `(provider, subject)` identity to exactly one active local account;
- resolve enabled external groups through the existing Program-scoped mapping service;
- create the standard AeroLink server-side session only after all checks pass;
- combine local Program memberships with transient mapped roles without persisting those transient grants;
- audit successful and denied sign-in and binding operations without storing tokens, assertions, secrets or raw claims;
- fail closed when provider configuration, binding, claims or authority are missing or ambiguous.

## Persistence

Add an additive `external_identity_bindings` table with provider, normalized subject, local user, enabled state and creation audit fields. Enforce unique `(provider_id, subject)` and `(provider_id, user_id)` constraints and restrictive foreign keys.

No access token, refresh token, ID token, authorization code, assertion, client secret, private key or raw claim payload may be persisted.

## API

Anonymous authentication endpoints:

- `GET /api/auth/external/providers`
- `GET /api/auth/external/{providerKey}/start?returnUrl=...`
- `GET /api/auth/external/{providerKey}/callback`

Administrator-only binding endpoints under `/api/admin/external-identity`:

- `GET /bindings?providerId=...&userId=...`
- `POST /bindings`
- `POST /bindings/{id}/enabled`

## Security requirements

- PKCE authorization-code flow only;
- HTTPS metadata and callbacks outside Development;
- local-path return URLs only;
- no email-only linking, automatic account creation or authority from untrusted claims;
- disabled providers, bindings and mappings grant no access;
- generic client errors with detailed security audit evidence;
- authentication rate limiting on start and callback.

## Acceptance evidence

Tests must prove provider discovery, PKCE/state/nonce generation, replay and tamper rejection, token validation failures, subject and binding failures, uniqueness constraints, disabled-object behavior, exact provider/Program role scope, preservation of local memberships, non-persistence of transient roles, standard revocable session creation, audit evidence without sensitive-data leakage, administrator-only binding management, PostgreSQL and SQLite migration safety, and a passing Product quality gate.

## Explicit non-claims

This increment does not complete SAML, SCIM, just-in-time account creation, logout propagation, provider health, break-glass policy, privileged step-up authentication or the full administration UI.

## Merge rule

Merge only after the Product quality gate succeeds and the implementation remains additive, provider-bounded, auditable, migration-safe and fail closed.