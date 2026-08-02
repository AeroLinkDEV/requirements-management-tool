# Domain Model and Glossary

This document defines the common product language. It is conceptual, not a database schema.

## Core Relationships

```text
Program
  -> Release
      -> Candidate Baseline
          -> exact Requirement Revisions and Trace Link Revisions
          -> approved SYSRD

SCR
  -> proposed Requirement Changes
      -> Requirement Revisions
          -> verified by Test Procedure Revisions
              -> used by Test Executions
                  -> outcomes and Test Evidence

Review and Approval records apply to controlled artifacts.
Audit Events record every material action.
PRs may motivate or be affected by changes across the chain.
```

## Artifact and Revision

**Artifact**: A controlled object with a stable, never-reused identity. Examples include an SCR, requirement, test procedure, PR, or generated document definition.

**Artifact Revision**: A version-specific representation of an artifact. Revisions preserve what was proposed, reviewed, approved, or used at a particular time.

The stable identity and revision identity remain separate even when the display number combines them. For
example, `SYSR-000001` is the stable requirement and `SYSR-000001.03` is its exact third controlled revision.

## Program and Delivery Terms

**Program**: The primary organizational and access-control boundary for lifecycle data. Artifact numbers are globally unique even when artifacts belong to different programs.

**Project**: The principal body of work within a program. In the initial target environment, a project commonly proceeds directly to a software product rather than requiring a deep aircraft/system/product hierarchy.

**Software Product**: The controlled software item delivered by a project, such as a Flight Management System. It has software releases and baselines.

**Product / System**: An optional controlled engineering layer when a program needs it. The initial default hierarchy is `Program -> Project -> Software Product -> Software Release`; additional product/system/configuration layers may be enabled when required.

**Configuration / Variant**: A defined applicability context that distinguishes product forms or options. Advanced variant management is later scope, but the initial model must not prevent it.

**Release / Software Build**: One product concept representing a planned or delivered software version. Its
official identifier derives from the release version (`1.6` becomes `SW-01.60`); “Build 1.6” is informal UI
wording. A released build is immutable and read-only; an in-work successor collects controlled changes,
requirements, verification, documents, and readiness evidence.

**Baseline**: An immutable, named set of exact artifact and relationship revisions approved for a defined purpose. Candidate baselines may be assembled and reviewed; released baselines cannot be edited.

**Candidate Baseline**: The implementation facet that assembles the proposed exact contents of an in-work
software build. It is not a separate product-level destination in the current UI.

## Change Terms

**System Change Request (SCR)**: A versioned, reviewable artifact that explains and proposes one or more system requirement introductions, modifications, or retirements. It includes problem, analysis, solution, target-release, affected-artifact, PR-reference, review, and approval information.

**Software Change Request (SWCR)**: The software-level counterpart to an SCR. It governs HLR and LLR
introductions, modifications, and retirements for one target build, including exact upward allocation or a
justified derived exception. HLR and LLR work is scoped independently. Saving a Draft persists incomplete
work; checking in applies the controlled working copy and closes its edit session.

**Requirement Change Item**: One proposed introduction, modification, or retirement contained in an SCR. It identifies the affected requirement when applicable, the proposed content or disposition, and the rationale needed to review the change.

**Retirement**: The controlled result previously described as requirement deletion. A retired requirement is excluded from later effective baselines but remains permanently available in historical baselines, revisions, SCRs, links, and audits.

**Deferral**: A controlled decision to move an SCR or approved change out of an intended release. Deferral changes planning and selection; it does not erase approval or history.

**Downstream Change Assessment**: Build-scoped work raised when an approved upstream change reaches a consuming
software discipline. System approval raises HLR assessment work; HLR approval raises LLR assessment work. The
consuming engineer records a justified no-change decision or links one or more Draft SWCRs, then submits the
assessment to a named independent approver. Superseded source work remains readable but cannot satisfy current
readiness. `ChangeRequired` is an explicit intermediate outcome: it records that work is needed but cannot be
submitted until a level-compatible SWCR is linked. The assessment detail retains a deep link to the source SCR,
its Problem/Analysis/Solution case, changed requirements, current downward trace, and the linked SWCRs.

**Prospective Upward Allocation**: The exact current parent requirement revisions selected for a proposed HLR
or LLR before review. HLR parents are System revisions; LLR parents are HLR revisions from the same Project and
target build. Selected IDs enter the review snapshot and materialize as immutable `AllocatedFrom` traces.

## Requirements and Documents

**System Requirement**: A stable controlled requirement identity at system level. It may contain formatted text, attributes, rationale, verification method, derived status, applicability, and controlled images or figures.

**System Requirement Revision**: The exact requirement content and metadata proposed within an SCR, authorized through approval of that SCR revision, or made effective through baseline inclusion. It does not have an independent review/approval workflow.

**High-Level Requirement (HLR)**: A software requirement that describes software behavior at a high level and normally traces upward to one or more system requirements unless justified as derived.

**Low-Level Requirement (LLR)**: A detailed software requirement that normally traces upward to one or more HLRs unless justified as derived.

**Derived Requirement**: A requirement not directly traceable to a higher-level requirement and therefore requiring explicit rationale and the program-defined review or feedback process.

**System Requirements Document (SYSRD)**: A generated controlled document containing the applicable approved system requirement revisions from a named baseline. Draft SYSRDs are clearly watermarked and are not approved lifecycle outputs.

**Software Requirements Document (SWRD)**: A generated controlled document containing applicable approved HLR
or LLR revisions from one exact software build, with document control and source-change provenance.

The abbreviation `SRD` is not used on its own because it is ambiguous.

## Verification Terms

**Verification Method**: The planned means of showing a requirement is satisfied, such as test or analysis. Allowed values and combination rules remain an open question.

**Test Case**: A verification intent or scenario, potentially realized by one or more procedures. The first slice is procedure-first; a separate test-case layer will be introduced only if its distinct value and relationships are defined.

**Test Procedure**: A stable controlled identity describing repeatable verification actions. A procedure may verify multiple requirements, and a requirement may be verified by multiple procedures.

**Test Procedure Revision**: The exact approved procedure content used by a test execution.

**Test Step**: An ordered instruction within a procedure, with expected outcome and any required inputs or conditions.

**Test Change Request**: A controlled record of the test work an approved change creates, one per affected discipline — System, Software HLR, Software LLR. It carries its own number and revisions, may cover more than one requirement change request, and may also be raised deliberately when a set of changes is best tested together. Claiming one claims every decision inside it.

**Verification Decision**: The explicit judgement recorded against each requirement inside a test change request — an approved procedure covers it, no test is required, a procedure is retired, retargeted or deliberately retained. There is deliberately no value meaning nobody looked. A decision may be reopened: what was decided stays in immutable history, the item returns to the release gate, and any coverage it claimed goes back to suspect.

**Build Test Set**: The procedures a particular build has to run, one set per build per discipline. A working list rather than a controlled artefact, recording who added each entry and why — because a requirement changed, because the change makes an area worth re-exercising, because a corrective action demands it, or simply because somebody chose it. Release gates measure recorded results against this set.

**Test Suite**: A named grouping or execution context for procedures, such as a real-engine or simulated-engine campaign. The precise suite/configuration distinction remains open.

**Test Execution**: An immutable historical record of running an exact procedure revision under a defined configuration. Execution occurs outside the platform initially; its data is entered or imported.

**Test Step Result**: The recorded outcome and observations for a specific step in an execution, when step-level capture is required.

**Test Result**: The controlled overall outcome of an execution. The minimum domain includes Pass, Fail, and Not Applicable; Blocked and Not Run are candidate operational states requiring definition.

**Test Evidence**: Controlled attachments or references supporting an execution or result, such as logs, screenshots, measurements, or data files. Evidence records include integrity and provenance metadata.

**Retest**: A new test execution performed after a failure or change. It relates to but never replaces or modifies the prior execution.

## Review and Control Terms

**Artifact Edit Session**: An attributable, server-side lease granting one user exclusive write authority over one configured draft artifact while all other authorized users retain read-only access. The session records checkout, activity, expiry, closure, version, and administrative recovery details.

**Draft Snapshot**: An immutable server-retained autosave payload for an edit session, identified by sequence, content hash, actor, and time. A snapshot is recovery evidence and does not approve, baseline, or release its draft content.

**Check-In**: The atomic operation that validates the expected artifact and session versions, applies the controlled draft changes, closes the edit session, and records attribution. **Discard** closes the session without applying its autosaved content.

**Forced Unlock**: A configuration-manager or administrator action that closes another user's active lease. It requires a reason and produces both lifecycle and security audit evidence.

**Workflow State**: The current lifecycle state of a revision or controlled process. State transitions are constrained and audited.

**Review**: A controlled evaluation of a submitted snapshot of a specific SCR, document, procedure, execution/result record, or candidate baseline revision by named reviewers. Requirement changes are reviewed as contents of the SCR rather than as independent review artifacts. An SCR review cycle contains its submitted snapshot, author-selected ordered approval sequence, comments, dispositions, outcomes, and timestamps. Multiple review cycles may occur for the same not-yet-approved SCR revision.

**Review Comment**: A versioned finding or question anchored to the reviewed revision. It must be dispositioned before approval when the workflow requires it.

**Approval**: An attributable unanimous decision by every approver selected by the artifact author that a specific submitted snapshot of an SCR, document, procedure, execution/result record, or candidate baseline revision satisfies its defined approval criteria. Approval of an SCR authorizes its contained requirement changes but does not automatically include those revisions in a release baseline.

**Review Cycle**: One submission of an artifact revision to a selected ordered approval sequence. A requested change closes the current cycle and returns an unapproved SCR to Draft at the same revision number. Resubmission creates a new cycle with a new immutable submitted snapshot.

**Approval Sequence**: The ordered list of approvers selected by the SCR author for a review cycle. Review advances one approver at a time. Future, not-yet-reached approvers may be replaced with audit history; completed or active stages cannot be substituted without cancelling and restarting the workflow.

**Cancelled Review Cycle**: A historical review cycle whose decisions no longer count toward approval. Cancellation is required when a completed approval stage used the wrong approver. The corrected workflow restarts from its first stage.

**Electronic Approval Record**: Immutable evidence of approver identity, decision, server time, reviewed exact
revision/snapshot hash, signature meaning, Program, artifact identity, and source address, created only after
session, authority, assignment, independence, and password confirmation succeed.

**Generated Document**: A file produced from a named baseline, template revision, and generator version, with identifiable approval state and integrity hash.

**Artifact Deep Link**: A durable, context-bearing URL for an authoritative page or record. It is permission checked on every request and remains usable after refresh or authentication.

**Universal Search**: A bounded, Program-scoped discovery operation over supported lifecycle record types. Identifier fragments, including suffix fragments, resolve to deep links without revealing unauthorized Programs.

## Traceability and Problem Terms

**Trace Link**: A controlled, typed relationship between exact artifact revisions or, where policy permits, stable artifact identities. It records type, direction, author, time, rationale, state, applicable baseline, and suspect status.

**Suspect Link**: A link whose continuing validity requires reassessment because a linked artifact changed or another defined trigger occurred. Suspect does not automatically mean invalid.

**Problem Report (PR)**: A build-scoped controlled record of an identified product, lifecycle-data, or process
problem. The delivered foundation retains its controlled number, raised date, title, description, additional
information, target build, status and relationships. PRs drive change; requirements do not automatically create
PRs. Any SCR, SWCR or TCR may cite one or more driving PRs. Approved changes appear on the PR as corrective
actions, and applicable executions/results appear as test evidence. Additional classification and closure-policy
depth remains incremental product work rather than implied behavior.

**Impact Analysis**: Identification and review of artifacts, links, tests, results, documents, and releases potentially affected by a proposed change or PR.

## Audit and Security Terms

**Audit Event**: An append-only record of a material action, including actor, time, action, target, relevant previous and new values, and correlation to the originating operation.

**Role**: A named set of permissions and responsibilities, such as author, reviewer, approver, verifier, configuration manager, quality representative, program administrator, or system administrator.

**Independence**: A program-defined constraint preventing a person from performing incompatible authoring, verification, review, or approval roles for the same controlled item.

**Authoritative Source**: The controlled structured artifact records and baselines. Generated documents and imported legacy documents are outputs or inputs with provenance, not parallel masters.
