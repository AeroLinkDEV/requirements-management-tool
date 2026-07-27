# Capability Roadmap — Competitive Gap Closure

**Recorded 2026-07-25.** Decisions taken after a gap analysis against commercial requirements-management
tools. This file records what was accepted, what was declined, and how each accepted item is to be built,
so the work can be picked up without re-deriving the reasoning.

Items are numbered as they were in the analysis, so the numbering is not sequential here.

## Accepted, in build order

**Status as of 2026-07-26: items 1, 2, 3, 5, 8 and 9 are built.** The designs below are retained as the
record of what was decided and why, not as a queue. Item 10 is measured with its claim deliberately
unchanged, and the path beyond it is costed and deliberately not started.

### 1. Email notification of required approvals — **built**

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

### 2. Rich content in requirements and change-request narrative — **built**

Images, tables and formatted content in requirement statements, and in the change request's problem,
analysis and solution fields. Must be easy to author, not a markup language people fight.

**Design.** A schema field type `RichText` already exists but stores a plain string; nothing renders or
sanitises embedded content. Needs a sanitising pipeline with a strict allow-list (defence against stored
XSS is mandatory when the content is authored by one user and read by an approver), content-addressed
image storage reusing the evidence-store pattern, and — the hard part — **deterministic reproduction in
generated DOCX and PDF**. A document that cannot reproduce its embedded table byte-for-byte breaks the
reproducibility claim the product rests on.

### 3. Attachments on requirements — **built**

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

### 8. Jira integration — **built**

Outbound webhooks with HMAC signing and dead-letter replay already exist, as does OSLC. What is missing is
a *named* connector: field mapping, link-back to the AeroLink artifact, and status reflection. Begin with
the hook and mapping, not a full bidirectional sync.

### 9. Controlled document templates — **built**

Document generation is fixed-form. Programs need to define their own SYSRD/SWRD layout, and the template
itself must be a controlled, approved, versioned artifact — otherwise changing a template silently changes
every document generated afterwards. Workstream 6 already carries this as an acceptance-gate item.

### 10. 150 concurrent users — **measured; claim unchanged**

Today's evidence is 150 simultaneous *database clients* and 50,000 requirements on one workstation. That is
not 150 rendered browser sessions, and the documentation is careful never to claim it is.

**Measurement first was the right call.** The `session-load` harness drives real authenticated HTTP sessions
— one account and one cookie jar per simulated person, all 150 signed in before any of them works — against
a 50,000-requirement PostgreSQL repository. It found three things, two of which are now fixed.

**Fixed — the product denied service to its own users.** Sign-in was rate limited to thirty attempts a
minute per network address. AeroLink is on-premises: a whole engineering group arrives through one corporate
proxy and presents one address, so thirty a minute was a budget shared by the entire site. At 150 users, 121
were refused at sign-in. Guessing at a password is stopped by the account itself, which locks after eight
failures wherever they come from; this limiter is only flood control, and its default now assumes a site
rather than a person.

**Fixed — the requirements explorer backfilled the whole project on every read.** Each GET loaded every
requirement, every revision, every profile and every specification node before returning the fifty rows
somebody asked for. A watermark now answers "is there anything to do" with one indexed count. At 50,000
requirements this took the page from 9.1s to 3.9s at the median under 25 concurrent sessions.

**Found and not fixed — the workspace query itself.** With the backfill gone, one request for 50 rows out of
50,000 still costs ~380ms with nobody else on the system. That is what now caps the page, and it is a query
problem rather than a concurrency one. Everything else scales: dashboard, change requests, and my-work sit
between 30ms and 120ms per request from 10 to 50 concurrent sessions.

**The claim does not change.** Measurement does not yet support stating 150 *users*, so the documentation
continues to say 150 simultaneous database clients, exactly as before. The next piece of work is the
workspace query, and the harness is now in the repository so the next person measures rather than guesses.

Measured on 4 cores with PostgreSQL, the API, and the harness all sharing them, which flatters nothing.

#### The path to 150 simultaneous users — held until the project is green-lit

**Not started deliberately.** The work below is understood and costed but is not worth doing before the
programme is approved. It is recorded here so it can be picked up cold, without re-deriving any of it.

**The bottleneck is one query, not the hardware.** `/api/enterprise-requirements/workspace` resolves the
current revision of each requirement with a correlated aggregate — the greatest-n-per-group anti-pattern:

```csharp
where revision.Revision == db.RequirementRevisions
    .Where(r => r.ArtifactId == artifact.Id).Max(r => r.Revision)
```

and then immediately runs `current.CountAsync(ct)` over it. The count forces that correlated aggregate
across all fifty thousand requirements on **every page load**, to render a "50 of 50,000" label. The fifty
rows anybody actually asked for are cheap; the count is not. Indexes are not the problem — both
`requirements(ProjectId, BaseNumber)` and `requirement_revisions(ArtifactId, Revision)` already exist. It is
fifty thousand index probes plus a full aggregate, per request, per user.

**In order:**

1. **Confirm before optimising.** `EXPLAIN (ANALYZE, BUFFERS)` on that query at fifty thousand
   requirements. Stated first because it was got wrong twice already in one evening: the first watermark
   guard was itself a fifty-thousand-row join and barely moved the number. Infer nothing here; measure.
2. **Denormalise the current revision.** `CurrentRevisionId` on `RequirementArtifact`, maintained by
   baseline materialization, which already writes every revision and is therefore the single place that has
   to change. The correlated aggregate becomes an indexed join. This is the large win — expected to take
   the page from ~380ms to tens of milliseconds.
3. **Stop counting the world.** Serve a cached project total when no filter is applied; count only when a
   search or filter narrows the set. Or return the page first and let the total follow.
4. **Fix the search path.** `ToLower().Contains(...)` over `Statement` and `Rationale` cannot use an index
   and sequentially scans every requirement. Needs `pg_trgm` with a GIN index, or a `tsvector` column. This
   matters as soon as anybody searches a real repository rather than pages through one.
5. **Re-measure at 150, then tune the ordinary things** — connection pool size, Kestrel limits, output
   caching. Those matter only once the per-request O(n) work is gone. Sizing hardware around a bad query
   moves the cliff rather than removing it.

**Estimate.** Steps 2 and 3 are the bulk and are roughly a day with tests; they alone are expected to bring
the workspace under the 2s p95 target at 150 sessions. Step 4 is a further half-day. Nothing else in the
product needs work: dashboard, change requests and my-work never exceeded 120ms from 10 to 50 sessions.

**How to re-measure.** Seed and drive from the repository, no external tooling:

```
dotnet run --project product/tools/AeroLink.Scale -- workspace --profile medium --reset
dotnet run --project product/tools/AeroLink.Scale -- session-load --users 150 --iterations 8 --api <url>
```

`session-load` provisions one account per simulated person and signs them all in before any of them works.
Do not shortcut that: driving 150 sessions from one account measures a scenario nobody has, and collides
with the sign-in limiter, which is keyed per account on purpose.

**Only then may the claim change.** Until these numbers exist, the documentation says 150 simultaneous
*database clients* and 50,000 requirements on one workstation, and says nothing about users.

## First-load cost — **halved; one part deferred**

Signing in used to download and execute the entire product: a single 583 kB script holding all fifteen
workspaces, whether or not anybody opened them. Each workspace is now fetched when it is first opened, and
warmed the moment its navigation entry is hovered or focused, so the code is usually already there when the
click lands.

| | before | after |
|---|---|---|
| First-load JavaScript | 583.2 kB (150.8 kB gzip) | 309.2 kB (89.3 kB gzip) |
| First-load stylesheet | 287.0 kB (64.4 kB gzip) | 287.4 kB (64.5 kB gzip) |

On a local network this is not about bandwidth. It is parse and execute time on the workstation, which costs
the same however fast the link is.

**Deferred: splitting the stylesheet, and the cascade work it needs.** Splitting CSS the same way takes the
first stylesheet from 287 kB to 98 kB — measured, then reverted. A chunk's stylesheet is appended when the
chunk loads, so on-demand stylesheets land in an order that depends on which page the reader opened first,
and this client has rules that win only by being loaded last. Three such reversals against the
always-loaded stylesheets were found and fixed properly rather than worked around:

- **Density.css** decided vertical rhythm for twenty row and card families with `padding-block`, and lost
  every one of them to a component's `padding` shorthand at equal specificity. Its selectors are now written
  `html .x`, so the density system wins by specificity instead of by luck. Without this, compact density
  silently stopped compressing anything — which the browser journeys caught.
- **`.formError`** was defined in the change-request editor's stylesheet and used by three surfaces in three
  different bundles. It now lives in App.css. The setup form's own version, which imposed a grid placement
  that made sense only inside that form, is scoped to `.setup`.
- **`.buildForm`** names two unrelated forms, in History and in the release execution workbench, styled
  differently. The workbench's rules are scoped to `.executionWorkbench`.

What remains are chunk-against-chunk pairs, where neither stylesheet is always present. Fixing those means
putting component rules in a cascade layer that the density and cohesion layers outrank, so order stops
mattering anywhere — a change to every stylesheet in the client, and the prerequisite for the remaining
190 kB.

**System Operations is also still eager**, for the same reason at a smaller scale: CohesionPass.css corrects
the type size on thirty-seven of that surface's selectors by being loaded later, not by being more specific.
Splitting it reverts all thirty-seven to 7–9 px production text, which is the defect the cohesion pass was
written to fix. Worth 40 kB until those rules belong to the surface instead of to a catch-all file.

## The API composition root — **split**

`Program.cs` was 2,019 lines: the whole composition root and 154 endpoints in one file. Both questions people
actually bring to it — *what does startup do?* and *where is this route handled?* — could only be answered by
reading all of it.

It is now 222 lines — 209 when the split landed, plus the client-hosting composition added by DEC-052 — and
holds only the order things happen in: services, the middleware every request passes
through, then the route table. The 154 endpoints moved into nine modules named after the part of the lifecycle
they serve, in the same shape as the thirteen modules that had already been split out:

| module | endpoints |
|---|---|
| `RequirementsEndpoints` | 32 |
| `EditSessionEndpoints` | 19 |
| `ChangeRequestEndpoints` | 18 |
| `BaselineEndpoints` | 17 |
| `ReleaseCampaignEndpoints` | 15 |
| `WorkspaceEndpoints` | 14 |
| `AuthEndpoints` | 13 |
| `VerificationEndpoints` | 13 |
| `AdministrationEndpoints` | 9 |

The 65 request records moved to `ApiContracts.cs`, and the helpers that more than one module needs — reading
the actor off a request, allocating the next controlled identifier, and `ApiMap` — to `ApiSupport.cs`. `ApiMap`
is why those are shared rather than private: a change request rendered by the change-request endpoints, by the
baseline endpoints, and by search has to be the same object in all three.

Nothing was rewritten; every handler moved verbatim, and the move was checked by comparing the route table
before and after — 154 routes, none missing, none duplicated. The one stray endpoint, `/api/quality/
metric-contracts`, went into the module that already owned the `/api/quality` group.

## Deferred, deliberately

### 4. Word round-trip

Reviewers marking up a generated DOCX and importing it back. Kept in mind, not scheduled. Generation is
one-way today; import is CSV/XLSX and ReqIF.

## Declined for now

- **6. Fine-grained permissions** beyond Program-scoped roles (per-module, per-attribute).
- **7. Rule-based requirement quality checking** (weak words, passive voice, missing units).

Both remain reasonable; neither is scheduled. Note that 7 does not conflict with the no-AI boundary, since
the useful version is rule-based.
