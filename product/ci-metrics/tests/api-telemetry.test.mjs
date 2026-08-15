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
    { className: 'AeroLink.Api.Tests.PureUnitApiTests', name: 'AeroLink.Api.Tests.PureUnitApiTests.A_rule_test_without_a_factory', durationMs: 12, outcome: 'Passed' },
  ]
  const report = aggregateApiTelemetry({ factoryRecords, trxTests })
  assert.equal(report.totals.trxTests, 3)
  assert.equal(report.totals.tests, 2)
  assert.equal(report.totals.factories, 2)
  assert.equal(report.totals.ambiguousTheoryRows, 0)
  assert.equal(report.totals.unmatchedMethods, 0)
  assert.equal(report.totals.trxWithoutFactoryTelemetry, 1)
  assert.equal(report.trxWithoutFactoryTelemetry[0].method, 'AeroLink.Api.Tests.PureUnitApiTests.A_rule_test_without_a_factory')
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
  const report = aggregateApiTelemetry({
    factoryRecords,
    trxTests: [{ className: 'AeroLink.Api.Tests.ExampleApiTests', name: 'A_test_uses_two_factories', durationMs: 2000, outcome: 'Passed' }],
  })
  assert.equal(report.totals.tests, 1)
  assert.equal(report.multipleFactoryTests.length, 1)
  assert.equal(report.multipleFactoryTests[0].factoryCount, 2)
})

test('parameterized theory rows are reported as ambiguous, never merged into one test', () => {
  const factoryRecords = [
    { type: 'factory', factoryId: 1, class: 'TheoryApiTests', method: 'A_theory_case', phase: 'host', constructionMs: 10, ms: 500 },
    { type: 'factory', factoryId: 2, class: 'TheoryApiTests', method: 'A_theory_case', phase: 'host', constructionMs: 10, ms: 600 },
  ]
  const trxTests = [
    { className: 'AeroLink.Api.Tests.TheoryApiTests', name: 'AeroLink.Api.Tests.TheoryApiTests.A_theory_case(x: "one", ownerId: "owner.one")', durationMs: 800, outcome: 'Passed' },
    { className: 'AeroLink.Api.Tests.TheoryApiTests', name: 'AeroLink.Api.Tests.TheoryApiTests.A_theory_case(x: "two", ownerId: "owner.two")', durationMs: 900, outcome: 'Passed' },
  ]
  const report = aggregateApiTelemetry({ factoryRecords, trxTests })
  assert.equal(report.totals.trxTests, 2)
  assert.equal(report.totals.tests, 0)
  assert.equal(report.totals.ambiguousTheoryRows, 2)
  assert.equal(report.ambiguousTheoryRows.length, 1)
  assert.equal(report.totals.unmatchedMethods, 0)
  assert.equal(report.ambiguousTheoryRows[0].trxRows, 2)
  assert.equal(report.ambiguousTheoryRows[0].factories, 2)
  assert.equal(report.multipleFactoryTests.length, 0)
  assert.equal(report.classes[0].theoryRows, 2)
})

test('connection-open sub-phase is aggregated separately and never added to startup', () => {
  const factoryRecords = [
    { type: 'factory', factoryId: 1, class: 'ExampleApiTests', method: 'A_test_creates_a_project', phase: 'host', constructionMs: 100, ms: 2000, schemaVersion: 'aerolink-api-telemetry/v2' },
    { type: 'factory', factoryId: 1, class: 'ExampleApiTests', method: 'A_test_creates_a_project', phase: 'connectionOpen', constructionMs: 0, ms: 350, schemaVersion: 'aerolink-api-telemetry/v2' },
    { type: 'factory', factoryId: 1, class: 'ExampleApiTests', method: 'A_test_creates_a_project', phase: 'dispose', constructionMs: 100, ms: 300, schemaVersion: 'aerolink-api-telemetry/v2' },
  ]
  const trxTests = [{ className: 'AeroLink.Api.Tests.ExampleApiTests', name: 'A_test_creates_a_project', durationMs: 5000, outcome: 'Passed' }]
  const report = aggregateApiTelemetry({ factoryRecords, trxTests })
  assert.equal(report.totals.summedStartupMs, 2400)
  assert.equal(report.totals.summedConnectionOpenMs, 350)
  assert.equal(report.slowestStartupTests[0].connectionOpenMs, 350)
})

test('construction, host, and disposal intervals are added exactly once each', () => {
  // Regression for the round-2 finding: constructionMs is captured BEFORE the host build, so the
  // aggregator must not re-add the dispose record's repeated constructionMs, and a constructionMs that
  // (incorrectly) already contains hostMs must not be double counted.
  const factoryRecords = [
    { type: 'factory', factoryId: 1, class: 'ExampleApiTests', method: 'A_test_creates_a_project', phase: 'host', constructionMs: 100, ms: 2000, schemaVersion: 'aerolink-api-telemetry/v2' },
    // The dispose record repeats the pre-host construction latency for provenance; it must not be added
    // again. A huge value here would reveal any code that summed constructionMs from dispose.
    { type: 'factory', factoryId: 1, class: 'ExampleApiTests', method: 'A_test_creates_a_project', phase: 'dispose', constructionMs: 999999, ms: 300, schemaVersion: 'aerolink-api-telemetry/v2' },
  ]
  const trxTests = [{ className: 'AeroLink.Api.Tests.ExampleApiTests', name: 'A_test_creates_a_project', durationMs: 5000, outcome: 'Passed' }]
  const report = aggregateApiTelemetry({ factoryRecords, trxTests })
  assert.equal(report.totals.summedStartupMs, 2400)
  assert.equal(report.slowestStartupTests[0].startupMs, 2400)
  assert.equal(report.slowestStartupTests[0].bodyMs, 2600)
})

test('unknown schema versions are rejected and the report is versioned v2', () => {
  const parsed = parseTelemetryLines(line({ class: 'ExampleApiTests', schemaVersion: 'aerolink-api-telemetry/v99' }))
  assert.equal(parsed.records.length, 0)
  assert.ok(parsed.malformed.some((entry) => entry.includes('schemaVersion')))
  const report = aggregateApiTelemetry({ factoryRecords: [], trxTests: [] })
  assert.equal(report.schemaVersion, 'aerolink-api-telemetry/v2')
})

test('fixture and helper factories with no TRX row are reported as unmatched, not attributed', () => {
  const factoryRecords = [
    { type: 'factory', factoryId: 1, class: 'ShowcaseApiFixture', method: 'CreateFactory', phase: 'host', constructionMs: 10, ms: 500 },
    { type: 'factory', factoryId: 2, class: 'ExampleApiTests', method: 'A_test_creates_a_project', phase: 'host', constructionMs: 10, ms: 600 },
  ]
  const trxTests = [{ className: 'AeroLink.Api.Tests.ExampleApiTests', name: 'A_test_creates_a_project', durationMs: 2000, outcome: 'Passed' }]
  const report = aggregateApiTelemetry({ factoryRecords, trxTests })
  assert.equal(report.totals.trxTests, 1)
  assert.equal(report.totals.tests, 1)
  assert.equal(report.totals.unmatchedMethods, 1)
  assert.equal(report.unmatchedMethods[0].className, 'ShowcaseApiFixture')
  assert.equal(report.unmatchedMethods[0].method, 'CreateFactory')
  assert.equal(report.unmatchedMethods[0].factories, 1)
  assert.equal(report.unmatchedMethods[0].startupMs, 510)
  assert.equal(report.totals.summedStartupMs, 610)
  assert.equal(report.totals.summedFixtureStartupMs, 510)
  assert.equal(report.totals.summedStartupWithFixturesMs, 1120)
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
  assert.match(markdown, /TRX tests: 0/)
  assert.ok(Buffer.byteLength(markdown, 'utf8') < 128 * 1024)
})
