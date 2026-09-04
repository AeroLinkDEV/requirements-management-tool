// Static contract tests for the metrics wiring inside .github/workflows/ci.yml.
//
// These guard the properties the round-1 review found missing:
// 1. Every metrics-only step is failure-isolated (continue-on-error), so telemetry can never fail a
//    product job or the required gate.
// 2. Fragment artifacts are attempt-scoped; the report downloads the current run's fragment artifacts
//    from all attempts into per-artifact subdirectories, and the aggregator selects the latest per
//    instance (superseded and fallback semantics are unit-tested). Previous runs' merged reports are
//    excluded by the prefix.
// 3. The metrics-report waits for every independently selected producer.
// 4. Every applicable test-bearing job is wired to structured counts (TRX, Playwright JSON, JUnit).
// 5. The gate's metrics dependency list is derived from the same event/classification predicates as the
//    trusted topology.

import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'

const repoRoot = fileURLToPath(new URL('../../../', import.meta.url))
const workflowPath = join(repoRoot, '.github', 'workflows', 'ci.yml')

function workflowLines() {
  return readFileSync(workflowPath, 'utf8').split(/\r?\n/)
}

function jobBodies(lines) {
  const jobs = {}
  const start = lines.findIndex((line) => /^jobs:$/.test(line))
  assert.ok(start >= 0, 'ci.yml must contain a top-level jobs: section')
  let current = null
  let currentLines = []
  const flush = () => {
    if (current) jobs[current] = currentLines
  }
  for (let index = start + 1; index < lines.length; index += 1) {
    const line = lines[index]
    const match = /^  ([a-zA-Z0-9_-]+):$/.exec(line)
    if (match && !line.startsWith('   ')) {
      flush()
      current = match[1]
      currentLines = [line]
    } else if (current) {
      currentLines.push(line)
    }
  }
  flush()
  return jobs
}

function stepBlocks(body) {
  const blocks = []
  let current = null
  for (const line of body) {
    const match = /^      - name: (.+)$/.exec(line)
    if (match) {
      if (current) blocks.push(current)
      current = { name: match[1], lines: [line] }
    } else if (current) {
      current.lines.push(line)
    }
  }
  if (current) blocks.push(current)
  return blocks
}

const METRICS_ONLY_STEPS = [
  'Capture job start',
  'Mark setup complete',
  'Mark test complete',
  'Write metrics fragment',
  'Upload metrics fragment',
  'Build run metadata',
  'Download fragments',
  'Aggregate and publish summary',
  'Upload merged metrics',
  'Compute metrics gate dependencies',
  'Write validated-tree manifest',
  'Upload validated-tree manifest',
]

test('every metrics-only step is failure-isolated with continue-on-error', () => {
  const jobs = jobBodies(workflowLines())
  const failures = []
  let count = 0
  for (const [job, body] of Object.entries(jobs)) {
    for (const block of stepBlocks(body)) {
      if (!METRICS_ONLY_STEPS.includes(block.name)) continue
      count += 1
      const text = block.lines.join('\n')
      if (!/continue-on-error:\s*true/.test(text)) failures.push(`${job}: ${block.name}`)
    }
  }
  assert.equal(failures.length, 0, `metrics-only steps without continue-on-error: ${failures.join(', ')}`)
  assert.ok(count >= 60, `expected at least 60 isolated metrics steps, found ${count}`)
})

test('fragment artifacts are attempt-scoped and the report pattern excludes merged reports', () => {
  const jobs = jobBodies(workflowLines())
  const uploads = []
  for (const [job, body] of Object.entries(jobs)) {
    for (const block of stepBlocks(body)) {
      if (block.name !== 'Upload metrics fragment') continue
      const text = block.lines.join('\n')
      const nameMatch = /^          name: (.+)$/m.exec(text)
      assert.ok(nameMatch, `${job}: Upload metrics fragment has no artifact name`)
      const name = nameMatch[1]
      assert.match(name, /^ci-metrics-fragment-/, `${job}: fragment artifact must use the ci-metrics-fragment- prefix (${name})`)
      assert.match(name, /\$\{\{\s*github\.run_attempt\s*\}\}/, `${job}: fragment artifact must be attempt-scoped (${name})`)
      uploads.push(name)
    }
  }
  assert.ok(uploads.length >= 12, `expected a fragment upload per fragment-producing job, found ${uploads.length}`)

  const report = jobBodies(workflowLines())['metrics-report'].join('\n')
  assert.match(report, /pattern:\s*ci-metrics-fragment-\*/)
  assert.doesNotMatch(report, /merge-multiple:\s*true/, 'each attempt artifact must land in its own subdirectory')
  assert.match(report, /if-no-files-found:\s*ignore/)
  assert.match(report, /name:\s*ci-metrics-run-\$\{\{\s*github\.run_id\s*\}\}-\$\{\{\s*github\.run_attempt\s*\}\}/)
  assert.match(report, /name:\s*validated-tree-\$\{\{\s*github\.run_id\s*\}\}-\$\{\{\s*github\.run_attempt\s*\}\}/)
  assert.match(report, /if:\s*success\(\)\s*\n\s*continue-on-error:\s*true\s*\n\s*uses: actions\/upload-artifact/, 'validated-tree manifest upload must be success-gated and isolated')
})

test('download-artifact uses download inputs while upload-only inputs stay on uploads', () => {
  const reportBlocks = stepBlocks(jobBodies(workflowLines())['metrics-report'])
  const downloadBlocks = reportBlocks.filter((block) => block.name === 'Download fragments')
  assert.equal(downloadBlocks.length, 1, 'metrics-report must have exactly one Download fragments step')
  const download = downloadBlocks[0]
  const downloadText = download.lines.join('\n')
  assert.match(downloadText, /uses:\s*actions\/download-artifact/, 'Download fragments must use download-artifact')
  assert.match(downloadText, /pattern:\s*ci-metrics-fragment-\*/)
  assert.match(downloadText, /path:\s*\$\{\{\s*runner\.temp\s*\}\}\/fragments/)
  assert.doesNotMatch(downloadText, /^\s*if-no-files-found:/m, 'if-no-files-found belongs to upload-artifact, not download-artifact')

  const uploadBlocks = reportBlocks.filter((block) => block.name === 'Upload merged metrics')
  assert.equal(uploadBlocks.length, 1, 'metrics-report must have exactly one Upload merged metrics step')
  const upload = uploadBlocks[0]
  const uploadText = upload.lines.join('\n')
  assert.match(uploadText, /uses:\s*actions\/upload-artifact/, 'Upload merged metrics must use upload-artifact')
  assert.match(uploadText, /^\s*if-no-files-found:\s*ignore/m, 'upload-only missing-file policy must remain explicit')
})

test('metrics-report waits for every independently selected producer', () => {
  const report = jobBodies(workflowLines())['metrics-report'].join('\n')
  assert.match(report, /needs:\s*\[gate,\s*changes,\s*warm-chromium-cache,\s*browser-full\]/)
  assert.match(report, /if:\s*always\(\)/)
  assert.match(report, /GITHUB_REF:\s*\$\{\{\s*github\.ref\s*\}\}/, 'provenance requires the real ref')
})

test('every applicable test-bearing job is wired to structured counts', () => {
  const jobs = jobBodies(workflowLines())
  const domain = jobs['backend-core-domain'].join('\n')
  assert.match(domain, /METRICS_COUNTS_SOURCE:\s*trx/)
  assert.match(domain, /domain\.trx/)
  const infrastructure = jobs['backend-core-infrastructure'].join('\n')
  assert.match(infrastructure, /METRICS_COUNTS_SOURCE:\s*trx/)
  assert.match(infrastructure, /infrastructure\.trx/)

  for (const job of ['browser-pr', 'browser-production', 'browser-full']) {
    const body = jobs[job].join('\n')
    assert.match(body, /METRICS_COUNTS_SOURCE:\s*playwright-json/, `${job} must wire Playwright JSON counts`)
  }

  const tooling = jobs['metrics-tooling'].join('\n')
  assert.match(tooling, /METRICS_COUNTS_SOURCE:\s*node-junit/)
  assert.match(tooling, /--test-reporter=junit/)
})

test('the gate derives its metrics dependency list from event and classifier predicates', () => {
  const gate = jobBodies(workflowLines())['gate'].join('\n')
  assert.match(gate, /Compute metrics gate dependencies/)
  assert.match(gate, /METRICS_NEEDS=\$needs/)
  assert.ok(gate.indexOf('Compute metrics gate dependencies') < gate.indexOf('Write metrics fragment'))
})

test('product enforcement in the gate has no telemetry prerequisite', () => {
  const gate = jobBodies(workflowLines())['gate'].join('\n')
  const enforceIndex = gate.indexOf('Summarise and enforce')
  const checkoutIndex = gate.indexOf('Check out repository')
  assert.ok(enforceIndex >= 0, 'gate must contain Summarise and enforce')
  assert.ok(checkoutIndex > enforceIndex, 'gate checkout must run after product enforcement')

  const blocks = stepBlocks(jobBodies(workflowLines())['gate'])
  const enforceBlock = blocks.find((block) => block.name === 'Summarise and enforce')
  assert.ok(enforceBlock, 'gate Summarise and enforce step must exist')
  const enforceText = enforceBlock.lines.join('\n')
  assert.doesNotMatch(enforceText, /METRICS_(TIMING|FRAGMENT|JOB|COUNTS|NEEDS)/, 'enforcement must not depend on metrics script state')
  assert.doesNotMatch(enforceText, /mark\.mjs|write-fragment|upload-artifact|Check out repository/, 'enforcement must run before any telemetry step')

  const checkoutBlock = blocks.find((block) => block.name === 'Check out repository')
  assert.ok(checkoutBlock, 'gate checkout step must exist')
  assert.match(checkoutBlock.lines.join('\n'), /continue-on-error:\s*true/, 'gate telemetry checkout must be isolated')

  const setupIndex = gate.indexOf('Mark setup complete')
  assert.ok(setupIndex > checkoutIndex, 'gate setup marker must run after the telemetry checkout so the script exists')
})

test('the merge-group aggregate refuses a queue entry whose product gates did not execute', () => {
  // A merge queue validates the composed tree with a merge_group run of this workflow. A job gated on
  // pull_request can silently disappear there; the merge-group guard inside "Summarise and enforce" is
  // what turns that missing evidence into a red required check rather than a merge. No other test pins it.
  //
  // The identical gate pair list also appears in the generic failure loop earlier in this step, which
  // deliberately permits skipped results. Every merge-group assertion below runs inside the text starting
  // at the merge-group condition, so removing a browser job from that specific loop still fails this test.
  const blocks = stepBlocks(jobBodies(workflowLines())['gate'])
  const enforceBlock = blocks.find((block) => block.name === 'Summarise and enforce')
  assert.ok(enforceBlock, 'gate Summarise and enforce step must exist')
  const enforceText = enforceBlock.lines.join('\n')

  // The complete opening line, line-anchored: a `true ||` prefix would disable the guard while an
  // unanchored search of the operands alone still matched.
  const conditionPattern = /^ {10}if \[ "\$EVENT_NAME" = "merge_group" \] && \[ "\$DOCS_ONLY" != "true" \]; then$/m
  const conditionMatch = enforceText.match(conditionPattern)
  assert.ok(conditionMatch, 'the merge-group enforcement condition must exist in the gate')
  const guardIndex = conditionMatch.index
  assert.ok(guardIndex >= 0, 'the merge-group enforcement condition must exist in the gate')
  // Bound the block by its own closing fi. guardText otherwise reaches the end of the step, so moving
  // the terminator up would make the loop unconditional while every assertion below still matched —
  // and an unconditional missing-gate check would fail ordinary label-dispatched runs whose lanes
  // legitimately skipped.
  const closeIndex = enforceText.slice(guardIndex).search(/^ {10}fi$/m)
  assert.ok(closeIndex >= 0, 'the merge-group guard block must be terminated by its closing fi')
  const guardText = enforceText.slice(guardIndex, guardIndex + closeIndex)

  // The refusal loop consumes env variables; pin each to its job's actual result, or a hardcoded
  // `success` would satisfy the guard while a job skipped. The same applies to the step's own
  // control inputs: a hardcoded EVENT_NAME would never select the merge-group branch, and a
  // hardcoded DOCS_ONLY of true would skip it for every run. Bindings must appear exactly once as a
  // live line of the step's actual env mapping — parsed from the env: block only, so a matching line
  // inside the run script's heredocs satisfies nothing.
  const envStart = enforceText.indexOf('        env:')
  const runStart = enforceText.indexOf('        run:')
  assert.ok(envStart >= 0 && runStart > envStart, 'the enforcement step must define its env mapping before the run script')
  const envMapping = enforceText.slice(envStart, runStart)
  const envBindingCount = (binding) => (envMapping.match(new RegExp(`^ {10}${binding.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}$`, 'gm')) || []).length
  for (const [envVar, job] of [
    ['BACKEND_API', 'backend-api'],
    ['BACKEND_CORE_DOMAIN', 'backend-core-domain'],
    ['BACKEND_CORE_INFRASTRUCTURE', 'backend-core-infrastructure'],
    ['CLIENT', 'client'],
    ['CONTRACTS', 'script-contracts'],
    ['BROWSER', 'browser-pr'],
    ['PRODUCTION', 'browser-production'],
  ]) {
    const binding = `${envVar}: \${{ needs.${job}.result }}`
    assert.equal(envBindingCount(binding), 1, `${envVar} must be bound exactly once to needs.${job}.result as a live env-mapping line — the merge-group refusal is only as truthful as its inputs`)
  }
  for (const binding of [
    "EVENT_NAME: ${{ inputs.pull_request_number != '' && 'pull_request' || github.event_name }}",
    'DOCS_ONLY: ${{ needs.changes.outputs.docs_only }}',
    'LAUNCHERS_ONLY: ${{ needs.changes.outputs.launchers_only }}',
    'POST_MERGE_SKIP: ${{ needs.changes.outputs.post_merge_skip }}',
  ]) {
    assert.equal(envBindingCount(binding), 1, `the gate must bind its guard input dynamically exactly once: ${binding.split(':')[0]} — a hardcoded value silently disarms the merge-group refusal`)
  }

  // The guard only matters if the gate runs at all: the workflow must keep its top-level merge_group
  // trigger, or queue candidates never start a Product run and every required check pends to timeout.
  // Scoped to the on: mapping — a job merely named merge_group: satisfies nothing here.
  // (full-ci-readiness-dispatch pins the trigger set too; this contract stays self-contained.)
  const lines = workflowLines()
  const onIndex = lines.findIndex((line) => line === 'on:')
  const jobsIndex = lines.findIndex((line) => line === 'jobs:')
  assert.ok(onIndex >= 0 && jobsIndex > onIndex, 'ci.yml must define its triggers in a top-level on: block')
  assert.ok(
    lines.slice(onIndex, jobsIndex).some((line) => line === '  merge_group:'),
    'the on: block must keep the merge_group workflow trigger',
  )

  // The guard only matters if the aggregate runs and its failure counts. A skipped dependency must
  // not skip the gate (needs default behavior), and the enforce step must not be failure-isolated.
  const gateBody = jobBodies(workflowLines())['gate'].join('\n')
  assert.match(gateBody, /^    if: always\(\)$/m, 'the gate must run even when a dependency was skipped — otherwise skipped evidence is never rejected')
  assert.doesNotMatch(enforceText, /continue-on-error:/, 'the enforcement step must propagate failure — continue-on-error would let GitHub swallow the refusal exit')
  assert.doesNotMatch(
    gateBody,
    /^ {4}continue-on-error:/m,
    'the gate job itself must have no failure isolation — a job-level continue-on-error would turn a red refusal into a passing run',
  )
  assert.doesNotMatch(
    enforceText,
    /^\s*if:/m,
    'the enforcement step must have no step-level condition — a restrictive if could exclude merge_group runs, and a skipped step never evaluates the refusal where it matters most',
  )

  assert.equal(
    (guardText.match(/missing=""/g) || []).length,
    1,
    'the missing set must be initialized exactly once before the loop — additional or equivalent resets discard earlier collected gates',
  )
  // Any assignment to the missing set inside the loop other than the predicate's collection — under
  // any quoting or spelling — discards gates collected by earlier iterations, and unset is the same
  // reset in another costume.
  const loopStart = guardText.indexOf('for pair in "backend-api:$BACKEND_API"')
  const loopEnd = guardText.indexOf('\n            done', loopStart)
  assert.ok(loopEnd > loopStart, 'the collecting loop must terminate with done before the refusal')
  const loopBody = guardText.slice(loopStart, loopEnd)
  assert.equal(
    (loopBody.match(/missing=/g) || []).length,
    1,
    'the only assignment to the missing set inside the loop must be the predicate collection',
  )
  assert.doesNotMatch(loopBody, /unset missing\b/, 'the missing set must not be unset inside the collecting loop')
  assert.doesNotMatch(
    loopBody,
    /^\s*(break|continue)\b/m,
    'the collecting loop must not exit early — a break or continue before collection leaves the missing set incomplete and the refusal vacuous',
  )
  assert.match(
    guardText,
    /^ {12}for pair in "backend-api:\$BACKEND_API" "backend-core-domain:\$BACKEND_CORE_DOMAIN" "backend-core-infrastructure:\$BACKEND_CORE_INFRASTRUCTURE" "client:\$CLIENT" "script-contracts:\$CONTRACTS" "browser-pr:\$BROWSER" "browser-production:\$PRODUCTION"; do$/m,
    'the merge-group refusal must enumerate the complete seven-gate list as a line-anchored command — a prefixed or degenerate loop that never runs would vacuously pass, and the generic loop additionally admits postgresql-smoke and permits skips',
  )
  assert.match(
    guardText,
    /name="\$\{pair%%:\*\}"\n\s*result="\$\{pair#\*:\}"\n\s*\[ "\$result" = "success" \] \|\| missing="\$missing \$name"/,
    'each pair must be split into its own name and result before the success predicate populates the missing set — a stale result from an earlier loop would vacuously pass',
  )
  const initIndex = guardText.indexOf('missing=""')
  const listIndex = guardText.indexOf('for pair in "backend-api:$BACKEND_API"')
  const finalIfIndex = guardText.indexOf('[ -n "$missing" ]; then')
  assert.ok(
    initIndex >= 0 && listIndex > initIndex && finalIfIndex > listIndex,
    'the missing set must be initialized before the collecting loop, and the loop must run before the refusal fires — re-initializing after the loop would erase every collected gate',
  )
  assert.match(
    guardText,
    /\[ "\$result" = "success" \] \|\| missing="\$missing \$name"\n\s*done\n\s*if \[ -n "\$missing" \]; then/,
    'the refusal must fire only after the collecting loop terminates — a check inside the loop tests an incomplete missing set',
  )
  assert.match(
    guardText,
    /\[ -n "\$missing" \]; then\n\s*echo "::error::A merge-queue run must actually execute the product gates\. These did not run:\$missing"\n\s*exit 1\n/,
    'a missing merge-group gate must fail the step in the foreground — a backgrounded or conditional exit would let the aggregate pass without evidence',
  )
  const refusalExitIndex = guardText.indexOf('exit 1\n', finalIfIndex)
  assert.ok(refusalExitIndex > finalIfIndex, 'the refusal exit must follow the missing-gate check')
  assert.equal(
    guardText.indexOf('exit'),
    refusalExitIndex,
    'the only exit inside the merge-group guard must be the refusal exit — an earlier successful exit would skip collection and pass vacuously',
  )
  // Exactly three control statements may exist: the outer condition (guardText's first line), the
  // collecting for loop, and the refusal if. Any nested wrapper such as 'if false; then' can render
  // the whole body unreachable while every substring and adjacency assertion above still matches.
  const controlLines = guardText.split('\n').filter((line) => /^\s*(if |elif |case |while |until |for )/.test(line))
  assert.deepEqual(
    controlLines.map((line) => line.trim()),
    [
      'if [ "$EVENT_NAME" = "merge_group" ] && [ "$DOCS_ONLY" != "true" ]; then',
      'for pair in "backend-api:$BACKEND_API" "backend-core-domain:$BACKEND_CORE_DOMAIN" "backend-core-infrastructure:$BACKEND_CORE_INFRASTRUCTURE" "client:$CLIENT" "script-contracts:$CONTRACTS" "browser-pr:$BROWSER" "browser-production:$PRODUCTION"; do',
      'if [ -n "$missing" ]; then',
    ],
    'the guard must contain exactly the outer condition, the collecting loop, and the refusal if — no nested wrappers',
  )
})
