import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { join } from 'node:path'
import { cadenceEntry, cadenceEntries, buildCadenceReport, CADENCE_SCHEMA_VERSION } from '../lib/cadence.mjs'

const sha = (char) => char.repeat(40)
const pr = (overrides = {}) => ({
  number: 10,
  created_at: '2026-08-17T10:00:00Z',
  merged_at: '2026-08-17T13:00:00Z',
  merge_commit_sha: sha('d'),
  head: { ref: 'feature/cadence', sha: sha('c') },
  ...overrides,
})
const run = (overrides = {}) => ({
  id: 1,
  event: 'pull_request',
  head_branch: 'feature/cadence',
  head_sha: sha('a'),
  created_at: '2026-08-17T10:05:00Z',
  run_attempt: 1,
  conclusion: 'success',
  ...overrides,
})

test('post-switch cadence counts Fast-observed pushes and readiness Full separately', () => {
  const product = [
    run({ id: 20, event: 'workflow_dispatch', head_sha: sha('c'), created_at: '2026-08-17T12:30:00Z', run_attempt: 2 }),
    run({ id: 21, event: 'push', head_branch: 'main', head_sha: sha('d'), created_at: '2026-08-17T13:00:05Z' }),
  ]
  const fast = [
    run({ id: 11, head_sha: sha('a'), created_at: '2026-08-17T10:05:00Z' }),
    run({ id: 12, head_sha: sha('b'), created_at: '2026-08-17T11:00:00Z' }),
    run({ id: 13, head_sha: sha('c'), created_at: '2026-08-17T12:00:00Z' }),
    run({ id: 14, head_sha: sha('c'), created_at: '2026-08-17T12:01:00Z' }),
  ]
  const entry = cadenceEntry(pr(), product, fast, new Map([[20, 120_000]]))
  assert.equal(entry.cadence, 'after')
  assert.equal(entry.pushes, 3)
  assert.equal(entry.finalPushObservedAt, '2026-08-17T12:00:00.000Z')
  assert.equal(entry.finalPushToMergeMs, 60 * 60 * 1000)
  assert.equal(entry.preMergeFullRuns, 1)
  assert.equal(entry.preMergeFullAttempts, 2)
  assert.equal(entry.readinessFullRuns, 1)
  assert.equal(entry.pullRequestFullRuns, 0)
  assert.equal(entry.postMergeFullRuns, 1)
  assert.equal(entry.cancelledRunnerMs, 120_000)
})

test('pre-switch cadence falls back to pull-request Product runs for pushes and Full runs', () => {
  const oldPr = pr({
    created_at: '2026-08-16T10:00:00Z',
    merged_at: '2026-08-16T13:00:00Z',
  })
  const product = [
    run({ id: 1, head_sha: sha('a'), created_at: '2026-08-16T10:05:00Z' }),
    run({ id: 2, head_sha: sha('b'), created_at: '2026-08-16T11:00:00Z' }),
    run({ id: 3, head_sha: sha('c'), created_at: '2026-08-16T12:00:00Z' }),
  ]
  const entry = cadenceEntry(oldPr, product, [])
  assert.equal(entry.cadence, 'before')
  assert.equal(entry.pushes, 3)
  assert.equal(entry.preMergeFullRuns, 3)
  assert.equal(entry.pullRequestFullRuns, 3)
  assert.equal(entry.readinessFullRuns, 0)
  assert.equal(entry.finalPushToMergeMs, 60 * 60 * 1000)
})

test('branch reuse and unrelated runs outside the PR window are excluded', () => {
  const product = [
    run({ id: 1, created_at: '2026-08-10T10:00:00Z' }),
    run({ id: 2, head_branch: 'other', created_at: '2026-08-17T11:00:00Z' }),
    run({ id: 3, event: 'workflow_dispatch', head_branch: 'other', created_at: '2026-08-17T11:00:00Z' }),
  ]
  const entry = cadenceEntry(pr(), product, [])
  assert.equal(entry.pushes, 0)
  assert.equal(entry.preMergeFullRuns, 0)
  assert.equal(entry.finalPushToMergeMs, null)
})

test('cadenceEntries rejects malformed PRs and bounds/sorts output', () => {
  const rows = cadenceEntries([
    pr({ number: 1, merged_at: '2026-08-17T13:00:00Z' }),
    pr({ number: 2, merged_at: '2026-08-18T13:00:00Z' }),
    { number: 3 },
  ], [], [])
  assert.deepEqual(rows.map((row) => row.pr), [2, 1])
})

test('report records all closeout metrics with before/after separation', () => {
  const before = cadenceEntry(pr({ number: 1, created_at: '2026-08-16T10:00:00Z', merged_at: '2026-08-16T13:00:00Z' }), [
    run({ id: 1, head_sha: sha('a'), created_at: '2026-08-16T10:00:00Z' }),
    run({ id: 2, head_sha: sha('c'), created_at: '2026-08-16T12:00:00Z' }),
  ], [])
  const after = cadenceEntry(pr({ number: 2 }), [
    run({ id: 20, event: 'workflow_dispatch', head_sha: sha('c'), created_at: '2026-08-17T12:30:00Z' }),
  ], [
    run({ id: 10, head_sha: sha('c'), created_at: '2026-08-17T12:00:00Z' }),
  ])
  const report = buildCadenceReport([after, before], { generatedAt: '2026-08-17T14:00:00Z' })
  assert.equal(report.schemaVersion, CADENCE_SCHEMA_VERSION)
  assert.equal(report.summary.before.merges, 1)
  assert.equal(report.summary.before.preMergeFullRuns, 2)
  assert.equal(report.summary.after.merges, 1)
  assert.equal(report.summary.after.preMergeFullRuns, 1)
  assert.equal(report.summary.after.fullRunsPerMerge.median, 1)
  assert.match(report.markdown, /Pushes/)
  assert.match(report.markdown, /Cancelled runner time/)
  assert.match(report.markdown, /Final-push-to-merge/)
  assert.match(report.markdown, /Full runs\/merge median/)
})

const repoRoot = fileURLToPath(new URL('../../../', import.meta.url))

test('cadence workflow is trusted, read-only, and non-authoritative', () => {
  const workflow = readFileSync(join(repoRoot, '.github/workflows/ci-cadence-metrics.yml'), 'utf8')
  assert.match(workflow, /^name: CI cadence metrics$/m)
  assert.match(workflow, /workflow_run:/)
  assert.match(workflow, /Product quality gate/)
  assert.match(workflow, /Fast PR feedback \(advisory\)/)
  assert.match(workflow, /contents: read/)
  assert.match(workflow, /actions: read/)
  assert.match(workflow, /pull-requests: read/)
  assert.doesNotMatch(workflow, /:\s*write\b/)
  assert.match(workflow, /ref: \$\{\{ github\.event\.repository\.default_branch \}\}/)
  assert.match(workflow, /cadence-collect\.mjs/)
  assert.match(workflow, /ci-cadence-metrics-/)
  assert.doesNotMatch(workflow, /Report what this run validated/)
  assert.doesNotMatch(workflow, /54329|product[\\/]\.local/)
})
