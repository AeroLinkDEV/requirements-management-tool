# System-Level Workflow

This document defines the intended system-level behavior and retains the original paper scenarios. It is not a technical design. **Reconciled through 10 August 2026:** later decisions supersede the original direct procedure-approval model with controlled Test Change Requests (DEC-103), and Problem Reports are Project-scoped with explicit target-build attribution (DEC-089). Historical scenario intent is preserved while current control semantics below take precedence.

## 1. Lifecycle Overview

```text
Current approved baseline
        +
selected approved SRCRs for a target release
        -> candidate requirement revisions and retirements
        -> review and impact resolution
        -> candidate baseline
        -> baseline approval
        -> approved immutable baseline
        -> approved SYSRD and traceability outputs
        -> executions of approved test procedures outside the platform
        -> entered/imported results and evidence
        -> verification review and release evidence
```

An SRCR authorizes and explains proposed change. A baseline determines what is effective. A SYSRD presents the requirement contents of a baseline. These are related but distinct approvals.

## 2. Roles

The workflow assumes these conceptual roles; one person may hold multiple roles only where independence rules permit:

- **Change Author:** drafts an SRCR and its requirement change items.
- **SRCR Author:** prepares the Problem, Analysis, Solution, and all proposed requirement introductions, modifications, and retirements within the SRCR package.
- **Reviewer:** evaluates a specified revision and records comments and a decision.
- **Approver:** provides the final approval decision when workflow criteria are satisfied.
- **Configuration Manager:** assembles and controls candidate baselines and document releases.
- **Verification/Test Engineer:** resolves verification-impact work and authors Link/Introduce/Modify/Retire procedure changes inside a governed TCR.
- **Test Lead / TCR Approver:** assigns or reviews TCR work under the configured independent approval workflow; procedures do not have a second independent approval path.
- **Test Contributor:** enters or imports externally produced execution data and evidence.
- **Verification Reviewer:** reviews executions, outcomes, and evidence where that workflow requires it.
- **Program Administrator:** configures authorized users, roles, and program-level policy.
- **System Administrator:** operates the platform without authority to erase controlled history.

For the initial product behavior, the SRCR author selects and orders the people required to approve the SRCR. Review proceeds sequentially in that order; only the active approver can record the next decision. Approval is unanimous among the author-selected sequence: every selected approver must approve the same SRCR revision and submitted content. Requirements contained in an SRCR are not reviewed or approved independently. Any selected approver rejection or request for changes prevents approval. Current implementation enforces role/independence authority and password-confirmed electronic approval evidence; later workflow configuration must preserve those assurance boundaries.

## 3. SRCR Lifecycle

### Required SRCR Content

Each SRCR has a globally unique, never-reused number and controlled revisions. At minimum it records:

- title and summary;
- problem, analysis, and proposed solution;
- program and affected product/system;
- proposed target release;
- one or more requirement change items;
- linked or referenced PRs where applicable;
- author, ownership, dates, comments, dispositions, and approvals; and
- impact-analysis status.

### States and Transitions

```text
Draft -> Ready for Review -> In Review
In Review -> Rework Required -> Draft
In Review -> Rejected
In Review -> Approved
Approved -> Selected for Candidate Baseline
Approved -> Deferred
Selected -> Deferred (with controlled reason)
```

- Draft SRCRs may be revised by authorized users and may miss an intended release without losing history.
- The SRCR author decides when the complete package is technically ready to submit. Submission validation checks that required Problem, Analysis, Solution, requirement-change, reviewer, link, and impact information is present; it does not replace the author’s engineering judgment.
- Submission creates an immutable review-cycle snapshot of the current SRCR revision.
- The approval sequence advances one person at a time. Completed and active stages are locked against ordinary name substitution.
- Before a future approver’s turn is reached, the SRCR author may replace that person without cancelling approvals already completed. The change, reason, actor, time, old approver, new approver, and resulting sequence are audited.
- If an approver who already approved was the wrong person, the approval workflow is cancelled. All decisions from that cycle become historical and non-counting, the corrected sequence is established, and review restarts from the first approver against the same submitted snapshot unless the SRCR content also changes.
- If the SRCR has never been approved and an approver requests changes, the SRCR returns to Draft **without increasing its revision number**. The review cycle, comments, decisions, and submitted snapshot remain historical; the author edits the same business revision and resubmits it for a new review cycle.
- If an already approved SRCR requires any content change, the approved revision remains immutable and the SRCR advances to its next revision. The author selects the ordered approval sequence for the new revision, and every selected approver must approve again.
- Every review comment requiring action must be dispositioned before approval.
- Approval requires every author-selected approver to approve the same submitted revision and review-cycle snapshot. Approval decisions from an earlier review cycle do not carry into a resubmission after requested changes, even when the SRCR revision number remains unchanged.
- Rejection preserves the SRCR and review history. A materially revised proposal proceeds as a new SRCR revision or a replacement SRCR according to policy.
- Approval makes the proposed changes eligible for baseline selection; it does not itself change the effective baseline.
- When an approved SRCR is linked to one or more PRs, the approved change is presented as an attributable
  corrective action on those PRs; ordinary requirement changes never create a PR automatically.
- Deferral records who deferred the SRCR, when, why, and any new target release.

## 4. Requirement Change Behavior

### Introduce

An SRCR introduction allocates a new globally unique requirement identity. The proposed initial revision contains requirement content and required attributes, including verification method and derived status. It is reviewed as part of the SRCR package, authorized when that exact SRCR revision is unanimously approved, and becomes effective only through baseline inclusion.

### Modify

A modification identifies the currently effective requirement revision and proposes a successor revision inside the SRCR. The platform shows the difference and preserves both. SRCR approval never overwrites the earlier revision.

### Retire

A retirement identifies the effective requirement and gives rationale inside the SRCR. Once the approved SRCR is selected into an approved baseline, the successor SYSRD omits the retired requirement from its effective body. Historical baselines and records retain its prior content and complete change story.

### Images and Figures

A requirement revision may contain controlled images or figures. Their content, order, captions, integrity information, and relationship to the revision are versioned with the requirement so generated outputs are reproducible.

## 5. Candidate Baseline and SYSRD Lifecycle

1. A configuration manager chooses a current approved baseline as the predecessor.
2. The manager selects approved SRCR revisions targeted to the release.
3. The platform deterministically applies their approved introductions, modifications, and retirements to construct candidate requirement contents.
4. The platform blocks contradictory selected changes to the same requirement until they are ordered or resolved through an approved decision.
5. Automated checks identify missing approvals, unresolved comments, suspect links, missing required attributes, identifier conflicts, and incomplete impact analysis.
6. A candidate baseline records its predecessor, target release, exact SRCR revisions, exact requirement revisions, exact trace-link revisions, check results, and assembler.
7. A draft SYSRD may be generated at any time from the candidate. It must be visibly marked `DRAFT`, identify its candidate source, and never appear approved.
8. Reviewers assess the candidate baseline and generated draft. Rework produces a revised candidate; rejected candidates remain historical.
9. Approval freezes the exact baseline contents. It does not mutate the predecessor baseline.
10. The approved SYSRD is generated from the approved baseline and records the document identifier and revision, source baseline, template revision, generator version, generation time, approval reference, and file hash.

If an error is discovered after baseline approval, users create a controlled corrective SRCR and successor baseline. Administrative editing of the released baseline is prohibited.

## 6. Test Procedure Lifecycle

The first slice is procedure-first. A separate test-case object is deferred until its distinct role is agreed.

### Procedure Behavior

- Each procedure has a globally unique identity and immutable controlled revisions.
- A procedure revision contains purpose, preconditions, configuration needs, ordered steps, expected outcomes,
  and applicable verification relationships.
- A many-to-many relationship is supported: one procedure may verify several requirements, and one requirement
  may use several procedures.
- **Procedure content changes only through a controlled TCR (DEC-103).** Universal direct procedure editing and
  a separate procedure-level approver are not current product paths.
- Requirement-change approval raises build/discipline-specific verification-impact assessment work. A manual
  TCR may deliberately cover several approved source changes; automatic assessment work may be folded into it
  without recreating item identity or deleting prior decisions.
- Link/Introduce/Modify/Retire proposals inside the TCR are bound to the governed Project, build, discipline and
  exact carried procedure/requirement scope. Modify preserves retained coverage and records governed
  additions/removals; Retire preserves historical procedure identity and title.
- Modify/Retire target selection uses the exact controlled procedure carried by the target build. If membership
  or the current revision changes before submission, the server returns a stale conflict and the engineer must
  refresh/reselect; AeroLink never silently remaps the request to a different procedure revision.
- TCR approval authorizes the governed package; materialization creates the new controlled procedure revision
  and exact coverage changes while prior revisions remain immutable.
- Procedures may be grouped into test suites or campaigns; the detailed model remains open.

## 7. Execution, Result, and Evidence Lifecycle

Test execution occurs outside the platform initially. An authorized user or integration records the outcome afterward.

Each execution records:

- a unique execution identity;
- exact procedure revision;
- program, product/system, release or baseline applicability;
- test environment, target configuration, equipment or suite, and relevant software/hardware identifiers;
- executor identity and execution time;
- step-level observations and outcomes when required;
- overall result;
- evidence attachments or controlled references;
- anomalies and PR references where applicable;
- data-entry/import provenance; and
- verification review and approval state.

Completed executions are immutable. A clerical correction uses a controlled amendment that preserves original and corrected values; a repeated run creates a new execution.

### Outcome Rules

- **Pass:** The test was completed and a qualified human reviewer concluded that the recorded execution and evidence successfully verify the applicable requirement(s). Automated step outcomes alone do not create an approved Pass.
- **Fail:** The test was completed and a qualified human reviewer concluded that the execution did not verify one or more applicable requirements or demonstrated that expected behavior was not satisfied. The failure remains visible even after later success.
- **Not Applicable:** The procedure or step is inapplicable under the recorded configuration and requires justification.
- **Blocked:** The execution could not reach a valid verification conclusion—for example, it could not run, the environment/configuration prevented completion, or the procedure did not adequately test the requirement. Blocked is neither Pass nor Fail and requires disposition.
- **Not Run:** The planned execution has not started. It is neither a verification result nor interchangeable with Blocked.

A failed execution may link to one or more PRs or external PR references. A later retest links to the earlier execution, relevant correction/change, and its own evidence. Reporting may show the latest valid status but must preserve the full sequence.

A PR may also be raised when review identifies that an applicable requirement is not fully covered by verification. That PR can drive creation or revision of test procedures and remains linked through the resulting execution and coverage closure.

Any approved procedure that covers a requirement introduced or modified in a build is mandatory pre-release
scope for that build. It cannot be removed from the build test set, and release readiness requires an accepted
passing execution with evidence for the exact procedure revision.

## 8. Traceability and Completeness

At minimum, the first slice supports controlled links for:

- SRCR **INTRODUCES**, **MODIFIES**, or **RETIRES** requirement revision;
- requirement revision **INCLUDED IN** baseline;
- requirement revision **VERIFIED BY** test procedure revision;
- test execution **EXECUTES** procedure revision;
- result/evidence **RECORDED FOR** execution;
- artifact **AFFECTED BY** or **ADDRESSES** PR reference; and
- generated document **GENERATED FROM** baseline.

The platform must answer interactively and in controlled reports:

- which requirements have no approved verification procedure;
- which approved procedures have no requirement relationship;
- which applicable requirements lack a reviewed passing execution;
- which failures lack an anomaly or PR disposition when policy requires one;
- which links are suspect after change;
- what exact contents and evidence support a release; and
- who changed, reviewed, approved, selected, executed, imported, or generated each item.

## 9. Paper Validation Scenarios

These scenarios are acceptance tests for the documentation baseline.

### Scenario 1: Introduce a Requirement

- **Actor/Input:** Change author drafts an SRCR with problem, analysis, solution, target release, and a new requirement change item.
- **State change:** The complete SRCR revision, including the proposed requirement introduction, passes unanimous review and approval, then is selected into a candidate baseline.
- **Output:** Successor approved baseline and SYSRD contain the new requirement.
- **History:** SRCR revisions, comments, dispositions, approvals, selection, baseline membership, document metadata, and audit events remain linked.

### Scenario 2: Modify an Approved Requirement

- **Actor/Input:** Author proposes a successor revision through an SRCR against the currently effective revision.
- **State change:** Approved successor is selected; affected verification links become suspect until reviewed.
- **Output:** New baseline contains the successor revision; prior baseline still contains the old revision.
- **History:** Difference, rationale, approvals, link reassessments, and both revisions remain retrievable.

### Scenario 3: Retire a Requirement

- **Actor/Input:** SRCR identifies the effective requirement and retirement rationale.
- **State change:** Approved retirement is selected into a successor baseline.
- **Output:** Requirement is not effective in the successor SYSRD according to the chosen retirement presentation policy.
- **History:** Identity, previous revisions, SRCR, approvals, prior baselines, links, and audits remain intact.

### Scenario 4: Defer an SRCR

- **Actor/Input:** Authorized user defers a draft, approved, or selected SRCR from the intended release with rationale.
- **State change:** SRCR is removed from candidate selection and optionally assigned a later target release.
- **Output:** Current candidate baseline is recalculated without its changes.
- **History:** Original target, deferral decision, actor, time, rationale, and any later selection remain visible.

### Scenario 5: Generate Draft and Approved SYSRDs

- **Actor/Input:** Configuration manager selects a candidate or approved baseline.
- **State change:** Generation creates a distinct immutable output record; it does not change requirements.
- **Output:** Candidate output is visibly watermarked `DRAFT`; approved output contains controlled metadata and hash without a draft mark.
- **History:** Exact source baseline, template, generator version, time, approval reference, and hash are recorded.

### Scenario 6: Reuse a Test Procedure

- **Actor/Input:** Verification/Test Engineer resolves governed impact work by linking an existing exact
  controlled procedure revision to several applicable requirement revisions, or authors the required coverage
  delta inside a TCR.
- **State change:** The TCR review governs the exact decision/procedure-change package. Materialization creates
  or confirms the exact coverage links; there is no independent procedure-approval cycle.
- **Output:** Trace views show the same exact procedure revision under every applicable requirement and all
  requirements under the procedure.
- **History:** TCR source, decision/procedure-change content, actor, rationale, review/signature evidence,
  materialized coverage, revision applicability, and later suspect/reopen transitions remain visible.

### Scenario 7: Failure and Retest

- **Actor/Input:** Contributor imports a failed external execution with configuration and evidence; later imports a retest after correction.
- **State change:** First execution is completed and immutable; retest is a new linked execution.
- **Output:** Current reporting may show the accepted retest outcome while also displaying the prior failure and its disposition.
- **History:** Both executions, evidence, PR/change relationships, reviews, and audit events remain intact.

### Scenario 8: Released Requirement Audit Story

- **Actor/Input:** User selects a released requirement revision.
- **State change:** None; this is a read-only trace and audit query.
- **Output:** The platform shows predecessor/current revisions, originating SRCR, reviews, approvals, baseline and SYSRD, procedures, executions, evidence, failures/retests, PR references, and release applicability.
- **History:** Results are derived from exact controlled records, not reconstructed from an uncontrolled document.

## 10. Baseline Correction and Recovery

- Released baselines and generated approved documents are never edited in place.
- A substantive content error uses an SRCR and successor baseline.
- A document-rendering defect with unchanged source data uses a new generated-document record and corrected template or generator revision; the defective output remains recorded and may be marked withdrawn.
- A mistaken execution entry uses a controlled amendment only for clerical correction; a changed test outcome requires a new execution.
- Recovery from backup must not create competing silent histories. Restored state and any reconciliation action are operationally logged and auditable.
