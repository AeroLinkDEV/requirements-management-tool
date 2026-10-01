# AeroLink project state — start here

**Last materially reconciled: 2026-09-17.**

**Product checkpoint used for this snapshot:** the #816 Slice 7 authority-provenance and integrated-acceptance completion, built on protected `main` at `14fdffc7c7f70fb9960f198c50f0913dc34b11f7`. That is a checkpoint, not a promise that live `main` will not move. Always refresh GitHub before starting work.

This is the single living product-level orientation record for AeroLink. It answers what the product is, what architecture is currently supported, what remains intentionally outside its claims, and where authoritative detail lives.

Do **not** use a dated handoff, old audit report, or historical issue count as a substitute for this file plus current GitHub state.

## What AeroLink is

AeroLink is an on-premises aerospace requirements-management and development-assurance platform. It is intended to provide a defensible controlled record across requirements, change, review/signature, exact traceability, verification, results/evidence, Problem Reports, baselines, documents, and release readiness.

It exists to make questions such as these answerable from controlled data rather than scattered documents/spreadsheets/memory:

- Which exact requirement revision belongs to this build?
- Which approved change authorized it?
- What exact upstream/downstream revisions does it trace to?
- Which Test Case verifies it?
- Which executable Test Procedure implements that Case?
- What was executed, against which controlled revision, and what evidence/result was retained?
- Which Problem Report or change package explains an issue?
- Who reviewed/approved a controlled package, under what frozen workflow/snapshot?
- Can the controlled document/evidence package be reproduced later?

## What AeroLink is not

These are deliberate boundaries unless an accepted decision changes them:

- **No certification, compliance, or tool-qualification claim.** AeroLink uses aerospace development-assurance concepts/terminology but does not claim that use of the product satisfies certification objectives.
- **No AI product capability.** AI-assisted product behavior is outside the current governed product scope.
- **Not a source-code repository or architecture/design-management replacement.** AeroLink can link to external engineering artifacts but does not become Git or a software design tool.
- **Not a generic document editor.** Structured controlled artifacts are authoritative; generated documents are controlled views. Managed Word documents remain authored in Word while AeroLink controls their revision/review/release record.
- **No automated test-bench execution.** Tests execute in external environments. AeroLink controls Test Cases/Procedures, execution records, results, evidence, retest history, and readiness.
- **No destructive rewrite of approved/released history through normal product workflows.** Historical controlled identity and evidence remain explainable.

See [Scope and Boundaries](docs/product-definition/SCOPE_AND_BOUNDARIES.md) and [Decisions and Open Questions](DECISIONS_AND_OPEN_QUESTIONS.md) for the durable boundary/decision records.

## Current technology and repository shape

- React + TypeScript client.
- A typed current route owns workspace and exact-artifact selection. Workspace hydration resolves that
  destination against authorized server workspaces; missing explicit projects/builds are unavailable rather
  than substituted. Shared request-generation guards reject stale shell and Requirements Explorer reads.
- ASP.NET Core / .NET application and API.
- Entity Framework Core persistence.
- PostgreSQL for real local/on-premises operation.
- SQLite and disposable PostgreSQL infrastructure for isolated automated qualification where appropriate.
- Modular-monolith organization with explicit Domain, Infrastructure, API, and client boundaries.

The application under `product/` is the single demonstrable software product. The earlier static showcase is historical/reference material.

See [product/docs/ARCHITECTURE.md](product/docs/ARCHITECTURE.md).

## Project creation and entry

Code provides build-scoped Merge Requests and Code Explorer views over the configured GitLab repository.
Source selection pins an exact commit; relationships identify exact controlled targets and files, with
attributable withdrawal/re-add history. Separately accepted implementation evidence binds a materialized
baseline and source-selection event, verifies actual merge-result ancestry or exact file availability, and
can contain multiple contributions. Contextual links and observed GitLab status do not complete a release gate.
No-code decisions retain their baseline prerequisites without requiring GitLab. Historical manual records
and signed manifests remain unchanged; a new evidence selector explicitly supersedes a prior decision.
The exact operator-bound synthetic FMS 1.5 release can receive one dated, source-only supplement under
DEC-131. This separate append-only record enables source browsing without becoming an original release
source selection, accepted implementation evidence, or proof of the delivered binary.

Administrators can create a server-side setup draft and resume it across sessions. The creator and
administrators may edit it; unfinished setup is distinct from a usable project. Fresh completion atomically
creates an isolated internal backing scope, the accepted effective ladder and review rules, necessary empty
containers, creator management access, and the chosen first **IN WORK** build. It inherits no FMS team or
engineering content. Canonical `SW-NN.NN` identity validation governs new build allocation without renumbering
historical records. Empty-project ladder corrections preserve configuration history and synchronize containers;
authored or inherited engineering content locks structural changes.

An unfinished saved setup can be discarded by its creator or an administrator, and only from **Draft**: the
confirmation names the setup, a **Finalizing** or completed setup is refused, and a page left open on the
discarded setup can no longer save or finalize it. Discard is logical abandonment through the existing
lifecycle — it stops being offered for resume while its record, staged source and shared evidence remain — and
never deletes a project, build or controlled record.

Supported capability subsets remain valid. Setup offers review subjects only for the configured change-control
and verification capabilities; artifact threads and traceability publications distinguish unavailable verification
from missing coverage using the effective ladder.

A project also chooses which major features it uses (DEC-136): Team Work, Requirements, Verification, Code,
Documentation Center, Problem Reports and Release. They are chosen in the Features step of Create New Project, recorded
as the project's first feature-history entry, and changed later in Project Configuration → Features. An inherited
starting point keeps Requirements, Verification and Release, which its source brings. Command Center and My Work
are always present. A project without a stored set has every feature. Code needs Requirements; a
feature can be switched off only while it holds no records, and the save boundary refuses new records for a feature
that is off. Navigation, quick navigation, deep links and Command Center follow the same set.

Verification without Requirements ("standalone verification", DEC-144) is being delivered in slices on #1188. The
server accepts the combination. A Case or System Procedure in such a project is Standalone: it names no parents, carries
no Derived rationale and creates no requirement coverage. The save boundary refuses a new Standalone artifact in a
project that uses Requirements. It still accepts a revision of one that is already Standalone, and continues a
Standalone decision made before Requirements was switched on: the next revision of the package that proposed it, and
the revision its approved package materializes. In such a project, a Case or System test change request may be raised
on its own case (origin `OwnCase`) when there is no change request or Problem Report to raise it from. A new one is
refused once the project uses Requirements; its later revisions keep the first revision as their one origin. A
candidate baseline in a project without Requirements freezes with no change requests, records an empty requirement
manifest, and then materializes approved verification work as usual. In the browser, setup and Project
Configuration → Features accept the combination; the test change request editor raises a Case or System package on
its own case, and each proposal it or the package page writes is Standalone. Once Requirements is switched on, the
package page modifies a Standalone case as Standalone by default (Keep Standalone) and traces it when Allocated is
chosen; the traced revision is new and the Standalone one is never rewritten.

Without Requirements there is no requirement coverage, so each case shows its execution status instead (DEC-144):
passed, failed, blocked or not run, from its latest build-scoped result. The Test Procedure Explorer coverage report
lists it, and Release Readiness replaces the coverage gate with "Every case has passed", relaxed by the same assurance
lever. The change-control, impact, verification-impact, trace and code gates report Not applicable with the reason,
and the documents gate owes only the verification documents.
The artifact thread places a Problem Report linked to an execution or test change request it already shows (a report
raised from a failed run, or one a package answers for), so without Requirements it runs Cases → Procedures → Executions
→ Problem Reports, and lanes with no records stay closed.

Project entry uses stable identity and opens the actual visual build-lineage selector, including for a single
build. Explicit build links retain their exact authorized target. Repository setup can remain Pending and be
configured later; a server-observed GitLab connection is separate from controlled implementation evidence.
See [Architecture](product/docs/ARCHITECTURE.md) and [Operations](product/docs/OPERATIONS.md).

The creation surface has three server-owned starting paths: Fresh, an exact authorized AeroLink baseline, and
an external ReqIF, CSV, or XLSX source. Fresh creates only the empty structure required by the selected ladder
and leaves engineering content, approvals, executions, evidence, and staffing empty. Native inception accepts a
Frozen or Released source baseline with its exact project, baseline, revision, relationship, and supported source
facts. External inception stages the uploaded bytes, parser observations, selected categories, mapping, and
server reconciliation before materialization. The supported matrix is Requirements, Traces, Cases, Procedures,
and Evidence for native sources; Requirements and Traces for ReqIF; and Requirements for CSV/XLSX. Unsupported
or excluded objects and relationships remain explicit source findings; the service does not invent missing facts.

Every source path creates one new first build in **IN WORK**. A native or external source remains a distinct
historical source fact, and its approvals, executions, evidence, identities, and provenance are not recast as
new-project approvals or target executions. Materialized records retain the exact source identifiers and revision
links, with target parent/trace IDs recorded separately. Source acceptance is a password-confirmed electronic
signature by the current AeroLink administrator over the source hash, selected categories, mapping, reconciliation,
accepted ladder, manifest, and target build identity, including when that administrator resumes another creator's
draft. It is an acceptance of source provenance and materialization, not an engineering approval.

Creation drafts, staged uploads, and accepted answers survive sign-out, API restart, and resumed sessions. Draft
version tokens protect edits and finalization; the finalization operation records its result so a
duplicate request or a lost success response returns the same created identities. Creator and administrators can
resume drafts, while native source access is checked against current source membership at every source boundary.
The internal backing Program is allocated once and is not a user-entered Program name or code. Repository setup
can remain visibly Pending and be configured later; it does not claim a connection without a server-observed
verification. Project entry opens the actual visual build-lineage selector, including a one-build project, and
requires explicit selection.

This state describes the implemented product boundary; qualification, protected integration, and final acceptance
remain tracked in [issue #1037](https://github.com/AeroLinkDEV/requirements-management-tool/issues/1037). Ordinary
CSV/XLSX requirement proposal preview/commit remains a separate change-request workflow and must not be confused
with external inception. Recovery uses the supported backup/restore procedures for the database and retained draft
source bytes; it does not use ad-hoc SQL, reset/reseed, or an installer-wide recovery claim.

## Current requirements architecture

The normal software-oriented lifecycle is:

```text
System Requirement
    ↓
High-Level Software Requirement (HLR)
    ↓
Low-Level Software Requirement (LLR)
```

Exact revision identity and build/baseline membership matter. A current/latest project revision is not automatically the revision carried by a particular released/in-work build.

HLR proposals allocate to exact applicable System revisions, and LLR proposals allocate to exact applicable HLR revisions unless a governed derived classification/rationale applies. Downstream assessments are explicit controlled engineering work rather than browser-only notifications.

## Current verification architecture

The overloaded “software test procedure” model was replaced by an explicit Case → Procedure architecture through the #720 programme and related work (#722, #724, #725, #727, #728, #726), then surfaced as the normal user experience by #762 / PR #767.

### System

```text
System Requirement
    ↓
System Test Procedure (SYSTP)
    ↓
Execution / Result / Evidence
```

System remains a one-tier Procedure verification model.

### Software HLR

```text
HLR Requirement
    ↓
HLR Test Case (HLRTC)
    ↓
HLR Test Procedure (HLRTP)
    ↓
Execution / Result / Evidence
```

### Software LLR

```text
LLR Requirement
    ↓
LLR Test Case (LLRTC)
    ↓
LLR Test Procedure (LLRTP)
    ↓
Execution / Result / Evidence
```

For a software verification profile configured as `[Case, Procedure]`, the Procedure is the executable artifact. A deliberately configured `[Case]` profile remains valid and the Case remains executable there.

The ordinary Software verification UI is profile-aware rather than FMS-hard-coded. Full-profile projects expose HLR/LLR Case and Procedure change-control contexts and a unified build-scoped Test Case/Procedure Explorer. Case-only or partial profiles expose only their configured artifact keys.

### Controlled verification identities

| Level | Artifact | Artifact prefix | Change Request | Controlled document |
| --- | --- | --- | --- | --- |
| System | Test Procedure | `SYSTP` | `SYSTPCR` | `SYSTD` |
| HLR | Test Case | `HLRTC` | `HLRTCCR` | `HLRTD` |
| HLR | Test Procedure | `HLRTP` | `HLRTPCR` | `HLRTPD` |
| LLR | Test Case | `LLRTC` | `LLRTCCR` | `LLRTD` |
| LLR | Test Procedure | `LLRTP` | `LLRTPCR` | `LLRTPD` |

`HLRTD` and `LLRTD` are deliberate retained controlled Case-document identities. They are not cosmetic names to “clean up” without a governed historical-identity migration.

## Verification change control

Verification work is controlled through Test Change Requests (TCRs), downstream assessments, exact artifact revisions, controlled review workflows, and build-scoped materialization.

The vertical software chain is important:

```text
Requirement change
    ↓
Case assessment / Case change
    ↓
Procedure assessment / Procedure change
```

A Procedure is not made to directly own Requirement coverage merely to simplify a UI. Requirement coverage belongs to the Case layer and rolls downstream through exact Case ↔ Procedure relationships.

Approved verification changes materialize into controlled revisions and exact build membership. Execution/readiness uses the carried exact executable artifact, not a project-global “latest” revision.

## Change requests, reviews, and controlled editing

AeroLink supports governed System/HLR/LLR change requests and Test Change Requests with:

- explicit controlled identifiers/revisions;
- Draft authoring and controlled checkout/edit/check-in where applicable;
- source/current-build scope;
- rich engineering narrative;
- exact allocation/trace references;
- configurable sequential/parallel review workflows with frozen stage/authority/version context;
- attributable approvals/signatures over controlled snapshots;
- preserved returned/superseded history;
- separation between approval and actual build/release inclusion.

The Digital Thread accepts a stable Change Request identity and presents the server-composed exact Change Request/provenance projection as a visual, layered node-and-edge map or equivalent accessible table for the same active investigation. Network, Inside and Artifact representations retain their own authoritative projection/context, exact identities, typed direct relationships and hop-qualified indirect context; changing representation does not change the subject. The CR inspector exposes the selected record's direct upstream/downstream relationships and labels additional connected records as hop-qualified context, and it can open the exact selected CR thread. Exact routeable identifiers use native links; unavailable targets remain explicitly non-openable. The existing baseline-exact requirement → verification → result/evidence → build path remains an explicitly named Baseline evidence report with server paging, exact revision/artifact links and relationship lifecycle controls where authorized; its PDF/DOCX exports are baseline-scoped. Proposed Introduce/Modify/Retire content remains visibly separate from materialized, effective-baseline, and evidence truth.

Requirement, Case/Procedure and change-record inspectors share trace presentation while retaining their family-specific relationship authority. Requirement and verification inspectors supplement direct allocation/coverage facts with an exact-build intersection of the existing Digital Thread graph, exposing Case-to-Procedure and execution/evidence paths as direct or explicitly hop-qualified context. Suspect relationships remain visible and separate from confirmed coverage. Missing or mismatched exact trace responses are unavailable; historical reachability alone does not assert build applicability.

Digital Thread reads select a bounded typed frontier or exact build membership before loading evidence. A derived, indexed frozen-review lookup preserves reverse historical reachability without scanning unrelated snapshot payloads; original snapshots and hashes remain authoritative. Oversized rooted traces fail explicitly, while build-network cuts retain the existing truncation signal. See [the read contract](product/docs/DIGITAL_THREAD_READS.md).

For a non-root requirement change request, the controlled Draft also records either exact upstream change-request
revision link(s) or an attributable no-upstream rationale before review, using one picker during creation and editing. New links require an exact currently Approved or SelectedForBaseline revision. Deferred candidates remain visible but selection opens reassignment guidance and creates no link. Same-build direct-parent linkage from the
effective Project ladder is the normal path; an explicitly requested earlier-build link must target an exact signed
predecessor-build revision and retain its cross-build rationale. Assessment-derived upstream evidence remains owned
by its build-scoped downstream assessment, while the review snapshot freezes the exact assessment/link identity that
satisfied the gate. Historical review contracts continue to hash under their original versions.

Approved work does not become silently rewritten because a later revision exists.

## Team Work projection

The server exposes a project-wide, read-only Team Work projection across current Change Requests, numbered Test
Change Requests, Problem Reports, and downstream assessments. The projection owns the four canonical work lanes,
0..N current-holder obligations, release/allocation provenance, and canonical record-opening identity; consumers do
not reconstruct that lifecycle truth from roles or browser state. Review holders and Review-versus-Approval meaning
come from the frozen active `ApprovalStep` records, not current workflow configuration, base project roles, or
Project Leadership. Incorporated, withdrawn, closed, rejected, superseded, linked, and unnumbered records leave the
active projection under explicit family policy. The Team Work client workspace now provides the read-only,
project-wide three-lane lifecycle board (In Work, In Review, Approved), people strip, search/build filters, layer-first contextual artifact-type
filters, current-holder grouping, and canonical record links. Selecting a person replaces the current person filter
and keeps the board visible; current-holder detail is available only through a separate explicit action. Reusable
person avatars use repository-owned synthetic portraits where available and retain an initials fallback. People
ordering is workload-first within the current build/layer/artifact-type/search scope, with a bounded local
selection-frequency boost and stable account-identity tie-breaking; zero-work members remain accessible after
people holding work. Person workload counts follow that roster scope, while board totals follow the displayed
person-filtered scope. Project Leadership remains separate metadata and Review/Approval remain frozen
workflow-stage meanings. Holder identity is 0..N, including parallel obligations, and no write, assignment,
due-date, or age-in-state behavior is implied.

The middle displayed lane combines canonical Review and Approval obligations. Compact cards show all next-action
holders with their active stage purposes and truthful Assigned Build; routine metadata remains in expandable Details.
Digital Thread hover emphasises the directed connected story **without moving the view**: no pan, zoom, or density
change. Linked foreground cards, including clipped cards, receive temporary vertical displacement in their own
lanes when they fit, taking visual space over canonically unchanged background cards. Deterministic partial fit
keeps smaller fitting cards usable even when another candidate is oversized; truthful directional continuation
and deliberate keyboard, touch and scrolling preserve access to overflow. A single click selects the exact
record, retains and reconciles the displayed arrangement against measured heights and actual tray clearance,
and owns the thread until the reader
clears it or selects another; hovering cannot preview or replace a selection, the selected root may leave the
viewport by deliberate navigation, and clearing holds the current camera. The Show-ID strip and its additional
selected-state reservation are removed. Case and Procedure changes have distinct lanes, and opening a change
selects its exact subject on arrival (DEC-126, DEC-127 and the narrow foreground-reveal supersession in DEC-129).

## Problem Reports

Problem Reports are **Project-scoped controlled records**. Target build is an explicit attribute/filter rather than the record's ownership boundary.

The #765 improvement programme delivered a substantially richer Problem Report workflow through phase 6 / PR #774, including:

- correction/editing by appropriate Project members while preserving exclusive lease/history behavior;
- a meaningful fixed category vocabulary with provenance for migrated classifications;
- structured rich authored content and inline emphasis without storing arbitrary executable markup;
- a document-like whole-record create/edit experience with explicit Save vs Save-and-check-in semantics, natural
  image paste/drop, bounded resizing, responsive side-by-side image layout, and typed content persistence through
  revision and generated output;
- controlled supporting attachments with immutable versions, SHA-256 metadata, attributable add/remove/replace
  history, revision-frozen attachment manifests, authorized download, and generated-output manifest entries;
- impact/evidence improvements;
- controlled symmetric same-Project “Related Problem Reports” relationships visible from either report, with history and closure-candidate invalidation.

Problem Reports can drive governed change work; requirements changes do not manufacture a Problem Report merely because a change exists.

A Problem Report changes lifecycle state only when a person explicitly transitions it (DEC-133). Recording a passing
corrective result offers to send the report to SQA on that result; the engineer confirms it. A later
closure-significant change withdraws the closure basis and blocks SQA closure, but leaves the report waiting for SQA
until a person returns it to Verifying. Any rationale given on a transition is kept (DEC-132).
In a project that does not use Verification, the report is sent to SQA on an attested statement of how the
correction was verified instead of a test result (DEC-137); SQA still closes it independently.

Problem Reports can be imported from another tool's CSV/XLSX export (DEC-139): every row is previewed and the import is
signed; source keys, reporters, dates and statuses stay source facts, source-closed reports arrive read-only as Closed in
source, and an already-imported key is skipped on re-import.

Integrity source packages have a separate one-time import path (DEC-145,
[#1186](https://github.com/AeroLinkDEV/requirements-management-tool/issues/1186)).
The Java extractor seals a bounded source package; AeroLink verifies its member hashes, previews mapped
Problem Reports and date meanings, and signs the exact reviewed Create/Skip outcomes. Reports, immutable
source identities, reconciliation, signature and staged-file references publish in one transaction. The
original package remains retrievable only to Configuration Manager, Program Manager and Administrator
roles with project access and Problem Reports enabled. Source history, signatures and relationships remain
source evidence; they grant no native approval, closure, effectivity or relationship authority.

Fixture execution is supported in disposable hosts. **Live Integrity qualification remains pending**:
the adapter's SDK response profile, visibility, full history, attachment coverage and source-freeze checks
must be demonstrated on the customer's server. Unproven packages are refused. Historical attachment
retrieval is not implemented; a live capture cannot be marked qualified unless the administrator's
inventory and qualification evidence establish that no historical attachment bytes are missing.
See [the Integrity import contract and operator guide](product/docs/INTEGRITY_IMPORT.md).

Upstream change requests and exact Case origins provide inherited Problem Report context for downstream authoring. Authors explicitly select direct links under the existing project/build and review-version rules; source approval does not automatically copy those links into a new verification assessment. Refreshing source context does not change independently accepted links or frozen review history. Software Procedure packages retain their exact Case origin when secondary Problem Reports are explicitly linked.

## Baselines, builds, and release control

AeroLink separates several facts that must not collapse into one:

- a controlled revision being approved;
- that revision being selected/carried by a particular build/baseline;
- verification obligations for that exact carried configuration;
- the release campaign becoming ready;
- the authorized human release decision.

Released baselines/builds remain immutable. Successor/in-work builds are explicitly assembled under user/configuration authority; AeroLink does not silently create or approve later product baselines.

A project that does not use Release moves between builds by a signed decision recorded as released without
readiness evidence (DEC-138); it is never presented as a readiness-backed release.

Exact manifests/effectivity are authoritative for what a build carries.

For genuinely pre-manifest builds, the compatibility projection remains explicitly non-exact. If an older
released-build header has no release timestamp, its completed release campaign can supply the cutoff only
when it names that exact project, release and baseline and retains its release hash. This reads an existing
release fact; it does not create a manifest or rewrite historical metadata.

## FMS live showcase context

The FMS Product Development dataset remains the principal deterministic live demonstration context.

- Build/version 1.5 is the released immutable predecessor.
- Build/version 1.6 is the active in-work successor used to demonstrate current controlled development.
- The effective software verification profile supports the full HLR/LLR Case → Procedure model.
- Named deterministic scenarios provide representative lifecycle, later-revision, trace-branching, assessment,
  verification/evidence, Problem Report, review/approval, leadership, avatar, and distributed-work
  coverage. Interface change-control scenarios are deliberately not seeded: the FMS ladder configures
  `[System, HighLevel, LowLevel]`. The owner-directed #1006 operator correction removes the audited older synthetic Interface aggregates and their history after verified backup and disposable-copy qualification (DEC-125), superseding #889's earlier retained-history acceptance. Other projects and normal controlled history retain their deletion protections. Named upstream examples use the approved exact source of their baseline parent; invalid older links receive recorded correction without rewriting frozen reviews. Fresh showcase creation is
  rollback-atomic; upgrading an existing synthetic showcase is an explicit administrator action that
  requires positive target and backup confirmation and never runs during ordinary startup.

The showcase is synthetic demonstration data. It must not be confused with live customer/company controlled engineering data.

See [FMS Live Showcase Dataset](docs/showcase/FMS_LIVE_SHOWCASE_DATASET.md) and [FMS 1.6 Release Campaign](docs/showcase/FMS_1_6_RELEASE_CAMPAIGN.md).

## FMS Test Bench (CMA-9000 CDU)

ACT RTE BACKTRACK records and reverses flown waypoint history, with reviewable MOD/ERASE/EXEC, immutable coordinates and attributes, special-procedure exclusions, retained SAR/hold origins and the manual default airborne option. Ground operations remain deferred under [DEC-148](DECISIONS_AND_OPEN_QUESTIONS.md#dec-148---helicopter-bench-ground-operations-follow-v1). The synchronized activation clears both computers' prior history; this remains session-level simulator behavior, with [applicability boundaries](product/docs/FMS_APPLICABILITY.md#flown-history-backtrack).

The helicopter bench consumes NOAA WMM2025 for MAG/TRUE angular entry and display, with model/epoch/checksum identity, a real simulator package loader, CRC-failed navigation withdrawal and a distinct age advisory. FMS cold/warm power and the preflight page flow are available separately from receiver power. GPS navigation refuses manual position/time replacement; pressure-altitude QNH is entered on POS INIT or VNAV. Aircraft geometry and wind remain true. The generic displays and simulator package are not installed CMA interfaces; height fallback, extrapolation and startup assumptions are documented in [FMS_APPLICABILITY.md](product/docs/FMS_APPLICABILITY.md#magnetic-reference-and-initialization).

Every project has an **FMS Test Bench** (`…/fms-test-bench`): a photorealistic, touchable CMA-9000 FMS control
display unit for engineers.

- **Target aircraft ([DEC-146](DECISIONS_AND_OPEN_QUESTIONS.md#dec-146---the-fms-test-bench-targets-the-helicopter-cma-9000-first), [DEC-147](DECISIONS_AND_OPEN_QUESTIONS.md#dec-147---the-helicopter-bench-targets-a-full-civil-sar-configuration)).**
  The bench simulates a rotorcraft CMA-9000, with the helicopter operational program S/W 169-614876-300 as its
  behavioural baseline. The aircraft profile is versioned data (`product/client/src/fmsCdu/profile.ts`, named with a
  fingerprint in every run report), and which source governs each behaviour is recorded in
  [`product/docs/FMS_APPLICABILITY.md`](product/docs/FMS_APPLICABILITY.md). The civil SAR profile separately records
  target options and their implemented, partial or pending status in `configuration.ts`, with all 87 optional-function
  occurrences mapped to explicit dispositions. Run reports identify both active parameters and remaining target gaps.
  The controller consumes its selected computer's declared limits; join/hold geometry and FMS commands share the bank
  envelope, with corrections saturated inside it. Coordinated flight remains a point-mass model with representative
  helicopter low-speed/hover modes; it is not an installed aircraft's flight-dynamics model. Military tactical
  approaches are unavailable under the civil configuration.

- **Civil measured navigation.** Timestamped sensor ports separate the FMS estimate from aircraft truth. GPS,
  measured DME/DME and VOR/DME feed the estimate; DR uses heading, TAS and last-valid computed wind. Stale input
  cannot navigate. S300 uncertain GPS retention is separate from stronger approach/hover authority. Predictive RAIM
  and its PRN exclusions use visibly simulated sky geometry; real almanac prediction and physical avionics adapters
  remain unavailable. The applicability matrix records radio, uncertainty and freshness assumptions.

- **Helicopter IFR procedures.** The subset reader imports HD/PD departures and PI, RF and AF geometry. The reviewed
  HUDSN ONE chart separates its VFR site-to-IDF segment from instrument guidance at the coded crossing altitude.
  S300 approach phase requires arming, the FAF path-distance gate and current/predicted integrity; loading a procedure
  grants neither NPA nor approach RNP. A MAP holds the final-course extension until the crew requests the missed
  approach or declares conditions for its verified Proceed VFR/Visually continuation. These declarations establish
  no computed weather, obstacle protection or landing clearance.

- **S300 advisory VNAV and separate SBAS profile.** The default helicopter uses barometric advisory approach VNAV,
  with runway/FAF construction, validity gates, temperature MOD/EXEC and crew QNH/MDA. It commands no vertical
  autopilot mode. The default KBTV mission is LNAV with advisory VNAV and crew VS; coupled LPV and its fault missions
  explicitly select the representative later-CMA/CMA-5024 profile, whose exact OEM installed baseline remains unqualified.
  Established S300 finals retain steering for five minutes after integrity-only loss; invalid position or HDOP over 4
  withdraws it immediately. The applicability matrix identifies the temperature/QNH models and adapter validity limits.

- The faceplate is rendered in Blender from `product/tools/AeroLink.FmsCduModel`, which uses the public CMC
  datasheet dimensions and the Operator's Manual front-panel figures. No manufacturer logo is shown.
- The user selects one of nine hardware variations. Each variation relabels the seven annunciators and the second
  function row; the faceplate itself is shared.
- The display is a live 14 × 24 AMLCD grid with the manual's colour conventions. The keys work by mouse, touch
  (including CLR held for one second) and the physical keyboard.
- Today a **scripted simulation** drives the display (`product/client/src/fmsCdu/scriptedFms.ts`). It implements
  the manual's rules for scratchpad entry, CLR/DELETE, line select entry and copy, MOD/ACT with EXEC and ERASE,
  paging, BRT and the MSG and EXEC annunciators. It is labelled as a simulation and is not a navigation computer.
  Its scope is measured against ICAO PBN and airline practice in
  [`product/docs/FMS_TEST_BENCH.md`](product/docs/FMS_TEST_BENCH.md), whose capability register holds each
  function's status (demonstrated, partial, placeholder, not implemented) and the open findings of the
  27 September independent review. It is a demonstrator, not an oracle for software under test. It models:
  - **Flight planning** from a navigation database (`navData.ts`): airports and runways, navaids, fixes, airways,
    and SIDs, STARs and approaches with transitions and missed approaches. The route is built through RTE (VIA/TO
    airway entry, company routes, SAVE), DEP/ARR, pilot waypoints (latitude/longitude, place/bearing/distance,
    place-bearing/place-bearing, along-track), SELECT DESIRED WPT for duplicate idents, REF NAV DATA and a
    secondary flight plan. The built-in data is invented demonstration data (the default start); a real FAA CIFP
    extract for KBTV (cycle 2609, public domain, for demonstration only, not for navigation) is bundled as a one-action
    demonstration with a start state and library scenarios for its RNAV (GPS) RWY 15. Engineers can load waypoints,
    navaids, airports, runways and airways from an ARINC 424 file (`arinc424.ts`, a subset reader). A file is
    validated first (refused whole, with no change, when empty, not recognised or holding an impossible coordinate)
    and becomes the inactive database cycle. Activating it on IDENT is recorded and does not move the active plan,
    whose fixes are pinned when it becomes active; they re-resolve only when the crew executes a modification.
  - **Lateral guidance**: automatic leg sequencing with fly-by and fly-over turns; ARINC 424 path terminators
    (TF, CF, DF, RF/AF arcs, PI construction, and the CA/FA/VA, VI and VM/FM conditional legs); DIRECT-TO with INTC CRS and ABEAM
    PTS; holds with their standard entry and status (including a one-turn exit); a lateral offset flown between
    start and end waypoints; search patterns flown along their geometry; and selected heading versus LNAV with
    arm and capture.
  - **Navigation sensors** (`civilNavigation.ts`): GPS, measured DME/DME and VOR/DME, and heading/TAS/wind DR in the civil priority
    order with automatic reversion; the FMS estimate advances independently of the true position in dead reckoning and shifts
    back when a sensor returns (POSITION SHIFT); ANP from the sources, RNP by phase of flight or crew entry, and
    CHECK ANP after the phase's time to alert; NAV STATUS and NAV OPTIONS (navaid inhibit, GPS deselect). IRS is not
    configured and is not reported as available. Radios acquire facilities and deliver measurements through the sensor port.
  - **Vertical guidance**: under the default helicopter profile the crew flies the vertical axis and the speed
    through the autopilot (ALT SEL, VS, ALT, SPD, and GA on TOGA), and the FMS constraints are advisories; the
    airline-style VNAV below is a selectable laboratory profile (`lab-airline-vnav`).
  - **Approaches and VNAV** (`vnav.ts`): the approach type (ILS, or RNAV to LPV minima with GPS integrity), ARM
    APPROACH, NO APPR INTEGRITY and go-around; speed and altitude constraints (at, at or above, at or below,
    windows, flight levels); a vertical profile with top and end of descent, climbs that level at constraints,
    UNABLE NEXT ALT, DES NOW and the VNAV descent path; winds; ETA and fuel predictions with FUEL RESERVE and
    NOT ENOUGH FUEL; cold temperature correction.
  - **Tactical functions**: RENDEZVOUS (arrive at a waypoint at a time, flying the required speed within crew
    limits, with RENDEZVOUS UNACHIEVABLE), moving waypoints that advance on a track and speed, and the tactical
    descent (TDN) at a computed angle to a target altitude before a reference point, which levels there until
    cancelled and is refused as TDN NOT POSSIBLE above the maximum angle.
  - **Database cycles, maintenance and dual operation**: IDENT shows the active and inactive navigation database
    cycles with their effective dates and swaps them; past the active cycle's end the FMS raises DATABASE OUT OF
    DATE; REF NAV DATA defines idents in a temporary database; a loaded ARINC 424 file becomes the active cycle.
    The MAINT page runs a self test that fails while a fault is present and keeps a fault log. Two actual simulator
    computers/CDUs share one physical aircraft through modeled cross-talk. SYNC has one MOD editor and transfers
    EXEC; independent crossfill arrives as receiving MOD/EXEC. SETUP mode confirmation, sourced refusals,
    measured 100 m source hysteresis and more-than-30-second phase disagreement are modeled. Link/power recovery
    requires crew synchronization. Shared civil RMS tuning uses device feedback independently of cross-talk;
    installed radio, RF and discrete interfaces remain partial. See the two-computer section in FMS_APPLICABILITY.md.
  - **Sensor fault laboratory**: the Conditions tab applies typed laboratory receiver, ground-station and measured
    navigation-input stimuli through the same dispatcher as authored scenarios and recorded replay. Physical radio
    and station faults are shared independently of cross-talk; station components resolve against the active database
    and unavailable or ambiguous targets fail closed. Navigation TAS/heading validity and heading bias do not change
    aircraft/AFCS truth. Recorded power targets FMS1 and models the C2 KALMAN interruption rule; GPS pair presets
    override integrity or position words separately. Reports retain applied time, value and laboratory source.
    The default profile refuses external radio-head stimuli (DEC-150). These are simulator controls, not installed
    RF, AFCS sensor-loss or hardware acceptance evidence.
  - The earlier pages: HOLD, the SQUARE, LADDER and SECTOR search patterns, the tactical approach, HOVER and
    TIMER. The ATC, FMC COMM and GSM/SMS pages are representative only (no datalink) and say so on screen.
- A **flight simulation** (`flight.ts`) flies the active route as an FMS-coupled autopilot would, in real or
  accelerated time. It is a point-mass model with a bank-limited turn, not a flight dynamics model: the aircraft flies a heading
  through the air, the wind carries the air mass, and the track and ground speed are the vector sum (no speed floor;
  predictions without measurable progress are unknown). Pause is a
  position freeze: the clock keeps running, so timers and the self test still complete. A north-up engineering
  **navigation map** (route, holds, patterns, offset track, navaids and airports, and the true position when the
  FMS has drifted) can replace the ND beside the CDU. The flight controls, flight mode annunciator and guidance
  readout sit under the displays; the scenario, condition, data, alert and lighting cards sit below.
- An **EFIS** beside the CDU draws a generic primary flight display and navigation display from an explicit FMS output bus
  (`efis.ts`: desired track, cross-track, vertical deviation, roll command, distance to go, targets and modes, each
  with a normal, no-data or failure status) plus the aircraft's attitude and air data. It uses airline colour
  conventions and the flight mode annunciator from the real mode state. A failed FMS removes its data and flags FMS
  FAIL and MAP. It is generic, not a CMA installation's EFIS; the bus is where an FMS under test would drive the
  displays later.
- The bench injects conditions that light the variation's annunciators and change the pages (FMS failure, GPS
  loss, GPS integrity loss, DME outage, forced RNP or NPA, offset, independent operation, GSM, SMS, ATC uplink,
  radio transmit, subsystem request) and raises any alert from the manual's alert message list. The POS, RNP and
  NPA annunciators follow the navigation state.
- The bench has Day, Night and NVG cockpit lighting: backlit key legends (NVIS green for NVG), NVIS-compatible
  annunciators, and display luminance from the light sensor (an ambient-light control) combined with BRT; NVG
  holds the display between 0.1 and 3 fL.
- The panel talks only to the `CduBackend` interface, so the real CMA-9000 operational program can replace the
  simulation later.
- **Scenarios** (`scenario.ts`) script a test on the bench: ordered steps triggered at the start, at a time, within
  a distance of a waypoint or when a waypoint becomes active, which press keys, inject conditions, raise alerts,
  select procedures, arm the approach or go around, and check screen lines, the scratchpad, alerts, annunciators and
  the active waypoint (optionally waiting a number of seconds). Built-in scenarios cover GPS lost before the final
  approach fix, GPS integrity lost on the approach, dead reckoning, and a crew RNP the navigation cannot meet. A run
  restarts the simulation and shows each step's result live. Time moves in 0.25 s ticks (clock, flight, then the
  scenario's observation), the same in the bench at any rate as in headless tests; a step due between ticks runs at
  the next one, nothing runs after the time limit, and a check met after its `within` window fails. A scenario is
  validated before it runs, and a run ends passed, failed, no checks, timed out, stopped, invalid or execution
  error: only a run whose checks all held is a pass. Pausing during a run stops its clock. The bench records a scenario from keys, conditions,
  alerts, APPR, TOGA and screen-line checks, and saves or loads scenarios as JSON. A scenario is written out as the
  fields of an AeroLink test procedure proposal to copy into a procedure change, and a run as a Markdown report
  marked as simulation evidence; the bench does not change controlled procedures or record evidence itself.

## Documents and publications

AeroLink supports controlled generated publications over structured artifacts and a Managed Documentation Center for externally authored Word documents.

Generated outputs are derived from controlled data/templates/effectivity and carry provenance rather than becoming independent masters. Managed Word documents retain their controlled DOCX/PDF candidates/revisions while Word remains the authoring application.

Publications for projects created through setup identify the user-facing Project; their isolated internal backing
scope is not a cover or document-control label. Existing project labels and retained source manifests remain unchanged.

See [Controlled Document Publication Standard](docs/product-definition/CONTROLLED_DOCUMENT_PUBLICATION_STANDARD.md) and [product/docs/MANAGED_DOCUMENTATION_CENTER.md](product/docs/MANAGED_DOCUMENTATION_CENTER.md).

## Identity, security, and audit

The product includes local identity/session controls, scoped roles/administration, MFA/recovery support, delegations, secure approval/signature behavior, and security/audit records appropriate to the current on-premises product foundation.

Deployment-specific federation/provider/TLS/monitoring/service-objective work remains dependent on a real deployment/customer contract where documented.

## Project authority: base roles, Project Leadership, and review workflow authority

The #816 programme split project authority into two separate facts that must never collapse again:

- **Base project roles** are jobs/eligibility many people may hold on a project (System Engineer, Software Engineer, System/Software Test Engineer, Project Engineer, Program Manager, Engineering Manager, Configuration Manager, Software Quality Assurance, Airworthiness). Holding one grants the job's own authority and nothing more.
- **Project Leadership** is a separate concept with exactly eight accountable positions (Project Engineer, Program Manager, Engineering Manager, Configuration Manager, and the four discipline leads). Each position has at most one current primary holder and one standing backup; the backup carries the same live position authority while the designation is valid. **Base-role eligibility is the qualification for a position, never the position's authority.**
- **Project Engineering Lead** and the old singular position roles are retired; their rows remain readable history and their accountability lives on the Project Leadership positions now.
- **Reviewer and Approver are not assignable jobs.** They are not offered as Personnel roles, not newly grantable as memberships, delegations, or standing backups, and not modern workflow authorities. They survive in the enum and in historical rows as compatibility data only.

A review workflow stage records two independent facts:

- the **required project authority**, represented explicitly as either `BaseRole` (a base project role) or `LeadershipPosition` (an accountable position, answered by its current primary and valid standing backup); and
- the **signature meaning**, which comes only from the stage's `ReviewStageKind` (`Review` or `Approval`) and never from a person's roles.

Workflow stages recorded before this cutover carry no authority kind: they remain readable through explicit legacy-compatibility semantics and are never reinterpreted under today's vocabulary. New and revised workflow configuration must be explicit, the server refuses legacy/ambiguous writes, and all workflow authority resolves through the one central effective-authority resolver so the candidate picker, the signing gate, and the audit record answer identically. Each newly assigned review step freezes the exact authority source and source-row identity, and the resulting electronic signature copies that provenance together with the frozen workflow, stage, cycle, position, and Review/Approval meaning. Historical rows with no recoverable source remain explicitly null rather than receiving fabricated provenance; historical and in-flight review workflow versions stay frozen and are not rewritten under current terminology.

See [Security and Identity Model](docs/product-definition/SECURITY_AND_IDENTITY_MODEL.md).

## Interchange and integrations

AeroLink includes governed import/export/interchange foundations such as CSV/XLSX onboarding, ReqIF-related workflows, versioned API behavior, service identities, webhooks/integration foundations, and external-system linking. Interchange must preserve provenance and must not bypass controlled change/review merely because data arrived from another tool.

The persistence write path is intentionally asynchronous and phase-ordered. `AeroLinkDbContext` retains the
model and final EF write while state repair, complete integrity validation, and lifecycle/outbox preparation are
cohesive internal save phases. The authoritative ordering, provider-read and transaction boundary, failure/tracked-
state condition, retry contract, and bounded child-state lookup rule live in
[product/docs/SAVE_BOUNDARY.md](product/docs/SAVE_BOUNDARY.md).

Routine change-request reads choose an explicit child-graph load contract so detail and command paths do not
materialize every controlled history collection; the provider-specific split-query and snapshot rules are
authoritatively documented in [product/docs/CHANGE_REQUEST_LOADS.md](product/docs/CHANGE_REQUEST_LOADS.md).

## Operations and recovery

Operational backup retention keeps at most one complete restore point per database per local day for 15 days. Ordinary repository CSV exports have a seven-day download lifetime and automatic file cleanup; controlled publications and engineering history retain their existing lifecycle. Disposable browser tests own separate database and evidence storage.

The repository provides stable Windows root launchers for development, production-style local operation, shared/remote demo modes, backup, restore validation, diagnostics, and related operator actions.

Those root launchers are intentionally treated as compatibility surfaces; their real logic generally delegates into `product/scripts`.

The normal persistent developer/demo PostgreSQL database uses port **54329** and is not disposable qualification state.

There are three supported operating modes, and they are deliberately independent of each other:

- **HOME canonical / production** — `START_AEROLINK_PRODUCTION.bat` on HOME, running from a **dedicated
  production source checkout** against the HOME canonical database.
- **Work-laptop local development** — `START_AEROLINK.bat` on the laptop, running that laptop's own
  checkout on any deliberate branch against that laptop's own database.
- **Protected remote demo** — `START_AEROLINK_REMOTE_DEMO.bat` on HOME or its recovery task, from the same
  dedicated production source. A remote-demo browser session is a view of HOME; the work-laptop repository
  and database are irrelevant to it.

A checkout is **source**; the persistent PostgreSQL cluster, evidence, attachments and backups are an
**installation** it points at. An ordinary clone is its own installation (`product/.local`, unchanged); a
checkout carrying `product/.local/installation.json` uses the installation that names. This is what lets
HOME have a second checkout without acquiring a second AeroLink, and it fails closed rather than falling
back — a dangling pointer is refused, because the fallback is a healthy, empty installation holding none of
the operator's data.

An installation may declare its own identity (`instance.json`), which the API publishes at
`/health/identity` and the client shows beside the wordmark. Canonical status is declared, never inferred
from the hostname. `/health/identity` also carries the source SHA and launcher mode, which is what lets a
launcher tell a matching process from a stale one — readiness alone never could.

HOME production's instance badge also reports main currency and check age. The existing source controller
records its remote observation beside the dedicated-source marker; the API reads that observation passively
and binds it to its running source identity. A failed, missing, mismatched, or more-than-30-minute-old
observation is Unverified. Browser status refreshes cannot fetch Git or trigger deployment; the production
reconciler retains its 30-minute schedule and explicit remote-demo Start reconciles before READY, except that
Monday to Friday 08:00-18:00 Eastern both hold the revision on disk and only a manual redeploy request
(`REDEPLOY_AEROLINK_PRODUCTION.bat`) advances it ([DEC-149](DECISIONS_AND_OPEN_QUESTIONS.md#dec-149---production-redeploys-only-on-request-during-work-hours)).
Merging and `main` are unaffected.

HOME production transitions preserve the prior protected-tunnel ON/OFF state under the initiating policy.
Exact runtime reuse skips rebuild and PostgreSQL startup. A transition is carried by **one outer authority** -
the caller's own process - which qualifies its launch context before touching anything, admits the attempt only
when every prior attempt is proven quiescent, contains delegate work in one kill-on-close job with a completion
witness, and obtains each surviving service (PostgreSQL, API, tunnel) by launch request with an explicit stdio
handle list. The outer re-verifies every required role against the exact registered instance and reconciles the
delegate's real exit status before it reports success; the shared installation lease is released last. An owed
origin is supplied before API startup and attributed to the new listener before protected-tunnel restoration.
Incomplete restoration is a failed transition even if local service remains available, and recovery runs only
when the failed attempt is proven quiescent. A scheduled transition refuses before teardown unless its exact
launch context is qualified; the supported entry points are the installed tasks and the repository `.bat`
launchers, while an unidentified PowerShell/Terminal launch is refused (see OPERATIONS.md). Managed process
creators establish account-SID Windows access across S4U and interactive logons, and stops verify executable and
process start identity. Legacy installations have an explicitly approved one-time elevated setup; ordinary
subsequent operation is non-admin.

Database upgrade posture is answered before a web server starts, by a maintenance mode of the application
host that reuses the same migration authorities startup runs. A deterministic upgrade is backed up and
validated on an isolated restored copy before the real database is touched; a modelled controlled-data
conflict is reported in seconds with the affected records and the supported operator decisions, and
AeroLink makes no authority decision itself.

See [product/docs/OPERATIONS.md](product/docs/OPERATIONS.md) and [docs/REMOTE_DEMO_OPERATOR.md](docs/REMOTE_DEMO_OPERATOR.md).

## Testing and quality gates

AeroLink has substantial Domain, Infrastructure, API, browser, production-browser, PostgreSQL, operator/recovery, and generated-contract coverage.

The repository uses:

- fast/advisory development feedback;
- a merge-ready full Product quality gate on the exact candidate SHA;
- changed-area/test-planning logic shared between local and CI workflows;
- sharded API/browser work where measurement justified it;
- durable failure diagnostics and exact-SHA provenance expectations.

### Merge-queue cutover status

The canonical GitHub repository is `AeroLinkDEV/requirements-management-tool`; established local checkout
paths did not change because of that ownership transfer. Issue #549 / PR #911 supplied the repository-side
trusted merge-queue verifier and App-bound check publisher. The queue is **active**: the repository-scoped
GitHub App is installed only on this repository, its private key is stored in the main-only
`merge-authority` environment, and the active `main` ruleset has no bypass actors. The legacy classic
required-status block, including strict "require branches to be up to date", is removed; classic administrator,
pull-request, no-force-push and no-deletion protections remain enabled.

The ruleset requires both the App-bound `Trusted merge-queue binding` and GitHub Actions'
`Full Product evidence aggregate`: the native check enters a non-success state as soon as a rerun is queued,
while the App check independently binds the exact evidence after protected-default-branch verification.
This documentation-only delivery establishes the single-entry acceptance path by passing pull-request
readiness and then landing through the exact composed queue candidate after the authority-maintenance fix.
Multi-entry composition, stale-base, deliberate-failure and non-cancellation scenarios remain tracked on
issue #549 until their evidence is recorded.

Do not change CI topology from intuition alone. Read [product/docs/BROWSER_AND_BACKEND_FEEDBACK_TIME.md](product/docs/BROWSER_AND_BACKEND_FEEDBACK_TIME.md) first.

## Important current boundaries / limitations

- No certification/tool-qualification claim.
- No AI product feature.
- No automatic external test execution.
- Deployment-owned services such as customer TLS/reverse proxy, real SMTP/provider qualification, protected off-device backup storage, external monitoring/alerting, customer RPO/RTO/SLOs, and identity-provider contracts remain deployment-specific where not otherwise implemented.
- Scale/performance claims must match measured evidence; do not turn database-client or synthetic-harness evidence into a broader browser-user claim.
- Legacy information is not given fabricated historical precision merely because today's schema is richer.

## Recent major architectural milestones

This is intentionally short. For the narrative history, use [docs/PROJECT_HISTORY.md](docs/PROJECT_HISTORY.md).

- System-level controlled lifecycle and released-baseline foundation.
- Software HLR/LLR controlled requirements and downward assessment model.
- First-class verification/TCR/results/evidence workspaces and build-scoped readiness.
- August audit/remediation hardening of exact history/effectivity/stale selection.
- Measured testing-efficiency/CI and concurrent-agent safety improvements.
- #720–#728 Case → Procedure software verification architecture.
- #762 / PR #767 unified normal Case/Procedure software UX.
- #765 phases 1–6 culminating in PR #774 richer Project-scoped Problem Reports and Related Problem Reports.
- #778 repository knowledge/hygiene programme.
- #816 Slices 2–7: Project Leadership and standing-backup authority, Personnel and workflow cutover, email/shared-access operability, ladder-aware assessment visibility, frozen approval-step/signature provenance, and disposable integrated SMTP-to-authenticated-signature acceptance.

## Live backlog and active work

**GitHub Issues are the live backlog authority.**

This file deliberately does not say “there are N open issues” or “issue X is the only open issue”; those statements age immediately. Refresh GitHub when deciding what remains to be done.

## Where to go next

- Repository/operator front door: [README.md](README.md)
- Coding-agent safety: [AGENTS.md](AGENTS.md)
- Accepted decisions/open questions: [DECISIONS_AND_OPEN_QUESTIONS.md](DECISIONS_AND_OPEN_QUESTIONS.md)
- Documentation map: [docs/README.md](docs/README.md)
- Durable product definition: [docs/product-definition/](docs/product-definition/README.md)
- Reference material: [docs/reference/](docs/reference/README.md)
- FMS showcase guidance: [docs/showcase/](docs/showcase/README.md)
- Source provenance: [docs/provenance/](docs/provenance/README.md)
- Major history: [docs/PROJECT_HISTORY.md](docs/PROJECT_HISTORY.md)
- Lessons learned: [docs/ENGINEERING_LESSONS.md](docs/ENGINEERING_LESSONS.md)
- Technical architecture: [product/docs/ARCHITECTURE.md](product/docs/ARCHITECTURE.md)
- Operations/recovery: [product/docs/OPERATIONS.md](product/docs/OPERATIONS.md)
- Merge workflow: [product/docs/MERGING.md](product/docs/MERGING.md)
- CI feedback-time evidence: [product/docs/BROWSER_AND_BACKEND_FEEDBACK_TIME.md](product/docs/BROWSER_AND_BACKEND_FEEDBACK_TIME.md)
- Live scoped work: GitHub Issues/PRs

When product architecture materially changes, update this file in the same PR. Do not turn it into a chronological handoff; put history in `docs/PROJECT_HISTORY.md` and active work in GitHub.
