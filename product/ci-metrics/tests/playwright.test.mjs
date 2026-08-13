import { test } from 'node:test'
import assert from 'node:assert/strict'
import { parsePlaywrightJson, specDurations, PlaywrightParseError } from '../lib/playwright.mjs'

const report = {
  stats: { expected: 5, unexpected: 1, flaky: 1, skipped: 1 },
  tests: [
    { title: 'stable test', file: 'alpha.spec.ts', status: 'expected', retries: 0, duration: 1200, results: [{ status: 'passed' }] },
    { title: 'flaky test', file: 'beta.spec.ts', status: 'expected', retries: 1, duration: 3000, results: [{ status: 'failed' }, { status: 'passed' }] },
    { title: 'failed test', file: 'beta.spec.ts', status: 'unexpected', retries: 0, duration: 500, results: [{ status: 'failed' }] },
    { title: 'skipped test', file: 'gamma.spec.ts', status: 'skipped', retries: 0, duration: 0, results: [{ status: 'skipped' }] },
    { title: 'other test', file: 'gamma.spec.ts', status: 'expected', retries: 0, duration: 800, results: [{ status: 'passed' }] },
  ],
}

test('parsePlaywrightJson reads totals, retries, and flaky titles', () => {
  const parsed = parsePlaywrightJson(report)
  assert.deepEqual(parsed.totals, { expected: 5, unexpected: 1, flaky: 1, skipped: 1, executed: 8 })
  assert.equal(parsed.tests.length, 5)
  assert.equal(parsed.tests[1].retries, 1)
  assert.deepEqual(parsed.flakyTitles, ['flaky test'])
})

test('specDurations aggregates by file and orders heaviest first', () => {
  const rows = specDurations(parsePlaywrightJson(report).tests)
  assert.equal(rows[0].name, 'beta.spec.ts')
  assert.equal(rows[0].durationMs, 3500)
  assert.equal(rows[0].tests, 2)
})

test('parsePlaywrightJson rejects malformed, non-object, and oversized input', () => {
  assert.throws(() => parsePlaywrightJson('{not json'), PlaywrightParseError)
  assert.throws(() => parsePlaywrightJson(7), PlaywrightParseError)
  assert.throws(() => parsePlaywrightJson('x'.repeat(51 * 1024 * 1024)), PlaywrightParseError)
  assert.throws(() => parsePlaywrightJson({ stats: { expected: 'bad' } }), PlaywrightParseError)
})

test('an empty Playwright report is a valid empty set, not an error', () => {
  const parsed = parsePlaywrightJson({ stats: { expected: 0, unexpected: 0, flaky: 0, skipped: 0 }, tests: [] })
  assert.equal(parsed.totals.executed, 0)
  assert.deepEqual(parsed.flakyTitles, [])
})
