# Project State — Start Here

**Last updated: 2026-07-26.**

This is the orientation record for anyone — human or model — picking up AeroLink. It answers *what
exists, what is true today, what is deliberately not being built, and where to start*. Every other
document in this repository is either a durable definition or a historical record; this one describes
the present.

When work changes the state of the project, update this file in the same change.

## What AeroLink is

An on-premises aerospace development assurance platform: the authoritative record for controlled
requirements and the evidence chain around them. It manages system requirements, software HLRs and
LLRs, change requests (SCR/SWCR), review and approval workflows, immutable baselines, generated
controlled documents, test procedures, externally produced results and evidence, typed traceability,
release campaigns, and a complete audit trail.

It exists to answer questions that are normally scattered across documents, spreadsheets and people:
*what exact requirement revision was approved for this release, which change authorized it, what
verifies it, what failed, who approved it, and can this document be reproduced years later?*

Read [PROJECT_VISION.md](PROJECT_VISION.md) for the full statement and
[PRODUCT_PRINCIPLES.md](PRODUCT_PRINCIPLES.md) for the fifteen behavioral rules that constrain every
design decision.

## What AeroLink is not

These are settled boundaries, not gaps awaiting work. They come from the original product brief and
are recorded in [SCOPE_AND_BOUNDARIES.md](SCOPE_AND_BOUNDARIES.md).

- **No certification, compliance, or tool-qualification claim.** The product is *informed by* ARP4754
  and DO-178 concepts and terminology. It does not claim to satisfy their objectives, and it is not a
  qualified tool. Never add language that implies otherwise.
- **No AI.** This is a hard delivery rule of the current program, not an oversight. No suggestion,
  scoring, generative, or assistant capability ships. It may be reconsidered as an explicitly
  governed, human-controlled future capability; it is not in scope now.
- **No plans or standards management.**
- **No architecture, design, or source-code management, and not a Git host.**
- **No automated test execution.** Tests run in external environments; AeroLink controls the
  procedures and captures or imports the results and evidence.
- **No deferral for test procedures.** Change requests can be put away for another day; procedures
  cannot. A requirement that is new or modified in the build being worked on is assumed to need
  coverage, so the procedures verifying it cannot be shelved while it ships — deferring one would
  remove coverage from a requirement still in the build and record it as ordinary planning. The
  deferral that matters happens one level up, on the change request, and verification work already
  follows its change request. See [DEC-058](DECISIONS_AND_OPEN_QUESTIONS.md).
- **Not a document editor.** Documents are generated outputs of controlled data. Uploaded files are
  never the authoritative record.

## Repository layout

| Path | What it is |
| --- | --- |
| `product/` | **The application.** This is the only software in the repository. |
| `product/src/AeroLink.Domain` | Lifecycle rules and invariants. Domain logic lives here, not in controllers. |
| `product/src/AeroLink.Infrastructure` | EF Core persistence, provider selection, migrations. |
| `product/src/AeroLink.Api` | HTTP boundary. |
| `product/client` | React + TypeScript user interface. |
| `product/tests`, `product/client/tests` | Backend test projects and Playwright browser journeys. |
| `design/mockups`, `docs/mockups` | North-star visual concepts. Reference material, not specifications. |
| Root `*.md` | Authoritative product definition. See the index in [README.md](README.md). |
| Root `*.docx` | The original supplied briefs, retained unmodified for provenance. |
| Root `*.bat` | Windows operator entry points for start, stop, backup, restore, diagnostics. |

A `showcase/` directory previously held a Phase 0.5 static-data prototype. It was retired on
2026-07-24 — see DEC-046. The product application is now the single demonstrable artifact.

## Technology

React and TypeScript client; ASP.NET Core on .NET 10 with Entity Framework Core; PostgreSQL for real
use; SQLite for isolated tests and disposable local runs. A modular monolith with explicit domain,
infrastructure and API boundaries. See [product/docs/ARCHITECTURE.md](product/docs/ARCHITECTURE.md).

## What is built and working

The full controlled chain runs end to end:

SCR/SWCR authoring with server-leased exclusive checkout and autosave recovery → sequential *and*
parallel author-selected approval sequences with frozen snapshot hashes → password-confirmed
electronic signatures → candidate baseline assembly with SHA-256 freeze → deterministic baseline
materialization → generated SYSRD/SWRD and test-procedure documents in DOCX and PDF with approval
provenance and document control → versioned test procedures → external execution import with evidence
and immutable retest chains → typed, version-aware traceability with suspect links and impact
analysis → a verification-impact queue that raises test work when an approved change alters what must be
verified → a governed release campaign with computed readiness gates and ordered release approval.

Around that core: enterprise requirements workspace, configurable artifact schemas, saved views and
structured queries, governed bulk operations, visual redlines, CSV/XLSX onboarding that lands in a
Draft SCR rather than bypassing approval, ReqIF 1.2 round trip, a versioned REST API with scoped
service identities, webhooks with HMAC signing and dead-letter replay, OSLC RM, product-line libraries
and variants, backup with integrity manifests, and isolated restore drills.

Delivered since, and not to be omitted when describing the product: **email notification of required
approvals** through an outbox over the existing in-app notification record; **rich authored content** —
tables, figures and symbols stored as structure rather than markup, so nothing ever becomes HTML — in
requirement statements and change-request narrative, reproduced in the generated DOCX and PDF; **configurable
review workflows**, where a project records who signs a change request, in what authority and in what order,
versioned and never edited in place; a **Jira connector** with field mapping and link-back; and **approved
document templates that decide what a generated document contains**, rather than being numbered and approved
while a generator ignored them.

Change control reads as two facts rather than one. **Allocation** says which build a change request is going
into, or that it is **deferred** — put away for another day with the state it had reached remembered, so a
signed-off change on the shelf is distinguishable from an unwritten one, and reinstating returns it exactly
there. **State** says how far it has got: Draft, In review, Approved, Incorporated once the build ships, and
Superseded once a later revision exists. The last two are derived from the release and from the revision set
rather than stored, so neither can disagree with reality. Listings show each change request's newest revision
with its superseded history one click away (DEC-056). A released build takes no new change requests and no
revisions of old ones (DEC-055, DEC-054).

Authoring says where a requirement goes and what the traces already know. An author chooses the specification
section a proposed requirement belongs in, applied at materialization — introduced requirements land there and
modified ones move (DEC-057). Beside the five impact dispositions, the proposal shows what the trace graph
records: the requirements that derive from this one and the procedures that verify it. That panel is read-only
and closes no gate — a tool finding no links and an engineer confirming no impact are different claims, and only
the second is worth anything in a review (DEC-059).

Documents are offered where the requirements are read, not only on the Digital Thread. The build decides which:
the approved controlled document for a released build, or a draft at the revision the released document will
carry, generated from the released baseline plus every approved change and stamped DRAFT on every page — never
stored, because a controlled record of content that is still moving is a record of nothing.

Identity: local accounts, Program-scoped roles, sessions, MFA with recovery codes, mandatory
temporary-password rotation, scoped service accounts, and security audit.

Verification impact: approving a change request raises an item for every requirement it introduces or
modifies, and for any procedure a retirement leaves covering nothing. A Test Lead distributes items;
a Test Engineer resolves each one either by naming an approved procedure or by recording that no test is
required — a requirement the author declared verifiable by analysis still needs that confirmation.
Undecided items hold the `verification_impact` release-readiness gate, so they block release approval; they
deliberately do **not** block the baseline freeze, because freezing and materializing is what creates the
requirement revisions a procedure is written against. "Decided" means the procedures are authored and
approved — it says nothing about whether they have been executed.

Materialization is where the loop closes, because it is the first moment requirement revisions exist. Each
item binds to the exact revision its change produced; coverage on a modified requirement carries forward
onto the new revision marked **suspect**; a decision that named a procedure becomes the real coverage link,
clearing the suspect flag rather than duplicating it; and a procedure a retirement left covering nothing
raises its own item. **Suspect coverage is not coverage**: the `coverage` readiness gate counts only
confirmed links, so a requirement cannot reach release on the strength of a procedure written against its
previous wording.

Presentation: one design system across every surface — a 12px readability floor, four radii, one type
scale, one focus treatment — with **comfortable and compact information density** expressed as spacing
tokens applied through the workspace shell, and **WCAG 2.2 AA as a commitment**: 4.5:1 body contrast,
3:1 large text, and 24x24 minimum target sizes, all measured on rendered pixels by
`product/client/tests/accessibility-contrast.spec.ts` and `design-system.spec.ts` in both densities.

## The demonstration dataset

`FMSLIVE` is a deterministic, production-shaped program built through the same domain and persistence
rules as any user-created program — not a mock data layer. Enabled by `DemoData:Enabled`, disabled by
default in production configuration.

Released **FMS 1.5** baseline: 150 system requirements, 400 HLRs, 700 LLRs, 1,250 effective revisions,
30 SCRs, 75 SWCRs, 1,100 typed traces, 515 procedures, 520 executions including retained retests, 6
controlled documents, 1 released build. **FMS 1.6** is derived from it and deliberately in work, with
eight change requests spread across approved, in-review, draft and deferred.

The tool never auto-creates or auto-approves a successor release. Details in
[FMS_LIVE_SHOWCASE_DATASET.md](FMS_LIVE_SHOWCASE_DATASET.md).

## Where delivery stands

Work is tracked as **AeroLink 3.0** ([issue #29](https://github.com/seanmccarthyns/requirements-management-tool/issues/29)),
whose contract is [AEROLINK_3_ENTERPRISE_LIFECYCLE_COMPLETION.md](AEROLINK_3_ENTERPRISE_LIFECYCLE_COMPLETION.md).
Per-workstream status is in [AEROLINK_3_IMPLEMENTATION_STATUS.md](AEROLINK_3_IMPLEMENTATION_STATUS.md);
that scorecard is the authority, and this section is a summary of it.

No workstream is Complete. Workstream 4 (enterprise identity) has a delivered slice with its remainder
**formally deferred** — federation, SCIM, break-glass, step-up, account recovery, provider health and
the identity administration UI are not in progress and not scheduled. The reason, the trigger to
resume and the order to resume in are recorded in the contract's Workstream 4 decision record. Do not
treat that deferral as a backlog to pick up without the trigger being met.

Open issues: **#29** (program parent), **#34** (identity — deferred remainder), **#38** (production
operations and qualification). The next active focus is deliberately unselected; record it in the
implementation status document when it is chosen.

**[PRODUCT_REVIEW_2026_07_26.md](PRODUCT_REVIEW_2026_07_26.md)** holds the findings from the first evening of
using the product as an engineer would. **Every item in it is now closed** — the six defects, and all nine that
needed a product decision first. The last of them was the impact-disposition question, which asked for the
computed trace impact to be shown beside the declared disposition; that is now in the proposal card (DEC-059).
The file is retained as the record of what was found and decided, not as a list of work outstanding.

A second evening of review followed on 27 July, and its eleven observations are also closed. Four of them were
not missing features but unreachable ones: a Revise action gated on a state no change request in the programme
rested in, a deferral shelf the domain supported and nothing exposed, a change-type field that was read-only on
the one proposal that arrived pre-seeded, and section filtering that worked on the read side while no authoring
path could set a section. **The recurring failure was reachability, not absence** — code that existed, was
correct, and had no route to it.

## Known limitations — state these accurately

Understating these is a product-integrity failure, not a marketing choice.

- **The scale claim is 150 simultaneous *database clients* and 50,000 requirements on one workstation,**
  with zero failures. This is **not** 150 rendered browser sessions on production topology, and must never
  be described as such. The HTTP path has since been measured too — the `session-load` harness signs in 150
  real authenticated sessions — and that measurement is what found the sign-in limiter refusing 121 of 150
  users. But per-page latency was measured from 10 to 50 concurrent sessions, and one query still caps the
  requirements workspace, so the *claim* does not change. Say "database clients", and say the HTTP path is
  measured but the user number is not yet supported. See
  [product/docs/SCALE_FOUNDATION.md](product/docs/SCALE_FOUNDATION.md) and the path to 150 users in
  [CAPABILITY_ROADMAP.md](CAPABILITY_ROADMAP.md), which is costed and deliberately not started.
- **Email delivery exists but no mail server is configured.** An outbox writes a delivery row in the same
  transaction as the domain change and a background dispatcher sends it over the organization's SMTP relay.
  With no relay configured, deliveries stay Pending and inspectable rather than being dropped. Nothing has
  been proved against a real relay, so treat "notifications reach people by email" as built and unexercised.
  This removed the hard dependency that self-service account recovery was blocked on.
- **Production deployment is not complete.** TLS, certificate and secret management, reverse-proxy
  topology, scheduled off-device backups, monitoring, retention enforcement and an independent
  security review remain organization-specific work. See
  [SECURITY_AND_IDENTITY_MODEL.md](SECURITY_AND_IDENTITY_MODEL.md).
- **Demonstration credentials are non-production** and must be replaced before any operational use.
The client has **no external runtime dependency**: it makes no network request outside its own origin,
and has been verified to start with all external requests blocked. Keep it that way — a CDN reference
in the client contradicts the on-premises posture and, as the resolved case below showed, can block
first paint for seconds on a restricted network. See DEC-047.

## How to run it

PostgreSQL must be installed once on a new machine — `product\scripts\Setup-Postgres.ps1`, which downloads
roughly 360 MB from `enterprisedb.com`. Neither launcher does this, and on a restricted network it is the
step most likely to fail.

**To demonstrate AeroLink, or to see what a deployment serves:** `START_AEROLINK_PRODUCTION.bat`. It builds
the client and serves it from the API on one origin at `http://127.0.0.1:5080` — one process, one port, no
CORS. This is the on-premises shape and the only path that runs the built client (DEC-052).

**To work on AeroLink:** `START_AEROLINK.bat`, which runs the Vite **dev** server on `http://127.0.0.1:5173`
against the API on 5080. `STOP_AEROLINK.bat`, `AEROLINK_DIAGNOSTICS.bat`, `BACKUP_AEROLINK.bat`,
`VERIFY_AEROLINK_BACKUP.bat` and `RESTORE_AEROLINK.bat` cover the rest. Full procedures in
[product/docs/OPERATIONS.md](product/docs/OPERATIONS.md); developer path and test commands in
[product/README.md](product/README.md).

Both launchers wait on `/health/ready`, which opens a database connection. They previously waited on
`/health`, which answers "is the process listening" and is true with no database at all.

The browser journeys run on Linux, macOS and Windows: `cd product/client && npx playwright test`, after
`npx playwright install chromium` once. They were Windows-only until the Playwright configuration stopped
launching its servers through a PowerShell prologue. Set `AEROLINK_E2E_SKIP_BUILD=true` to reuse an
already-built API and cut about a minute per run.

`npm run test:production` runs a separate set of journeys against the **built** client served by the API.
Everything else serves the client with `vite dev`, which is a different artifact — unbundled modules with
stylesheets injected as they evaluate, rather than chunked code and one extracted, hashed stylesheet. Expect
it to catch things the dev journeys structurally cannot; it found three defects on its first runs.

CI runs the dev journeys and the production journeys on Linux for pull requests, plus one unsharded Windows
pass on the schedule, because Windows remains a supported deployment platform. The Windows job also runs
lint, typecheck and build — it did not until 2026-07-26, and the client had been failing to compile there.

Local demonstration identities (`admin`, `systems.author`, `software.author`, `systems.reviewer`,
`release.manager`) share a local-only password documented in `product/README.md`. Production
deployment uses the one-time protected administrator bootstrap instead.

## How this project governs itself

AeroLink is developed under the same discipline it sells. Respect it — these conventions are the
reason the document set can be trusted.

- **Markdown in Git is authoritative.** Generated Word or PDF copies are snapshots, not sources.
- **Decisions are append-only.** Recorded in
  [DECISIONS_AND_OPEN_QUESTIONS.md](DECISIONS_AND_OPEN_QUESTIONS.md) as `DEC-nnn`. If a decision
  changes, add a superseding decision and retain the original. Never edit a decision's meaning in place
  and never silently change it in another document.
- **Capabilities get stable identifiers** in [FEATURE_CATALOG.md](FEATURE_CATALOG.md). Identifiers are
  never reused.
- **Scope changes are recorded**, not made quietly.
- **Normative language is deliberate**: **must** is mandatory, **should** is preferred, **may** is
  optional.
- **Deferrals are written down** with reason, resume trigger and excluded acceptance criteria — see
  Workstream 4 for the worked example.

## Lessons this project has already paid for

- **A green gate is not evidence a capability is reachable.** An identity migration was authored
  without the attributes Entity Framework needs to discover it, so it never ran; the tables existed
  only inside a hand-written test fixture, and every endpoint depending on them would have failed at
  runtime. Every test passed, because no test and no smoke step ever called those endpoints. Guard
  tests now fail the build if a migration is undiscoverable or the model drifts from its snapshot.
  When adding a capability, ask what would fail if it were entirely absent — and make sure something
  does.
- **Migrations must be generated, not hand-authored.** Use
  `dotnet ef migrations add <Name> --project src/AeroLink.Infrastructure --startup-project src/AeroLink.Api --output-dir Persistence/Migrations`.
  Entities must also be mapped in `AeroLinkDbContext`, because the non-PostgreSQL path builds its
  schema from the model rather than from migrations.
- **Prefer deferring honestly over building speculatively.** Workstream 4's remainder was deferred
  because nothing in it had a real user yet. Recording that is better than a silent backlog.
- **A test can pass by racing past the thing it checks.** The readability journey asserted that no text
  on the change-request surface renders below 12px, and it passed for months while the page in fact
  rendered 9px initials and an 11px lifecycle chip — it sampled the page before those rows appeared.
  Making the client faster removed the race and the assertion started failing, correctly. When a test
  starts failing after an unrelated performance change, suspect the test was never really exercising
  its subject.
- **A suite that cannot run where the work happens does not run.** The browser journeys launched both
  their servers through a PowerShell prologue, so they were Windows-only: they could not be executed on a
  Linux development machine at all, and CI paid the Windows rate to run them. Two real defects sat behind
  that wall — a flexbox row that overflowed the page and a release gate wired to the wrong transition —
  and neither was findable locally. The config now passes configuration through `webServer.env` and the
  same suite runs on either platform. Before trusting a gate, check that you can run it yourself.
- **A gate belongs on the transition the workflow can actually satisfy.** The verification-impact queue
  was first wired to block *baseline freeze*. Freezing and then materializing is what creates the
  requirement revisions a test engineer needs before a procedure can exist, so the gate withheld the test
  team's own inputs and deadlocked the release. It is now the `verification_impact` readiness gate on
  release approval, which is what was actually asked for. The gate also shipped with no test of its own;
  an existing journey caught it, and only once that journey could run.
- **Auditing default states is auditing the easy case.** The design contract was checked on each surface
  as it first rendered. A surface can be contained at rest and overflow the moment a panel opens — the
  requirements workspace did exactly that — and a queue with no rows in it hides every colour its rows
  use. Two contrast failures on My Work appeared only once other journeys had created work items. Audits
  now cover both densities, an opened inspector, and populated surfaces.
- **Density is spacing, not type.** Compact reduced body text to 14px, and every unstyled `<small>` — at
  the user agent's 0.8333em — silently fell from 12.5px to 11.67px, under the readability floor. The
  floor had never been measured in compact because nothing exercised compact. Relative font sizes make a
  floor unpredictable; pin the element instead of trusting inheritance.
- **A method nothing calls is a claim nothing keeps.** The verification feature shipped with
  `LinkRequirementRevision`, `CarriedForward`, `MarkSuspect` and `ConfirmStillValid` fully written, tested
  at the domain level, and never called from production code. The documentation described suspect
  carry-forward as product behaviour while no code path produced a suspect link. Domain tests pass happily
  against methods no caller reaches; before believing a capability exists, follow it from an endpoint.
- **Look for the mechanism that already exists before adding one.** Release reconciliation had been
  carrying coverage forward across baselines for as long as it existed — silently, and unmarked, which
  asserted that a procedure written against previous wording still verified the new one. Adding a second,
  safer carry-forward simply produced two mechanisms; the fix was to delete the unmarked one. A failing
  test in an unrelated area is often the first sign that the behaviour you are adding is already there.
- **An on-premises product must be measured on a hostile network, not a good one.** The client fetched
  its webfonts from a public CDN. On a fast connection this was invisible; when the request hung rather
  than failing fast — the normal behaviour of a firewall that drops packets instead of rejecting them —
  first paint took 12,994 ms instead of 147 ms. Fixed by self-hosting (DEC-047). Before shipping
  anything that loads a resource, ask what happens when that resource is unreachable *slowly*.
- **A benchmark measures what it drives, not what it is named after.** The scale harness had always
  reported 150 concurrent clients, and the documentation was careful to call them *database* clients —
  correctly, because the harness issued EF queries straight at PostgreSQL. Nobody had driven the HTTP
  path. Doing so found that sign-in refused 121 of 150 users, because the rate limiter partitioned on
  network address and an on-premises site reaches the product through one proxy: the product denied
  service to its own users, and no database-level measurement could ever have shown it. When a number is
  quoted in a unit, check that something actually measures that unit.
- **A read that writes will be slow, and the cost hides in a method that looks idempotent.** The
  requirements explorer called a project-wide backfill on every GET. It was correct, it was idempotent,
  and it loaded every requirement, revision, profile and specification node in the project before
  returning fifty rows — nine seconds a page at fifty thousand requirements. "Idempotent" says nothing
  about what it costs to discover there is nothing to do. The first guard was itself a join through
  fifty thousand rows and barely helped; the fix was to make the check one indexed count.
- **The control existed; the thing it controlled did not.** Document templates were numbered, approved by
  a named person, versioned, and hashed at approval — and their body was JSON that no generator ever
  opened. Every ceremony was real and none of it changed a document. The same shape appeared twice more
  in one week: a rich-text field nothing rendered, and an attachment vault reachable only from a screen
  nobody working on a change request would open. Before building a control, check whether the last one is
  wired to anything.
- **A rule that wins by being loaded last is a rule with no owner.** Splitting the client so each workspace
  arrives when somebody opens it also moves its stylesheet, which then lands after everything already on the
  page. Twenty row and card families immediately lost their density spacing, because Density.css set
  `padding-block` and each component set `padding` — identical specificity, decided purely by order, and the
  order had been an accident of the module graph. The same shape appeared twice more: two unrelated forms
  sharing the class `.buildForm`, and a setup-form rule imposing its grid placement on every error box in the
  product. None of the three was caused by the split; the split only removed the accident that had been
  hiding them. Before relying on a rule, ask what makes it win — and if the answer is "it happens to be last",
  it will stop being last.
- **Verify the mechanism, not just the failure.** The contrast audit began failing on a colour that had been
  wrong all along; the split had merely changed the timing enough for the element to be on screen when the
  audit sampled. Running the same test on an untouched checkout is what separated "I broke this" from "this
  was always broken" — two findings that look identical and need opposite responses.
- **A gate that cannot run on the deployment platform is not a gate for that platform.** The journeys were
  freed from Windows so they could run on Linux, and every check that could observe a Windows-only failure
  stayed on Linux with them. `RichContent.tsx` and `richContent.ts` differ only in case: two modules on
  Linux, one file on Windows, so `npm run build` and `npm run typecheck` failed on the platform this product
  is deployed to, for as long as both files existed. The Windows job ran only `npx playwright test`, and
  Playwright serves the journeys through `vite dev`, which transpiles each file without checking types — so
  the one job on the right platform was structurally incapable of seeing it. Moving a suite to where it runs
  easily is not the same as covering where the product runs.
- **Running a thing is not the same as running the thing you ship.** Every gate, both launchers and every
  journey served the client with `vite dev`. The production bundle was compiled on every pull request and
  never once rendered in a browser, on any platform — while the demonstration brief named a dry run from a
  production build as the one preparation that could not be skipped. It was not untested; the environment
  did not exist. Its first four runs found a page that scrolled sideways, an 11px label under the readability
  floor, and a content security policy that blocked eight self-hosted typefaces. Ask what artifact the gate
  is actually exercising, and whether anybody ships that one.
- **A hardcoded list of surfaces stops being a list of surfaces.** The design audit named twelve. Review
  Procedures and New Change Request arrived later and were never added, so neither had ever been measured,
  and both were breaking the contract. The production journey reads the navigation instead — it cannot go
  stale, because the product tells it what exists. Prefer enumerating the thing over describing it.
- **Specificity is the other way a rule loses.** `.richFileInput` set a visually-hidden control to one pixel
  and lost to `.controlledEditor input { width: 100% }` — (0,1,0) against (0,1,1) — so the input rendered at
  1160px and pushed the page 106px off screen. The cascade lesson above is about load order; this is the same
  failure through the other mechanism, and the same fix applies. When a rule matters, make it win on purpose.
