# Project State — Start Here

**Last updated: 2026-07-24.**

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
analysis → a governed release campaign with computed readiness gates and ordered release approval.

Around that core: enterprise requirements workspace, configurable artifact schemas, saved views and
structured queries, governed bulk operations, visual redlines, CSV/XLSX onboarding that lands in a
Draft SCR rather than bypassing approval, ReqIF 1.2 round trip, a versioned REST API with scoped
service identities, webhooks with HMAC signing and dead-letter replay, OSLC RM, product-line libraries
and variants, backup with integrity manifests, and isolated restore drills.

Identity: local accounts, Program-scoped roles, sessions, MFA with recovery codes, mandatory
temporary-password rotation, scoped service accounts, and security audit.

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

## Known limitations — state these accurately

Understating these is a product-integrity failure, not a marketing choice.

- **Scale is proven at the database layer only.** 150 simultaneous *database clients* and 50,000
  requirements on one workstation, with zero failures. This is **not** 150 rendered browser sessions
  on production topology, and must never be described as such. See
  [product/docs/SCALE_FOUNDATION.md](product/docs/SCALE_FOUNDATION.md).
- **No email transport exists.** Notifications are in-app only. Any feature needing outbound mail —
  self-service account recovery in particular — carries this as a hard dependency.
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

Windows operator path: double-click `START_AEROLINK.bat`, which starts or verifies PostgreSQL, the API
and the website, then opens `http://127.0.0.1:5173`. `STOP_AEROLINK.bat`,
`AEROLINK_DIAGNOSTICS.bat`, `BACKUP_AEROLINK.bat`, `VERIFY_AEROLINK_BACKUP.bat` and
`RESTORE_AEROLINK.bat` cover the rest. Full procedures in
[product/docs/OPERATIONS.md](product/docs/OPERATIONS.md).

Developer path and the test commands are in [product/README.md](product/README.md). Note that
`START_AEROLINK.bat` runs the Vite **dev** server; a production build is served differently and is the
better choice for demonstrations.

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
- **An on-premises product must be measured on a hostile network, not a good one.** The client fetched
  its webfonts from a public CDN. On a fast connection this was invisible; when the request hung rather
  than failing fast — the normal behaviour of a firewall that drops packets instead of rejecting them —
  first paint took 12,994 ms instead of 147 ms. Fixed by self-hosting (DEC-047). Before shipping
  anything that loads a resource, ask what happens when that resource is unreachable *slowly*.
