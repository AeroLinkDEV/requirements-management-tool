import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { awaitQueueHead, QUEUE_BRANCH_PATTERN, queueWaitDecision } from '../lib/queue-head-wait.mjs'

const repoFile = path => readFileSync(fileURLToPath(new URL(`../../../${path}`, import.meta.url)), 'utf8')

test('the binding job waits before minting any token, within its timeout, and never judges a superseded run', () => {
  const workflow = repoFile('.github/workflows/merge-queue-binding.yml')
  const bind = workflow.slice(workflow.indexOf('  bind:'), workflow.indexOf('  review-maintenance:'))
  const wait = bind.indexOf('node product/ci-metrics/bin/await-queue-head.mjs')
  assert.ok(wait > 0, 'the bind job runs the queue-head wait')
  for (const later of ['detect-maintenance-candidate.mjs', 'id: maintenance-evidence-token', 'id: authority-token', 'verify-merge-authority.mjs']) {
    assert.ok(bind.indexOf(later) > wait, `${later} runs after the wait, so no token ages while waiting`)
  }
  for (const id of ['detect-maintenance', 'authority-token', 'bind']) {
    const step = bind.slice(bind.indexOf(`id: ${id}`))
    assert.match(step.slice(0, step.indexOf('\n      - ') > 0 ? step.indexOf('\n      - ') : undefined),
      /steps\.queue-head\.outputs\.superseded != 'true'/, `${id} is skipped for a discarded candidate`)
  }
  const timeout = Number(/timeout-minutes: (\d+)/.exec(bind)?.[1])
  const budget = /BUDGET_MS = (\d+) \* 60 \* 1000/.exec(repoFile('product/ci-metrics/bin/await-queue-head.mjs'))
  assert.ok(budget, 'the wait budget is declared in minutes')
  assert.ok(timeout >= Number(budget[1]) + 10, `job timeout ${timeout} leaves the verifier room after a ${budget[1]}-minute wait`)
})

const sha = character => character.repeat(40)
const entry = (position, oid = sha('c')) => ({ position, state: 'AWAITING_CHECKS', headCommit: { oid } })

test('a protected-diff candidate behind another entry waits; everything else goes to the verifier', () => {
  const candidateSha = sha('c')
  const protectedDiff = ['product/ci-metrics/']
  assert.equal(queueWaitDecision({ entry: entry(2), candidateSha, protectedDiff }), 'wait')
  assert.equal(queueWaitDecision({ entry: entry(1), candidateSha, protectedDiff }), 'evaluate')
  // An ordinary candidate behind ordinary entries is judged at once, as today.
  assert.equal(queueWaitDecision({ entry: entry(3), candidateSha, protectedDiff: [] }), 'evaluate')
  // The queue re-composed this PR: this run's candidate is discarded, so it is never judged.
  assert.equal(queueWaitDecision({ entry: entry(2, sha('d')), candidateSha, protectedDiff }), 'superseded')
  // No longer queued, or anything malformed: the verifier decides exactly as it always has.
  for (const [name, input] of [
    ['not queued', { entry: null, candidateSha, protectedDiff }],
    ['no candidate commit yet', { entry: { position: 2, headCommit: null }, candidateSha, protectedDiff }],
    ['malformed position', { entry: { ...entry(2), position: '2' }, candidateSha, protectedDiff }],
    ['malformed candidate sha', { entry: entry(2), candidateSha: 'abc', protectedDiff }],
    ['unknown protected diff', { entry: entry(2), candidateSha, protectedDiff: undefined }],
  ]) assert.equal(queueWaitDecision(input), 'evaluate', name)
})

function clock() {
  let t = 0
  return { now: () => t, sleep: async ms => { t += ms } }
}

test('the wait ends when the candidate heads the queue', async () => {
  const positions = [3, 2, 1]
  const result = await awaitQueueHead({ ...clock(), candidateSha: sha('c'), budgetMs: 600_000, intervalMs: 60_000,
    readProtectedDiff: async () => ['.github/'], readEntry: async () => entry(positions.shift()) })
  assert.deepEqual(result, { outcome: 'evaluate', reason: 'position 1' })
})

test('the wait stops without judging when the queue discards the candidate', async () => {
  const entries = [entry(2), entry(2, sha('d'))]
  const result = await awaitQueueHead({ ...clock(), candidateSha: sha('c'), budgetMs: 600_000, intervalMs: 60_000,
    readProtectedDiff: async () => ['.github/'], readEntry: async () => entries.shift() })
  assert.equal(result.outcome, 'superseded')
})

test('an exhausted budget or any read failure falls back to the verifier', async () => {
  const stuck = await awaitQueueHead({ ...clock(), candidateSha: sha('c'), budgetMs: 180_000, intervalMs: 60_000,
    readProtectedDiff: async () => ['.github/'], readEntry: async () => entry(2) })
  assert.deepEqual(stuck, { outcome: 'evaluate', reason: 'wait-budget-exhausted' })
  const noDiff = await awaitQueueHead({ ...clock(), candidateSha: sha('c'), budgetMs: 600_000, intervalMs: 60_000,
    readProtectedDiff: async () => { throw new Error('tree') }, readEntry: async () => entry(2) })
  assert.equal(noDiff.outcome, 'evaluate')
  const noQueue = await awaitQueueHead({ ...clock(), candidateSha: sha('c'), budgetMs: 600_000, intervalMs: 60_000,
    readProtectedDiff: async () => ['.github/'], readEntry: async () => { throw new Error('graphql') } })
  assert.deepEqual(noQueue, { outcome: 'evaluate', reason: 'queue-unreadable' })
})

test('only merge-queue candidate branches name a PR', () => {
  assert.equal(QUEUE_BRANCH_PATTERN.exec(`gh-readonly-queue/main/pr-1156-${sha('a')}`)?.[1], '1156')
  assert.equal(QUEUE_BRANCH_PATTERN.exec(`gh-readonly-queue/release/pr-1156-${sha('a')}`), null)
  assert.equal(QUEUE_BRANCH_PATTERN.exec('ci/1156-queue-waits-not-refuses'), null)
})
