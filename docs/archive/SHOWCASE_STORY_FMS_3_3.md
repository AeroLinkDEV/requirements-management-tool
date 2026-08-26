# Interactive Showcase Story: FMS Software Version 3.3

> **Historical record.** This was the canonical story for the Phase 0.5 prototype, which was retired on
> 2026-07-24 (DEC-046). The live demonstration dataset is now `FMSLIVE`, described in
> [FMS_LIVE_SHOWCASE_DATASET.md](FMS_LIVE_SHOWCASE_DATASET.md), which uses a different release
> numbering (1.5 released, 1.6 in work) and is built through the product's real domain and persistence
> rules. Use that document for anything current. This one is retained because it captures the workflow
> narrative — sequential approval, approver replacement, coverage gap, blocked execution and retest —
> that the product was built to support.

This was the canonical fictional story for the Phase 0.5 interactive showcase. It gave every dashboard, workflow, and traceability screen one coherent set of data.

The story is representative and fictional. It must not contain customer, proprietary, export-controlled, or real program data.

## Program Context

- **Program:** Program Atlas
- **Project:** Flight Management System (FMS)
- **Current released software baseline:** FMS Software Version 3.2
- **Target release:** FMS Software Version 3.3
- **Primary showcase users:** System Engineer and Manager
- **Change package:** Two SRCRs selected for the Version 3.3 candidate baseline

The showcase hierarchy is:

```text
Program Atlas
  -> Flight Management System Project
      -> FMS Software Product
          -> Software Version 3.2 (current baseline)
          -> Software Version 3.3 (candidate/released baseline)
```

## Release Story

FMS Software Version 3.3 is created by applying two approved SRCRs to the Version 3.2 baseline.

### SRCR-0001049: Introduce Round Robin Function

**Purpose:** Add a new “Round Robin” FMS function.

The showcase will demonstrate that this SRCR:

- contains the problem/opportunity, analysis, and proposed solution;
- introduces several new controlled requirements;
- affects existing interface or navigation behavior where appropriate;
- creates or revises verification procedures;
- has its ordered approval sequence selected by the SRCR author;
- becomes approved only when every selected approver approves the same submitted SRCR snapshot in sequence;
- is explicitly selected for the Version 3.3 candidate baseline; and
- contributes its exact approved artifact revisions to the released Version 3.3 baseline.

Proposed fictional system requirements and their allocated software HLRs:

| System Requirement Revision | Allocated HLR Revisions | Purpose | Verification |
| --- | --- | --- | --- |
| `SYSR-00002375.01` | `HLR-00003142.01`, `HLR-00003143.01` | Provide selectable Round Robin route sequencing | Functional test |
| `SYSR-00002376.01` | `HLR-00003144.01` | Advance to the next eligible waypoint after the configured trigger | Functional and robustness test |
| `SYSR-00002377.01` | `HLR-00003145.01`, `HLR-00003146.01` | Skip ineligible or unavailable waypoints without corrupting sequence state | Robustness test |
| `SYSR-00002378.01` | `HLR-00003147.01` | Display current Round Robin state and selected waypoint to the crew | Functional test |
| `SYSR-00002379.01` | `HLR-00003148.01`, `HLR-00003149.01` | Preserve or restore defined sequence state across applicable mode transitions | Functional and recovery test |

The showcase demonstrates both levels: the system requirement expresses the externally meaningful FMS behavior, while linked HLRs allocate and refine that behavior for the software implementation. Both sets of proposed revisions are contained in and reviewed through the SRCR; neither requirement type has an independent approval workflow.

Proposed fictional verification artifacts:

- `TP-00004501` — Round Robin nominal sequence test
- `TP-00004502` — Ineligible waypoint skip test
- `TP-00004503` — Mode transition and state recovery test
- `TP-00004504` — Display and crew-feedback test

During SRCR review, a revision to `SYSR-00002376.01` and its allocated `HLR-00003144.01` makes one verification link suspect. The System Engineer resolves the impact by updating and reapproving the affected test procedure before baseline readiness becomes complete.

### SRCR-0001050: Resolve Four FMS Problem Reports

**Purpose:** Incorporate four approved bug fixes into Version 3.3.

The showcase assumes the user’s reference to a “second PR” meant a **second SRCR linked to four PRs**. This assumption remains visible until confirmed.

The fictional linked PRs are:

| PR | Problem | Controlled Resolution Story |
| --- | --- | --- |
| PR-00002841 | Route discontinuity can remain after waypoint deletion | Modify requirement and regression procedure |
| PR-00002842 | Hold-page sequencing displays stale leg information | Modify display requirement and associated test |
| PR-00002843 | Crossfill can duplicate an advisory after rapid reconnect | Add robustness requirement and test |
| PR-00002844 | Invalid database record can block route activation | Clarify rejection behavior and add negative test |

`PR-00002844` also demonstrates incomplete verification coverage: review reveals that the requirement is not fully covered. The PR drives creation of an additional test procedure. The initial execution is Blocked because the procedure/configuration cannot properly exercise the requirement; a corrected procedure revision is then run successfully and receives a human-approved Pass result.

## Dashboard Story

### Manager View

At the beginning of the showcase, the Version 3.3 dashboard shows:

- two SRCRs targeted to the release;
- one SRCR approved and one still in review;
- incomplete baseline readiness;
- one suspect link;
- one verification-coverage gap;
- one blocked test execution; and
- assigned owners for every blocking item.

As the presenter resolves the simulated story, the dashboard updates to show that all required reviews are complete, the suspect link is dispositioned, the added procedure covers the requirement, the corrected execution passes, and the candidate baseline is ready for approval.

### System Engineer View

The System Engineer dashboard prioritizes:

- assigned SRCR reviews and the requirement changes contained within them;
- the Round Robin requirement revision requiring review;
- the suspect link and affected procedure;
- the uncovered requirement associated with `PR-00002844`;
- the Blocked execution requiring corrective action; and
- the Version 3.3 candidate-baseline checks relevant to engineering.

## Showcase Walkthrough

1. Open the manager dashboard scoped to FMS Version 3.3.
2. Explain the two-SRCR release package and visible blocking items.
3. Open `SRCR-0001049.01`, compare the contained system-requirement and HLR revisions, and inspect the author-selected approval order and review-cycle history of the complete SRCR package.
4. Show the affected procedures and suspect link caused by the revision.
5. Switch to the System Engineer dashboard and open the assigned impact-review action.
6. Resolve the simulated link/procedure review and show the dashboard status update.
7. Open `SRCR-0001050` and its four linked PRs.
8. Drill into `PR-00002844`, the uncovered requirement, and the new test procedure.
9. Show the Blocked execution, corrected procedure revision, new execution, evidence, and human-approved Pass result.
10. Inspect the Version 3.3 candidate baseline and exact included revisions.
11. Navigate the complete trace from SRCR and PR through requirement, test, execution, result, and release.
12. Return to the manager dashboard and show the release as ready for its unanimous baseline/document approval workflow.

## Showcase Success Criteria

- A manager can understand Version 3.3 progress and blocking issues without interpreting a static trace matrix.
- A System Engineer can identify and navigate directly to assigned reviews, suspect links, verification gaps, and failed/blocked evidence.
- Every dashboard value drills into the exact fictional records that produce it.
- Approval visibly advances through every author-selected approver in order against the same submitted snapshot.
- Version 3.2 remains unchanged while Version 3.3 contains exact selected successor revisions.
- A retired requirement, if demonstrated later, does not appear in the effective Version 3.3 SYSRD but remains in Version 3.2 history and the audit/change story.
- Blocked is not shown as a verification success or failure; it signals that a valid verification conclusion could not be reached.
- Pass is shown only after a qualified human reviews the execution and concludes that the applicable requirement was successfully verified.
