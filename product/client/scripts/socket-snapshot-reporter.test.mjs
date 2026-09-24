import { test } from 'node:test'
import assert from 'node:assert/strict'
import SocketSnapshotReporter, { SNAPSHOT_SCRIPT } from './socket-snapshot-reporter.mjs'

const fakeTest = { titlePath: () => ['', 'rendered', 'artifact-thread-rendered.spec.ts', 'evidence', 'the whole hash'] }
const reporterWith = options => {
  const lines = []
  const scripts = []
  const reporter = new SocketSnapshotReporter({
    enabled: true, capture: script => { scripts.push(script); return { total: 3, states: { TimeWait: 2 } } }, log: line => lines.push(line), ...options,
  })
  return { reporter, lines, scripts }
}

test('a failed attempt logs one socket snapshot line with the test and attempt', () => {
  const { reporter, lines, scripts } = reporterWith()
  reporter.onTestEnd(fakeTest, { status: 'failed', retry: 0 })
  assert.equal(scripts.length, 1)
  assert.equal(scripts[0], SNAPSHOT_SCRIPT)
  assert.equal(lines.length, 1)
  assert.match(lines[0], /^\[socket-snapshot\] evidence › the whole hash \(attempt 1, failed, \d+ ms\): \{"total":3,"states":\{"TimeWait":2\}\}$/)
})

test('passing and skipped attempts capture nothing', () => {
  const { reporter, lines } = reporterWith()
  reporter.onTestEnd(fakeTest, { status: 'passed', retry: 0 })
  reporter.onTestEnd(fakeTest, { status: 'skipped', retry: 0 })
  assert.deepEqual(lines, [])
})

test('timed-out attempts are captured and the per-run limit bounds the cost', () => {
  const { reporter, lines } = reporterWith({ limit: 2 })
  for (let retry = 0; retry < 4; retry += 1) reporter.onTestEnd(fakeTest, { status: 'timedOut', retry })
  assert.equal(lines.length, 2)
  assert.match(lines[1], /attempt 2, timedOut/)
})

test('disabled outside a CI Windows runner', () => {
  const { reporter, lines } = reporterWith({ enabled: false })
  reporter.onTestEnd(fakeTest, { status: 'failed', retry: 0 })
  assert.deepEqual(lines, [])
})

test('the snapshot never asks for remote endpoints', () => {
  assert.doesNotMatch(SNAPSHOT_SCRIPT, /RemoteAddress|RemotePort/)
})
