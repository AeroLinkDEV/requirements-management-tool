import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { parsePlaywrightJson, specDurations, PlaywrightParseError } from '../lib/playwright.mjs'

test('the real Playwright suite hierarchy is parsed with exact totals, durations, and flaky titles', () => {
  const fixture = JSON.parse(readFileSync(new URL('./fixtures/playwright-real.json', import.meta.url), 'utf8'))
  const parsed = parsePlaywrightJson(fixture)
  assert.deepEqual(parsed.totals, { expected: 2, unexpected: 0, flaky: 1, skipped: 1, planned: 4, executed: 3, passed: 3 })
  assert.equal(parsed.tests.length, 4)

  const flaky = parsed.tests.find((test) => test.title.includes('assessment deep link'))
  assert.equal(flaky.status, 'passed')
  assert.equal(flaky.retries, 1)
  assert.equal(flaky.durationMs, 5700)
  assert.equal(flaky.flaky, true)
  assert.deepEqual(parsed.flakyTitles, ['an assessment deep link explains impact'])

  const password = parsed.tests.find((test) => test.title === 'Password visibility test')
  assert.equal(password.status, 'passed')
  assert.equal(password.durationMs, 1100)
  assert.equal(password.flaky, false)

  const skipped = parsed.tests.find((test) => test.title.includes('token refresh'))
  assert.equal(skipped.status, 'skipped')

  const rows = specDurations(parsed.tests)
  assert.deepEqual(rows.map((row) => row.name), [
    'tests/downstream-assessments.spec.ts',
    'tests/form-semantics.spec.ts',
    'tests/other.spec.ts',
    'tests/auth.spec.ts',
  ])
  assert.equal(rows[0].durationMs, 5700)
})

test('a stats-only report yields totals and reports missing detail rather than inventing rows', () => {
  const parsed = parsePlaywrightJson({ stats: { expected: 10, unexpected: 1, flaky: 1, skipped: 2 }, errors: [] })
  assert.deepEqual(parsed.totals, { expected: 10, unexpected: 1, flaky: 1, skipped: 2, planned: 14, executed: 12, passed: 11 })
  assert.equal(parsed.tests.length, 0)
  assert.match(parsed.detailMissing, /no suites hierarchy/)
})

test('parsePlaywrightJson rejects malformed, non-object, and oversized input', () => {
  assert.throws(() => parsePlaywrightJson('{not json'), PlaywrightParseError)
  assert.throws(() => parsePlaywrightJson(7), PlaywrightParseError)
  assert.throws(() => parsePlaywrightJson('x'.repeat(51 * 1024 * 1024)), PlaywrightParseError)
  assert.throws(() => parsePlaywrightJson({ stats: { expected: 'bad' } }), PlaywrightParseError)
  assert.throws(() => parsePlaywrightJson({ stats: { expected: -1, unexpected: 0, flaky: 0, skipped: 0 } }), /non-negative/)
})

test('an empty Playwright report is a valid empty set, not an error', () => {
  const parsed = parsePlaywrightJson({ stats: { expected: 0, unexpected: 0, flaky: 0, skipped: 0 }, suites: [] })
  assert.equal(parsed.totals.planned, 0)
  assert.equal(parsed.totals.executed, 0)
  assert.deepEqual(parsed.flakyTitles, [])
  assert.equal(parsed.detailMissing, null)
})

test('timedOut and interrupted results count as failures for flaky classification', () => {
  const report = {
    stats: { expected: 0, unexpected: 0, flaky: 1, skipped: 0 },
    suites: [{
      title: 's',
      specs: [{
        title: 'spec',
        file: 'x.spec.ts',
        tests: [{
          title: 'timed out then passed',
          results: [{ status: 'timedOut', duration: 100 }, { status: 'passed', duration: 200 }],
          retries: 1,
        }],
      }],
      suites: [],
    }],
  }
  const parsed = parsePlaywrightJson(report)
  assert.equal(parsed.tests[0].flaky, true)
  assert.equal(parsed.tests[0].retries, 1)
})

test('stats that contradict the test rows are rejected', () => {
  const report = {
    stats: { expected: 2, unexpected: 0, flaky: 0, skipped: 0 },
    suites: [{ title: 's', specs: [{ title: 'spec', file: 'x.spec.ts', tests: [{ title: 'one', results: [{ status: 'passed', duration: 100 }], retries: 0 }] }], suites: [] }],
  }
  assert.throws(() => parsePlaywrightJson(report), /inconsistent with the test rows/)
})

test('missing per-result durations make the test and its spec duration unknown, never zero', () => {
  const report = {
    stats: { expected: 2, unexpected: 0, flaky: 0, skipped: 0 },
    suites: [{ title: 's', specs: [
      { title: 'with duration', file: 'a.spec.ts', tests: [{ title: 'with duration', results: [{ status: 'passed', duration: 500 }], retries: 0 }] },
      { title: 'without duration', file: 'b.spec.ts', tests: [{ title: 'without duration', results: [{ status: 'passed' }], retries: 0 }] },
    ], suites: [] }],
  }
  const parsed = parsePlaywrightJson(report)
  assert.equal(parsed.tests[0].durationMs, 500)
  assert.equal(parsed.tests[1].durationMs, null)
  const rows = specDurations(parsed.tests)
  assert.equal(rows.find((row) => row.name === 'a.spec.ts').durationMs, 500)
  assert.equal(rows.find((row) => row.name === 'b.spec.ts').durationMs, null)
})

test('a stats.flaky count that does not match the row-derived flaky titles is rejected', () => {
  const report = {
    stats: { expected: 2, unexpected: 0, flaky: 1, skipped: 0 },
    suites: [{ title: 's', specs: [
      { title: 'one', file: 'a.spec.ts', tests: [{ title: 'one', results: [{ status: 'passed', duration: 100 }], retries: 0 }] },
      { title: 'two', file: 'a.spec.ts', tests: [{ title: 'two', results: [{ status: 'passed', duration: 100 }], retries: 0 }] },
    ], suites: [] }],
  }
  assert.throws(() => parsePlaywrightJson(report), /flakyRows=0/)
})
