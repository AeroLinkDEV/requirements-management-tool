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

### 5. Configurable workflows

Each team defines the states and transitions that fit its process, rather than inheriting the fixed
`ScrState` enum.

**Design.** Strictly additive. `ScrState` is embedded in the readiness gates, the browser journeys, the
history filters and the seeded showcase; replacing it wholesale would destabilise the product. The
approach is a workflow *definition* per artifact type — states, allowed transitions, and which transitions
demand a signature or a rationale — with the current enum as the built-in default definition, so existing
programs keep working unchanged.

**Preserve the `Deferred`-after-`Approved` behaviour.** Approval and inclusion are separate decisions: a
change can be technically approved and still not belong in this release. Deferring removes it from the
release's readiness obligations without discarding a valid review, and `Retarget` moves it later. Most
tools handle this badly; it is worth keeping explicitly in any configurable model.

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
