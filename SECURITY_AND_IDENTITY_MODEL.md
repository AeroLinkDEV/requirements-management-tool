# Security, Identity, and Electronic Approval Model

Status: implemented foundation, subject to formal organizational security review before production use.

## Purpose

AeroLink decisions must be attributable to authenticated people, not browser-supplied names. Identity, authority, electronic signatures, and security auditing are lifecycle controls rather than presentation features.

## Identity and session controls

- Accounts are local and on-premises in the current implementation. A future enterprise connector may federate Active Directory or another approved identity provider without changing artifact history.
- Usernames are normalized, unique, and retained after an account is disabled so historic authorship remains resolvable.
- Passwords are stored only as salted PBKDF2-SHA-256 derivations with 310,000 iterations. Plaintext passwords are never persisted.
- Eight consecutive failures lock an account. Successful authentication resets the failure counter.
- The browser receives a random opaque session token in an HTTP-only cookie. Only its SHA-256 digest is stored. Sessions expire after 12 hours and can be explicitly revoked.
- Material API routes require an authenticated session. Actor names supplied by clients are ignored; the server derives the actor from the session.

## Program authority

Authority is scoped to a Program through additive role assignments:

| Role | Intended authority |
|---|---|
| Engineer | Author and revise controlled drafts. |
| Reviewer | Review assigned lifecycle artifacts and request changes. |
| Approver | Apply an electronic approval when assigned in the active ordered stage. |
| Configuration Manager | Assemble, freeze, materialize, and release controlled configurations. |
| Test Engineer | Author procedures, record determinations, and associate evidence. |
| Program Manager | Govern release readiness and program decisions. |
| Administrator | Manage local identities and Program authority. Administration does not silently replace an assigned approval identity. |

Role delegations are time-bounded, Program-scoped, attributable, and revocable. Delegation grants authority; it does not rewrite the original assignee or historic identity.

## Electronic approval

An approval is valid only when the user has an active session, is assigned to the active ordered stage, holds Program Approver authority, re-enters the correct password, provides an explicit signature meaning, and the server applies the domain transition with an immutable signature record.

Each signature captures the user identifier, username and display-name snapshot, Program, artifact type and stable identity, artifact revision, action, meaning, exact content or snapshot hash, source address, and server timestamp. A signature supplements—not replaces—the artifact's ordered review history.

## Separation of duties

The foundation prevents identity impersonation and requires explicit assignment. Program policy can later impose stronger combinations such as author-not-approver, independent assurance approval, or mandatory Configuration Manager release authority. These policies should be configuration-controlled because appropriate separation varies by organization, artifact type, and assurance level.

## My Work and management visibility

My Work is a server-derived queue, not a manually maintained task list. It identifies active SCR approval stages, active release approvals, and owned drafts for the authenticated person. Administrators can inspect accounts, state, last access, and Program roles; create local accounts; grant authority; and disable accounts without deleting historic attribution. Security audit events retain successful and denied logins, logout, account administration, role grants, and delegation creation.

## Production hardening still required

Before operational deployment, the organization must define password policy, TLS and certificate management, reverse-proxy topology, backup and recovery, session revocation procedures, privileged-administration oversight, clock synchronization, log retention/export, vulnerability management, and enterprise identity integration. The local demonstration password must be replaced and demonstration credential prefill removed.
