import { test } from 'node:test'
import assert from 'node:assert/strict'
import {
  discoverSpecs, weightFiles, planShard, packIntoLanes, verifyLaneCoverage, mergeLaneReports, laneEnvironment,
} from './lane-plan-lib.mjs'

const listed = `[chromium] › a.spec.ts:1:1 › a1
[chromium] › a.spec.ts:2:1 › a2
[chromium] › b.spec.ts:1:1 › b1
[chromium] › c.spec.ts:1:1 › c1
[chromium] › z.spec.ts:1:1 › z1
`

test('discoverSpecs counts tests per spec file', () => {
  const counts = discoverSpecs(listed)
  assert.equal(counts.get('a.spec.ts'), 2)
  assert.equal(counts.get('b.spec.ts'), 1)
  assert.equal(counts.get('z.spec.ts'), 1)
  assert.equal(counts.size, 4)
})

test('packIntoLanes splits heaviest-first with no overlap and complete union', () => {
  const entries = weightFiles(discoverSpecs(listed), {})
  const lanes = packIntoLanes(entries, 2)
  const files = lanes.flatMap((lane) => lane.files)
  assert.equal(new Set(files).size, files.length)
  assert.equal(files.length, 4)
  assert.ok(lanes.every((lane) => lane.files.length > 0 && lane.expected > 0))
})

test('planShard matches the existing single-process shard semantics', () => {
  const counts = discoverSpecs(listed)
  const { mine, expected } = planShard(counts, {}, 1, 2)
  assert.equal(expected, mine.reduce((sum, entry) => sum + entry.tests, 0))
})

test('verifyLaneCoverage rejects empty, overlapping, and undercounted lanes', () => {
  const plan = {
    expected: 5,
    lanes: [
      { name: 'a', files: ['a.spec.ts', 'b.spec.ts'], expected: 3 },
      { name: 'b', files: ['c.spec.ts', 'z.spec.ts'], expected: 2 },
    ],
  }
  const good = verifyLaneCoverage({
    plan,
    lanes: [
      { files: ['a.spec.ts', 'b.spec.ts'], executed: 3 },
      { files: ['c.spec.ts', 'z.spec.ts'], executed: 2 },
    ],
  })
  assert.equal(good.ok, true)

  const overlap = verifyLaneCoverage({
    plan,
    lanes: [
      { files: ['a.spec.ts', 'b.spec.ts', 'c.spec.ts'], executed: 3 },
      { files: ['c.spec.ts', 'z.spec.ts'], executed: 2 },
    ],
  })
  assert.equal(overlap.ok, false)
  assert.ok(overlap.errors.some((error) => /more than one lane/.test(error)))

  const missing = verifyLaneCoverage({
    plan,
    lanes: [
      { files: ['a.spec.ts'], executed: 1 },
      { files: ['c.spec.ts', 'z.spec.ts'], executed: 2 },
    ],
  })
  assert.equal(missing.ok, false)
  assert.ok(missing.errors.some((error) => /did not run/.test(error)))

  const empty = verifyLaneCoverage({ plan: { ...plan, lanes: [{ name: 'a', files: [], expected: 0 }, plan.lanes[1]] }, lanes: [] })
  assert.equal(empty.ok, false)
})

test('mergeLaneReports sums stats and concatenates suites', () => {
  const merged = mergeLaneReports([
    { stats: { expected: 3, unexpected: 0, flaky: 1, skipped: 0 }, suites: [{ title: 'a' }] },
    { stats: { expected: 2, unexpected: 1, flaky: 0, skipped: 0 }, suites: [{ title: 'b' }] },
  ], { shard: 1 })
  assert.deepEqual(merged.stats, { expected: 5, unexpected: 1, flaky: 1, skipped: 0 })
  assert.equal(merged.suites.length, 2)
})

test('laneEnvironment isolates run id, ports, databases, and directories per lane', () => {
  const a = laneEnvironment({ runId: 'run-1', shard: 1, lane: 'a' })
  const b = laneEnvironment({ runId: 'run-1', shard: 1, lane: 'b' })
  assert.notEqual(a.AEROLINK_E2E_RUN_ID, b.AEROLINK_E2E_RUN_ID)
  assert.notEqual(a.AEROLINK_E2E_API_PORT, b.AEROLINK_E2E_API_PORT)
  assert.notEqual(a.AEROLINK_E2E_CLIENT_PORT, b.AEROLINK_E2E_CLIENT_PORT)
  assert.notEqual(a.AEROLINK_E2E_OUTPUT_DIR, b.AEROLINK_E2E_OUTPUT_DIR)
  assert.notEqual(a.PLAYWRIGHT_JSON_OUTPUT_NAME, b.PLAYWRIGHT_JSON_OUTPUT_NAME)
  assert.equal(a.AEROLINK_E2E_SKIP_BUILD, 'true')
  const shard2 = laneEnvironment({ runId: 'run-1', shard: 2, lane: 'a' })
  assert.notEqual(a.AEROLINK_E2E_API_PORT, shard2.AEROLINK_E2E_API_PORT)
})
