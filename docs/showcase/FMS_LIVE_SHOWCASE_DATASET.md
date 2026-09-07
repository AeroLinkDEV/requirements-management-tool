# FMS Live Showcase Dataset

> **Dataset contract, reconciled 2026-08-01.** Exact released Build 1.5 counts remain deterministic. Build
> 1.6 values below describe the initial idempotent seed; the persistent demonstration database now also
> contains legitimate engineering records created through live workflows. Present-tense seed descriptions
> below should be read as initial conditions. See [PROJECT_STATE.md](../../PROJECT_STATE.md) for the current product
> checkpoint; the former [2026-08-10 handoff](../archive/CURRENT_PRODUCT_HANDOFF_2026-08-10.md) is retained
> as historical restart context.

## Purpose

The FMS live showcase is a deterministic, production-shaped program used for demonstrations, continuing development, regression testing, and performance validation. It uses the same domain and persistence rules as user-created programs and is not a disconnected mock-data layer.

The showcase coexists with the clean **Create a new program** workflow. Its program code is `FMSLIVE`, and repeated generation is idempotent.

## How the dataset is entered

After authentication, the user selects **FMS Product Development** on the Projects page and then chooses a
Software Build. Build 1.5 opens this released dataset as a read-only historical workspace. Build 1.6 opens the
in-work successor and scopes normal queries and mutations to that release. Builds 0.5 and 1.0 are lineage-only
placeholders. Historical Build 1.5 evidence may appear inside 1.6 when clearly labelled, but it never switches
the active workspace. A final **Plan next build** card is a non-record placeholder and creates no future release
or version. See DEC-070 and DEC-088.

## Released FMS 1.5 baseline

| Controlled content | Exact count |
|---|---:|
| System requirements | 150 |
| High-level software requirements (HLRs) | 400 |
| Low-level software requirements (LLRs) | 700 |
| Effective requirement revisions | 1,250 |
| Historical system SRCRs | 30 |
| Historical HLRCRs and LLRCRs | 75 |
| HLR-to-system trace links | 400 |
| LLR-to-HLR trace links | 700 |
| Test procedures | 515 |
| Test executions, including retained retests | 520 |
| Controlled document records | 6 |
| Released software builds | 1 |

Requirement identities are stable and revisions are immutable. Requirements at revision `.01` or `.02` include retained superseded revisions. Every effective requirement revision is included in the exact FMS 1.5 baseline and has at least one version-aware test-procedure coverage link.

**Two of those links do not currently count.** `SYSTP-000040` carries an FMS 1.6 draft revision alongside its approved one — an in-work procedure change — and coverage settles only when the procedure it names has no revision in flight. The two system requirements that procedure covers therefore read **Suspect** rather than Covered, which is what lets the showcase demonstrate the product finding a verification gap at all. Released FMS 1.5 is untouched by this: the approved revision, its coverage links, the baseline, the build, the executions and the controlled documents are all exactly as they were, and the counts above are unchanged because `Test procedures` counts procedures rather than revisions.

No **Uncovered** requirement is seeded, deliberately. Reaching one would mean either stripping coverage from a released requirement — a released baseline that failed its own coverage gate — or materializing the FMS 1.6 baseline, which would discard the `WaitingForPrerequisite` position DEC-066 exists to demonstrate. Uncovered appears as soon as somebody materializes 1.6, which is the honest way to show it. See DEC-068.

The released build is `FMS-1.5.0-RELEASE`. It references the frozen and materialized FMS 1.5 baseline, whose SRCR manifest and effective-requirement manifest have independent SHA-256 hashes.

## Allocation and verification

- Each HLR derives from an exact system-requirement revision.
- Each LLR derives from an exact HLR revision.
- Trace links are typed and version-aware.
- Test coverage links reference exact requirement and procedure revisions.
- One test procedure may cover multiple requirement revisions.
- Initial failed executions and their passing retests remain independently visible.

## Controlled outputs

The baseline records a SYSRD, HLR SWRD, LLR SWRD, System Test Procedures, HLR Test Procedures, and LLR Test Procedures. Each record identifies its exact release, baseline, revision, artifact count, generation time, and content hash.

The Code center seeds a deliberately small sample rather than inventing hundreds of GitLab MRs, but the sample
no longer decides the gate. Every build owes implementation evidence for exactly the LLR revisions it
introduced or modified. Build 1.5 is the originating build, so it introduced all 700 LLR revisions in its
baseline and owes evidence for every one of them; five labelled demonstration mappings stand against that
total. Build 1.6 owes evidence for the LLR revisions its own change requests alter, once its baseline is
materialized.

A released build carrying almost no code evidence is what adopting AeroLink mid-life actually looks like: the
code for 1.5 was written before anything recorded the link. Every seeded mapping is labelled demonstration
data, and GitLab is identified as the source of truth for code and merge content.

## Active FMS 1.6 development

At initial seed, FMS 1.6 begins from the FMS 1.5 predecessor baseline and contains eight controlled change
requests — the original System/HLR/LLR work packages:

- 2 Selected for Baseline, 1 Approved, 1 In Review, 3 Draft, 1 Deferred, and 0 Withdrawn

The seeded changes include a new system-level oceanic round-robin function and representative HLR and LLR
modifications. Subsequent realistic testing may add approved,
returned, deferred, or draft records; these are persistent product data, not seed drift. The FMS 1.6 workspace
remains editable and is not released.

The seed no longer creates Interface change-control scenarios (#889): the FMS ladder configures
`[System, HighLevel, LowLevel]`, and records at a level the project does not configure forced every
ladder-shaped consumer — the Digital Thread change network first among them — to explain work it must not
present. An installation upgraded by an older seed closes those scenarios out through the explicit
`interface-scenario-retirement` showcase upgrade step: open and approved scenario work is withdrawn under
its own author's identity, a draft-baseline selection is reversed through the baseline aggregate, and a
selection already frozen into a baseline — like every approval, review, audit event, and requirement change
— is retained as controlled history. Nothing is deleted.

### Problem Reports and work distribution

The deterministic FMS seed also carries eight Problem Reports: six historical records target released Build 1.5
and two records remain against in-work Build 1.6 (one Verifying and one Rejected). Every one has an authoritative controlled `BuildScope`
link to its target release, so build work lists and the report centre answer the same scope question. They
intentionally cover Draft, Open, Implementing, Verifying, Waiting for SQA closure, Closed, and Rejected states,
with Task, Improvement, Code, Requirements, Test, and Environment categories. The two governed verification
scenarios target Build 1.5 and carry a real failed-execution → passing-retest predecessor chain, a controlled
`ResolutionVerification` link, immutable `ResolutionVerified` history, and closure candidate evidence; the
closed scenario also carries a frozen `ClosureApproved` package. Responsible work is distributed across five
eligible synthetic engineers/test engineers; the broader identity seed includes additional project members with
no current obligations, so Team Work can show both populated and zero-obligation people without inventing
ownership.

Problem Report scenario ownership is recorded durably in one immutable
`ShowcaseUpgradeStep` mapping per scenario key; its detail is the exact created artifact GUID. The visible FMS
marker in narrative text is only a display breadcrumb and is never used to locate or mutate a controlled row,
so user-authored content that happens to contain the same text is safe. The retired Interface scenarios were
located the same way, which is what lets the retirement step remove exactly what the seed created and nothing
else. Scenarios prefer the deterministic
`86601` number range; if a legitimate record already occupies a preferred number, the next free number is
selected. The upgrade marker is written only after all scenario postconditions pass. An interrupted or
incomplete enrichment remains unchanged at startup and is retried only by the explicit, administrator-only
`POST /api/showcase/upgrade` operation after the operator has verified the target database and backup.

On a fresh demo-data startup, the seeded identity directory is created before FMS controlled closure evidence
is frozen, and one database transaction creates the complete FMS dataset or rolls it all back. That transaction
grants only current job/base-eligibility memberships required by its controlled scenarios; review/approval
meaning remains on the selected workflow stages, and Project Leadership authority remains on its assignments.
Once `FMSLIVE` exists, the normal startup and `/api/showcase/seed` identity passes leave its memberships and
Project Leadership assignments unchanged. Controlled enrichment of an existing FMS is operator-controlled
through `POST /api/showcase/upgrade`, after target confirmation and a verified backup; it never runs as an
automatic incomplete-enrichment retry.

## Terminology

- `SRCR` identifies a system change request.
- `HLRCR`, `LLRCR` identifies a software change request.
- An software change request can affect HLRs, LLRs, or both.
- System, High-Level, and Low-Level are formal requirement and test-procedure levels.

## Generation and validation

Local development enables the dataset through `DemoData:Enabled`. The generator creates `FMSLIVE` only when it does not already exist and never deletes or modifies unrelated programs.

Automated validation proves exact counts, idempotence, complete baseline membership, complete test coverage, active-release state distribution, artifact searchability, trace/document access, and clean onboarding when demonstration data is disabled.

The administrator-only `GET /api/showcase/upgrade-state` also returns a structured `inventory` alongside the
invariants and Team Work distribution. Each build reports its own materialized requirement membership,
verification membership, change requests, test change reviews, assessments, impact items, executions,
Problem Reports, publications, code traces and baselines. Rows include native states, attributable authors or
assigned engineers, and exact example identities. Those people are not presented as current holders; the
separate Team Work projection owns current-holder meaning.

An in-work build with no materialized requirements reports zero members and `waitingForPrerequisite`, rather
than copying its predecessor's population into that build's count. Managed document revisions remain in the
project library unless explicit build provenance exists. Verification coverage follows the baseline's exact
coverage population: System Procedures and software Cases, including the exact source Cases of selected
software Procedures. Executable membership and coverage membership are separate facts. Retained off-ladder
change requests are explicitly reported and are not sent through the current ladder's trace-state classifier.
Each build and coverage summary exposes `VerificationScopes` with baseline identity and `IsExactManifest`.
Verification rows from a pre-manifest baseline explicitly say **legacy compatibility selection**, including
when mixed with exact selections. Such counts never claim an exact historical manifest.

Pre-scope software change requests without a governed `SoftwareLevel` have a separate legacy-history row
and retain their authored levels. They do not pad HLRCR/LLRCR counts or receive an invented off-ladder state.
The inventory is descriptive evidence, not permission to reset an older installation or infer missing
historical manifests. Qualification must inspect the actual target's inventory; a fresh-seed result does not
establish that an already-used HOME dataset has the same lifecycle or build state.

The explicit upgrade archives the obsolete, unapproved `SYSTP-000001.01` coverage-warning fixture left by
early HOME runs. Eligibility requires the exact original body, author, timestamp and empty provenance,
with no baseline, coverage, execution or Case/Procedure references. Changed or controlled records remain
untouched. The complete original draft is retained in a `ShowcaseLegacyDraftArchived` operator audit event;
the audit and removal from the working revision register commit in the same transaction. The maintained
warning scenario remains `SYSTP-000040`; no approved revision, coverage or manifest is repaired by rewriting it.

### Active Build 1.6 trace completeness

The showcase's `activeTrace` diagnostic scores the native completeness populations for the exact active
build: current governed change-request revisions, the build's own materialized requirement revisions, and
its exact software Case-to-Procedure obligations. The denominator includes existing operator changes, with
one current revision per controlled number and withdrawn current revisions excluded. A requirement with
both upstream and coverage gaps is counted once. Each population reports its own total and exact named
gaps; the combined incomplete share must remain between 5% and 10%.

A fresh Build 1.6 retains its waiting-for-materialization lifecycle. Its materialized populations are zero
and explicitly waiting; Build 1.5 members are never substituted into this count. Other artifact-family
relationships, checksummed execution evidence, external code samples and full release readiness remain
separate evidence. This percentage is not a claim that the build is ready to release.

The waiting verification step becomes pending again after that exact build materializes. The maintenance
analyzer reports it and the explicit supported showcase upgrade resumes it. A completed materialized step
is not retried to hide later selection, result or evidence drift.

Thirty named System/HLR/LLR authoring packages provide connected current work. They cover two input/recovery
boundaries across the fifteen FMS topic areas, use exact effective requirement revisions and their actual
System/HLR/LLR relationships, and remain Draft. They add no historical approvals or signatures. Eligible
synthetic engineering owners are assigned by package responsibility and checked against current authority.
The original review, approval, deferred and incomplete-authoring examples remain intact. Immutable scenario
ownership metadata records each proposal's exact source baseline, requirement revision and upstream revision;
the diagnostic checks the mutable draft against those identities.

For an existing materialized Build 1.6 with an exact Procedure manifest, the supported upgrade selects its
effective software Procedures and adds explicitly labelled synthetic demonstration results only where no
applicable result already exists. Existing Pass, Fail and Blocked determinations are preserved. The final
quarter of each exact Case family supplies candidate waiting work. Cases connected to positive Cases through
shared required Procedures join the positive population, so withholding a shared result cannot break an
unnamed obligation. No existing result is removed to manufacture a gap. New fixture results share a
checksummed, downloadable JSON evidence artifact that explicitly states
that no external bench execution or binary qualification is asserted. All new timestamps and attribution
describe the current enrichment operation. Direct qualification must supply an owned evidence store; there
is no persistent-store fallback.

The exact existing HOME requirement and change-control warning identities are catalogued in source as
retained incomplete scenarios. Their warning classes and revision identities must still match; the upgrade
does not automatically adopt arbitrary gaps or invent missing historical upstream answers. A removed
positive link, missing Case selection/effectivity, absent owned execution/evidence link, or changed scenario
identity fails the diagnostic with the offending identifier. Rerunning the upgrade cannot normalize that
drift by silently calling it intentional.
