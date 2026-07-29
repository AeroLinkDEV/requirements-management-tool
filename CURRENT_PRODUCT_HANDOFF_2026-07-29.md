# Current product and backlog handoff — 2026-07-29

This is the restart-ready record for a future Codex or Claude session. It supplements
[PROJECT_STATE.md](PROJECT_STATE.md), which remains the canonical description of the product, and the
append-only decisions in [DECISIONS_AND_OPEN_QUESTIONS.md](DECISIONS_AND_OPEN_QUESTIONS.md).

Start from `main`. The product implementation checkpoint before this documentation-only alignment is
`f43bb7e` (PR #168).

## What changed on 28–29 July

The review backlog was audited as issues #99–#139. Focused PRs #140–#165 delivered production mutation
fixes, authoring invariants, typed change routing, server-owned attribution and authority, verification
coverage/readiness corrections, audit containment, stable links, structured content/evidence, scalable
filters, showcase reconciliation and atomic identifier allocation.

Three product-facing increments then changed the primary navigation model:

- **PR #166 — Projects landing:** successful login opens `/projects`; only FMS Product Development is
  functional.
- **PR #167 — build-scoped workspaces:** FMS opens `/projects/fms-product-development/builds`; Build 1.5
  is released/read-only, Build 1.6 is in work, and workspace routes carry the selected release ID.
- **PR #168 — usability simplification:** Command Center, Change Requests, requirement details and
  navigation were reduced to the current System/Software/Verification story.

These changes are decisions, not temporary test accommodations. See DEC-070 through DEC-072.

## Current entry and workspace flow

1. Sign in.
2. Select a project at `/projects`.
3. Select FMS Product Development.
4. Select a build at `/projects/fms-product-development/builds`.
5. Enter the canonical workspace route:
   `/programs/{programId}/projects/{projectId}/releases/{releaseId}/command-center`.
6. Use **Back to Software Builds** to leave the workspace before selecting another build.

The route is the durable source of active project/build context. Refresh and valid deep links restore it.
There is deliberately no in-workspace build dropdown.

### Build behavior

| Build | State | Access |
| --- | --- | --- |
| 0.5 | Released | Visible lineage; inaccessible |
| 1.0 | Released | Visible lineage; inaccessible |
| 1.5 | Released | Accessible historical workspace; read-only |
| 1.6 | In Work | Accessible active-development workspace |

Build 1.5 does not show a completion percentage: release is the completed state. Build 1.6 may show
labelled read-only evidence originating in Build 1.5 without switching the active workspace.

## Current supported product surface

### Command Center

The dashboard is a three-way view:

- **System:** total System changes and Draft/In review/Approved counts.
- **Software:** total Software changes and Draft/In review/Approved counts.
- **Verification:** triage posture for System, Software HLR and Software LLR change work.

The old requirement-inventory banner, Release Attention rail and Change Request Flow visualization are gone.
Lifecycle Decision Room remains available. A released build presents released/read-only context rather than a
readiness percentage.

### Change Requests

- System and Software remain separate areas.
- The Change Requests page makes the active build explicit and shows changes applicable to it, including
  deferred changes raised in that build.
- System **New change request** opens System authoring directly.
- Software **New change request** first asks HLR or LLR.
- Search is intentionally limited to text and lifecycle state; System/SWCR and Release filters are removed.
- The old sidebar **New Change Request** action and Change Request **Software Builds** tab are removed.
- An empty create form does not allocate a number. A title is required before the first save; after that,
  incomplete Drafts can be saved.

### Change authoring and downstream impact

The author describes the proposed change. The right side may display a read-only live trace of relationships
already known for the affected requirement. The author does not decide trace, verification, controlled
document, baseline/build, collaboration or lifecycle consequences.

Those decisions belong to the engineers who consume and triage the change. The former five author-impact
dispositions, lifecycle-impact summary and their server/baseline/integrity gates were intentionally removed.
Do not restore them from issue #133; that issue is now closed as superseded/not planned.

### Requirements Explorer

The inspector keeps **Overview**, **Trace & Impact**, **History** and **Discussion**. Overview no longer repeats:

- the Controlled Revision banner;
- the Digital Thread banner/facts;
- the empty “no open discussion decisions” status.

Trace & Impact remains the focused relationship/evidence surface, including suspect coverage. The complete
Digital Thread remains available from that tab and the Assurance navigation.

### Verification

The Command Center summarizes verification as System, Software HLR and Software LLR triage. The Verification
workspace is split conceptually into **pre-release procedure alignment** and **evidence & results**.

- Every approved CR creates one controlled Test Change Review per affected discipline: System, Software HLR,
  or Software LLR. A mixed HLR/LLR software request creates two reviews.
- Each impacted requirement receives an explicit create/link/modify/retire/no-test procedure decision.
- All decisions must be complete before the Test Change Review can be submitted and approved.
- The verification engineer explicitly marks the subset of procedures whose passing evidence is required
  before release. All other execution/evidence work may continue after release.
- A post-release failure remains evidence against the released build. Software correction occurs only through
  a later software build; released content is not rewritten.
- `SW-01.50` seeded history contains completed, approved Test Change Reviews and procedure coverage.
- `SW-01.60` contains the current open review work generated from its active changes.

Future redesign may simplify evidence/result capture further, but must preserve these discipline, build and
approval boundaries.

### Naming and navigation additions

- SCR and SWCR identifiers use five digits everywhere: `SCR-00039.00`, not `SCR-00000039.00`. The migration
  rewrites existing primary identifiers and stored textual references; new allocation uses the five-digit form.
- A baseline and a software build are the same product concept. `SW-01.60` is the official name; “Build 1.6”
  is informal supporting language only.
- Documents moved into the relevant Engineering and Assurance navigation groups. System has one requirements
  document; Software has HLR and LLR requirements documents; Assurance exposes the corresponding test
  procedure documents and traceability-document guidance. Digital Thread no longer owns document generation.
- Password controls have an accessible Show/Hide toggle.
- Modification authoring searches both exact requirement identifiers and requirement wording.
- Missing source-change links show an explanatory empty state, and inspector references to requirements,
  changes and test procedures are actionable links.

## Deliberately dormant UI

The following are not reachable through navigation, command-palette results, artifact links or supported direct
routes:

- Problem Reports;
- Product Versions;
- Candidate Baselines;
- Change Request Software Builds history/management.

Their client/domain/API implementation is retained intentionally. Do not reconnect it merely because code
exists, and do not delete it mechanically. A future request may reuse parts under a different product design.

## GitHub issue alignment

The live review-backlog state after reconciliation is:

- **Still open and applicable:** #100, #101, #102, #106, #110, #112, #113, #115 and #132.
- **Parent/long-lived program records:** #29, #34 and #38.
- **#102 is correctly open after partial delivery:** digest recomputation landed, but durable background scans,
  unexpected/duplicate objects and qualification evidence remain.
- **#131 is closed as not planned:** Problem Reports are intentionally hidden, so adding them to global search
  would contradict the product surface.
- **#133 is closed as not planned/superseded:** author impact-disposition enforcement is no longer the desired
  contract.
- **#132 was rewritten:** it now audits accessible semantics only on reachable forms and must not restore hidden
  controls.
- **#130 remains completed but dormant:** its correct discipline-preserving corrective route is retained for
  possible reuse.

Refresh GitHub before starting work. Do not use the issue list in the older
[AUTONOMOUS_BACKLOG_HANDOFF_2026-07-28.md](AUTONOMOUS_BACKLOG_HANDOFF_2026-07-28.md); it is a historical
checkpoint.

## Validation at PR #168

- .NET build: passed with zero warnings.
- Domain tests: 136 passed.
- API tests: 117 passed.
- Infrastructure tests: 135 passed.
- Client lint, type check and production build: passed.
- Full Playwright suite: 79 active journeys passed; one capture-only test intentionally skipped.
- Production-build Playwright suite: 10 passed.
- GitHub build/test, two browser shards, production browser and PostgreSQL migration/bootstrap jobs: passed.

## Safe restart sequence

1. Checkout and pull `main`; verify a clean understanding of any local files before editing.
2. Read this file, `PROJECT_STATE.md`, and DEC-070 through DEC-072.
3. Refresh open GitHub issues and read the selected issue body plus every alignment comment.
4. Preserve project/build-scoped routes, Build 1.5 read-only enforcement and Build 1.6 mutation capability.
5. Do not restore #131 or #133 as apparent regressions.
6. Keep System and Software separate. Software creation must retain its HLR/LLR decision.
7. Treat hidden modules as dormant, not supported and not automatically disposable.
8. Use focused branches/PRs, assert durable outcomes, wait for GitHub checks and merge only green work.

## Known next conversations

- The Verification workspace needs a product-owner-led simplification/redesign.
- #132 needs a rendered accessible-name/axe audit of active forms.
- #112 remains live: `main` has no protection or ruleset, and launcher-only validation can still be misleading.
- #100, #101 and the remaining #102 work are operations/authority risks, not showcase polish.
- #113 remains important: major workspaces and several retained modules are still oversized and CSS remains
  import-order-sensitive.
