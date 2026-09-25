import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { selectSupersededRuns, isQueueRef } from '../lib/queue-supersession.mjs'

const repoRoot = fileURLToPath(new URL('../../../', import.meta.url))
const REF = `gh-readonly-queue/main/pr-1142-${'5'.repeat(40)}`
const OLD = 'a'.repeat(40)
const NEW = 'b'.repeat(40)
const TRIGGER = '2026-09-25T12:36:50Z'

function run(overrides = {}) {
  return {
    id: 1, path: '.github/workflows/ci.yml', event: 'merge_group', head_branch: REF, head_sha: OLD,
    status: 'in_progress', created_at: '2026-09-25T12:20:00Z', ...overrides,
  }
}

test('a run testing a deleted queue candidate is cancelled, and nothing else is', () => {
  // Shaped like pr-1142 on 2026-09-25: the ref was deleted at 12:36:47 and its run went on until 13:23:20.
  const runs = [
    run({ id: 10 }),
    run({ id: 11, status: 'queued' }),
    run({ id: 20, status: 'completed' }),
    run({ id: 21, path: '.github/workflows/merge-queue-binding.yml' }),
    run({ id: 22, event: 'workflow_dispatch' }),
    run({ id: 23, head_branch: `gh-readonly-queue/main/pr-1143-${'6'.repeat(40)}` }),
    run({ id: 24, created_at: '2026-09-25T12:37:30Z' }),
    run({ id: 25, head_sha: 'not-a-sha' }),
  ]
  const { cancel, keep } = selectSupersededRuns({ deletedRef: REF, runs, currentRefSha: null, triggeredAt: TRIGGER })
  assert.deepEqual(cancel.map((r) => r.id), [10, 11])
  assert.deepEqual(Object.fromEntries(keep.map((k) => [k.id, k.reason])), {
    20: 'already complete',
    21: 'not the Product workflow',
    22: 'event workflow_dispatch is not merge_group',
    23: 'a different ref',
    24: 'created after the deletion was observed',
    25: 'no usable commit',
  })
})

test('a candidate the queue recreated under the same name keeps its run', () => {
  // The same ref name held two runs on 2026-09-25 (pr-1116): a recreated ref must not lose its live run.
  const runs = [run({ id: 30, head_sha: OLD }), run({ id: 31, head_sha: NEW })]
  const { cancel, keep } = selectSupersededRuns({ deletedRef: REF, runs, currentRefSha: NEW, triggeredAt: TRIGGER })
  assert.deepEqual(cancel.map((r) => r.id), [30])
  assert.deepEqual(keep, [{ id: 31, reason: 'the ref exists again at this commit' }])
})

test('the selection refuses to act without a queue ref or a trigger time', () => {
  for (const deletedRef of ['main', 'claude/feature', 'gh-readonly-queue/main/', 'gh-readonly-queue/other/pr-1-x', null]) {
    assert.equal(isQueueRef(deletedRef), false, String(deletedRef))
    assert.throws(() => selectSupersededRuns({ deletedRef, runs: [run()], triggeredAt: TRIGGER }))
  }
  assert.throws(() => selectSupersededRuns({ deletedRef: REF, runs: [run()], triggeredAt: null }), /trigger time/)
  assert.throws(() => selectSupersededRuns({ deletedRef: REF, runs: [run()], currentRefSha: 'x', triggeredAt: TRIGGER }), /malformed/)
})

test('the canceller runs only from the default branch definition on queue-ref deletions', () => {
  const workflow = readFileSync(`${repoRoot}.github/workflows/cancel-superseded-queue-runs.yml`, 'utf8')
  assert.match(workflow, /^on:\n  delete:\n/m)
  assert.match(workflow, /if: github\.event\.ref_type == 'branch' && startsWith\(github\.event\.ref, 'gh-readonly-queue\/main\/'\)/)
  assert.match(workflow, /ref: \$\{\{ github\.event\.repository\.default_branch \}\}/)
  assert.match(workflow, /permissions:\n  actions: write\n  contents: read\n\n/)
})
