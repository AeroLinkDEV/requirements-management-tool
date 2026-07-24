# OIDC Runtime Federation Implementation Checklist

- [ ] Add external identity binding domain model and additive migration.
- [ ] Add administrator-only binding persistence and API operations.
- [ ] Add provider discovery endpoint for enabled OIDC providers.
- [ ] Add provider-neutral OIDC protocol adapter boundary.
- [ ] Add PKCE, signed state, nonce, replay protection and safe return URL handling.
- [ ] Validate issuer, audience, signature, expiry, nonce and stable subject.
- [ ] Resolve one active local account through an enabled binding.
- [ ] Resolve current Program roles from trusted external groups.
- [ ] Create the existing AeroLink revocable session cookie.
- [ ] Preserve local memberships and keep mapped roles transient.
- [ ] Add successful and rejected security audit evidence.
- [ ] Add migration, persistence, API, authorization and end-to-end tests.
- [ ] Run the Product quality gate and address all failures.
