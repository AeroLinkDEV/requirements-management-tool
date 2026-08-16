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
  const download = reportBlocks.find((block) => /uses:\s*actions\/download-artifact/.test(block.lines.join('\n')))
  assert.ok(download, 'metrics-report must have a download-artifact step')
  const downloadText = download.lines.join('\n')
  assert.match(downloadText, /pattern:\s*ci-metrics-fragment-\*/)
  assert.match(downloadText, /path:\s*\$\{\{\s*runner\.temp\s*\}\}\/fragments/)
  assert.doesNotMatch(downloadText, /^\s*if-no-files-found:/m, 'if-no-files-found belongs to upload-artifact, not download-artifact')

  const upload = reportBlocks.find((block) => /uses:\s*actions\/upload-artifact/.test(block.lines.join('\n')))
  assert.ok(upload, 'metrics-report must have an upload-artifact step')
  assert.match(upload.lines.join('\n'), /^\s*if-no-files-found:\s*ignore/m, 'upload-only missing-file policy must remain explicit')
})

test('metrics-report waits for every independently selected producer', () => {
  const report = jobBodies(workflowLines())['metrics-report'].join('\n')
  assert.match(report, /needs:\s*\[gate,\s*changes,\s*warm-chromium-cache,\s*browser-full\]/)
  assert.match(report, /if:\s*always\(\)/)
  assert.match(report, /GITHUB_REF:\s*\$\{\{\s*github\.ref\s*\}\}/, 'provenance requires the real ref')
})

test('every applicable test-bearing job is wired to structured counts', () => {
  const jobs = jobBodies(workflowLines())
  const core = jobs['backend-core'].join('\n')
  assert.match(core, /METRICS_COUNTS_SOURCE:\s*trx/)
  assert.match(core, /domain\.trx/)
  assert.match(core, /infrastructure\.trx/)

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
