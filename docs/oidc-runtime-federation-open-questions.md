# OIDC Runtime Federation Decisions

The implementation shall use the existing AeroLink session model after external identity validation rather than introducing a parallel bearer-token authorization path.

External identity subjects shall be explicitly bound to existing local accounts. Email claims shall not create or link accounts.

External group-derived Program roles shall be calculated at sign-in and represented in the authenticated session projection without creating durable local Program memberships.

Provider protocol handling shall sit behind a testable adapter so acceptance tests can validate the complete callback behavior without contacting a live identity provider.

Provider secrets and protocol credentials shall be supplied through deployment configuration or a secret store and shall not be returned by administration APIs or persisted in the new identity mapping tables.
