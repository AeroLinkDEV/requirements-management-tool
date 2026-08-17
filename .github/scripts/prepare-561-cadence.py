from pathlib import Path


def require_once(text: str, needle: str, label: str) -> None:
    count = text.count(needle)
    if count != 1:
        raise SystemExit(f"expected exactly one {label}, found {count}")


ci = Path(".github/workflows/ci.yml")
text = ci.read_text(encoding="utf-8")
needle = "on:\n  pull_request:\n"
require_once(text, needle, "automatic PR trigger header")
text = text.replace(needle, "on:\n", 1)

browser_pr_old = "    if: (github.event_name == 'pull_request' || github.event_name == 'merge_group') && needs.changes.outputs.browser == 'true'\n"
browser_pr_new = "    if: (github.event_name == 'pull_request' || github.event_name == 'merge_group' || (github.event_name == 'workflow_dispatch' && inputs.pull_request_number != '')) && needs.changes.outputs.browser == 'true'\n"
require_once(text, browser_pr_old, "browser-pr event guard")
text = text.replace(browser_pr_old, browser_pr_new, 1)

browser_full_old = "    if: (github.event_name == 'schedule' || github.event_name == 'workflow_dispatch') && needs.changes.outputs.browser == 'true'\n"
browser_full_new = "    if: (github.event_name == 'schedule' || (github.event_name == 'workflow_dispatch' && inputs.pull_request_number == '' && inputs.full_diagnostics == true)) && needs.changes.outputs.browser == 'true'\n"
require_once(text, browser_full_old, "browser-full diagnostics guard")
text = text.replace(browser_full_old, browser_full_new, 1)

gate_event_old = "          EVENT_NAME: ${{ github.event_name }}\n"
gate_event_new = "          EVENT_NAME: ${{ inputs.pull_request_number != '' && 'pull_request' || github.event_name }}\n"
require_once(text, gate_event_old, "protected gate event normalization")
text = text.replace(gate_event_old, gate_event_new, 1)

metrics_old = """          if [ \"${{ needs.changes.outputs.browser }}\" = \"true\" ]; then
            if [ \"${{ github.event_name }}\" = \"pull_request\" ] || [ \"${{ github.event_name }}\" = \"merge_group\" ]; then
              needs=\"$needs,browser-pr\"
            fi
"""
metrics_new = """          effective_event=\"${{ inputs.pull_request_number != '' && 'pull_request' || github.event_name }}\"
          if [ \"${{ needs.changes.outputs.browser }}\" = \"true\" ]; then
            if [ \"$effective_event\" = \"pull_request\" ] || [ \"$effective_event\" = \"merge_group\" ]; then
              needs=\"$needs,browser-pr\"
            fi
"""
require_once(text, metrics_old, "gate metrics browser dependency accounting")
text = text.replace(metrics_old, metrics_new, 1)

gate_comment_old = """  # This job always runs, fails if anything it waited on failed, and writes down what was and was not
  # validated. It is the one check worth requiring for a merge: it cannot be skipped into a pass, and its
  # summary says in words what the tick is claiming.
"""
gate_comment_new = """  # This job always runs, fails if anything it waited on failed, and writes down what was and was not
  # validated. For readiness-dispatched pull requests the trusted default-branch requester waits on this
  # internal aggregate, re-authenticates the exact PR identity, and is the only workflow allowed to emit the
  # protected `Report what this run validated` context.
"""
require_once(text, gate_comment_old, "Product aggregate authority comment")
text = text.replace(gate_comment_old, gate_comment_new, 1)

gate_name_old = "    name: Report what this run validated\n"
gate_name_new = "    name: Full Product evidence aggregate\n"
require_once(text, gate_name_old, "Product aggregate job name")
text = text.replace(gate_name_old, gate_name_new, 1)

metrics_name_old = "      METRICS_JOB_NAME: Report what this run validated\n"
metrics_name_new = "      METRICS_JOB_NAME: Full Product evidence aggregate\n"
require_once(text, metrics_name_old, "Product aggregate metrics name")
text = text.replace(metrics_name_old, metrics_name_new, 1)
ci.write_text(text, encoding="utf-8")

workflow_jobs = Path("product/test-planner/lib/workflow-jobs.mjs")
text = workflow_jobs.read_text(encoding="utf-8")
const_old = "const EVENT = /^github\\.event_name$/\n"
const_new = const_old + "const INPUT = /^inputs\\.(pull_request_number|full_diagnostics)$/\n"
require_once(text, const_old, "workflow input evaluator constant marker")
text = text.replace(const_old, const_new, 1)

resolve_old = """  if (EVENT.test(token)) return context.event
  const value = literal(token)
"""
resolve_new = """  if (EVENT.test(token)) return context.event
  if (INPUT.test(token)) {
    const key = INPUT.exec(token)[1]
    return String(context.inputs[key])
  }
  if (token === 'true' || token === 'false') return token
  const value = literal(token)
"""
require_once(text, resolve_old, "workflow input evaluator resolve block")
text = text.replace(resolve_old, resolve_new, 1)

select_old = "export function selectJobs(workflowText, classification, { event = 'pull_request', postMergeSkip = false } = {}) {\n  const context = {\n    event,\n    outputs: {\n"
select_new = "export function selectJobs(workflowText, classification, { event = 'pull_request', postMergeSkip = false, inputs = {} } = {}) {\n  const context = {\n    event,\n    inputs: {\n      // The local planner normally models an ordinary PR, where workflow-dispatch inputs do not exist.\n      // Empty/false match GitHub's effective defaults for those branches of the workflow conditions.\n      pull_request_number: inputs.pull_request_number ?? '',\n      full_diagnostics: inputs.full_diagnostics ?? false,\n    },\n    outputs: {\n"
require_once(text, select_old, "workflow input evaluator context block")
text = text.replace(select_old, select_new, 1)
workflow_jobs.write_text(text, encoding="utf-8")

merging = Path("product/docs/MERGING.md")
text = merging.read_text(encoding="utf-8")
start_marker = "## Arm it and walk away\n"
end_marker = "## What auto-merge does not do\n"
if start_marker not in text or end_marker not in text:
    raise SystemExit("MERGING.md cadence replacement markers missing")
start = text.index(start_marker)
end = text.index(end_marker)
if end <= start:
    raise SystemExit("MERGING.md cadence replacement markers are out of order")
replacement = """## Request Full CI only when the current SHA is merge-ready

Every pull-request SHA receives **advisory Fast feedback automatically**. Fast is for development feedback; it
is not merge authority and cannot satisfy branch protection.

When implementation and review are finished and the **current SHA is the one you intend to merge**, request
Full Product evidence by applying the repository label:

```bash
gh pr edit <number> --add-label ready-for-full-ci
```

The trusted default-branch `pull_request_target` requester authenticates the live pull request, exact head SHA,
base SHA, head ref, same-repository origin and readiness label. It dispatches Product Full exactly once for that
SHA, waits for the internal `Full Product evidence aggregate`, then re-authenticates the PR before its own
protected `Report what this run validated` job can succeed. Fast cannot satisfy that protected context.

Only after readiness is requested for the final SHA, arm auto-merge:

```bash
gh pr merge <number> --squash --auto
```

If another commit is pushed, the trusted synchronize guard removes `ready-for-full-ci`. The old Full result
belongs to the old SHA and cannot authorize the new one; finish the fix, then request readiness again.

To disarm auto-merge:

```bash
gh pr merge <number> --disable-auto
```

"""
merging.write_text(text[:start] + replacement + text[end:], encoding="utf-8")

feedback = Path("product/docs/BROWSER_AND_BACKEND_FEEDBACK_TIME.md")
text = feedback.read_text(encoding="utf-8")
marker = "## Measured, 2026-08-13\n"
if marker not in text:
    raise SystemExit("feedback-time insertion marker missing")
section = """## Merge-ready Full-CI cadence, 2026-08-17

Issue #561 changes **when** Full evidence is purchased, not what the final evidence proves. Ordinary pull-request
open/reopen/synchronize events run the separate advisory Fast workflow. The Product quality gate no longer
starts automatically for each development push.

When `ready-for-full-ci` is applied to the final SHA, the trusted default-branch `pull_request_target` requester
that never checks out PR code dispatches Product Full against the exact same-repository head. Product keeps the
same API, browser, production-browser, backend/core, client, PostgreSQL and operator lanes and records them in
the internal `Full Product evidence aggregate`. Only after that exact aggregate succeeds and the live PR identity
is re-authenticated does the trusted requester satisfy branch protection with `Report what this run validated`.
There is no always-green placeholder and Fast is not authoritative.

A synchronize event removes stale readiness, so a later SHA must request Full again. Fast and Full use different
concurrency groups; development feedback cannot cancel final Full evidence. The rolling metrics collector remains
the source for post-switch full-gates-per-merge, cancellation waste, queue/final-push-to-merge timing and
regression data; re-measure the new cadence rather than assuming savings.

"""
if "## Merge-ready Full-CI cadence, 2026-08-17\n" not in text:
    feedback.write_text(text.replace(marker, section + marker, 1), encoding="utf-8")

planner = Path("product/test-planner/README.md")
text = planner.read_text(encoding="utf-8")
marker = "## Pull-request overlap advisory\n"
if marker not in text:
    raise SystemExit("planner guidance insertion marker missing")
section = """## Merge-ready Full CI

GitHub pull requests receive the Fast workflow automatically; both local `-Mode Fast` and hosted Fast remain
non-authoritative. Once the current PR SHA is final, apply `ready-for-full-ci`. The trusted default-branch
requester binds PR number, base SHA and exact head SHA, dispatches Product Full once, waits for the internal
`Full Product evidence aggregate`, then re-authenticates the live PR before its protected
`Report what this run validated` job can succeed.

Do not apply readiness early. A later push removes the label through the trusted synchronize guard and the new
SHA must request Full again. Do not use Fast success, an older SHA's Full result, or a placeholder check as
merge evidence.

"""
if "## Merge-ready Full CI\n" not in text:
    planner.write_text(text.replace(marker, section + marker, 1), encoding="utf-8")

contract = Path("product/test-planner/tests/full-ci-readiness-dispatch.test.mjs")
text = contract.read_text(encoding="utf-8")
const_marker = "const full = read('.github/workflows/ci.yml')\n"
if const_marker not in text:
    raise SystemExit("full-CI contract constant marker missing")
if "const fast = read('.github/workflows/fast-pr-feedback.yml')" not in text:
    text = text.replace(
        const_marker,
        const_marker
        + "const fast = read('.github/workflows/fast-pr-feedback.yml')\n"
        + "const reset = read('.github/workflows/reset-full-ci-readiness.yml')\n",
        1,
    )

test_block = r"""

test('Full runs only by trusted readiness while Fast stays on development PR updates', () => {
  assert.doesNotMatch(full, /^  pull_request:\s*$/m)
  for (const trigger of ['merge_group', 'push', 'schedule', 'workflow_dispatch']) {
    assert.match(full, new RegExp(`^  ${trigger}:`, 'm'))
  }
  assert.match(fast, /^  pull_request:\n    types: \[opened, synchronize, reopened, ready_for_review\]$/m)
  assert.match(reset, /^  pull_request_target:\n    types: \[synchronize\]$/m)
})

test('trusted readiness dispatch preserves ordinary PR browser and gate semantics', () => {
  assert.ok(full.includes("if: (github.event_name == 'pull_request' || github.event_name == 'merge_group' || (github.event_name == 'workflow_dispatch' && inputs.pull_request_number != '')) && needs.changes.outputs.browser == 'true'"))
  assert.ok(full.includes("if: (github.event_name == 'schedule' || (github.event_name == 'workflow_dispatch' && inputs.pull_request_number == '' && inputs.full_diagnostics == true)) && needs.changes.outputs.browser == 'true'"))
  assert.ok(full.includes("EVENT_NAME: ${{ inputs.pull_request_number != '' && 'pull_request' || github.event_name }}"))
  assert.ok(full.includes("effective_event=\"${{ inputs.pull_request_number != '' && 'pull_request' || github.event_name }}\""))
  assert.doesNotMatch(full, /if: \(github\.event_name == 'schedule' \|\| github\.event_name == 'workflow_dispatch'\) && needs\.changes\.outputs\.browser == 'true'/)
})

test('only trusted default-branch requester carries the protected check name', () => {
  assert.match(requester, /pull_request_target:/)
  assert.match(requester, /Report what this run validated/)
  assert.match(requester, /Full Product evidence aggregate/)
  assert.doesNotMatch(requester, /actions\/checkout|git checkout|git clone/)
  assert.match(full, /  gate:\n    name: Full Product evidence aggregate/)
  assert.doesNotMatch(full, /  gate:\n    name: Report what this run validated/)
})
"""
if "test('Full runs only by trusted readiness while Fast stays on development PR updates'" not in text:
    text += test_block
contract.write_text(text, encoding="utf-8")
