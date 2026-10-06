import { test } from 'node:test'
import assert from 'node:assert/strict'
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { execFileSync, spawnSync } from 'node:child_process'
import { join, resolve } from 'node:path'
import { tmpdir } from 'node:os'
import { runInNewContext } from 'node:vm'

const read = (relative) => readFileSync(new URL(`../../../${relative}`, import.meta.url), 'utf8')
const requester = read('.github/workflows/request-full-ci.yml')
const full = read('.github/workflows/ci.yml')
const fast = read('.github/workflows/fast-pr-feedback.yml')
const reset = read('.github/workflows/reset-full-ci-readiness.yml')
const bash = process.platform === 'win32'
  ? resolve(execFileSync('git', ['--exec-path'], { encoding: 'utf8' }).trim(), '../../../bin/bash.exe')
  : 'bash'

test('Product concurrency keeps adjacent main commits and independent proof channels separate', () => {
  const template = full.match(/^  group: (.+)$/m)?.[1].trim()
  assert.ok(template, 'Product must declare its workflow concurrency group')
  const expression = template.match(/\$\{\{\s*(.*?)\s*\}\}/)?.[1]
  assert.ok(expression, 'the group must use the live event context')
  // Execute the workflow's actual boolean/property/format expression. These normalized event strings
  // use the common JavaScript/Actions subset; this does not simulate scheduler ordering or cancellation.
  const group = ({ event = 'push', sha = 'a'.repeat(40), ref = 'refs/heads/main', pr = '', ready = '' } = {}) => {
    const value = runInNewContext(expression, {
      github: { event_name: event, sha, ref, event: { pull_request: { number: pr } } },
      inputs: { pull_request_number: ready },
      format: (pattern, argument) => pattern.replace('{0}', String(argument)),
    }, { timeout: 1_000 })
    return template.replace(/\$\{\{.*?\}\}/, String(value))
  }
  assert.notEqual(group(), group({ sha: 'b'.repeat(40) }), 'adjacent main SHAs must not supersede one another')
  assert.equal(group(), group(), 'duplicate proof for the same pushed SHA retains cancellation')
  for (const [context, expected] of [
    [{ event: 'pull_request', pr: 1234 }, 'quality-1234'],
    [{ event: 'workflow_dispatch', ready: '1234', ref: 'refs/heads/topic' }, 'quality-1234'],
    [{ event: 'merge_group', ref: 'refs/heads/gh-readonly-queue/main/candidate-a' }, 'quality-refs/heads/gh-readonly-queue/main/candidate-a'],
    [{ event: 'merge_group', ref: 'refs/heads/gh-readonly-queue/main/candidate-b' }, 'quality-refs/heads/gh-readonly-queue/main/candidate-b'],
    [{ event: 'schedule' }, 'quality-scheduled'],
    [{ event: 'workflow_dispatch' }, 'quality-diagnostics-refs/heads/main'],
    [{ event: 'workflow_dispatch', ref: 'refs/heads/topic' }, 'quality-diagnostics-refs/heads/topic'],
  ]) {
    assert.equal(group(context), expected, JSON.stringify(context))
  }
})

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

test('an earlier refresh waits for the separate readiness dispatcher without dispatching Full', () => {
  const noneBranch = requester.match(/            NONE\)\n([\s\S]*?)              ;;/)[1].replace(/^          /gm, '')
  assert.match(requester, /REQUEST_LABEL: \$\{\{ github\.event\.label\.name \}\}/)
  assert.match(requester, /group: full-ci-request-.*github\.event\.label\.name == 'ready-for-full-ci' && 'dispatch' \|\| 'refresh'/)
  assert.match(requester, /NONE\|PENDING\) poll_pause; continue ;;/)
  assert.match(requester, /No successful trusted Product workflow_dispatch completed/)
  const scratch = mkdtempSync(join(tmpdir(), 'aerolink-readiness-dispatch-'))
  try {
    // Execute the workflow's actual branch with transport/JSON production intercepted. No network call
    // occurs; the ready event records one dispatch and the refresh enters polling with an empty run id.
    const script = `set -euo pipefail\nheaders=()\napi=https://invalid.example\npython() { cat >/dev/null; printf '{}'; }\ncurl() { printf 'DISPATCH\\n'; }\n${noneBranch}\n[ -z "$run_id" ]`
    for (const label of ['authority-maintenance-requested', 'documentation', '', 'READY-FOR-FULL-CI', 'ready-for-full-ci']) {
      const result = spawnSync(bash, ['--noprofile', '--norc', '-c', script], {
        env: { ...process.env, REQUEST_LABEL: label, RUNNER_TEMP: scratch.replaceAll('\\', '/') },
        encoding: 'utf8', timeout: 10_000,
      })
      assert.ifError(result.error)
      assert.equal(result.status, 0, `${label}: ${result.stderr}`)
      assert.equal(result.stdout, label === 'ready-for-full-ci' ? 'DISPATCH\n' : '', label)
    }
  } finally {
    rmSync(scratch, { recursive: true, force: true })
  }
  // Already-running or completed trusted runs continue through the existing verifier, not dispatch.
  assert.match(requester, /FOUND\*\) read -r _ run_id _ _ <<< "\$match" ;;/)
  assert.match(requester, /PENDING\) run_id="" ;;/)
})

// #987: the bound step's actual script, so the tests below exercise the workflow's own shell rather than a copy.
const boundStep = () => {
  const start = requester.indexOf('- name: Authenticate live ready PR, dispatch once, and bind exact Product success')
  const end = requester.indexOf('- name: Mint the repository-scoped Merge Authority token', start)
  assert.ok(start > 0 && end > start, 'the bound step must exist')
  return requester.slice(start, end)
}

test('completed Product jobs re-read only an absent authentication step, then fail closed at the bound (#1507)', () => {
  const step = boundStep()
  // Exercise the workflow's jobs-read shell, before its final aggregate validator. On the pre-fix workflow
  // this is the one-shot read: the missing-then-success row fails because it never requests the second page.
  const oneShot = step.indexOf('          read_api "$api/actions/runs/$run_id/jobs?')
  const retry = step.indexOf('          jobs_attempt=1')
  const end = step.indexOf('          python - "$RUNNER_TEMP/product-jobs.json"', oneShot)
  assert.ok(oneShot >= 0 && end > oneShot, 'the workflow must read and validate Product jobs')
  const shell = step.slice(retry >= 0 ? retry : oneShot, end).replace(/^          /gm, '')
  const success = { name: 'Authenticate label-dispatched pull-request context', status: 'completed', conclusion: 'success' }
  const jobs = steps => ({ jobs: [{ name: 'Classify changed product areas', steps }] })
  // This shell owns polling, not the final aggregate verdict. Existing authentication/aggregate guards remain
  // in the following validator. No production export or test-only flag is needed.
  for (const [name, responses, reads, exitCode] of [
    ['success', [jobs([success])], 1, 0],
    ['missing then success', [jobs([]), jobs([success])], 2, 0],
    ['missing twice then success', [jobs([]), jobs([]), jobs([success])], 3, 0],
    ['missing throughout', [jobs([])], 3, 1],
    ['duplicate authentication', [jobs([success, success]), jobs([success])], 1, 1],
    ...['failure', 'cancelled', 'skipped', null].map(conclusion => [
      `authentication ${conclusion}`, [jobs([{ ...success, conclusion }]), jobs([success])], 1, 1,
    ]),
    ['authentication incomplete', [jobs([{ ...success, status: 'in_progress' }]), jobs([success])], 1, 1],
    ['missing then failure', [jobs([]), jobs([{ ...success, conclusion: 'failure' }]), jobs([success])], 2, 1],
    ['no classifier', [{ jobs: [] }, jobs([success])], 1, 1],
    ['duplicate classifier', [{ jobs: [...jobs([]).jobs, ...jobs([]).jobs] }, jobs([success])], 1, 1],
  ]) {
    const scratch = mkdtempSync(join(tmpdir(), 'aerolink-jobs-read-'))
    try {
      responses.forEach((response, index) => writeFileSync(join(scratch, `response-${index + 1}.json`), JSON.stringify(response)))
      // gh api is stubbed at the transport boundary. The workflow itself still chooses when to re-read,
      // when to delay, and whether absent evidence or a definitive mismatch can authorize proceeding.
      const script = `set -euo pipefail
api=https://invalid.example
run_id=123
reads=0
gh() {
  [ "$1" = api ] && [ "$2" = "$api/actions/runs/$run_id/jobs?filter=latest&per_page=100" ] && [ "$3" = --output ]
  reads=$((reads + 1))
  printf '%s\\n' "$reads" >> "$RUNNER_TEMP/reads"
  response=$reads
  if [ "$response" -gt ${responses.length} ]; then response=${responses.length}; fi
  cp "$RUNNER_TEMP/response-$response.json" "$4"
}
read_api() { gh api "$@"; }
sleep() { printf '%s\\n' "$1" >> "$RUNNER_TEMP/sleeps"; }
${shell}
echo PROCEED
`
      const path = join(scratch, 'jobs-read.sh')
      writeFileSync(path, script)
      const result = spawnSync(bash, ['--noprofile', '--norc', path.replace(/\\/g, '/')], {
        env: { ...process.env, RUNNER_TEMP: scratch.replace(/\\/g, '/') }, encoding: 'utf8', timeout: 10_000,
      })
      assert.ifError(result.error)
      const actualReads = readFileSync(join(scratch, 'reads'), 'utf8').trim().split('\n').length
      assert.equal(actualReads, reads, `${name}: number of jobs API reads`)
      assert.equal(result.status, exitCode, `${name}: ${result.stdout}\n${result.stderr}`)
      if (reads > 1) assert.deepEqual(readFileSync(join(scratch, 'sleeps'), 'utf8').trim().split('\n'), Array(reads - 1).fill('3'), name)
      if (exitCode === 0) assert.match(result.stdout, /^PROCEED$/m, name)
      else assert.doesNotMatch(result.stdout, /^PROCEED$/m, name)
    } finally {
      rmSync(scratch, { recursive: true, force: true })
    }
  }
})

test('every read the requester makes retries transient errors, and no write is ever retried', () => {
  const step = boundStep()
  const helper = step.match(/          read_api\(\) \{\n([\s\S]*?)\n          \}/)
  assert.ok(helper, 'reads go through one helper')
  assert.match(helper[1], /curl --retry 4 --retry-delay 5 --retry-max-time 90 --fail-with-body --silent --show-error --dump-header "\$RUNNER_TEMP\/read-api-headers" "\$\{headers\[@\]\}" "\$@" && return 0/)
  // #1318: a failed read keeps curl's failure and reports the status and rate-limit headers it was refused with,
  // and a spent API budget waits for its reset instead of failing every waiting requester at once.
  // Run the workflow's own helper with curl, sleep and the clock intercepted: curl answers each read in turn with
  // the given response headers (a status line of 200 succeeds), sleep records its pause, and the clock is fixed.
  const runHelper = (responses) => {
    const scratch = mkdtempSync(join(tmpdir(), 'aerolink-read-api-'))
    try {
      const answers = responses.map((headers, index) => `    ${index + 1}) printf '${headers}' > "$RUNNER_TEMP/read-api-headers"; ${headers.startsWith('HTTP/2 200') ? 'return 0' : 'return 22'} ;;`).join('\n')
      const script = [
        'set -euo pipefail',
        `RUNNER_TEMP='${scratch.replace(/\\/g, '/')}'`,
        'headers=()',
        'reads=0',
        `curl() {\n  reads=$((reads + 1))\n  case "$reads" in\n${answers}\n    *) echo "unexpected read $reads"; return 99 ;;\n  esac\n}`,
        'sleep() { echo "SLEPT $1"; }',
        'date() { echo 1790000000; }',
        helper[0].replace(/^          /gm, ''),
        'if read_api https://invalid.example; then echo "PASSED after $reads"; else echo "FAILED $? after $reads"; fi',
      ].join('\n')
      const result = spawnSync(bash, ['--noprofile', '--norc', '-c', script], { encoding: 'utf8', timeout: 10_000 })
      assert.ifError(result.error)
      assert.equal(result.status, 0, result.stderr)
      return result.stdout
    } finally {
      rmSync(scratch, { recursive: true, force: true })
    }
  }
  const spent = (reset) => `HTTP/2 403\\r\\nx-ratelimit-limit: 1000\\r\\nx-ratelimit-remaining: 0\\r\\nx-ratelimit-reset: ${reset}\\r\\nx-ratelimit-resource: core\\r\\ncontent-type: application/json\\r\\n`
  const ok = 'HTTP/2 200\\r\\nx-ratelimit-remaining: 999\\r\\n'

  // A spent budget waits until five seconds after the reset GitHub names, then reads again.
  let output = runHelper([spent(1790000060), ok])
  assert.match(output, /::warning::GitHub API read failed: HTTP\/2 403 x-ratelimit-limit: 1000 x-ratelimit-remaining: 0 x-ratelimit-reset: 1790000060 x-ratelimit-resource: core/)
  assert.doesNotMatch(output, /content-type/)
  assert.match(output, /::notice::GitHub API budget spent; reading again in 65s/)
  assert.match(output, /^SLEPT 65$/m)
  assert.match(output, /^PASSED after 2$/m)
  // A secondary limit's retry-after is the wait; a reset already past still waits a little.
  assert.match(runHelper(['HTTP/2 403\\r\\nretry-after: 30\\r\\n', ok]), /^SLEPT 30$[\s\S]*^PASSED after 2$/m)
  assert.match(runHelper([spent(1789999000), ok]), /^SLEPT 5$[\s\S]*^PASSED after 2$/m)
  // A budget that stays spent waits at most 15 minutes a time, reads three times in all, and keeps curl's failure.
  output = runHelper([spent(1790009999), spent(1790009999), spent(1790009999)])
  assert.deepEqual(output.match(/^SLEPT \d+$/gm), ['SLEPT 900', 'SLEPT 900'])
  assert.match(output, /^FAILED 22 after 3$/m)
  // Any other refusal fails at once, without waiting: a 403 with budget left is a real refusal.
  output = runHelper(['HTTP/2 403\\r\\nx-ratelimit-remaining: 512\\r\\n'])
  assert.doesNotMatch(output, /SLEPT|::notice::/)
  assert.match(output, /^FAILED 22 after 1$/m)
  // Every other curl in the step is a write, and none of them retries: a retried dispatch could start a second
  // Full run, and a retried check-run publication could publish twice.
  const others = [...step.matchAll(/^ *curl (?:[^\n]*\\\n)*[^\n]*/gm)].map(match => match[0]).filter(call => !call.includes('--retry 4'))
  assert.equal(others.length, 1, 'the bound step has exactly one non-helper curl: the dispatch')
  for (const call of others) {
    assert.match(call, /--request POST/)
    assert.doesNotMatch(call, /--retry/)
  }
  for (const endpoint of ['"$api/pulls/$PR_NUMBER"', '"$api/actions/workflows/ci.yml/runs?', '"$api/actions/runs/$run_id/jobs?']) {
    assert.ok(step.includes(`read_api ${endpoint}`), `${endpoint} is read through the retrying helper`)
  }
  const publish = requester.slice(requester.indexOf('- name: Publish trusted pull-request readiness'), requester.indexOf('\n  pr-product-aggregate:\n'))
  assert.match(publish, /--request POST/)
  assert.doesNotMatch(publish, /--retry/)
})

test('the requester waits on a time budget, polling every 10s for two minutes, every 30s to ten, then every 120s', () => {
  const step = boundStep()
  const timeout = Number(requester.match(/dispatch-and-bind:[\s\S]*?timeout-minutes: (\d+)/)[1])
  const budget = Number(step.match(/wait_deadline=\$\(\(wait_started \+ (\d+)\)\)/)[1])
  assert.equal(budget, 70 * 60, 'the wait keeps its 70-minute budget')
  // A final poll plus its retries (at most 90s each for two reads) and the job lookup must still fit the job.
  assert.ok(timeout * 60 - budget >= 4 * 60, `timeout-minutes ${timeout} must stay ahead of the ${budget}s wait`)
  assert.match(step, /while \[ "\$SECONDS" -lt "\$wait_deadline" \]; do/)
  assert.doesNotMatch(step, /seq 1 420|sleep 10; continue/)
  const pause = step.match(/          poll_pause\(\) \{\n[\s\S]*?\n          \}/)[0].replace(/^          /gm, '')
  // Run the workflow's own function with sleep intercepted. SECONDS is bash's clock and can be set directly.
  const script = `set -euo pipefail\nsleep() { printf '%s\\n' "$1"; }\n${pause}\nwait_started=0\nfor at in 0 60 119 120 599 600 4100; do SECONDS=$at; poll_pause; done`
  const result = spawnSync(bash, ['--noprofile', '--norc', '-c', script], { encoding: 'utf8', timeout: 10_000 })
  assert.ifError(result.error)
  assert.equal(result.status, 0, result.stderr)
  // #1318: at a steady 30s one requester spent about 300 reads an hour of the repository's shared budget.
  assert.deepEqual(result.stdout.trim().split('\n'), ['10', '10', '10', '30', '30', '120', '120'])
  // The last poll can start just inside the deadline, then read (two reads, at most 90s of retries each) and
  // pause once more before the loop ends: the job's timeout must still cover it.
  assert.ok(timeout * 60 - budget >= 2 * 90 + 120, `timeout-minutes ${timeout} must cover a last poll and its 120s pause`)
})

test('the actual required aggregate refuses every non-success prerequisite result', () => {
  const job = requester.slice(requester.indexOf('\n  pr-product-aggregate:\n'))
  const script = job.match(/        run: \|\n([\s\S]*)/)[1].replace(/^          /gm, '')
  // Git Bash is already the workflow's shell on Windows; use its installed executable rather than WSL.
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
