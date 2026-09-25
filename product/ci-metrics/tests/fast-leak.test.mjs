import { test } from 'node:test'
import assert from 'node:assert/strict'
import { summarizeFastLeak, fastVerdict, normalizeLane, renderFastLeak } from '../lib/fast-leak.mjs'

const sha = (c) => c.repeat(40)
const readiness = (id, headSha, conclusion = 'failure', overrides = {}) =>
  ({ id, head_sha: headSha, event: 'workflow_dispatch', status: 'completed', conclusion, ...overrides })
const fast = (headSha, conclusion, status = 'completed') => ({ head_sha: headSha, conclusion, status })

test('a Full failure counts as caught when Fast was red on that SHA and leaked when Fast was green', () => {
  const summary = summarizeFastLeak({
    readinessRuns: [
      readiness(1, sha('a')), // Fast red: caught
      readiness(2, sha('b')), // Fast green: leaked
      readiness(3, sha('c')), // no Fast: neither
      readiness(4, sha('d'), 'success'), // not a failure
      readiness(5, sha('e'), 'failure', { event: 'merge_group' }), // not readiness
      readiness(6, sha('f'), 'cancelled'), // not a failure
    ],
    fastRuns: [fast(sha('a'), 'failure'), fast(sha('a'), 'success'), fast(sha('b'), 'success'), fast(sha('c'), null, 'in_progress')],
    failedJobsByRun: {
      1: ['Domain test suite', 'Full Product evidence aggregate'],
      2: ['Browser journeys (2/4)', 'Browser journeys (4/4)', 'API test suite (1/3)'],
    },
  })
  assert.equal(summary.failedReadiness, 3)
  assert.equal(summary.caught, 1)
  assert.equal(summary.leaked, 1)
  assert.equal(summary.noFast, 1)
  assert.equal(summary.leakRate, 0.5)
  assert.deepEqual(summary.lanes, {
    'Domain test suite': { failed: 1, leaked: 0 },
    'Browser journeys': { failed: 1, leaked: 1 },
    'API test suite': { failed: 1, leaked: 1 },
  })
  assert.deepEqual(summary.leakedRuns, [{ id: 2, headSha: sha('b'), lanes: ['API test suite', 'Browser journeys'] }])
  assert.match(renderFastLeak(summary, { since: '2026-09-11' }), /Leak rate: 50\.0%/)
})

test('Fast verdicts and lane names are normalized conservatively', () => {
  assert.equal(fastVerdict([fast(sha('a'), 'success'), fast(sha('a'), 'failure')]), 'red')
  assert.equal(fastVerdict([fast(sha('a'), 'cancelled'), fast(sha('a'), 'success')]), 'green')
  assert.equal(fastVerdict([fast(sha('a'), 'cancelled')]), 'none')
  assert.equal(normalizeLane('Browser journeys (3/4)'), 'Browser journeys')
  assert.equal(summarizeFastLeak({}).leakRate, null)
})
