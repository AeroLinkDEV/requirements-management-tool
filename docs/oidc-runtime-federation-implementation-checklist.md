# OIDC Runtime Federation Implementation Checklist

## Implemented in the current draft

- [x] Add explicit external subject-to-local-account binding schema.
- [x] Enforce unique provider/subject and provider/user bindings.
- [x] Add administrator-oriented binding service operations.
- [x] Add a validated-principal runtime boundary that never accepts raw tokens.
- [x] Fail closed for missing/disabled providers, bindings, accounts, groups and Program mappings.
- [x] Issue standard revocable AeroLink sessions after successful authority resolution.
- [x] Record accepted and rejected binding and federated-login audit events.

## Still required before this PR can leave draft

- [ ] Add dynamic OIDC discovery and signing-key retrieval.
- [ ] Implement authorization-code exchange with PKCE.
- [ ] Implement protected, expiring, single-use state and nonce storage.
- [ ] Validate token signature, algorithm, issuer, audience, lifetime and nonce.
- [ ] Map the validated protocol principal into `ValidatedExternalPrincipal`.
- [ ] Add public start/callback/logout endpoints without trusting browser-supplied claims.
- [ ] Add binding administration API coverage.
- [ ] Add SQLite and PostgreSQL migration tests.
- [ ] Add runtime service and end-to-end authentication tests.
- [ ] Run and pass the full Product quality gate.
