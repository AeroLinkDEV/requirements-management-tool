# Security, Identity, and Electronic Approval Model

Status: implemented foundation, subject to formal organizational security review before production use.

## Purpose

AeroLink decisions must be attributable to authenticated people, not browser-supplied names. Identity, authority, electronic signatures, and security auditing are lifecycle controls rather than presentation features.

## Identity and session controls

- Accounts are local and on-premises. An account is either Local (created by an administrator or the
  first-install bootstrap, signing in with an AeroLink password) or Windows (created by a first Windows sign-in,
  with no AeroLink password); see *Windows sign-in* below. OIDC/SAML federation remains deferred (DEC-051).
- Usernames are normalized, unique, and retained after an account is disabled so historic authorship remains resolvable.
- Newly created, changed, and reset passwords are stored only as salted PBKDF2-SHA-256 derivations with 600,000 iterations. Existing hashes continue to verify at their encoded iteration count until an ordinary password change or reset; login and password confirmation do not rewrite them. Plaintext passwords are never persisted.
- Eight consecutive failures lock an account. Successful authentication resets the failure counter.
- The browser receives a random opaque session token in an HTTP-only cookie. Only its SHA-256 digest is stored. Sessions expire after 12 hours and can be explicitly revoked. On a declared
  `SiteProduction` or `SiteDevelopment` installation, the session, antiforgery and Windows-link cookie names use
  the persisted instance GUID as their sole suffix: `__Host-aerolink_session-<InstanceId:N>`,
  `__Host-AeroLink.PreferenceConfirmation-<InstanceId:N>` and
  `__Host-AeroLink.WindowsLink-<InstanceId:N>`. A missing or malformed instance GUID refuses site startup;
  non-site launches retain their existing names. Site cookies remain Secure, `Path=/`, host-only and use their
  existing HttpOnly/SameSite settings.
- Account Security identifies the current session, lists retained session history, and permits a user to revoke
  other active sessions without revoking the session performing the action.
- Material API routes require an authenticated session. Actor names supplied by clients are ignored; the server derives the actor from the session.

### Windows sign-in

Where `Identity:Windows:Enabled` is set ([DEC-154](../../DECISIONS_AND_OPEN_QUESTIONS.md#dec-154---windows-sign-in-on-the-company-host)),
`GET /api/auth/windows` signs a person in with the Windows logon their browser holds:

- **Proof.** Negotiate on Kestrel (Kerberos, NTLM fallback) authenticates that one route only: the authentication
  middleware runs only on that path, and Negotiate is never the default scheme. A completed NTLM handshake does not
  persist on its connection. The route then issues the same AeroLink session cookie a password sign-in issues, so nothing
  after sign-in depends on the method. Its challenge is a 401 with `WWW-Authenticate`, which no error-page
  middleware may rewrite, and Negotiate requires an HTTP/1.1 listener. The 401 carries a short page linking to the
  password sign-in (`/?windows=password`), for browsers that cannot finish Negotiate. Only a top-level navigation
  may sign in: a request whose `Sec-Fetch-Dest` is anything but `document` is refused.
- **Who may sign in.** The SID decides: only a domain user SID (`S-1-5-21-a-b-c-RID`) whose domain SID is in
  `Identity:Windows:AllowedDomainSids` and whose RID is at least 1000. Local, built-in and well-known accounts are
  refused, as are computer and managed service accounts (`$` names), and an account the directory cannot find or
  that has no directory email. Refusals return to the
  sign-in page with a reason and are audited.
- **Accounts.** The first sign-in creates a Windows account keyed on the SID and named `DOMAIN\user`; the name
  stays fixed if the directory later renames the person, and the display name and email are refreshed from the
  directory on every sign-in. It has no AeroLink password, so password sign-in refuses it without counting a
  failure, and an administrator password reset refuses it. Nobody is provisioned until first-install setup has
  created `admin`. A Windows sign-in records the sign-in time but never clears a password failure count.
- **Default access.** A new Windows account is Engineer in every Program, and every active Windows account is
  made Engineer in each Program created afterwards, both granted by `system.windows-provisioning`. Nothing else
  re-grants it, so an administrator's narrowing stands.
- **Linking.** `?intent=link` holds the proven Windows identity for five minutes in an encrypted, host-only,
  SameSite=Strict Windows-link cookie (the site name is the derived `__Host-AeroLink.WindowsLink-<InstanceId:N>`;
  non-site launches retain their established name); `POST /api/auth/windows/link` then attaches it to an existing account after that
  account's password and second factor, counting failures toward lockout; an account still holding an
  administrator-issued temporary password cannot be linked. The form names the Windows logon from the ticket. The
  administrator roster shows each local account's linked Windows logon, and an administrator can unlink it, which
  revokes that account's sessions. A SID already linked elsewhere is
  refused unless it belongs to a Windows account an administrator has disabled, which is how a SID used before
  linking is freed. A linked Local account (the administrator) keeps its own profile and signs in with Windows
  without MFA; its password plus MFA remains the break-glass.
- **Audit.** Every sign-in audit event records `AuthenticationMethod` (`password` or `windows`) and, for Windows,
  the `DOMAIN\user` principal. New electronic signatures record `windows-password` when the account has a durable
  Windows link and `password` for an unlinked Local account; signatures recorded before B2 retain an empty method.
- **Startup.** Production refuses to start Windows sign-in with a scheme other than Negotiate (which keeps the
  test scheme out of real deployments), with no allowed domain, when running as SYSTEM, or without an HTTP/1.1-only
  listener (`SiteServer:Http1Only` or `Kestrel:EndpointDefaults:Protocols=Http1`).
- **Residual risk.** The Negotiate handler does not apply TLS channel binding, which is possible but not built
  (#1646). Until it is, a relayed NTLM exchange, or a Kerberos ticket relayed by another application sharing this
  host name's `HOST/` SPN, is accepted; a relay of the owner's logon is global administration, because the linked
  `admin` signs in with Windows without MFA. Windows sign-in must not sit behind a reverse proxy, tunnel or any
  intermediary that shares connections between users. Each Windows sign-in event records Kerberos or NTLM. See
  DEC-154.
- **Browser.** With Windows sign-in on, arriving without a session goes to Windows sign-in, except in a tab that
  signed out, which stays signed out until the person chooses to sign in again, and never twice in a row: an
  automatic attempt that comes back without a session leaves the next arrival on the sign-in page.

Global administration is still the `admin` account alone: Program roles, including a Program's Administrator role,
can be granted to Windows accounts like any other, but no route grants the global administrator to another account (#1639).

### Electronic-signature reauthentication

Every signature action re-reads the actor's account and uses one shared server boundary immediately before the
controlled mutation. An account with a durable `WindowsSid` (including a linked Local account) must re-enter its
persisted `DOMAIN\\user` Windows password. The production validator uses `LogonUser` with
`LOGON32_LOGON_NETWORK` and requires the returned token SID to match the persisted SID; the browser cannot supply
the identity. An unlinked Local account uses the existing PBKDF2 AeroLink password verifier. Ambient Negotiate
session state never signs, and credentials are transient, never stored or logged.

Failed proofs retain the existing generic refusal and lockout transition and do not create a signature. Successful
signatures persist the method (`windows-password` or `password`) in the audit projection. Cancellation is checked
before the signature transaction commits. Password change and MFA-disable remain separate break-glass operations
using the Local password verifier.

The Documentation Center desktop connector never receives a reusable browser session. AeroLink issues a
short-lived one-use launch token scoped to one document revision and mode. After redemption, the connector keeps
its scoped access token only in memory, renews the exclusive lease while Word is open, and cannot use that token
to browse another Project or artifact. Remote origins require HTTPS; loopback HTTP remains available for local
demonstration. Stale-source check-ins fail without overwriting another user's work.

## Program authority

Authority is scoped to a Program through additive role assignments:

| Role family | Current roles and intended authority |
|---|---|
| General control | Engineer, Reviewer, Approver, Configuration Manager |
| Verification | Test Engineer, Test Lead |
| Program leadership | Program Manager, Administrator |
| Engineering jobs | System Engineer, Software Engineer, System Engineering Lead, Software Engineering Lead, Project Engineering Lead, Engineering Manager |
| Independent stakeholders | Software Quality Analyst, Airworthiness |

A precise engineering job satisfies the general Engineer authority it implies; naming the real job must not
remove ordinary authoring capability. Independent stakeholder roles do not implicitly gain engineering write
authority. Global system administration is separate from a Program's Administrator role.

Role delegations are time-bounded, Program-scoped, attributable, and revocable. Current, future, expired, and
revoked records retain Program, delegator, delegate, role, interval, reason, granting/revoking actor, and state.
Only an active interval grants authority. Delegation does not rewrite the original assignee or historic identity.

## Review-stage authority

A configured review procedure names the authority required by each stage rather than assuming every stage is a
generic Approver. The selected person must hold the exact required authority when the review starts; Administrator
substitution remains explicit. The authority actually used is frozen on the `ApprovalStep` so the historical
record remains explainable after memberships change.

Where no configured procedure exists, the backwards-compatible independent-Approver fallback applies. The
selected reviewer must hold Approver authority, including the established administrator/delegation rules. The
server remains authoritative even when a browser picker is filtered.

## Electronic approval

The required electronic-signature contract is:

- the user has an active authenticated session;
- the user is the person assigned to an active review stage;
- the applicable configured-stage or no-workflow fallback authority is satisfied;
- the user re-enters the verifier-specific Windows or AeroLink password immediately before signing;
- the user provides an explicit signature meaning distinct from engineering rationale; and
- the server applies the domain transition and immutable signature record in one successful unit of work.

Each signature captures the user identifier, username and display-name snapshot, Program, artifact type and stable identity, artifact revision, action, meaning, exact content or snapshot hash, source address, and server timestamp. A signature supplements—not replaces—the artifact's ordered review history.

### Current implementation qualification

Ordinary System/Software Change Request approvals use password re-confirmation and configured-stage authority.
Managed-document and release approval paths retain their own qualified contracts.

All nine signature callers use the shared reauthentication boundary, including Change Request, Test Change Review,
release campaign, release-without-readiness, managed-document, integrity-import, problem-report-import, and project
setup source-acceptance actions. The two password-confirmation operations for changing a Local password and
disabling Local MFA remain separate. Every new signature records its method in the API read projection without
rewriting historical signatures.

## Separation of duties

The foundation prevents identity impersonation and requires explicit assignment. Program policy can impose
artifact-specific combinations such as author-not-approver, independent assurance approval, or mandatory
Configuration Manager release authority. Configured workflows are versioned controlled procedures because
appropriate separation varies by organization, artifact type, and assurance level.

TCR self-approval is refused and active-stage identity is enforced. Ordinary Test Engineers work unassigned or
self-held packages; Test Leads and Administrators use attributable supervisory authority. These rules do not
remove the electronic-signature gap identified above.

## My Work and management visibility

My Work is a server-derived queue, not a manually maintained task list. It identifies active change-request,
downstream-assessment, verification, procedure, Test Change Request, release, and owned-draft work for the
authenticated person. Administrators can inspect accounts, state, last access, and current Program roles;
create local accounts; grant or revoke one role; and disable accounts without deleting historic attribution.
Duplicate grants fail as conflicts. Security audit events retain successful and denied logins, logout, account
administration, role grant/revocation, session revocation, and delegation lifecycle actions.

## Untrusted input and response hardening

Everything a browser or a machine account sends is a claim, and the boundary treats it as one.

- **Session reachability is decided in one place.** A middleware ahead of endpoint routing decides which paths are reachable without a session, from an explicit path list. `.AllowAnonymous()` on an endpoint has no effect there, because that middleware runs before the endpoint is selected and has no metadata to read. Anything intended to be reachable from a mail client — the unsubscribe link — must be named in that list and must prove its own authority instead, which it does through an HMAC over the recipient. An unsubscribe request answers identically whether the account exists or not, so the link cannot be used to enumerate people.
- **Authentication is not authorization.** Every project/artifact read and write resolves the owning Program and proves the caller is entitled to know it exists. PR #404 closed #395 with two-Program negative coverage across verification reads and evidence downloads; later routes must preserve the same fail-closed pattern.
- **A declared content type is not a fact.** An uploaded inline image is checked against its own signature bytes before it is stored, because it is later streamed back from this deployment's own origin and referenced from an approved requirement.
- **Imported XML cannot reach outward or expand without bound.** ReqIF packages and spreadsheet parts are read with document type definitions prohibited, no resolver, and a ceiling on characters actually read. A zip file's declared entry sizes are checked too, but that check alone governs a number the sender chose.
- **Every API response carries protection headers** — `nosniff`, `DENY` framing, no referrer, and a content security policy of `default-src 'none'; sandbox`. An API returns data, never a document, so nothing it serves should be able to load or run anything. The API-served production client has its own explicit same-origin document CSP; deployment reverse proxies may add stricter organization-owned headers but must not weaken it.
- **Secret comparison is constant-time** for passwords, session tokens, service API keys, webhook signatures, one-time codes, the bootstrap secret, and stored file digests.
- **The sign-in limiter is flood control for one network address, not the control against password guessing.** That control is the account itself, which locks after eight failures wherever they come from. On-premises, an engineering group can reach the server through one corporate proxy and present one address, so the limit is budgeted for a site rather than a person.
- **Rich content is stored as structured blocks, never as markup.** There is no stored HTML anywhere in the product, so there is nothing to sanitize and no injection surface to keep closed.

## Production hardening still required

Before operational deployment, the organization must define password policy, TLS and certificate management, reverse-proxy topology and its document response headers, backup and recovery, session revocation procedures, the procedure for releasing a locked account, privileged-administration oversight, clock synchronization, log retention/export, vulnerability management, and enterprise identity integration. The local demonstration password must be replaced and demonstration credential prefill removed.

The #395 and #398 security/electronic-signature corrections are complete and qualified. Deployment still must
re-run the applicable security qualification against its selected topology, identity policy, and reverse proxy.

Account lockout is deliberately permanent until an administrator releases it, which means anybody who can reach the sign-in page can lock an account they know the name of. That is the correct trade for a controlled tool — an attacker must not be handed an automatic retry window — but it makes the release procedure an operational requirement rather than an optional one.
