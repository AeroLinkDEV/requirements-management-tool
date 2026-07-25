# Capability Roadmap — Competitive Gap Closure

**Recorded 2026-07-25.** Decisions taken after a gap analysis against commercial requirements-management
tools. This file records what was accepted, what was declined, and how each accepted item is to be built,
so the work can be picked up without re-deriving the reasoning.

Items are numbered as they were in the analysis, so the numbering is not sequential here.

## Accepted, in build order

### 1. Email notification of required approvals — **first**

Every event that makes an approval someone's responsibility must reach them by email, with a link to the
exact page: change-request review activation, document approval, test-procedure approval, verification
impact assignment, and release approval.

**Design.** Email is a *delivery channel over the existing `UserNotification` record*, not a parallel
system. There is already an in-app notification carrying recipient, type, title, detail and a `Route` such
as `scr:{id}`; adding a second notion of "who should be told what" would create two sources of truth that
drift.

- **Outbox, never inline.** A delivery row is written in the same transaction as the domain change; a
  background dispatcher sends it. An SMTP timeout must never fail an approval submission, and a
  notification must never be lost because a send was attempted before the transaction committed.
- **Deep links** need a configured public base URL, because the server cannot know its own external
  address. `Route` maps to a client path.
- **Fail visible, not silent.** With no SMTP configured, deliveries stay Pending and are inspectable.
  Dropping them quietly is the failure mode that loses an approval.
- **Unsubscribe** is a signed token in the body resolving to an unauthenticated opt-out endpoint. Scoped
  per user now; per-notification-type granularity later.
- Nothing is emailed that the recipient could not already see in the product, and no token, credential or
  raw claim ever appears in a message.

### 2. Rich content in requirements and change-request narrative

Images, tables and formatted content in requirement statements, and in the change request's problem,
analysis and solution fields. Must be easy to author, not a markup language people fight.

**Design.** A schema field type `RichText` already exists but stores a plain string; nothing renders or
sanitises embedded content. Needs a sanitising pipeline with a strict allow-list (defence against stored
XSS is mandatory when the content is authored by one user and read by an approver), content-addressed
image storage reusing the evidence-store pattern, and — the hard part — **deterministic reproduction in
generated DOCX and PDF**. A document that cannot reproduce its embedded table byte-for-byte breaks the
reproducibility claim the product rests on.

### 3. Attachments on requirements

Evidence upload exists but is bound to test executions and release campaigns. Requirements need their own
attachments (an ICD extract, a diagram). Reuses the evidence store and its integrity manifest.

### 5. Configurable workflows — **built**

Each team records who has to sign a change request, in what authority, and in what order.

**What was built.** A `ReviewWorkflow` per project and change-request type: ordered named stages, each
naming the *authority* that must sign it (Reviewer, Configuration Manager, Approver…) rather than a person,
so the procedure survives somebody changing jobs. The workflow also fixes the order — sequential or
parallel — and that choice wins over whatever an author picks at submission, because a team that recorded a
parallel board does not want an author quietly making it sequential.

Reviews record which procedure and which version they were judged by, and each approval step records the
stage it answers, so an approval reads as "the configuration manager signed" rather than as "position 1
signed". An administrator can stand in for any stage: somebody has to be able to unblock a review when the
named authority is on leave, and a control that can never proceed is not a control.

**Additive, not replacing.** A project with no recorded workflow submits reviews exactly as before, with
free approver choice. A rule nobody has written down yet must not become a rule that blocks work. `ScrState`
is untouched — it is embedded in the readiness gates, the browser journeys, the history filters and the
seeded showcase, and replacing it wholesale would destabilise the product for no gain the teams asked for.

**Versioned, never edited in place.** Revising a procedure produces the next version and retires the prior
one, which stays retained. A completed review has to remain explainable by the rules it was actually judged
against; rewriting the procedure underneath it would make its record say something that never happened.

#### What "deferred after approval" means

This came up as a question, and it is worth stating plainly because it is the piece most tools get wrong.

**Approving a change and shipping it are two different decisions.** Approval says: *this change is
technically correct, the analysis holds, the requirement text is right, and the named authorities have
signed for it.* Inclusion says: *this change belongs in the release we are about to freeze.*

Those come apart all the time. A change is approved in March and the release freezes in April with a scope
the programme has cut. The engineering judgement has not changed — the change is still correct, and the
signatures on it are still valid — but it is not going in this one.

Most tools force one of two bad answers at that point:

- **Reject it**, which throws away a completed, signed review. When it comes back for the next release,
  everybody reviews it again from scratch, and the audit trail shows a rejection that never reflected any
  actual engineering objection.
- **Leave it approved and pointed at the release**, which means the release's readiness gates keep counting
  it as outstanding work. The release can never show as ready, so people learn to ignore the gate — and a
  gate people ignore is worse than no gate.

AeroLink's answer is a third state. `Deferred` is reachable from `Approved`, and it means: *approved, and
deliberately not in this release.* The review stays intact, the signatures stay valid, and the change drops
out of the release's readiness obligations without anybody pretending it was rejected. `Retarget` then
moves it to the release it is actually going into, recording who moved it and why.

So the state is not "approved then un-approved". It is "approved, and scheduled elsewhere" — and the reason
it is a distinct state rather than a flag is that the decision to defer is itself attributable, needs a
rationale, and belongs in the audit record next to the approval it follows.

This behaviour is preserved unchanged by configurable workflows. Workflows configure *who signs and in what
order*; they do not configure away the separation between approving a change and shipping it.

### 8. Jira integration

Outbound webhooks with HMAC signing and dead-letter replay already exist, as does OSLC. What is missing is
a *named* connector: field mapping, link-back to the AeroLink artifact, and status reflection. Begin with
the hook and mapping, not a full bidirectional sync.

### 9. Controlled document templates

Document generation is fixed-form. Programs need to define their own SYSRD/SWRD layout, and the template
itself must be a controlled, approved, versioned artifact — otherwise changing a template silently changes
every document generated afterwards. Workstream 6 already carries this as an acceptance-gate item.

### 10. 150 concurrent users

Today's evidence is 150 simultaneous *database clients* and 50,000 requirements on one workstation. That is
not 150 rendered browser sessions, and the documentation is careful never to claim it is.

**Design.** Measurement first: a load harness driving real authenticated sessions through real endpoints,
then fix what it finds — N+1 queries, missing indexes, connection-pool limits, unbounded result sets. The
claim is only allowed to change when the measurement supports it.

## Deferred, deliberately

### 4. Word round-trip

Reviewers marking up a generated DOCX and importing it back. Kept in mind, not scheduled. Generation is
one-way today; import is CSV/XLSX and ReqIF.

## Declined for now

- **6. Fine-grained permissions** beyond Program-scoped roles (per-module, per-attribute).
- **7. Rule-based requirement quality checking** (weak words, passive voice, missing units).

Both remain reasonable; neither is scheduled. Note that 7 does not conflict with the no-AI boundary, since
the useful version is rule-based.
