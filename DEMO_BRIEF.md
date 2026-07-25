# Demonstration Brief

**Status: inputs captured 2026-07-25. The script itself is to be drafted, targeted for the following
Monday.** This file exists so that work starts from decisions already made rather than re-deriving them.

## The occasion

A demonstration of AeroLink to engineering management, ahead of a decision. Duration 30–60 minutes; the
script must work at both lengths by dropping sections marked optional, not by rushing the ending.

## What is actually being asked for

**Approval to continue development.** This is not a proposal to replace DOORS, and the script must not
drift into arguing for one. The room is deciding whether this line of work continues and is worth its
budget, which means the demonstration needs to establish credibility and momentum rather than answer a
full migration case. Migration questions should be answered honestly and briefly, then set down.

## The audience

- The presenter's manager, and that manager's manager.
- The software quality group, who routinely and properly challenge any proposed tool change.

The quality group is the audience to write for. They will ask what the tool claims, what it does not
claim, and how those claims are evidenced — which is the ground AeroLink is strongest on, provided the
answers are exact. Vague claims will lose the room; precise limitations will win it.

## Framing decision: authorship is a reveal

AeroLink is AI-built and the presenter will not hide it. The decision is to hold that back initially,
let the product be judged on its own, and then surface it deliberately — the intent being that the
work is assessed before the method is known.

Two things to get right when drafting:

- The reveal needs a purpose beyond surprise. It is strongest as evidence about delivery pace and cost,
  which speaks directly to the budget decision being made.
- It must not read as concealment. The line between "I let you judge the work first" and "you weren't
  told" is tone, and the script should make the reveal feel like the point rather than a confession.

## Candidate beats, in order of expected impact

1. **A requirement changes, and verification finds out.** Approve a change request; verification work
   appears with a named owner. This states the current pain point as a solved problem.
2. **The release refuses.** Show a readiness gate genuinely blocking, satisfy it, watch it clear.
   Management cares about "can this ship" more than about data models.
3. **A controlled document that is generated, not maintained by hand.** Direct hit on the stated
   complaint about the incumbent tool's document generation.
4. **Reconstruct history.** What exactly was approved for FMS 1.5, who signed it, reproduced now.

Four beats landed properly beats eleven screens toured. Resist showing everything.

## Predictable challenges, and the exact answers

These must be rehearsed verbatim, because precision is the whole argument with this audience.

- **Is it certified or qualified?** No. AeroLink is *informed by* ARP4754 and DO-178 concepts and
  terminology, claims neither compliance nor tool qualification, and the repository says so in
  `SCOPE_AND_BOUNDARIES.md`.
- **Does it scale?** Proven at the database layer: 150 simultaneous database clients and 50,000
  requirements on one workstation with zero failures. That is **not** 150 rendered browser sessions on
  production topology, and must never be described as such.
- **Is it production ready?** No. TLS, certificate and secret management, reverse-proxy topology,
  scheduled off-device backups, monitoring, retention enforcement and independent security review are
  organization-specific work that has not been done.
- **How would we get off DOORS?** ReqIF 1.2 round-trip exists. Migration itself is real, unestimated work.
- **Who maintains it?** An honest answer is owed here and has not yet been decided.
- **Would people sign in with their corporate account, or yet another password?** Corporate account, by
  design, though not yet by working code. What exists and is tested today: external identity provider
  definitions with canonicalized issuer trust anchors, Program-scoped mappings from an external directory
  group to an AeroLink role, fail-closed matching, and an administration API whose every authority change
  is saved together with its audit evidence. The PostgreSQL smoke gate exercises those tables on every run.
  What does not exist: the sign-in path itself — OpenID Connect discovery, the authorization-code exchange
  with PKCE, token validation, and the start and callback endpoints. That work is designed and drafted in
  pull request #53, and deliberately left as a draft.

  This is a good answer to give rather than one to dodge, because it is the shape of the whole product
  argument: the controlled, auditable half is built and evidenced, the half that needs a real deployment
  decision is designed and honestly unbuilt. Say it in that order.

  Two design points worth offering if the quality group presses, since they are what a security reviewer
  actually checks. An external identity must be **explicitly bound** to an existing account — no account is
  created from an email claim, which is what turns federation into an account-takeover route. And no tokens,
  secrets or raw claims are persisted, with directory-derived roles calculated at sign-in rather than
  written as durable memberships, so removing someone from a group removes their access.

  Do not offer to demonstrate this. It cannot be shown without a live identity provider.

## Deliberately out of scope for this demonstration

Identity federation (pull request #53) stays a draft and is not worked before the demonstration. Finishing
it means the genuinely difficult security work — PKCE, token and signature validation, key rotation — none
of which the room is deciding next week, and none of which can be shown without a live identity provider.
The deferral is recorded in DEC-046, whose trigger to resume is the first commitment to deploy AeroLink for
an organization authenticating against its own directory. That commitment has not been made.

Rebasing that branch can wait until it is genuinely picked up; rebasing an unscheduled draft only means
doing it twice.

## Preparation that cannot be skipped

A dry run on the presenting machine, from a **production build** — not the Vite dev server that
`START_AEROLINK.bat` launches. This is the single highest-risk untested path, because it is the only
environment the demonstration actually depends on.
