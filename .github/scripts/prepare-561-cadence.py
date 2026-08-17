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
ci.write_text(text, encoding="utf-8")

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
the existing Full Product gate by applying the repository label:

```bash
gh pr edit <number> --add-label ready-for-full-ci
```

The trusted default-branch dispatcher authenticates the live pull request, exact head SHA, base SHA, head ref,
same-repository origin and readiness label before dispatching Full validation. Branch protection still requires
`Report what this run validated`, so the pull request remains blocked until that exact-head Full run succeeds.

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

Issue #561 changes **when** the existing Full evidence is purchased, not what it proves. Ordinary pull-request
open/reopen/synchronize events run the separate advisory Fast workflow. The Product quality gate no longer
starts automatically for each development push.

When `ready-for-full-ci` is applied to the final SHA, a trusted `pull_request_target` dispatcher that never
checks out PR code dispatches this unchanged Product gate against the exact same-repository head. The gate
still selects the same API, browser, production-browser, backend/core, client, PostgreSQL and operator lanes,
and branch protection still requires the same `Report what this run validated` aggregate. There is no
always-green placeholder and Fast is not authoritative.

A synchronize event removes stale readiness, so a later SHA must request Full again. Fast and Full use
different concurrency groups; development feedback cannot cancel final Full evidence. The rolling metrics
collector remains the source for post-switch full-gates-per-merge, cancellation waste, queue/final-push-to-merge
timing and regression data; re-measure the new cadence rather than assuming savings.

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
requester binds PR number, base SHA and exact head SHA and dispatches the existing Full Product workflow.
`Report what this run validated` remains the required branch-protection context.

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
"""
if "test('Full runs only by trusted readiness while Fast stays on development PR updates'" not in text:
    text += test_block
contract.write_text(text, encoding="utf-8")
