import { test } from 'node:test'
import assert from 'node:assert/strict'
import { chmodSync, existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync, mkdirSync } from 'node:fs'
import { execFileSync, spawnSync } from 'node:child_process'
import { join, resolve, sep } from 'node:path'
import { tmpdir } from 'node:os'

const read = (relative) => readFileSync(new URL(`../../../${relative}`, import.meta.url), 'utf8').replace(/\r\n/g, '\n')
const requester = read('.github/workflows/request-full-ci.yml')
const full = read('.github/workflows/ci.yml')
const fast = read('.github/workflows/fast-pr-feedback.yml')
const reset = read('.github/workflows/reset-full-ci-readiness.yml')
const bash = process.platform === 'win32'
  ? resolve(execFileSync('git', ['--exec-path'], { encoding: 'utf8' }).trim(), '../../../bin/bash.exe')
  : 'bash'

// The embedded Python helpers are executed for real when a runtime is available (the Product
// gate's ubuntu runners ship one; local runs may point AEROLINK_TEST_PYTHON at any runtime).
const pythonExecutable = process.env.AEROLINK_TEST_PYTHON || 'python'
const pythonReady = spawnSync(pythonExecutable, ['--version'], { encoding: 'utf8' }).status === 0
const skipWithoutPython = pythonReady ? false : 'no Python runtime available for real-execution tests'

let scratchCounter = 0

function pythonBlock(afterAnchor, marker = 'PY') {
  const anchorAt = requester.indexOf(afterAnchor)
  assert.notEqual(anchorAt, -1, `anchor missing: ${afterAnchor}`)
  const heredocAt = requester.indexOf(`<<'${marker}'`, anchorAt + afterAnchor.length - 1)
  assert.notEqual(heredocAt, -1, `heredoc missing after: ${afterAnchor}`)
  const from = requester.indexOf('\n', heredocAt) + 1
  const terminator = requester.indexOf(`\n          ${marker}`, from)
  assert.notEqual(terminator, -1, `terminator missing for: ${afterAnchor}`)
  return requester.slice(from, terminator + 1).replace(/^ {10}/gm, '')
}

function runPython(source, args, env = {}) {
  const scratch = mkdtempSync(join(tmpdir(), 'aerolink-requester-py-'))
  try {
    const file = join(scratch, 'embedded.py')
    writeFileSync(file, source)
    return spawnSync(pythonExecutable, [file, ...args], {
      encoding: 'utf8', timeout: 20_000, env: { ...process.env, ...env },
    })
  } finally {
    rmSync(scratch, { recursive: true, force: true })
  }
}

// ---- structural contracts (preserved controls) ------------------------------------

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

test('the actual required aggregate refuses every non-success prerequisite result', () => {
  const job = requester.slice(requester.indexOf('\n  pr-product-aggregate:\n'))
  const script = job.match(/        run: \|\n([\s\S]*)/)[1].replace(/^          /gm, '')
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

// ---- same-head retry: structure of the corrected orchestration ---------------------

test('retry orchestration keeps one common gate before every dispatch POST and never dispatches from refresh lanes', () => {
  assert.match(requester, /authorize_dispatch\(\) \{/)
  assert.match(requester, /dispatch_full_run\(\) \{/)
  const dispatcherAt = requester.indexOf('dispatch_full_run() {')
  const dispatcherEnd = requester.indexOf('\n          }\n', dispatcherAt)
  const dispatcherBody = requester.slice(dispatcherAt, dispatcherEnd)
  assert.match(dispatcherBody, /authorize_dispatch$/m)
  assert.match(dispatcherBody, /return_run_details/)
  assert.match(dispatcherBody, /actions\/workflows\/ci\.yml\/dispatches/)
  assert.equal((requester.match(/^ {14,16}dispatch_full_run$/gm) || []).length, 2)
  assert.match(requester, /EXHAUSTED\*[\s\S]*?ready-for-full-ci" \]; then[\s\S]*?dispatch_full_run/)
  assert.match(requester, /a refresh cannot dispatch Full/)
  const noneBranch = requester.match(/NONE\)\n([\s\S]*?)\n            \*\) echo/)
  assert.match(noneBranch[1], /REQUEST_LABEL" = "ready-for-full-ci"/)
})

test('the gate demands mandatory requester identity, first attempts, self-postdating and spend-once consumption', () => {
  assert.match(requester, /if self_run_attempt != 1:/)
  assert.match(requester, /requester rerun \(attempt %s\)/)
  assert.match(requester, /own\.get\("event"\) != "pull_request_target"/)
  assert.match(requester, /own\.get\("head_sha"\) != head_sha/)
  assert.match(requester, /identity is missing from the history or mismatches this head/)
  assert.match(requester, /not own_created > latest/)
  assert.match(requester, /created >= latest/)
  assert.match(requester, /authorization already consumed by requester run/)
  assert.match(requester, /requester predates the authorization/)
  assert.match(requester, /for page in 1 2 3/)
  assert.match(requester, /rel="next"/)
  assert.match(requester, /History pagination did not complete within the bounded walk/)
  assert.match(requester, /unestablishable failure boundary/)
  assert.match(requester, /unestablishable readiness label timestamp/)
  assert.match(requester, /unestablishable requester timestamp/)
})

test('uncertain dispatch outcomes end in refusal; the dispatched run is pinned by response identity or boundary-filtered discovery', () => {
  assert.match(requester, /return_run_details/)
  assert.match(requester, /workflow_run_id/)
  assert.match(requester, /int\(run\.get\("id", 0\)\) > boundary_id/)
  assert.match(requester, /attempts\/\$PINNED_ATTEMPT\/jobs/)
  assert.match(requester, /poll-check/)
  assert.doesNotMatch(requester, /filter=latest/)
  assert.match(requester, /cannot bind evidence to the selected attempt/)
  assert.match(requester, /attempt \$PINNED_ATTEMPT\) to PR #/)
  assert.match(requester, /pinned run attempt changed/)
})

test('refresh lanes and stale/exhausted product states never dispatch; only a qualified readiness request does', () => {
  const noneBranch = requester.match(/NONE\)\n([\s\S]*?)\n            \*\) echo/)
  assert.match(noneBranch[1], /REQUEST_LABEL" = "ready-for-full-ci"/)
  assert.match(requester, /REQUEST_LABEL" != "ready-for-full-ci"/)
  assert.match(requester, /a refresh cannot dispatch Full/)
  assert.match(requester, /NONE\) sleep 10; continue ;;/)
})

// ---- executed embedded Python: the exact-head matcher -------------------------------

function productRun(id, status, conclusion, updated = '2026-09-23T10:19:05Z', overrides = {}) {
  const run = {
    id, head_sha: 'a'.repeat(40), head_branch: 'glm/some-branch', event: 'workflow_dispatch',
    actor: { login: 'github-actions[bot]' }, triggering_actor: { login: 'github-actions[bot]' },
    pull_requests: [{ number: 1066 }], status: 'in_progress', run_attempt: 1,
    created_at: '2026-09-23T09:55:00Z', updated_at: updated, ...overrides,
  }
  if (status === 'completed') { run.status = 'completed'; run.conclusion = conclusion }
  return run
}

test('the exact-head matcher verb selection executes against scripted transcripts', { skip: skipWithoutPython }, () => {
  const source = pythonBlock('find_product_run() {')
  const sha = 'a'.repeat(40)
  const scratch = mkdtempSync(join(tmpdir(), 'aerolink-requester-matcher-'))
  try {
    const transcript = (runs) => {
      const path = join(scratch, `runs-${scratchCounter++}.json`)
      writeFileSync(path, JSON.stringify({ workflow_runs: runs }))
      return path
    }
    const run = (file, boundary = '') => runPython(source, [file, sha, '1066', 'glm/some-branch'], { BOUNDARY_ID: boundary })

    let out = run(transcript([])).stdout.trim()
    assert.equal(out, 'NONE')

    out = run(transcript([productRun(100, 'completed', 'failure', '2026-09-23T10:19:05Z', { triggering_actor: { login: 'seanmccarthyns' } })])).stdout.trim()
    assert.equal(out, 'NONE') // untrusted attempts are invisible

    out = run(transcript([productRun(101, 'in_progress', undefined, '2026-09-23T12:10:00Z'), productRun(100, 'in_progress')])).stdout.trim()
    assert.equal(out, 'PENDING 101')

    // PENDING must attach to an UNFINISHED attempt: a completed failure among the
    // matches is already accounted for by EXHAUSTED reporting and is never selected.
    out = run(transcript([productRun(100, 'in_progress'), productRun(101, 'completed', 'failure', '2026-09-23T12:30:00Z')])).stdout.trim()
    assert.equal(out, 'PENDING 100')

    out = run(transcript([productRun(100, 'completed', 'failure'), productRun(101, 'completed', 'failure', '2026-09-23T12:30:00Z')])).stdout.trim()
    assert.match(out, /^EXHAUSTED 101 failure 2026-09-23T12:30:00Z$/)

    out = run(transcript([productRun(300, 'completed', 'success'), productRun(200, 'completed', 'success')])).stdout.trim()
    assert.equal(out, 'FOUND 200 completed success')

    // post-dispatch discovery: pre-boundary history can neither bind nor shadow
    out = run(transcript([productRun(100, 'completed', 'success'), productRun(101, 'completed', 'success')]), '100').stdout.trim()
    assert.equal(out, 'FOUND 101 completed success')
    out = run(transcript([productRun(100, 'completed', 'success'), productRun(101, 'in_progress')]), '100').stdout.trim()
    assert.equal(out, 'PENDING 101')
    out = run(transcript([productRun(100, 'completed', 'failure')]), '100').stdout.trim()
    assert.equal(out, 'NONE')
  } finally {
    rmSync(scratch, { recursive: true, force: true })
  }
})

// ---- executed embedded Python: the common authorization gate -------------------------

function labeledEvent(id, created, name = 'ready-for-full-ci', actor = 'seanmccarthyns') {
  return { id, event: 'labeled', label: { name }, created_at: created, actor: { login: actor } }
}

function requesterRun(id, created, { status = 'completed', attempt = 1, sha = 'a'.repeat(40) } = {}) {
  return {
    id, head_sha: sha, event: 'pull_request_target', created_at: created, updated_at: created,
    status, conclusion: status === 'completed' ? 'failure' : null, run_attempt: attempt,
  }
}

test('the common dispatch gate executes: freshness, spend-once, identity, ties, histories and shapes', { skip: skipWithoutPython }, () => {
  const source = pythonBlock('authorize_dispatch() {')
  const sha = 'a'.repeat(40)
  const scratch = mkdtempSync(join(tmpdir(), 'aerolink-requester-gate-'))
  try {
    let caseCounter = 0
    const runGate = ({ events, requester = [], runs = [], selfRunId, selfAttempt = 1, shapes = {} }) => {
      const prefix = join(scratch, `case-${caseCounter++}`)
      mkdirSync(prefix, { recursive: true })
      writeFileSync(join(prefix, 'events.page.1'),
        shapes.events === 'flat-object' ? JSON.stringify({ items: events }) : JSON.stringify(events))
      writeFileSync(join(prefix, 'requester.page.1'),
        shapes.requester === 'flat-array' ? JSON.stringify(requester) : JSON.stringify({ workflow_runs: requester }))
      writeFileSync(join(prefix, 'product-runs.json'),
        shapes.runs === 'flat-array' ? JSON.stringify(runs) : JSON.stringify({ workflow_runs: runs }))
      return runPython(source, [prefix, sha, 'glm/some-branch', '1066', String(selfRunId), String(selfAttempt)])
    }
    const freshEvents = [
      labeledEvent(1, '2026-09-22T10:49:17Z'),
      labeledEvent(2, '2026-09-23T12:00:00Z'),
    ]
    const staleEvents = [labeledEvent(1, '2026-09-23T09:54:44Z')]
    const staleRun = (updated) => productRun(100, 'completed', 'failure', updated)

    let out = runGate({ events: freshEvents, requester: [requesterRun(7000, '2026-09-23T12:00:05Z')], selfRunId: 7000 }).stdout.trim()
    assert.match(out, /^ALLOWED /)

    out = runGate({ events: staleEvents, requester: [requesterRun(7000, '2026-09-23T10:00:00Z')], runs: [staleRun('2026-09-23T10:19:05Z')], selfRunId: 7000 }).stdout.trim()
    assert.match(out, /^REFUSED .*stale/)

    for (const status of ['pending', 'in_progress', 'cancelled', 'completed']) {
      out = runGate({
        events: freshEvents,
        requester: [requesterRun(7000, '2026-09-23T12:00:05Z'), requesterRun(6000, '2026-09-23T12:00:10Z', { status })],
        selfRunId: 7000,
      }).stdout.trim()
      assert.match(out, /^REFUSED .*consumed by requester run 6000$/, status)
    }
    out = runGate({
      events: freshEvents,
      requester: [requesterRun(7000, '2026-09-23T12:00:05Z'), requesterRun(6000, '2026-09-23T12:00:00Z')],
      selfRunId: 7000,
    }).stdout.trim()
    assert.match(out, /^REFUSED .*consumed/) // tie consumes

    out = runGate({
      events: [labeledEvent(1, '2026-09-22T10:49:17Z'), labeledEvent(2, '2026-09-23T13:00:00Z')],
      requester: [requesterRun(7000, '2026-09-23T12:00:05Z')],
      selfRunId: 7000,
    }).stdout.trim()
    assert.match(out, /^REFUSED .*predates/)

    out = runGate({
      events: freshEvents,
      requester: [requesterRun(7000, '2026-09-23T12:00:05Z', { attempt: 2 })],
      selfRunId: 7000, selfAttempt: 2,
    }).stdout.trim()
    assert.match(out, /^REFUSED .*rerun/)

    out = runGate({ events: freshEvents, requester: [], selfRunId: 7000 }).stdout.trim()
    assert.match(out, /^REFUSED /)
    out = runGate({
      events: freshEvents,
      requester: [{ id: 7000, head_sha: 'b'.repeat(40), event: 'pull_request_target', created_at: '2026-09-23T12:00:05Z' }],
      selfRunId: 7000,
    }).stdout.trim()
    assert.match(out, /^REFUSED /)
    out = runGate({
      events: freshEvents,
      requester: [{ id: 7000, head_sha: sha, event: 'workflow_dispatch', created_at: '2026-09-23T12:00:05Z' }],
      selfRunId: 7000,
    }).stdout.trim()
    assert.match(out, /^REFUSED /)

    out = runGate({
      events: freshEvents,
      requester: [requesterRun(7000, '2026-09-23T12:00:05Z')],
      runs: [productRun(100, 'completed', 'failure', '')],
      selfRunId: 7000,
    }).stdout.trim()
    assert.match(out, /^REFUSED .*boundary/)
    out = runGate({
      events: [labeledEvent(1, '2026-09-23T09:54:44Z'), labeledEvent(2, 'not-a-timestamp')],
      requester: [requesterRun(7000, '2026-09-23T12:00:05Z')],
      selfRunId: 7000,
    }).stdout.trim()
    assert.match(out, /^REFUSED .*label timestamp/)
    out = runGate({
      events: freshEvents,
      requester: [requesterRun(7000, '2026-09-23T12:00:05Z'), requesterRun(6000, '')],
      selfRunId: 7000,
    }).stdout.trim()
    assert.match(out, /^REFUSED .*requester timestamp/)

    out = runGate({
      events: freshEvents, requester: [requesterRun(7000, '2026-09-23T12:00:05Z')],
      selfRunId: 7000, shapes: { requester: 'flat-array' },
    }).stdout.trim()
    assert.match(out, /^REFUSED .*requester history response shape/)
    out = runGate({
      events: freshEvents, requester: [requesterRun(7000, '2026-09-23T12:00:05Z')],
      runs: [staleRun('2026-09-23T10:19:05Z')], selfRunId: 7000, shapes: { runs: 'flat-array' },
    }).stdout.trim()
    assert.match(out, /^REFUSED .*Product run transcript shape/)
    out = runGate({
      events: freshEvents, requester: [requesterRun(7000, '2026-09-23T12:00:05Z')],
      selfRunId: 7000, shapes: { events: 'flat-object' },
    }).stdout.trim()
    assert.match(out, /^REFUSED .*events history response shape/)
  } finally {
    rmSync(scratch, { recursive: true, force: true })
  }
})

// ---- executed embedded Python: pinned Product run identity ---------------------------

test('pinned Product identity checks execute: pin/poll modes, trust, attempt, and terminal states', { skip: skipWithoutPython }, () => {
  const source = pythonBlock('cat > "$RUNNER_TEMP/check-product-run.py"')
  const sha = 'a'.repeat(40)
  const scratch = mkdtempSync(join(tmpdir(), 'aerolink-requester-pinned-'))
  try {
    const record = (overrides = {}) => JSON.stringify({
      id: 101, head_sha: sha, head_branch: 'glm/some-branch', event: 'workflow_dispatch',
      actor: { login: 'github-actions[bot]' }, triggering_actor: { login: 'github-actions[bot]' },
      pull_requests: [{ number: 1066 }], run_attempt: 1, status: 'in_progress', ...overrides,
    })
    const run = (record, mode, expected = '') => {
      const path = join(scratch, `pin-${scratchCounter++}.json`)
      writeFileSync(path, record)
      const args = [mode, path, sha, 'glm/some-branch', '1066']
      if (expected !== '') args.push(expected)
      return runPython(source, args)
    }

    let out = run(record(), 'pin').stdout.trim()
    assert.equal(out, 'ATTEMPT 1')
    out = run(record({ status: 'completed', conclusion: 'success' }), 'pin').stdout.trim()
    assert.equal(out, 'ATTEMPT 1')
    out = run(record({ status: 'completed', conclusion: 'failure' }), 'pin').stdout.trim()
    assert.match(out, /^REFUSED .*completed failure/)
    out = run(record(), 'poll', '1').stdout.trim()
    assert.equal(out, 'RUNNING')
    out = run(record({ status: 'completed', conclusion: 'success' }), 'poll', '1').stdout.trim()
    assert.equal(out, 'SUCCEEDED')
    out = run(record({ status: 'completed', conclusion: 'failure' }), 'poll', '1').stdout.trim()
    assert.equal(out, 'FAILED failure')
    out = run(record({ status: 'completed', conclusion: 'success', run_attempt: 2 }), 'poll', '1').stdout.trim()
    assert.match(out, /^REFUSED .*attempt changed/)
    out = run(record({ head_sha: 'b'.repeat(40) }), 'pin').stdout.trim()
    assert.match(out, /^REFUSED .*head SHA/)
    out = run(record({ triggering_actor: { login: 'seanmccarthyns' } }), 'pin').stdout.trim()
    assert.match(out, /^REFUSED .*trust identity/)
    out = run(record({ pull_requests: [{ number: 999 }] }), 'poll', '1').stdout.trim()
    assert.match(out, /^REFUSED .*pull request/)
  } finally {
    rmSync(scratch, { recursive: true, force: true })
  }
})

// ---- executed shell: bounded pagination completeness ---------------------------------

test('the bounded pagination walk refuses truncated histories', () => {
  const start = requester.indexOf('collect_pages() {')
  assert.notEqual(start, -1)
  const from = requester.indexOf('\n', start) + 1
  const end = requester.indexOf('\n          }', from)
  const fn = requester.slice(start, end) + '\n}'
  const scratch = mkdtempSync(join(tmpdir(), 'aerolink-requester-pages-'))
  try {
    const mockCurl = [
      'curl() {',
      '  local dump="" out="" url="" prev=""',
      '  for arg in "$@"; do',
      '    case "$arg" in',
      '      --dump-header|--output) prev="$arg" ;;',
      '      --*) prev="" ;;',
      '      *)',
      '        if [ "$prev" = "--dump-header" ]; then dump="$arg"; prev=""',
      '        elif [ "$prev" = "--output" ]; then out="$arg"; prev=""',
      '        else url="$arg"',
      '        fi ;;',
      '    esac',
      '  done',
      '  local page',
      "  page=\"$(printf '%s' \"$url\" | sed -n 's/.*[?&]page=\\([0-9]*\\).*/\\1/p')\"",
      "  printf 'HTTP/2 200\\n' > \"$dump\"",
      '  if [ "$page" -lt "$TOTAL_PAGES" ]; then',
      "    printf 'link: <https://example/list?page=%s>; rel=\"next\"\\n' \"$((page + 1))\" >> \"$dump\"",
      '  fi',
      "  printf '%s\\n' \"$url\" >> \"$prefix.urls.log\"",
      "  printf '[]\\n' > \"$out\"",
      '}',
    ].join('\n')
    const script = [
      'set -euo pipefail',
      'headers=()',
      'TOTAL_PAGES=4',
      fn,
      mockCurl,
      'collect_pages "$1" "$2"',
      'echo COLLECTED',
    ].join('\n')
    const run2 = spawnSync(bash, ['--noprofile', '--norc', '-c', script, '_', join(scratch, 'truncated'), 'https://example/list?x=1'], {
      encoding: 'utf8', timeout: 10_000,
    })
    assert.equal(run2.status, 1, run2.stderr)
    assert.match(run2.stdout, /bounded walk/)
    // the actual requested URLs: the base https://example/list?x=1 already carries a
    // query ("?x=1"), so every page correctly appends with "&"
    const truncatedUrls = readFileSync(join(scratch, 'truncated.urls.log'), 'utf8').split('\n').filter(Boolean)
    assert.deepEqual(truncatedUrls, [
      'https://example/list?x=1&page=1&per_page=100',
      'https://example/list?x=1&page=2&per_page=100',
      'https://example/list?x=1&page=3&per_page=100',
    ])
    const script1 = script.replace('TOTAL_PAGES=4', 'TOTAL_PAGES=1')
    const run1 = spawnSync(bash, ['--noprofile', '--norc', '-c', script1, '_', join(scratch, 'single'), 'https://example/list?x=1'], {
      encoding: 'utf8', timeout: 10_000,
    })
    assert.equal(run1.status, 0, run1.stderr)
    assert.match(run1.stdout, /COLLECTED/)
    // a query-bearing base keeps its parameters and switches to "&"
    const queryBase = 'https://example/runs?head_sha=' + 'a'.repeat(40) + '&event=pull_request_target'
    const scriptQ = script.replace('TOTAL_PAGES=4', 'TOTAL_PAGES=1')
    const runQ = spawnSync(bash, ['--noprofile', '--norc', '-c', scriptQ, '_', join(scratch, 'query'), queryBase], {
      encoding: 'utf8', timeout: 10_000,
    })
    assert.equal(runQ.status, 0, runQ.stderr)
    assert.match(runQ.stdout, /COLLECTED/)
    const queryUrls = readFileSync(join(scratch, 'query.urls.log'), 'utf8').split('\n').filter(Boolean)
    assert.deepEqual(queryUrls, [queryBase + '&page=1&per_page=100'])
  } finally {
    rmSync(scratch, { recursive: true, force: true })
  }
})

// ---- executed shell: the initial decision dispatches only for a qualified readiness label

test('the initial decision dispatches once for readiness labels and never for refreshes; authoritative success is reused', () => {
  const decisionStart = requester.indexOf('match="$(find_product_run)"')
  assert.notEqual(decisionStart, -1)
  const end = requester.indexOf('\n          esac', decisionStart)
  const decision = requester.slice(decisionStart, end + '\n          esac'.length).replace(/^ {10}/gm, '')

  const scriptFor = (stubMatch, label) => [
    'set -euo pipefail',
    'REQUEST_LABEL=' + JSON.stringify(label),
    'BOUNDARY_ID=0',
    'match=' + JSON.stringify(stubMatch),
    'find_product_run() { printf \'%s\\n\' "$match"; }',
    'dispatch_full_run() { echo DISPATCHED; }',
    'fetch_and_pin_run() { echo "PINNED $1"; }',
    'verify_live_pr() { :; }',
    decision,
  ].join('\n')
  const scratch = mkdtempSync(join(tmpdir(), 'aerolink-requester-decision-'))
  try {
    const run = (stubMatch, label) => spawnSync(bash, ['--noprofile', '--norc', '-c', scriptFor(stubMatch, label)], {
      encoding: 'utf8', timeout: 10_000,
    })
    let outcome = run('NONE', 'ready-for-full-ci')
    assert.equal(outcome.status, 0, outcome.stderr)
    assert.equal(outcome.stdout, 'DISPATCHED\n')
    outcome = run('EXHAUSTED 100 failure 2026-09-23T10:19:05Z', 'ready-for-full-ci')
    assert.equal(outcome.status, 0, outcome.stderr)
    assert.equal(outcome.stdout, 'DISPATCHED\n')
    outcome = run('NONE', 'authority-maintenance-requested')
    assert.equal(outcome.status, 0)
    assert.equal(outcome.stdout, '')
    outcome = run('EXHAUSTED 100 failure 2026-09-23T10:19:05Z', 'documentation')
    assert.equal(outcome.status, 1)
    assert.match(outcome.stdout, /a refresh cannot dispatch Full/)
    outcome = run('FOUND 200 completed success', 'documentation')
    assert.equal(outcome.status, 0)
    assert.equal(outcome.stdout, 'PINNED 200\n') // authoritative reuse without dispatch
    outcome = run('PENDING 101', 'documentation')
    assert.equal(outcome.status, 0)
    assert.equal(outcome.stdout, 'PINNED 101\n')
  } finally {
    rmSync(scratch, { recursive: true, force: true })
  }
})

// ---- integrated harness: the actual workflow shell + embedded Python over a mocked transport
// The full run-block body is extracted, dedented and executed verbatim against a stateful
// Python curl replacement. POSTs are counted ONLY at that transport boundary; authorization,
// dispatch, pinning, polling and qualification are the real code.

const INTEGRATED_SHA = 'a'.repeat(40)
const INTEGRATED_REF = 'glm/integrated-branch'

function workflowRunBody() {
  const stepAt = requester.indexOf('- name: Authenticate live ready PR, dispatch once, and bind exact Product success')
  assert.notEqual(stepAt, -1)
  const runAt = requester.indexOf('        run: |', stepAt)
  assert.notEqual(runAt, -1)
  const bodyStart = runAt + '        run: |\n'.length
  const nextStep = requester.indexOf('\n      - name: Mint', bodyStart)
  const dedented = requester.slice(bodyStart, nextStep === -1 ? undefined : nextStep).replace(/^ {10}/gm, '')
  // the harness supplies the requester identity through its environment
  return dedented
    .replace(/^SELF_RUN_ID="\$\{\{ github\.run_id \}\}"\n/m, '')
    .replace(/^SELF_RUN_ATTEMPT="\$\{\{ github\.run_attempt \}\}"\n/m, '')
}

function mockCurlSource() {
  return [
    'import json',
    'import os',
    'import re',
    'import sys',
    '',
    'method, url, dump, out, data = sys.argv[1:6]',
    'state = os.environ["MOCK_STATE"]',
    'head_sha = os.environ["HEAD_SHA"]',
    '',
    'def readj(name, default):',
    '    path = os.path.join(state, name)',
    '    if not os.path.exists(path):',
    '        return default',
    '    with open(path, encoding="utf-8") as handle:',
    '        return json.load(handle)',
    '',
    'def writej(name, obj):',
    '    with open(os.path.join(state, name), "w", encoding="utf-8") as handle:',
    '        json.dump(obj, handle)',
    '',
    'counters = readj("counters.json", {"listGets": 0, "pinnedGets": 0, "postSeen": False, "postCount": 0})',
    'scenario = readj("scenario.json", {})',
    'code = 200',
    'resp = {}',
    '',
    'with open(os.path.join(state, "requests.log"), "a", encoding="utf-8") as handle:',
    '    handle.write(method + " " + url + chr(10))',
    '',
    'def bad_url():',
    '    code = 404',
    '    resp = {"message": "malformed request (mock rejects the query or path)"}',
    '    writej("counters.json", counters)',
    '    if dump:',
    "        with open(dump, 'w', encoding='utf-8') as handle:",
    "            handle.write('HTTP/2 404' + chr(10))",
    '    if out:',
    '        with open(out, "w", encoding="utf-8") as handle:',
    '            json.dump(resp, handle)',
    '    raise SystemExit(22)',
    '',
    'if method == "POST" and url.endswith("/dispatches"):',
    '    counters["postSeen"] = True',
    '    counters["postCount"] = counters.get("postCount", 0) + 1',
    '    if data and os.path.exists(data):',
    '        os.replace(data, os.path.join(state, "last-dispatch.json"))',
    '    if scenario.get("dispatchResponse", "details") == "details":',
    '        resp = {"workflow_run_id": scenario.get("dispatchRunId", 101)}',
    '    else:',
    '        resp = {}',
    '        code = 202',
    '    transport_exit = scenario.get("transportExit")',
    '    writej("counters.json", counters)',
    '    if transport_exit is not None:',
    '        raise SystemExit(int(transport_exit))',
    'elif "/actions/workflows/ci.yml/runs" in url:',
    "    if not re.fullmatch(r\".*/actions/workflows/ci.yml/runs\\?head_sha=[0-9a-f]{40}&event=workflow_dispatch&per_page=100\", url):",
    '        bad_url()',
    '    counters["listGets"] = counters.get("listGets", 0) + 1',
    '    runs = list(readj("static-runs.json", {"workflow_runs": []}).get("workflow_runs", []))',
    '    final = readj("run-101-final.json", None)',
    '    reveal = int(scenario.get("revealAfter", 1))',
    '    if counters.get("postSeen") and final is not None and counters.get("listGets", 0) >= reveal:',
    '        runs.append(final)',
    '    resp = {"workflow_runs": runs, "total_count": len(runs)}',
    'elif "request-full-ci.yml/runs" in url:',
    "    if not re.fullmatch(r\".*/request-full-ci.yml/runs\\?head_sha=[0-9a-f]{40}&event=pull_request_target&page=[0-9]+&per_page=100\", url):",
    '        bad_url()',
    '    resp = {"workflow_runs": readj("requester-runs.json", [])}',
    'elif "/issues/" in url:',
    "    if not re.fullmatch(r\".*/issues/1066/events\\?page=[0-9]+&per_page=100\", url):",
    '        bad_url()',
    '    page = int(re.search(r"page=([0-9]+)", url).group(1))',
    '    pages = scenario.get("eventsPages") or []',
    '    if pages:',
    '        resp = pages[page - 1] if page <= len(pages) else []',
    '    else:',
    '        resp = readj("events.json", [])',
    'elif "/pulls/" in url:',
    '    resp = readj("pr.json", {})',
    'elif "/actions/runs/" in url and "/attempts/" in url:',
    "    with open(os.path.join(state, 'jobs-requests'), 'a', encoding='utf-8') as handle:",
    "        handle.write(url.split('mock.local', 1)[-1] + chr(10))",
    '    resp = readj("jobs.json", {})',
    'elif "/actions/runs/" in url:',
    "    if not re.fullmatch(r\".*/actions/runs/[0-9]+\", url):",
    '        bad_url()',
    '    run_id = int(url.rsplit("/", 1)[-1])',
    '    counters["pinnedGets"] = counters.get("pinnedGets", 0) + 1',
    "    pin_read_exit = scenario.get(\"pinnedReadExit\")",
    "    pin_read_status = scenario.get(\"pinnedReadStatus\")",
    '    threshold = int(scenario.get("pinnedFinalAfter", 2))',
    '    if (pin_read_exit is not None or pin_read_status is not None) and counters.get("pinnedGets", 0) >= threshold:',
    '        writej("counters.json", counters)',
    '        if pin_read_exit is not None:',
    "            raise SystemExit(int(pin_read_exit))",
    '        code = int(pin_read_status)',
    '        resp = readj("pinned-read-body.json", {"message": "injected failure"})',
    '        if dump:',
    "            with open(dump, 'w', encoding='utf-8') as handle:",
    "                handle.write('HTTP/2 %d' % code + chr(10))",
    '        if out:',
    "            with open(out, 'w', encoding='utf-8') as handle:",
    '                json.dump(resp, handle)',
    "        raise SystemExit(22)",
    "    initial = readj('run-%d-initial.json' % run_id, None)",
    "    final = readj('run-%d-final.json' % run_id, None)",
    "    plain = readj('run-%d.json' % run_id, None)",
    '    rec = plain',
    '    if initial is not None or final is not None:',
    '        if final is None or not counters.get("postSeen"):',
    '            rec = initial',
    '        elif counters.get("pinnedGets", 0) >= threshold:',
    '            rec = final',
    '        else:',
    '            rec = initial',
    '    if rec is None:',
    '        code = 404',
    '        resp = {"message": "Not Found"}',
    '    else:',
    '        resp = rec',
  ]
}

function mockCurlTail() {
  return [
    '',
    'writej("counters.json", counters)',
    'with open(os.path.join(state, "last-code"), "w", encoding="utf-8") as handle:',
    '    handle.write(str(code))',
    'if dump:',
    '    link_next = len(scenario.get("eventsPages") or []) > 1 and "/issues/" in url',
    "    with open(dump, \"w\", encoding=\"utf-8\") as handle:",
    "        handle.write('HTTP/2 %d' % code + chr(10))",
    '        if link_next and re.search(r"page=([0-9]+)", url).group(1) == "1":',
    "            handle.write('link: <https://mock.local' + url.split('mock.local', 1)[-1].split('?')[0] + '?page=2&per_page=100>; rel=\"next\"' + chr(10))",
    'if out:',
    '    with open(out, "w", encoding="utf-8") as handle:',
    '        handle.write(json.dumps(resp))',
    'if code >= 400:',
    '    raise SystemExit(22)',
  ].join(String.fromCharCode(10))
}

function buildIntegrated(scenario) {
  const state = mkdtempSync(join(tmpdir(), 'aerolink-requester-integrated-'))
  mkdirSync(join(state, 'tmp'), { recursive: true })
  const freshEvents = [
    labeledEvent(1, '2026-09-22T10:49:17Z'),
    labeledEvent(2, '2026-09-23T12:00:00Z'),
  ]
  const staleEvents = [labeledEvent(1, '2026-09-23T09:54:44Z')]
  // every Product record is normalized onto the integrated head/ref so the trust
  // checks inside the workflow's real helper accept exactly what they should
  const integrated = (rec) => ({ ...rec, head_sha: INTEGRATED_SHA, head_branch: INTEGRATED_REF })
  const productRuns = (scenario.productRuns ?? []).map(integrated)
  const runRecords = {}
  for (const [name, rec] of Object.entries(scenario.runRecords ?? {})) {
    runRecords[name] = integrated(rec)
  }
  writeFileSync(join(state, 'scenario.json'), JSON.stringify(scenario, null, 2))
  if (scenario.eventsPages) {
    writeFileSync(join(state, 'events-pages.json'), JSON.stringify(scenario.eventsPages))
  } else {
    writeFileSync(join(state, 'events.json'), JSON.stringify(scenario.stale ? staleEvents : freshEvents))
  }
  writeFileSync(join(state, 'requester-runs.json'), JSON.stringify(scenario.requester ?? []))
  writeFileSync(join(state, 'static-runs.json'), JSON.stringify({ workflow_runs: productRuns }))
  for (const [name, rec] of Object.entries(runRecords)) {
    writeFileSync(join(state, `run-${name}.json`), JSON.stringify(rec))
  }
  for (const [name, raw] of Object.entries(scenario.rawRunRecords ?? {})) {
    // verbatim payload: used to inject records the checker must fail on
    writeFileSync(join(state, `run-${name}.json`), raw)
  }
  if (scenario.pinnedReadBody) {
    // the failed-response body is normalized onto the integrated identity too: the
    // named condition is an HTTP failure carrying OTHERWISE ACCEPTABLE success evidence
    writeFileSync(join(state, 'pinned-read-body.json'), JSON.stringify(integrated(scenario.pinnedReadBody)))
  }
  if (scenario.prePinLeftover) {
    // an earlier success-shaped response file left behind by a previous read —
    // built from the trusted record so only the read failure can refuse
    writeFileSync(join(state, 'tmp', 'pinned-101.json'), JSON.stringify(integrated(success101)))
  }
  writeFileSync(join(state, 'jobs.json'), JSON.stringify(scenario.jobs ?? {
    jobs: [
      { name: 'Classify changed product areas', steps: [{ name: 'Authenticate label-dispatched pull-request context', status: 'completed', conclusion: 'success' }] },
      { name: 'Full Product evidence aggregate', status: 'completed', conclusion: 'success' },
    ],
  }))
  writeFileSync(join(state, 'pr.json'), JSON.stringify({
    state: 'open',
    head: { sha: INTEGRATED_SHA, ref: INTEGRATED_REF, repo: { full_name: 'owner/repo' } },
    labels: [{ name: 'ready-for-full-ci' }],
  }))
  writeFileSync(join(state, 'counters.json'), JSON.stringify({ listGets: 0, pinnedGets: 0, postSeen: false, postCount: 0 }))
  writeFileSync(join(state, 'jobs-requests'), '')
  writeFileSync(join(state, 'summary.md'), '')
  writeFileSync(join(state, 'mock-curl.py'), mockCurlSource().join('\n') + '\n' + mockCurlTail())
  return state
}

function integratedHarness(stateDir, selfAttempt = 1) {
  const shim = join(stateDir, 'shim')
  mkdirSync(shim, { recursive: true })
  // PATH needs the POSIX spelling of the shim directory; native Windows paths keep the
  // drive-letter conversion, POSIX systems already are POSIX.
  const shimForPath = process.platform === 'win32'
    ? '/' + shim[0].toLowerCase() + shim.slice(2).split(sep).join('/')
    : shim
  const pythonShim = join(shim, 'python')
  writeFileSync(pythonShim, '#!/usr/bin/env bash\nexec "' + pythonExecutable + '" "$@"\n')
  chmodSync(pythonShim, 0o755)
  // the production loop keeps the 420-poll bound; this test-scaled copy bounds at 60 polls
  const body = workflowRunBody().replace('for attempt in $(seq 1 420); do', 'for attempt in $(seq 1 60); do')
  const lines = [
    'set -euo pipefail',
    'export GITHUB_API_URL="https://mock.local"',
    'export GITHUB_TOKEN="integration-mock-token"',
    'export GITHUB_REPOSITORY="owner/repo"',
    'export HEAD_SHA="' + INTEGRATED_SHA + '"',
    'export HEAD_REF="' + INTEGRATED_REF + '"',
    'export BASE_SHA="' + 'b'.repeat(40) + '"',
    'export PR_NUMBER="1066"',
    'export REQUEST_LABEL="ready-for-full-ci"',
    'export SELF_RUN_ID="7000"',
    'export SELF_RUN_ATTEMPT="' + selfAttempt + '"',
    'export RUNNER_TEMP="' + join(stateDir, 'tmp') + '"',
    'export GITHUB_STEP_SUMMARY="' + join(stateDir, 'summary.md') + '"',
    'export MOCK_STATE="' + stateDir + '"',
    'export PATH="' + shimForPath + '":$PATH',
    'api="$GITHUB_API_URL/repos/$GITHUB_REPOSITORY"',
    'headers=()',
    'BOUNDARY_ID=0',
    'PINNED_ID=""',
    'PINNED_ATTEMPT=""',
    'sleep() { :; }',
    'node() { :; }',
    'curl() {',
    '  local method="GET" dump="" out="" data="" want_code=0 prev="" url="" arg',
    '  for arg in "$@"; do',
    '    case "$arg" in',
    '      --request|--dump-header|--output|--data-binary) prev="$arg" ;;',
    '      --write-out) prev="WANT" ;;',
    '      --*) prev="" ;;',
    '      *)',
    '        case "$prev" in',
    '          --request) method="$arg" ;;',
    '          --dump-header) dump="$arg" ;;',
    '          --output) out="$arg" ;;',
    '          --data-binary) data="${arg#@}" ;;',
    '          WANT) want_code=1 ;;',
    '          "") url="$arg" ;;',
    '        esac',
    '        prev=""',
    '        ;;',
    '    esac',
    '  done',
    '  local status=0',
    '  python "' + stateDir + '/mock-curl.py" "$method" "$url" "$dump" "$out" "$data" || status=$?',
    '  if [ "$want_code" = "1" ] && [ -f "$MOCK_STATE/last-code" ]; then cat "$MOCK_STATE/last-code"; fi',
    '  return "$status"',
    '}',
    body,
  ]
  return lines.join('\n')
}

function runIntegrated(state, selfAttempt = 1) {
  const script = integratedHarness(state, selfAttempt)
  const file = join(state, 'harness.sh')
  writeFileSync(file, script)
  return spawnSync(bash, ['--noprofile', '--norc', file], { encoding: 'utf8', timeout: 240_000 })
}

function integratedState(scenario) {
  const state = buildIntegrated(scenario)
  return { state, run: (attempt) => runIntegrated(state, attempt ?? 1) }
}

const counters = (state) => JSON.parse(readFileSync(join(state, 'counters.json'), 'utf8'))

const inflight101 = productRun(101, 'in_progress', undefined, '2026-09-23T12:10:00Z', { created_at: '2026-09-23T12:05:00Z' })
const success101 = productRun(101, 'completed', 'success', '2026-09-23T12:20:00Z', { created_at: '2026-09-23T12:05:00Z' })
const failed100 = productRun(100, 'completed', 'failure', '2026-09-23T10:19:05Z')
const success200 = productRun(200, 'completed', 'success', '2026-09-22T11:00:00Z')
const integratedSelf = requesterRun(7000, '2026-09-23T12:00:05Z')


// ---- integrated scenarios (executed on POSIX; skipped on Windows) ----

const skipIntegrated = skipWithoutPython || process.platform === 'win32'
  ? 'integrated harness needs a POSIX runtime (executed by the ubuntu Product gate)'
  : false

function diagnostic(state, outcome) {
  let jobs = ''
  let summary = ''
  try { jobs = readFileSync(join(state, 'jobs-requests'), 'utf8') } catch {}
  try { summary = readFileSync(join(state, 'summary.md'), 'utf8') } catch {}
  return ['stdout=<' + outcome.stdout + '>', 'stderr=<' + outcome.stderr + '>', 'summary=<' + summary + '>', 'jobs-requests=<' + jobs + '>'].join(' || ')
}

test('integrated: eligible NONE dispatches once, pins the response identity, and qualifies the pinned attempt', { skip: skipIntegrated }, () => {
  const { state, run } = integratedState({
    requester: [integratedSelf], productRuns: [],
    runRecords: { '101-initial': inflight101, '101-final': success101 },
    dispatchResponse: 'details', revealAfter: 1, pinnedFinalAfter: 2,
  })
  const outcome = run()
  assert.equal(outcome.status, 0, diagnostic(state, outcome))
  assert.equal(counters(state).postCount, 1)
  assert.match(readFileSync(join(state, 'last-dispatch.json'), 'utf8'), /return_run_details/)
  assert.match(readFileSync(join(state, 'summary.md'), 'utf8'), /run 101 \(attempt 1\)/)
  assert.match(readFileSync(join(state, 'jobs-requests'), 'utf8'), /\/actions\/runs\/101\/attempts\/1\/jobs\?per_page=100/)
})

test('integrated: eligible EXHAUSTED dispatch with a lost transport response recovers by discovery — one POST total', { skip: skipIntegrated }, () => {
  const { state, run } = integratedState({
    requester: [integratedSelf], productRuns: [failed100],
    runRecords: { '101-initial': inflight101, '101-final': success101 },
    dispatchResponse: 'empty', transportExit: 28, revealAfter: 2, pinnedFinalAfter: 2,
  })
  const outcome = run()
  assert.equal(outcome.status, 0, diagnostic(state, outcome))
  assert.equal(counters(state).postCount, 1)
  assert.match(readFileSync(join(state, 'summary.md'), 'utf8'), /run 101 \(attempt 1\)/)
})

test('integrated: a lost response that never resolves refuses after exactly one POST', { skip: skipIntegrated }, () => {
  const { state, run } = integratedState({
    requester: [integratedSelf], productRuns: [failed100], dispatchResponse: "empty",
  })
  const outcome = run()
  assert.equal(outcome.status, 1)
  assert.match(diagnostic(state, outcome), /No successful trusted Product workflow_dispatch/)
  assert.equal(counters(state).postCount, 1)
})

test('integrated: stale, consumed and missing-identity refusals post zero times', { skip: skipIntegrated }, () => {
  const stale = integratedState({
    stale: true, requester: [requesterRun(7000, "2026-09-23T10:00:00Z")], productRuns: [failed100],
  })
  const staleOutcome = stale.run()
  assert.equal(staleOutcome.status, 1)
  assert.match(diagnostic(stale.state, staleOutcome), /Dispatch refused/)
  assert.match(diagnostic(stale.state, staleOutcome), /stale/)
  assert.equal(counters(stale.state).postCount, 0)
  const consumed = integratedState({
    requester: [integratedSelf, requesterRun(6000, "2026-09-23T12:00:10Z")], productRuns: [failed100],
  })
  const consumedOutcome = consumed.run()
  assert.equal(consumedOutcome.status, 1)
  assert.match(diagnostic(consumed.state, consumedOutcome), /authorization already consumed by requester run 6000/)
  assert.equal(counters(consumed.state).postCount, 0)
  const missing = integratedState({ requester: [], productRuns: [failed100] })
  const missingOutcome = missing.run()
  assert.equal(missingOutcome.status, 1)
  assert.match(diagnostic(missing.state, missingOutcome), /identity is missing from the history or mismatches this head/)
  assert.equal(counters(missing.state).postCount, 0)
})

test('integrated: a requester rerun refuses with zero POSTs', { skip: skipIntegrated }, () => {
  const { state, run } = integratedState({
    requester: [requesterRun(7000, "2026-09-23T12:00:05Z", { attempt: 2 })], productRuns: [failed100],
  })
  const outcome = run(2)
  assert.equal(outcome.status, 1)
  assert.match(diagnostic(state, outcome), /requester rerun/)
  assert.equal(counters(state).postCount, 0)
})

test('integrated: pinned-run trust and attempt changes refuse after a valid pin and a later poll', { skip: skipIntegrated }, () => {
  const trust = integratedState({
    requester: [integratedSelf], productRuns: [],
    runRecords: {
      '101-initial': inflight101,
      '101-final': { ...success101, triggering_actor: { login: 'seanmccarthyns' } },
    },
    dispatchResponse: 'details', pinnedFinalAfter: 2,
  })
  const trustOutcome = trust.run()
  assert.equal(trustOutcome.status, 1)
  assert.match(diagnostic(trust.state, trustOutcome), /trust identity changed/)
  assert.equal(counters(trust.state).postCount, 1)
  assert.ok(counters(trust.state).pinnedGets >= 2, "the original attempt must have been pinned before the mutation was served")
  const attempt = integratedState({
    requester: [integratedSelf], productRuns: [],
    runRecords: {
      '101-initial': inflight101,
      '101-final': { ...success101, run_attempt: 2 },
    },
    dispatchResponse: 'details', pinnedFinalAfter: 2,
  })
  const attemptOutcome = attempt.run()
  assert.equal(attemptOutcome.status, 1)
  assert.match(diagnostic(attempt.state, attemptOutcome), /attempt changed/)
  assert.equal(counters(attempt.state).postCount, 1)
})

test('integrated: a missing pinned record (404) refuses after the single dispatch', { skip: skipIntegrated }, () => {
  const { state, run } = integratedState({
    requester: [integratedSelf], productRuns: [], dispatchResponse: "details",
  })
  const outcome = run()
  assert.notEqual(outcome.status, 0) // curl --fail exit 22 propagates through set -e
  assert.equal(counters(state).postCount, 1)
})

test('integrated: an existing authoritative success is reused without dispatch', { skip: skipIntegrated }, () => {
  const { state, run } = integratedState({
    requester: [integratedSelf], productRuns: [success200],
    runRecords: { 200: success200 },
  })
  const outcome = run()
  assert.equal(outcome.status, 0, diagnostic(state, outcome))
  assert.equal(counters(state).postCount, 0)
  assert.match(readFileSync(join(state, 'summary.md'), 'utf8'), /run 200 \(attempt 1\)/)
})

test('integrated: failed qualification evidence refuses and the per-attempt jobs request is exact', { skip: skipIntegrated }, () => {
  const { state, run } = integratedState({
    requester: [integratedSelf], productRuns: [],
    runRecords: { '101-initial': inflight101, '101-final': success101 },
    dispatchResponse: 'details', revealAfter: 1, pinnedFinalAfter: 2,
    jobs: {
      jobs: [
        { name: 'Classify changed product areas', steps: [{ name: 'Authenticate label-dispatched pull-request context', status: 'completed', conclusion: 'success' }] },
        { name: 'Full Product evidence aggregate', status: 'completed', conclusion: 'failure' },
      ],
    },
  })
  const outcome = run()
  assert.equal(outcome.status, 1)
  assert.match(diagnostic(state, outcome), /Product aggregate is not authoritative success/)
  assert.equal(counters(state).postCount, 1)
  assert.match(readFileSync(join(state, 'jobs-requests'), 'utf8'), /\/actions\/runs\/101\/attempts\/1\/jobs\?per_page=100/)
})

test('integrated: multi-page event histories are walked with query-correct URLs and consumed by the gate', { skip: skipIntegrated }, () => {
  const { state, run } = integratedState({
    requester: [integratedSelf], productRuns: [],
    eventsPages: [
      [labeledEvent(9, "2026-09-23T11:30:00Z")],
      [labeledEvent(20, "2026-09-23T12:00:00Z")],
    ],
    runRecords: { '101-initial': inflight101, '101-final': success101 },
    dispatchResponse: 'details', revealAfter: 1, pinnedFinalAfter: 2,
  })
  const outcome = run()
  assert.equal(outcome.status, 0, diagnostic(state, outcome))
  assert.equal(counters(state).postCount, 1)
  const requests = readFileSync(join(state, 'requests.log'), 'utf8')
  assert.match(requests, /\/issues\/1066\/events\?page=1&per_page=100/)
  assert.match(requests, /\/issues\/1066\/events\?page=2&per_page=100/)
  assert.match(readFileSync(join(state, 'summary.md'), 'utf8'), /run 101 \(attempt 1\)/)
})

// ---- I07 regressions: a failed pinned read after a valid pin must refuse the
// requester before any RUNNING/SUCCEEDED verdict, qualification request, or
// rebinding — with the original pin proven to have occurred first (pinnedGets
// reaches 2: one fetch during pinning, one failed poll).

function postPinFailureState(overrides = {}) {
  return integratedState({
    requester: [integratedSelf], productRuns: [],
    runRecords: { '101-initial': inflight101, '101-final': success101 },
    dispatchResponse: 'details', pinnedFinalAfter: 2,
    ...overrides,
  })
}

function assertPostPinRefusal(state, outcome) {
  assert.equal(outcome.status, 1)
  assert.match(diagnostic(state, outcome), /pinned-run read failed/)
  // the original pin occurred before the failure injection: two pinned-record
  // reads (one during pinning, one failed poll) against exactly one dispatch
  assert.equal(counters(state).pinnedGets, 2)
  assert.equal(counters(state).postCount, 1)
  // no successful binding summary, no qualification request after the failed poll
  assert.equal(readFileSync(join(state, 'summary.md'), 'utf8'), '')
  assert.equal(readFileSync(join(state, 'jobs-requests'), 'utf8'), '')
}

test('integrated: post-pin transport failure with a stale success-shaped file refuses', { skip: skipIntegrated }, () => {
  const { state, run } = postPinFailureState({ pinnedReadExit: 28, prePinLeftover: true })
  const leftover = join(state, 'tmp', 'pinned-101.json')
  assert.equal(existsSync(leftover), true, 'precondition: stale success-shaped file present')
  // the stale payload carries the trusted integrated identity — only the read
  // failure, not a trust mismatch, may refuse this scenario
  const staleRecord = JSON.parse(readFileSync(leftover, 'utf8'))
  assert.equal(staleRecord.head_sha, INTEGRATED_SHA)
  assert.equal(staleRecord.head_branch, INTEGRATED_REF)
  assert.equal(staleRecord.status, 'completed')
  assert.equal(staleRecord.conclusion, 'success')
  const outcome = run()
  // poll_pinned removes the stale file before the read, then curl exits 28:
  // the stale file is gone and its contents were never interpreted
  assert.equal(existsSync(leftover), false, 'stale file must be removed before the read')
  assertPostPinRefusal(state, outcome)
})

test('integrated: post-pin failed HTTP carrying a success-shaped body refuses', { skip: skipIntegrated }, () => {
  const { state, run } = postPinFailureState({ pinnedReadStatus: 500, pinnedReadBody: success101 })
  const outcome = run()
  // a 500 response whose body looks like a run must not be treated as evidence;
  // the mock must deliver the NORMALIZED success body it was given
  const delivered = JSON.parse(readFileSync(join(state, 'tmp', 'pinned-101.json'), 'utf8'))
  assert.equal(delivered.head_sha, INTEGRATED_SHA)
  assert.equal(delivered.head_branch, INTEGRATED_REF)
  assert.equal(delivered.status, 'completed')
  assert.equal(delivered.conclusion, 'success')
  assert.equal(delivered.run_attempt, 1)
  assert.match(readFileSync(join(state, 'pinned-read-body.json'), 'utf8'), /glm\/integrated-branch/)
  assertPostPinRefusal(state, outcome)
})

test('integrated: post-pin 404 refuses', { skip: skipIntegrated }, () => {
  const { state, run } = postPinFailureState({ pinnedReadStatus: 404, pinnedReadBody: { message: 'Not Found' } })
  const outcome = run()
  assertPostPinRefusal(state, outcome)
})

test('integrated: checker failure after pinning refuses without a bind', { skip: skipIntegrated }, () => {
  // run-101-final.json is verbatim '[]' — valid JSON, wrong shape — so the
  // checker crashes (nonzero, empty verdict) instead of producing a verdict
  const { state, run } = postPinFailureState({ rawRunRecords: { '101-final': '[]' } })
  const outcome = run()
  assert.equal(outcome.status, 1)
  assert.match(diagnostic(state, outcome), /verification failed/)
  assert.equal(counters(state).postCount, 1)
  assert.equal(readFileSync(join(state, 'summary.md'), 'utf8'), '')
  assert.equal(readFileSync(join(state, 'jobs-requests'), 'utf8'), '')
})
