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

At initial seed, FMS 1.6 begins from the FMS 1.5 predecessor baseline and contains eight controlled change requests:

- 2 Approved and selected in the working candidate baseline
- 2 In Review
- 3 Draft
- 1 Deferred

The seeded changes include a new system-level oceanic round-robin function and representative HLR and LLR modifications. Subsequent realistic testing may add approved, returned, deferred, or draft records; these are persistent product data, not seed drift. The FMS 1.6 workspace remains editable and is not released.

## Terminology

- `SRCR` identifies a system change request.
- `HLRCR`, `LLRCR` identifies a software change request.
- An software change request can affect HLRs, LLRs, or both.
- System, High-Level, and Low-Level are formal requirement and test-procedure levels.

## Generation and validation

Local development enables the dataset through `DemoData:Enabled`. The generator creates `FMSLIVE` only when it does not already exist and never deletes or modifies unrelated programs.

Automated validation proves exact counts, idempotence, complete baseline membership, complete test coverage, active-release state distribution, artifact searchability, trace/document access, and clean onboarding when demonstration data is disabled.
