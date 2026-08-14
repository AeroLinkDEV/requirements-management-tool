import { test } from 'node:test'
import assert from 'node:assert/strict'
import { parseTelemetryLines, aggregateApiTelemetry, renderApiTelemetryMarkdown } from '../lib/api-telemetry.mjs'

const line = (overrides = {}) => JSON.stringify({
  type: 'factory',
  factoryId: 1,
  class: 'ExampleApiTests',
  method: 'A_test_creates_a_project',
  phase: 'host',
  constructionMs: 120,
  ms: 2500,
  ...overrides,
})

test('parseTelemetryLines accepts valid lines and rejects malformed ones', () => {
  const text = [
    line({ factoryId: 1, phase: 'host' }),
    line({ factoryId: 1, phase: 'dispose', ms: 90 }),
    'not json',
    line({ factoryId: 'x' }),
    line({ phase: 'mystery' }),
    line({ ms: -1 }),
  ].join('\n')
  const parsed = parseTelemetryLines(text)
  assert.equal(parsed.records.length, 2)
  assert.equal(parsed.malformed.length, 4)
  assert.equal(parsed.truncated, false)
})

test('aggregateApiTelemetry computes startup floor and per-class summaries', () => {
  const factoryRecords = [
    { type: 'factory', factoryId: 1, class: 'ExampleApiTests', method: 'A_test_creates_a_project', phase: 'host', constructionMs: 100, ms: 2000 },
    { type: 'factory', factoryId: 1, class: 'ExampleApiTests', method: 'A_test_creates_a_project', phase: 'dispose', constructionMs: 100, ms: 300 },
    { type: 'factory', factoryId: 2, class: 'ExampleApiTests', method: 'A_test_deletes_a_project', phase: 'host', constructionMs: 90, ms: 1800 },
    { type: 'factory', factoryId: 2, class: 'ExampleApiTests', method: 'A_test_deletes_a_project', phase: 'dispose', constructionMs: 90, ms: 200 },
  ]
  const trxTests = [
    { className: 'AeroLink.Api.Tests.ExampleApiTests', name: 'A_test_creates_a_project', durationMs: 5000, outcome: 'Passed' },
    { className: 'AeroLink.Api.Tests.ExampleApiTests', name: 'A_test_deletes_a_project', durationMs: 4000, outcome: 'Passed' },
  ]
  const report = aggregateApiTelemetry({ factoryRecords, trxTests })
  assert.equal(report.totals.tests, 2)
  assert.equal(report.totals.factories, 2)
  assert.equal(report.totals.summedWallMs, 9000)
  assert.equal(report.totals.summedStartupMs, 4490)
  assert.equal(report.classes[0].className, 'ExampleApiTests')
  assert.equal(report.classes[0].startupFraction, 0.5)
  assert.equal(report.slowestStartupTests[0].startupMs, 2400)
  assert.equal(report.slowestStartupTests[0].bodyMs, 2600)
})

test('aggregateApiTelemetry detects multiple-factory tests', () => {
  const factoryRecords = [
    { type: 'factory', factoryId: 1, class: 'ExampleApiTests', method: 'A_test_uses_two_factories', phase: 'host', constructionMs: 10, ms: 500 },
    { type: 'factory', factoryId: 2, class: 'ExampleApiTests', method: 'A_test_uses_two_factories', phase: 'host', constructionMs: 10, ms: 600 },
  ]
  const report = aggregateApiTelemetry({ factoryRecords })
  assert.equal(report.totals.tests, 1)
  assert.equal(report.multipleFactoryTests.length, 1)
  assert.equal(report.multipleFactoryTests[0].factoryCount, 2)
})

test('credential-shaped telemetry is rejected and the markdown is bounded', () => {
  const parsed = parseTelemetryLines(line({ class: 'Password=hunter2' }))
  assert.equal(parsed.records.length, 0)
  assert.ok(parsed.malformed.some((reason) => /Credential-shaped/.test(reason)))
  const report = aggregateApiTelemetry({ factoryRecords: [
    { type: 'factory', factoryId: 1, class: 'ExampleApiTests', method: 'A_test', phase: 'host', constructionMs: 10, ms: 500 },
  ] })
  const markdown = renderApiTelemetryMarkdown(report)
  assert.match(markdown, /API startup-floor telemetry/)
  assert.ok(Buffer.byteLength(markdown, 'utf8') < 128 * 1024)
})
