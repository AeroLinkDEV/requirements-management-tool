# August 2 Afternoon Observation Reconciliation

This is the implementation audit for `Aug 2nd afternoon observations.docx` and the product-owner answers that
followed it. The source Word document remains unmodified. GitHub issues #277-#280 converted the observations
into focused acceptance criteria; this record explains where each observation landed and prevents a visually
small change from being mistaken for the complete request.

## Observation-to-delivery map

| Source observation | Accepted outcome | Delivery |
| --- | --- | --- |
| 1-6, 39: sign-in code/security controls, marketing text, live-data and footer claims | Hide unfinished MFA/account-security UI; retain backend capability; enlarge AeroLink mark; use concise sign-in copy | #277 / PR #281; browser coverage checks every removed control and phrase |
| 7-9, 42: Command Center, released-build, Requirements Explorer, and build-card prose | Remove non-actionable and marketing copy without removing real controls | #277 / PR #281; Build 1.5 remains explicitly released/read-only |
| 12: wrong change level in an LLR review context | Preserve exact System/HLR/LLR requirement, CR, procedure, and review scope | Existing build-scope controls plus #278 / PR #282 regression coverage |
| 14: unknown baseline label and incomplete Digital Thread | Show a real exact SYSR → HLR → LLR → procedure → result → evidence → build path; retain focused traversal | #280; exact-baseline API projection and browser regression |
| 16: PR picker lists every PR | Search on demand; show only already-linked PRs, with remove and add-another behavior | #277 / PR #281 and the existing server-scoped PR picker |
| 19: artifact number and wording are cramped | Add consistent vertical separation between controlled identity and statement | #277 / PR #281 |
| 21: supporting content mirrors the statement | Start supporting content empty while retaining paragraph/table/image authoring | #277 / PR #281 |
| 22: “Leave where it is” section choice | Hydrate and select the requirement's current section for Modify work | #277 / PR #281 |
| 23-26: downstream decisions and SWCR actions belong inside the selected assessment | Queue rows open the workbench; decision, create/link, submit, approve, and return actions live inside it and appear only when applicable | #278 / PR #282 |
| 26: “impact controlled” collapses two different facts | Show the assessment conclusion separately from each linked SWCR's current state | #278 / PR #282 |
| 27: CR author defaults to creator | Authenticated creator remains the immutable author; ownership/assignment is the later-change mechanism | Existing server-authoritative identity plus #278 verification |
| 28: Testing Coverage lacks downstream decision depth | Verification decision drawers carry source change, exact requirements, proposed procedures, PRs, responsibility, rationale, history, return, and independent approval | #278 / PR #282 |
| 29-32: PR fields, creation, filters, and hidden history | Deliver the agreed progressive field set, lifecycle, AND filters, Corrective Actions, closure evidence, and internal History tab | #279 / PR #283 |
| 33-34: System procedures cannot be opened/created | Real procedures, history, create completion, modal exit, direct link, and refresh are exercised | #278 / PR #282 qualification |
| 35: autosave/check-in meaning | Autosave retains an attributable draft snapshot; Save and check in applies the controlled working copy and closes the checkout | Existing universal controlled editing contract, retained and tested |
| 37: Engineering should be Requirements; PR is standalone | Requirements group contains Change Requests, Requirements Explorer, Documents, and Digital Thread; Verification remains grouped; Problem Reports has no disclosure arrow | #280 navigation regression |
| 40: future-build icon | Add a non-record **Plan next build** placeholder after 1.6; create no route, release, version, or baseline | #280 build-lineage regression |
| 44: standalone Code area | Add Code between Verification and Problem Reports; Build 1.5 is historical and Build 1.6 active | #280 Code/GitLab regression |

## Confirmed Problem Report policy

The follow-up answers are implemented as one combined MVP, not as disconnected fields:

- lifecycle: Draft → Ready for SCCB → Open → Implementing → Verifying → Awaiting SQA Closure → Closed;
- only Title and rich Problem Description are required to retain a Draft;
- raised-by/date are automatic and immutable; owner and one target build may be changed with history;
- Additional Information, Problem Description, Proposed Corrective Action, and Root Cause support rich content;
- System requirements, HLR, LLR, Code, Tests, Documents, combined System/Aircraft, and Safety use
  Unknown/No/Yes impact decisions;
- an owner action or a linked Draft-or-later CR may enter Implementing;
- an approver performs the light SCCB Open action; no SCCB signature is invented yet;
- approved linked CRs are automatic read-only Corrective Action cards;
- only test results deliberately selected to support closure appear as Test Evidence;
- History is a tab; filters combine with AND; saved views, attachments, containment, preventive action, and
  fine-grained classification remain outside this increment;
- SQA closure is independent, and released Build 1.5 is immutable.

## Code and build boundary

GitLab is the source of truth for code, MRs, reviews, CI, and commit content. AeroLink stores only an immutable
build-scoped pointer from an exact approved LLR revision to a GitLab MR and merge SHA, or a justified no-code
decision. Seeded records are visibly marked demonstration data. The future-build card is only a planning entry
point; it does not create a hidden or fake build.

## Qualification evidence

The regression set covers removed sign-in and marketing UI, requirement authoring defaults, HLR/LLR isolation,
downstream and verification workbenches, System procedure creation/history, the PR lifecycle and fields,
navigation order, Code deep links and released-build protection, the future-build placeholder, and the complete
Digital Thread path. Domain, API, infrastructure, client build/lint, browser, PostgreSQL migration, CI, and
exact-merge requalification are the delivery gates.
