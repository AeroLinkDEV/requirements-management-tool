import { test } from 'node:test'
import assert from 'node:assert/strict'
import { mkdirSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { tmpdir } from 'node:os'
import { buildDurationCandidatePlan } from '../lib/api-packing-shadow.mjs'
import { runApiPackingBenchmark } from '../lib/api-benchmark.mjs'
import { normalizeApiDiscovery } from '../lib/api-packing-shadow.mjs'

const commitSha = 'd'.repeat(40)
const treeSha = 'e'.repeat(40)
const discovery = normalizeApiDiscovery({
  schemaVersion: 'aerolink-api-discovery/v1',
  repository: 'AeroLinkDEV/requirements-management-tool',
  source: 'dotnet-list-tests',
  project: 'product/tests/AeroLink.Api.Tests/AeroLink.Api.Tests.csproj',
  commitSha,
  treeSha,
  tests: [
    'AeroLink.Api.Tests.AlphaTests.One',
    'AeroLink.Api.Tests.BetaTests.Two',
    'AeroLink.Api.Tests.GammaTests.Three',
    'AeroLink.Api.Tests.DeltaTests.Four',
    'AeroLink.Api.Tests.EpsilonTests.Five',
    'AeroLink.Api.Tests.ZetaTests.Six',
  ],
})

function trxForNames(names) {
  const definitions = names.map((name, index) => {
    const dot = name.lastIndexOf('.')
    return { id: `test-${index}`, name, className: name.slice(0, dot), method: name.slice(dot + 1) }
  })
  return `<TestRun><ResultSummary><Counters total="${names.length}" executed="${names.length}" passed="${names.length}" failed="0" notExecuted="0" /></ResultSummary><TestDefinitions>${definitions.map((entry) => `<UnitTest id="${entry.id}"><TestMethod className="${entry.className}" name="${entry.method}" /></UnitTest>`).join('')}</TestDefinitions><Results>${definitions.map((entry) => `<UnitTestResult testId="${entry.id}" testName="${entry.name}" outcome="Passed" duration="00:00:00.001" />`).join('')}</Results></TestRun>`
}

test('duration candidate shares the current coverage and preserves unknown classes', () => {
  const plan = buildDurationCandidatePlan(discovery, { 'AeroLink.Api.Tests.AlphaTests': 10_000, 'AeroLink.Api.Tests.BetaTests': null, 'AeroLink.Api.Tests.GammaTests': 4_000 }, 3)
  assert.equal(plan.coverage.complete, true)
  assert.deepEqual(plan.observationCoverage.missingDurationClasses.sort(), [
    'AeroLink.Api.Tests.BetaTests', 'AeroLink.Api.Tests.DeltaTests', 'AeroLink.Api.Tests.EpsilonTests', 'AeroLink.Api.Tests.ZetaTests',
  ])
  assert.equal(plan.shards.reduce((sum, shard) => sum + shard.tests.length, 0), discovery.tests.length)
})

test('local benchmark records both cohorts and exact TRX reconciliation without running on the gate', async () => {
  const sourceDir = process.cwd()
  const outputDir = join(tmpdir(), `aerolink-api-benchmark-test-${Date.now()}-${Math.random().toString(16).slice(2)}`)
  const observations = {
    sourceMetadata: { run: { id: 9001, treeSha } },
    inventory: { digest: discovery.digest },
    shards: [{ classDurations: discovery.classes.map((entry, index) => ({ className: entry.className, durationMs: (index + 1) * 1000 })) }],
  }
  const runner = async ({ command, args }) => {
    if (command === 'dotnet' && args[0] === 'build') return { exitCode: 0, signal: null, timedOut: false, durationMs: 10, stdout: '', stderr: '' }
    const filter = args[args.indexOf('--filter') + 1]
    const resultDir = args[args.indexOf('--results-directory') + 1]
    const classNames = filter.split('|').map((part) => part.replace('FullyQualifiedName~', '').replace(/\.$/, ''))
    const names = discovery.tests.filter((name) => classNames.some((className) => name.startsWith(`${className}.`)))
    mkdirSync(resultDir, { recursive: true })
    writeFileSync(join(resultDir, 'shard.trx'), trxForNames(names), 'utf8')
    return { exitCode: 0, signal: null, timedOut: false, durationMs: 20, stdout: '', stderr: '' }
  }
  const report = await runApiPackingBenchmark({
    sourceDir,
    outputDir,
    discovery,
    observations,
    platform: 'win32',
    git: () => ({ commitSha, treeSha, dirty: false }),
    runner,
  })
  assert.equal(report.configuration.ordinaryCiChanged, false)
  assert.equal(report.configuration.protectedGateEligible, false)
  assert.equal(report.configuration.durationObservation.provenance, 'caller-supplied-unverified-run')
  assert.equal(report.configuration.durationObservation.metadataAuthenticated, false)
  assert.equal(report.source.cleanBefore, true)
  assert.equal(report.source.cleanAfter, true)
  assert.equal(report.cohorts.current.success, true)
  assert.equal(report.cohorts.proposed.success, true)
  assert.equal(report.cohorts.current.shards.every((shard) => shard.reconciliation.ok), true)
  assert.equal(report.cohorts.proposed.shards.every((shard) => shard.reconciliation.ok), true)
  assert.ok(['promising-but-not-proven', 'no-measured-improvement', 'regression'].includes(report.comparison.verdict))
})

test('benchmark refuses non-Windows execution', async () => {
  await assert.rejects(() => runApiPackingBenchmark({
    sourceDir: process.cwd(), outputDir: join(tmpdir(), 'aerolink-benchmark-refused'), discovery, observations: { shards: [] }, platform: 'linux',
}), /LOCAL WINDOWS only/)
})

test('benchmark refuses an injected dirty source identity before execution', async () => {
  await assert.rejects(() => runApiPackingBenchmark({
    sourceDir: process.cwd(), outputDir: join(tmpdir(), 'aerolink-benchmark-dirty'), discovery,
    observations: { shards: [] }, platform: 'win32', git: () => ({ commitSha, treeSha, dirty: true }),
  }), /Source tree is dirty/)
})
