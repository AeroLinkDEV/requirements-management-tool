(Retained copy of the packet submitted to Astra via Sean on 2026-09-24: the six implementation findings corrected in PR #1098, new candidate 41b75ced.)

ASTRA REVIEW REQUEST — #1040 / PR #1098 — IMPLEMENTATION CANDIDATE v2 (the six findings corrected)

**Requested decision and next action:**
The six findings are corrected in the existing PR #1098 worktree and the same two files; the requester and relevant contract suites were rerun; a new committed candidate with exact provenance and an accurate coverage statement is below. No #1066 code/head change, no rebase, no readiness label, no maintenance approval request, no live dispatch, no queue admission, no merge, no HOME action. Installation and rollback remain unresolved under the protected-workflow transition contract.

**CANDIDATE AND PR:**
- PR #1098 (draft, base main): new head **41b75ced653eb918bdffa805079cfcf8de4d8eb3**.
- Commits: `eace33f6` (initial candidate) → `efe0f53a` (the six corrections) → `41b75ced` (remove a temporary transport trace from the integrated harness). Base unchanged: `bac43d094cff642766eda2b91fd9d6b41d15a23f`.
- Diff: still exactly the same two files.

**I01 — the gate now terminates the dispatch path.** `authorize_dispatch` wraps the gate verdict: `ALLOWED` proceeds; `REFUSED` exits with `::error::Dispatch refused: <reason>`; empty/unexpected output exits as unexpected. The POST is unreachable on refusal. Exercised through the real dispatcher in the integrated harness: stale, consumed, rerun, and missing-self each end with **zero transport POSTs** (asserted).

**I02 — history collection wired to the real API contract.** `collect_pages` takes (prefix, base-url); page 1 appends "?", later pages "&" (both endpoint shapes work). The gate validates each endpoint's response shape before use: issue-event pages must be bare JSON arrays; run-list pages must be objects with a `workflow_runs` array; the Product transcript must be an object with `workflow_runs` — wrong shapes refuse (`unexpected ... history response shape` / `unexpected Product run transcript shape`) instead of crashing or reasoning over nothing. Exercised with real-shaped payloads (arrays vs wrapped objects), and shape violations at all three endpoints assert refusals (unit level, t20-area + integrated refusals).

**I03 — coherent pinning protocol.** `check-product-run.py` has explicit modes: `pin` validates every identity/trust check and reports `ATTEMPT <n>` for a running or successful run (a completed failure is never pinned — refused); `poll` derives terminal states (`RUNNING`/`SUCCEEDED`/`FAILED <conclusion>`) and refuses attempt changes. `fetch_and_pin_run` consumes pin mode; `poll_pinned` consumes poll mode. Exercised helper+caller together in the integrated scenarios for both running and already-completed records (response-ID pinning, discovery pinning, and authoritative-success reuse all bind with the recorded attempt).

**I04 — uncertain transport handling.** The dispatch curl's exit status and HTTP code are captured (`|| curl_status=$?`, `--write-out` on stdout). Transport error, non-2xx, or missing identity → `::warning::Dispatch outcome uncertain ...` → boundary-filtered discovery only, bounded wait, refusal; exactly ONE POST always. Exercised: lost response with delayed visibility (recovers by discovery, 1 POST) and lost-never-resolves (bounded refusal, 1 POST).

**I05 — PENDING selects the newest UNFINISHED trusted attempt.** Mixed transcript executed: run 100 in_progress + run 101 completed failure → `PENDING 100`.

**I06 — integrated harness executing the actual workflow shell and embedded Python together.** The full run-block body is extracted from the workflow, dedented, and executed verbatim against a stateful Python curl replacement (per-endpoint routing, paginated histories, stateful run records, POST capture). POST counts assert at the transport boundary. Scenarios: eligible NONE and EXHAUSTED dispatches (1 POST each, bound with pinned attempt); every refusal family with 0 POSTs (stale, consumed across statuses, predating, rerun, missing-self, shape violations); authoritative-success reuse (0 POSTs, full identity + qualification recheck); PENDING and terminal-first discovery; lost-response recovery and never-resolution (1 POST each); queued-successor consumption; trust-change, attempt-change, and 404 refusals after pinning; and the exact per-attempt jobs request (`/actions/runs/101/attempts/1/jobs?per_page=100`) with rejection of failing qualification evidence. Unit and structural tests are retained and now labeled as unit/structural in the evidence narrative.

**EXECUTED LOCAL QUALIFICATION:**
- `AEROLINK_TEST_PYTHON=<runtime> node --test product/test-planner/tests/full-ci-readiness-dispatch.test.mjs` → **27 tests: 18 passed / 9 skipped / 0 failed**. The 9 skipped are the integrated scenarios: they require a POSIX runtime — the Windows/MSYS path translation breaks their file-boundary semantics (reproduced and documented during development); they execute in the ubuntu Product gate. All non-integrated coverage (structural controls, matcher/gate/pinned-check embedded-Python execution, pagination and initial-decision shell fragments) passes locally.
- Every `product/test-planner/tests/*.test.mjs` (15 files) → **0 failures**.
- Workflow YAML parse-validated after the edits.
- Provenance: worktree clean before and after; only the two files committed.

**REMAINING LIMITS (unchanged plus one):** `return_run_details` response shape requires pinned-API-version qualification at acceptance; no live API qualification was performed; the integrated scenarios' executed runs happen in the ubuntu Product gate (their local Windows execution is blocked as documented); installation and rollback remain separately unresolved — merging this draft does not establish an installation route.

**CONFIRMATION:** #1066 remains draft, auto-merge disabled, clean at `027c985e82d1d1b764a2f24eac6e6c2ed4ec2c71`; its worktree untouched. #1040 open. Checkpoint D NOT passed. D01–D03 closed.

Please return your verdict and the precise next action — I will wait for Sean to relay your actual response.

CHECKPOINT D — AWAITING ASTRA REVIEW. Queue admission, merge, rollout and issue closure remain gated.
