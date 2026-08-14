import { test } from 'node:test'
import assert from 'node:assert/strict'
import { parseJunitXml, fileDurations, JunitParseError } from '../lib/junit.mjs'

test('parseJunitXml reads totals, outcomes, and per-file durations from a Node 24 fixture', () => {
  const xml = `<?xml version="1.0" encoding="utf-8"?>
<testsuites>
  <testcase name="passes" time="0.5" classname="test" file="C:/repo/product/ci-metrics/tests/a.test.mjs"/>
  <testcase name="skipped one" time="0" classname="test" file="C:/repo/product/ci-metrics/tests/a.test.mjs"><skipped/></testcase>
  <testcase name="fails" time="1.25" classname="test" file="C:/repo/product/ci-metrics/tests/b.test.mjs"><failure>boom</failure></testcase>
  <!-- tests 3 -->
  <!-- suites 0 -->
  <!-- pass 1 -->
  <!-- fail 1 -->
</testsuites>`
  const parsed = parseJunitXml(xml)
  assert.deepEqual(parsed.totals, { total: 3, executed: 2, passed: 1, failed: 1, skipped: 1 })
  assert.equal(parsed.tests.length, 3)
  const durations = fileDurations(parsed.tests)
  assert.equal(durations[0].name, 'C:/repo/product/ci-metrics/tests/b.test.mjs')
  assert.equal(durations[0].durationMs, 1250)
  assert.equal(durations[1].durationMs, 500)
})

test('parseJunitXml rejects inputs that are not usable test results', () => {
  assert.throws(() => parseJunitXml('not xml'), JunitParseError)
  assert.throws(() => parseJunitXml('<testsuites></testsuites>'), /no testcase elements/)
  assert.throws(() => parseJunitXml(42), JunitParseError)
  const huge = `<testsuites>${'x'.repeat(11 * 1024 * 1024)}</testsuites>`
  assert.throws(() => parseJunitXml(huge), /bounded parse limit/)
})

test('parseJunitXml refuses a testcase count that contradicts the comment totals', () => {
  const xml = `<?xml version="1.0" encoding="utf-8"?>
<testsuites>
  <testcase name="only" time="0.1" classname="test" file="a.test.mjs"/>
  <!-- tests 7 -->
</testsuites>`
  assert.throws(() => parseJunitXml(xml), /does not match the reported comment totals/)
})

test('cancelled testcases count as failures, never as passes', () => {
  const xml = `<?xml version="1.0" encoding="utf-8"?>
<testsuites>
  <testcase name="cancelled" time="0.2" classname="test" file="a.test.mjs"><cancelled/></testcase>
  <testcase name="passes" time="0.3" classname="test" file="a.test.mjs"/>
</testsuites>`
  const parsed = parseJunitXml(xml)
  assert.deepEqual(parsed.totals, { total: 2, executed: 2, passed: 1, failed: 1, skipped: 0 })
})

test('missing durations make the file duration unknown, never zero', () => {
  const xml = `<?xml version="1.0" encoding="utf-8"?>
<testsuites>
  <testcase name="no time" classname="test" file="a.test.mjs"/>
</testsuites>`
  const parsed = parseJunitXml(xml)
  assert.equal(fileDurations(parsed.tests)[0].durationMs, null)
})
