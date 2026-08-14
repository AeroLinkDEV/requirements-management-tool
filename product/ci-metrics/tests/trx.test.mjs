import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { parseTrx, parseDuration, classDurations, TrxParseError } from '../lib/trx.mjs'

const sample = `<?xml version="1.0" encoding="utf-8"?>
<TestRun id="run-1">
  <Results>
    <UnitTestResult testId="a" testName="T_One" outcome="Passed" duration="00:00:01.2500000" />
    <UnitTestResult testId="b" testName="T_Two" outcome="Failed" duration="00:00:02.5000000" />
    <UnitTestResult testId="c" testName="T_Three" outcome="Passed" duration="00:00:00.7500000" />
  </Results>
  <TestDefinitions>
    <UnitTest id="a" name="T_One">
      <TestMethod className="AeroLink.Api.Tests.AlphaTests" name="T_One" />
    </UnitTest>
    <UnitTest id="b" name="T_Two">
      <TestMethod className="AeroLink.Api.Tests.BetaTests" name="T_Two" />
    </UnitTest>
    <UnitTest id="c" name="T_Three">
      <TestMethod className="AeroLink.Api.Tests.AlphaTests" name="T_Three" />
    </UnitTest>
  </TestDefinitions>
  <ResultSummary>
    <Counters total="3" executed="3" passed="2" failed="1" error="0" timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0" notRunnable="0" notExecuted="0" disconnected="0" warning="0" completed="0" inProgress="0" pending="0" />
  </ResultSummary>
</TestRun>`

test('parseTrx reads totals, per-test outcomes, and class mapping', () => {
  const parsed = parseTrx(sample)
  assert.deepEqual(parsed.totals, { total: 3, executed: 3, passed: 2, failed: 1, skipped: 0 })
  assert.equal(parsed.tests.length, 3)
  assert.equal(parsed.tests[0].className, 'AeroLink.Api.Tests.AlphaTests')
  assert.equal(parsed.tests[1].outcome, 'Failed')
  assert.equal(parsed.tests[0].durationMs, 1250)
  assert.equal(parsed.tests[2].durationMs, 750)
})

test('parseDuration handles days and fractions', () => {
  assert.equal(parseDuration('00:00:01.2500000'), 1250)
  assert.equal(parseDuration('1.00:00:01'), 86401000)
  assert.equal(parseDuration('00:01:02'), 62000)
  assert.equal(parseDuration('not-a-duration'), null)
})

test('classDurations aggregates by class and orders heaviest first', () => {
  const { tests } = parseTrx(sample)
  const rows = classDurations(tests)
  assert.deepEqual(rows.map((row) => row.name), ['AeroLink.Api.Tests.BetaTests', 'AeroLink.Api.Tests.AlphaTests'])
  assert.equal(rows[0].durationMs, 2500)
  assert.equal(rows[0].tests, 1)
  assert.equal(rows[1].tests, 2)
})

test('parseTrx rejects a file without counters', () => {
  assert.throws(() => parseTrx('<TestRun></TestRun>'), TrxParseError)
})

test('parseTrx rejects non-text and oversized input', () => {
  assert.throws(() => parseTrx(42), TrxParseError)
  assert.throws(() => parseTrx('x'.repeat(51 * 1024 * 1024)), TrxParseError)
})

test('a representative failed TRX fixture yields exact totals and per-test outcomes', () => {
  const fixture = readFileSync(new URL('./fixtures/trx-failure.trx', import.meta.url), 'utf8')
  const parsed = parseTrx(fixture)
  assert.deepEqual(parsed.totals, { total: 4, executed: 4, passed: 2, failed: 2, skipped: 0 })
  assert.equal(parsed.tests.length, 4)
  const byName = new Map(parsed.tests.map((entry) => [entry.name, entry]))
  assert.equal(byName.get('BetaTests.Failing_one').outcome, 'Failed')
  assert.equal(byName.get('BetaTests.Failing_one').durationMs, 1100)
  assert.equal(byName.get('AlphaTests.Passing_one').outcome, 'Passed')
  const rows = classDurations(parsed.tests)
  assert.deepEqual(rows.map((row) => row.name), ['AeroLink.Api.Tests.BetaTests', 'AeroLink.Api.Tests.AlphaTests'])
  assert.equal(rows[0].durationMs, 2000)
})
