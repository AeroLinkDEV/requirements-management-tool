import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'

const read = (relative) => readFileSync(new URL(`../../../${relative}`, import.meta.url), 'utf8')
const requester = read('.github/workflows/request-full-ci.yml')
const full = read('.github/workflows/ci.yml')
const fast = read('.github/workflows/fast-pr-feedback.yml')
const reset = read('.github/workflows/reset-full-ci-readiness.yml')

test('ready label requester is trusted-base, exact-label, same-repository, and dispatch-only', () => {
  assert.match(requester, /pull_request_target:/)
  assert.match(requester, /types: \[labeled\]/)
  assert.match(requester, /actions: write/)
  assert.match(requester, /contents: read/)
  assert.match(requester, /pull-requests: read/)
  assert.doesNotMatch(requester, /actions\/checkout|git checkout|git clone/)
  assert.match(requester, /github\.event\.label\.name == 'ready-for-full-ci'/)
  assert.match(requester, /github\.event\.pull_request\.head\.repo\.full_name == github\.repository/)
  assert.match(requester, /actions\/workflows\/ci\.yml\/dispatches/)
})

test('trusted binding requires Product own readiness authentication before accepting Full evidence', () => {
  assert.match(requester, /Classify changed product areas/)
  assert.match(requester, /Authenticate label-dispatched pull-request context/)
  assert.match(requester, /expected exactly one Product classifier job/)
  assert.match(requester, /expected exactly one Product label-dispatch authentication step/)
  assert.match(requester, /Product label-dispatch authentication is not authoritative success/)
  assert.match(requester, /authentication\.get\("status"\) != "completed"/)
  assert.match(requester, /authentication\.get\("conclusion"\) != "success"/)
})

test('full workflow authenticates exact ready PR state before trusting dispatch inputs', () => {
  for (const input of ['pull_request_number', 'pull_request_base_sha', 'pull_request_head_sha']) {
    assert.match(full, new RegExp(`${input}:`))
  }
  assert.match(full, /pull-requests: read/)
  assert.match(full, /      - name: Authenticate label-dispatched pull-request context\n        if:/)
  assert.match(full, /        env:\n          GITHUB_TOKEN:/)
  assert.match(full, /REQUESTED_HEAD_SHA/)
  assert.match(full, /REQUESTED_BASE_SHA/)
  assert.match(full, /ready-for-full-ci is no longer present/)
})

test('immutable head readiness survives harmless base-branch advancement', () => {
  assert.match(requester, /Readiness belongs to the immutable PR head/)
  assert.doesNotMatch(requester, /base SHA moved/)
  assert.doesNotMatch(requester, /pr\.get\("base".*base_sha/)
  assert.match(full, /REQUESTED_BASE_SHA came from the trusted pull_request_target label event/)
  assert.doesNotMatch(full, /pr\.base\.sha -ne \$env:REQUESTED_BASE_SHA/)
  assert.match(requester, /run\.get\("actor".*github-actions\[bot\]/)
  assert.match(requester, /run\.get\("triggering_actor".*github-actions\[bot\]/)
})

test('label-dispatched Full reuses PR classification and exact indentation', () => {
  assert.match(full, /inputs\.pull_request_number \|\| github\.event\.pull_request\.number \|\| github\.ref/)
  assert.match(full, /        env:\n          EVENT_NAME: .*inputs\.pull_request_number/)
  assert.match(full, /          BASE_SHA: .*inputs\.pull_request_base_sha/)
  assert.match(full, /          HEAD_SHA: .*inputs\.pull_request_head_sha/)
})


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

test('Product internal aggregate is distinct from the trusted protected PR binding', () => {
  assert.match(full, /^    name: Full Product evidence aggregate$/m)
  assert.doesNotMatch(full, /^    name: Report what this run validated$/m)
  assert.match(requester, /accepted_names = \{"Report what this run validated", "Full Product evidence aggregate"\}/)
})
