import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { execFileSync, spawnSync } from 'node:child_process'
import { resolve } from 'node:path'

const read = (relative) => readFileSync(new URL(`../../../${relative}`, import.meta.url), 'utf8')
const requester = read('.github/workflows/request-full-ci.yml')
const full = read('.github/workflows/ci.yml')
const fast = read('.github/workflows/fast-pr-feedback.yml')
const reset = read('.github/workflows/reset-full-ci-readiness.yml')

test('ready label requester is trusted-base, readiness-gated, same-repository, and dispatch-only', () => {
  assert.match(requester, /pull_request_target:/)
  assert.match(requester, /types: \[labeled\]/)
  assert.match(requester, /actions: write/)
  assert.doesNotMatch(requester, /^  checks: write$/m)
  assert.match(requester, /contents: read/)
  assert.match(requester, /pull-requests: read/)
  assert.doesNotMatch(requester, /actions\/checkout|git checkout|git clone/)
  assert.match(requester, /contains\(github\.event\.pull_request\.labels\.\*\.name, 'ready-for-full-ci'\)/)
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

test('Product aggregate is a real PR-suite job gated by trusted exact-run verification', () => {
  assert.match(full, /^    name: Full Product evidence aggregate$/m)
  assert.doesNotMatch(full, /^    name: Report what this run validated$/m)
  assert.match(requester, /accepted_names = \{"Report what this run validated", "Full Product evidence aggregate"\}/)
  const prProductStart = requester.indexOf('\n  pr-product-aggregate:\n')
  assert.notEqual(prProductStart, -1)
  const prProductJob = requester.slice(prProductStart)
  assert.match(prProductJob, /^  pr-product-aggregate:$/m)
  assert.match(prProductJob, /^    name: Full Product evidence aggregate$/m)
  assert.match(prProductJob, /^    if: always\(\)$/m)
  assert.match(prProductJob, /^    needs: dispatch-and-bind$/m)
  assert.match(prProductJob, /^    permissions: \{\}$/m)
  assert.match(prProductJob, /TRUSTED_REQUEST_RESULT: \$\{\{ needs\.dispatch-and-bind\.result \}\}/)
  assert.match(prProductJob, /if \[ "\$TRUSTED_REQUEST_RESULT" != "success" \]; then/)
  assert.match(prProductJob, /refusing PR Product authority/)
  assert.doesNotMatch(prProductJob, /continue-on-error:\s*true/)
  assert.doesNotMatch(requester, /aerolink-product-evidence:pull-request/)
  assert.ok(
    requester.indexOf('Authenticate live ready PR, dispatch once, and bind exact Product success') <
      requester.indexOf('pr-product-aggregate:'),
    'the PR-associated Product job must depend on exact Product verification',
  )
})

test('an unrelated label cannot dispatch Full when no trusted run exists', () => {
  const noneBranch = requester.match(/            NONE\)\n([\s\S]*?)              ;;/)[1]
  const guard = noneBranch.match(/node -e '([^']+)'/)[1]
  assert.ok(noneBranch.indexOf('node -e') < noneBranch.indexOf('--request POST'))
  assert.match(requester, /REQUEST_LABEL: \$\{\{ github\.event\.label\.name \}\}/)
  for (const label of ['authority-maintenance-requested', 'documentation', '', 'READY-FOR-FULL-CI']) {
    const result = spawnSync(process.execPath, ['-e', guard], {
      env: { ...process.env, REQUEST_LABEL: label }, encoding: 'utf8',
    })
    assert.equal(result.status, 1, label)
    assert.match(result.stderr, /only the readiness label may dispatch Full/)
  }
  const ready = spawnSync(process.execPath, ['-e', guard], {
    env: { ...process.env, REQUEST_LABEL: 'ready-for-full-ci' }, encoding: 'utf8',
  })
  assert.equal(ready.status, 0)
  // Already-running or completed trusted runs continue through the existing verifier, not dispatch.
  assert.match(requester, /FOUND\*\) read -r _ run_id _ _ <<< "\$match" ;;/)
  assert.match(requester, /PENDING\) run_id="" ;;/)
})

test('the actual required aggregate refuses every non-success prerequisite result', () => {
  const job = requester.slice(requester.indexOf('\n  pr-product-aggregate:\n'))
  const script = job.match(/        run: \|\n([\s\S]*)/)[1].replace(/^          /gm, '')
  // Git Bash is already the workflow's shell on Windows; use its installed executable rather than WSL.
  const bash = process.platform === 'win32'
    ? resolve(execFileSync('git', ['--exec-path'], { encoding: 'utf8' }).trim(), '../../../bin/bash.exe')
    : 'bash'
  for (const result of ['success', 'failure', 'skipped', 'cancelled', '', 'in_progress']) {
    const run = spawnSync(bash, ['--noprofile', '--norc', '-c', script], {
      env: { ...process.env, TRUSTED_REQUEST_RESULT: result, GITHUB_STEP_SUMMARY: '/dev/null' },
      encoding: 'utf8', timeout: 10_000,
    })
    assert.ifError(run.error)
    assert.equal(run.status, result === 'success' ? 0 : 1, result)
    if (result !== 'success') assert.match(run.stdout, /refusing PR Product authority/)
  }
})
