import { test } from 'node:test'
import assert from 'node:assert/strict'
import { mkdtempSync, readFileSync, rmSync, writeFileSync, mkdirSync } from 'node:fs'
import { execFileSync, spawnSync } from 'node:child_process'
import { join, resolve } from 'node:path'
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

// Extracts a quoted heredoc body (YAML-indented by ten spaces) that appears after `anchor`
// and is terminated by the marker line. Returns the body dedented, ready to execute.
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
  // the gate and the POST live inside the shared dispatcher, so no path can bypass them
  const dispatcherAt = requester.indexOf('dispatch_full_run() {')
  const dispatcherEnd = requester.indexOf('\n          }\n', dispatcherAt)
  const dispatcherBody = requester.slice(dispatcherAt, dispatcherEnd)
  assert.match(dispatcherBody, /authorize_dispatch$/m)
  assert.match(dispatcherBody, /return_run_details/)
  assert.match(dispatcherBody, /actions\/workflows\/ci\.yml\/dispatches/)
  // exactly two call sites: the EXHAUSTED retry and the NONE first dispatch, both gated on
  // the readiness label; refresh lanes never dispatch.
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
  // bounded pagination completeness for both histories
  assert.match(requester, /for page in 1 2 3/)
  assert.match(requester, /rel="next"/)
  assert.match(requester, /History pagination did not complete within the bounded walk/)
  // strict timestamps on every validated record kind
  assert.match(requester, /unestablishable failure boundary/)
  assert.match(requester, /unestablishable readiness label timestamp/)
  assert.match(requester, /unestablishable requester timestamp/)
})

test('uncertain dispatch outcomes end in refusal; the dispatched run is pinned by response identity or boundary-filtered discovery', () => {
  assert.match(requester, /return_run_details/)
  assert.match(requester, /workflow_run_id/)
  // discovery filters candidates to ids newer than the pre-dispatch boundary
  assert.match(requester, /int\(run\.get\("id", 0\)\) > boundary_id/)
  // run + attempt pinning: qualification evidence comes from the pinned attempt
  assert.match(requester, /attempts\/\$PINNED_ATTEMPT\/jobs/)
  assert.doesNotMatch(requester, /filter=latest/)
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

test('the common dispatch gate executes: freshness, spend-once, identity, ties and histories', { skip: skipWithoutPython }, () => {
  const source = pythonBlock('authorize_dispatch() {')
  const sha = 'a'.repeat(40)
  const scratch = mkdtempSync(join(tmpdir(), 'aerolink-requester-gate-'))
  try {
    let caseCounter = 0
    const runGate = ({ events, requester = [], runs = [], selfRunId, selfAttempt = 1 }) => {
      const prefix = join(scratch, `case-${caseCounter++}`)
      mkdirSync(prefix, { recursive: true })
      writeFileSync(join(prefix, 'events.page.1'), JSON.stringify(events))
      writeFileSync(join(prefix, 'requester.page.1'), JSON.stringify(requester))
      writeFileSync(join(prefix, 'product-runs.json'), JSON.stringify({ workflow_runs: runs }))
      return runPython(source, [prefix, sha, 'glm/some-branch', '1066', String(selfRunId), String(selfAttempt)])
    }
    const freshEvents = [
      labeledEvent(1, '2026-09-22T10:49:17Z'),
      labeledEvent(2, '2026-09-23T12:00:00Z'),
    ]
    const staleEvents = [labeledEvent(1, '2026-09-23T09:54:44Z')]
    const staleRun = (updated) => productRun(100, 'completed', 'failure', updated)

    // first young requester over a fresh authorization dispatches
    let out = runGate({ events: freshEvents, requester: [requesterRun(7000, '2026-09-23T12:00:05Z')], selfRunId: 7000 }).stdout.trim()
    assert.match(out, /^ALLOWED /)

    // stale authorization refuses
    out = runGate({ events: staleEvents, requester: [requesterRun(7000, '2026-09-23T10:00:00Z')], runs: [staleRun('2026-09-23T10:19:05Z')], selfRunId: 7000 }).stdout.trim()
    assert.match(out, /^REFUSED .*stale/)

    // consumption at-or-after the authorization, across statuses; ties consume
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

    // an old requester cannot borrow a newer authorization
    out = runGate({
      events: [labeledEvent(1, '2026-09-22T10:49:17Z'), labeledEvent(2, '2026-09-23T13:00:00Z')],
      requester: [requesterRun(7000, '2026-09-23T12:00:05Z')],
      selfRunId: 7000,
    }).stdout.trim()
    assert.match(out, /^REFUSED .*predates/)

    // requester reruns reconcile or refuse, never dispatch
    out = runGate({
      events: freshEvents,
      requester: [requesterRun(7000, '2026-09-23T12:00:05Z', { attempt: 2 })],
      selfRunId: 7000, selfAttempt: 2,
    }).stdout.trim()
    assert.match(out, /^REFUSED .*rerun/)

    // identity verification: missing, wrong head, wrong event
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

    // malformed timestamps refuse on every validated record kind
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
  } finally {
    rmSync(scratch, { recursive: true, force: true })
  }
})

// ---- executed embedded Python: pinned Product run identity ---------------------------

test('pinned Product identity checks execute: trust, attempt, and terminal states', { skip: skipWithoutPython }, () => {
  const source = pythonBlock('cat > "$RUNNER_TEMP/check-product-run.py"')
  const sha = 'a'.repeat(40)
  const scratch = mkdtempSync(join(tmpdir(), 'aerolink-requester-pinned-'))
  try {
    const record = (overrides = {}) => JSON.stringify({
      id: 101, head_sha: sha, head_branch: 'glm/some-branch', event: 'workflow_dispatch',
      actor: { login: 'github-actions[bot]' }, triggering_actor: { login: 'github-actions[bot]' },
      pull_requests: [{ number: 1066 }], run_attempt: 1, status: 'in_progress', ...overrides,
    })
    const run = (record, expected = '') => {
      const path = join(scratch, `pin-${scratchCounter++}.json`)
      writeFileSync(path, record)
      return runPython(source, [path, sha, 'glm/some-branch', '1066', expected])
    }

    let out = run(record()).stdout.trim()
    assert.equal(out, 'RUNNING')
    out = run(record({ status: 'completed', conclusion: 'success' })).stdout.trim()
    assert.equal(out, 'SUCCEEDED')
    out = run(record({ status: 'completed', conclusion: 'failure' })).stdout.trim()
    assert.equal(out, 'FAILED failure')
    out = run(record({ status: 'completed', conclusion: 'success', run_attempt: 2 }), '1').stdout.trim()
    assert.match(out, /^REFUSED .*attempt changed/)
    out = run(record({ head_sha: 'b'.repeat(40) })).stdout.trim()
    assert.match(out, /^REFUSED .*head SHA/)
    out = run(record({ triggering_actor: { login: 'seanmccarthyns' } })).stdout.trim()
    assert.match(out, /^REFUSED .*trust identity/)
    out = run(record({ pull_requests: [{ number: 999 }] })).stdout.trim()
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
    // A function-defined curl overrides the binary, so the extracted collect_pages runs
    // unmodified against a scripted transport that simulates paginated Link headers.
    const mockCurl = `curl() {
  local dump="" out="" url="" prev=""
  for arg in "$@"; do
    case "$arg" in
      --dump-header|--output) prev="$arg" ;;
      --*) prev="" ;;
      *)
        if [ "$prev" = "--dump-header" ]; then dump="$arg"; prev=""
        elif [ "$prev" = "--output" ]; then out="$arg"; prev=""
        else url="$arg"
        fi ;;
    esac
  done
  local page
  page="$(printf '%s' "$url" | sed -n 's/.*[?&]page=\\([0-9]*\\).*/\\1/p')"
  printf 'HTTP/2 200\\n' > "$dump"
  if [ "$page" -lt "$TOTAL_PAGES" ]; then
    printf 'link: <https://example/list?page=%s>; rel="next"\\n' "$((page + 1))" >> "$dump"
  fi
  printf '[]\\n' > "$out"
}
`
    const scriptFor = (totalPages) => `set -euo pipefail
headers=()
TOTAL_PAGES=${totalPages}
${fn}
${mockCurl}
collect_pages "$1" "$2"
echo COLLECTED
`
    // single complete page: collected
    const run1 = spawnSync(bash, ['--noprofile', '--norc', '-c', scriptFor(1), '_', join(scratch, 'single'), 'https://example/list?x=1'], {
      encoding: 'utf8', timeout: 10_000,
    })
    assert.equal(run1.status, 0, run1.stderr)
    assert.match(run1.stdout, /COLLECTED/)
    // four pages: the bounded walk refuses
    const run2 = spawnSync(bash, ['--noprofile', '--norc', '-c', scriptFor(4), '_', join(scratch, 'truncated'), 'https://example/list?x=1'], {
      encoding: 'utf8', timeout: 10_000,
    })
    assert.equal(run2.status, 1, run2.stderr)
    assert.match(run2.stdout, /bounded walk/)
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

  const scriptFor = (stubMatch, label) => `set -euo pipefail
REQUEST_LABEL=${JSON.stringify(label)}
BOUNDARY_ID=0
match=${JSON.stringify(stubMatch)}
find_product_run() { printf '%s\\n' "$match"; }
dispatch_full_run() { echo DISPATCHED; }
fetch_and_pin_run() { echo "PINNED $1"; }
verify_live_pr() { :; }
${decision}
`
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
