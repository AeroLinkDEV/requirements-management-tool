import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { join, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'
import { spawnSync } from 'node:child_process'
import { test } from 'node:test'
import assert from 'node:assert/strict'
import {
  API_DISCOVERY_SCHEMA_VERSION,
  API_DISCOVERY_SOURCE,
  API_OBSERVATIONS_SCHEMA_VERSION,
  API_PROJECT,
  API_REPOSITORY,
  buildApiPackingShadowReport,
  buildCurrentCountPlan,
  normalizeApiDiscovery,
  renderApiPackingShadowMarkdown,
} from '../lib/api-packing-shadow.mjs'

const sha = (character) => character.repeat(40)

function makeDiscovery({ collections = undefined } = {}) {
  const tests = [
    'AeroLink.Api.Tests.AlphaApiTests.Creates_a_record',
    'AeroLink.Api.Tests.AlphaApiTests.Updates_a_record',
    'AeroLink.Api.Tests.AlphaApiTests.Rejects_a_stale_write',
    'AeroLink.Api.Tests.BetaApiTests.Creates_a_document',
    'AeroLink.Api.Tests.BetaApiTests.Rejects_a_foreign_document',
    'AeroLink.Api.Tests.GammaApiTests.Reads_a_projection',
    'AeroLink.Api.Tests.GammaApiTests.Reads_a_paged_projection',
    'AeroLink.Api.Tests.DeltaApiTests.Rejects_an_empty_request',
    'AeroLink.Api.Tests.EpsilonApiTests.Publishes_an_evidence_record',
  ]
  return {
    schemaVersion: API_DISCOVERY_SCHEMA_VERSION,
    repository: API_REPOSITORY,
    source: API_DISCOVERY_SOURCE,
    project: API_PROJECT,
    commitSha: sha('a'),
    treeSha: sha('b'),
    tests,
    ...(collections === undefined ? {} : { collections }),
  }
}

function makeObservations(discovery, { runs = 8, mutate = undefined } = {}) {
  const normalized = normalizeApiDiscovery(discovery)
  const sourceRuns = Array.from({ length: runs }, (_, index) => ({
    runId: index + 1,
    event: 'merge_group',
    workflow: 'Product quality gate',
    conclusion: 'success',
    cohort: 'post-seeder',
    commitSha: `${index + 1}${'c'.repeat(39)}`,
    treeSha: `${index + 1}${'d'.repeat(39)}`,
    discoveryDigest: normalized.digest,
    validatedTree: true,
  }))
  const weights = normalized.classes.map((entry, index) => ({
    className: entry.className,
    durationMs: 100 + ((normalized.classes.length - index) * 300),
    sourceRunIds: sourceRuns.map((run) => run.runId),
  }))
  const observations = {
    schemaVersion: API_OBSERVATIONS_SCHEMA_VERSION,
    repository: API_REPOSITORY,
    provenance: 'validated-queue-telemetry',
    cohort: 'post-seeder',
    sourceRuns,
    weights,
  }
  return mutate ? mutate(observations, normalized) : observations
}

test('count and composite plans preserve exact test coverage, filters and whole classes', () => {
  const discovery = makeDiscovery({ collections: [{ name: 'shared-showcase', classNames: ['AeroLink.Api.Tests.AlphaApiTests', 'AeroLink.Api.Tests.BetaApiTests'], preserveTogether: true }] })
  const report = buildApiPackingShadowReport({ discovery, observations: makeObservations(discovery), shardCount: 3 })
  assert.equal(report.currentPlan.coverage.complete, true)
  assert.equal(report.proposedPlan.coverage.complete, true)
  assert.equal(report.currentPlan.coverage.expected, 9)
  assert.equal(report.proposedPlan.coverage.assigned, 9)
  assert.deepEqual(report.currentPlan.coverage.missing, [])
  assert.deepEqual(report.proposedPlan.coverage.duplicated, [])
  assert.deepEqual(report.proposedPlan.coverage.splitClasses, [])
  for (const plan of [report.currentPlan, report.proposedPlan]) {
    const byClass = new Map()
    for (const shard of plan.shards) for (const className of shard.classes) byClass.set(className, shard.shard)
    assert.equal(byClass.get('AeroLink.Api.Tests.AlphaApiTests'), byClass.get('AeroLink.Api.Tests.BetaApiTests'))
    const all = plan.shards.flatMap((shard) => shard.tests)
    assert.equal(new Set(all).size, 9)
    assert.ok(plan.shards.every((shard) => shard.filter.length > 0))
  }
  const markdown = renderApiPackingShadowMarkdown(report)
  assert.match(markdown, /Class duration sums are rank signals only/)
  assert.match(markdown, /speedup claim: \*\*none\*\*/)
  assert.equal(report.executionSelectorChanged, false)
  assert.equal(report.noSpeedupClaim, true)
})

test('valid eight-run provenance creates a deterministic composite candidate without authority claims', () => {
  const discovery = makeDiscovery()
  const observations = makeObservations(discovery)
  const first = buildApiPackingShadowReport({ discovery, observations, shardCount: 3 })
  const second = buildApiPackingShadowReport({ discovery, observations: JSON.parse(JSON.stringify(observations)), shardCount: 3 })
  assert.deepEqual(first.currentPlan, second.currentPlan)
  assert.deepEqual(first.proposedPlan, second.proposedPlan)
  assert.equal(first.evidence.valid, true)
  assert.equal(first.evidence.matchedRunCount, 8)
  assert.equal(first.evidence.adoptionEligible, true)
  assert.equal(first.proposedPlan.algorithm, 'composite-duration-and-case-shadow')
  assert.equal(first.recommendation, 'candidate-is-shadow-only-and-requires-independent-adoption-review')
  assert.ok(first.comparison.proposedCompositeLoads.every((load) => Number.isFinite(load)))
  assert.equal(first.currentPlan.coverage.complete, true)
  assert.equal(first.proposedPlan.coverage.complete, true)
})

test('missing, stale, mixed-cohort and unverified weights fall back exactly to the count plan', () => {
  const discovery = makeDiscovery()
  const normalized = normalizeApiDiscovery(discovery)
  const baseline = buildCurrentCountPlan(normalized, 3)
  const cases = [
    ['missing weights file', (observations) => { delete observations.weights }],
    ['stale discovery', (observations) => { observations.sourceRuns[0].discoveryDigest = 'e'.repeat(64) }],
    ['mixed cohort', (observations) => { observations.sourceRuns[1].cohort = 'pre-seeder' }],
    ['unverified source tree', (observations) => { observations.sourceRuns[2].validatedTree = false }],
  ]
  for (const [name, mutate] of cases) {
    const report = buildApiPackingShadowReport({ discovery, observations: makeObservations(discovery, { mutate }), shardCount: 3 })
    assert.equal(report.evidence.valid, false, name)
    assert.equal(report.evidence.fallbackUsed, true, name)
    assert.equal(report.evidence.adoptionEligible, false, name)
    assert.deepEqual(report.proposedPlan, baseline, name)
    assert.match(report.recommendation, /retain-current-count-plan/, name)
  }
})

test('a bounded top-class sample leaves the unweighted tail on count-based placement and blocks adoption', () => {
  const discovery = makeDiscovery()
  const observations = makeObservations(discovery)
  observations.weights = observations.weights.slice(0, 2)
  const report = buildApiPackingShadowReport({ discovery, observations, shardCount: 3 })
  assert.equal(report.evidence.valid, true)
  assert.equal(report.evidence.completeWeightCoverage, false)
  assert.equal(report.evidence.missingWeightClasses.length, 3)
  assert.equal(report.evidence.adoptionEligible, false)
  assert.ok(report.evidence.reasons.some((reason) => /count-based placement/.test(reason)))
  assert.equal(report.proposedPlan.coverage.complete, true)
  assert.ok(report.proposedPlan.shards.every((shard) => Number.isInteger(shard.caseCount)))
})

test('fewer than eight valid runs remain a candidate measurement only', () => {
  const discovery = makeDiscovery()
  const report = buildApiPackingShadowReport({ discovery, observations: makeObservations(discovery, { runs: 2 }), shardCount: 3 })
  assert.equal(report.evidence.valid, true)
  assert.equal(report.evidence.adoptionEligible, false)
  assert.equal(report.proposedPlan.algorithm, 'composite-duration-and-case-shadow')
  assert.match(report.evidence.reasons[0], /2 matched source runs/)
  assert.equal(report.noSpeedupClaim, true)
})

test('collection metadata must opt into preserving the entire group', () => {
  const discovery = makeDiscovery({ collections: [{ name: 'shared-showcase', classNames: ['AeroLink.Api.Tests.AlphaApiTests'], preserveTogether: false }] })
  assert.throws(() => normalizeApiDiscovery(discovery), /preserveTogether=true/)
})

test('offline CLI writes bounded JSON and Markdown artifacts', () => {
  const root = dirname(fileURLToPath(import.meta.url))
  const bin = join(root, '..', 'bin', 'report-api-packing-shadow.mjs')
  const directory = mkdtempSync(join(process.env.TEMP ?? process.env.TMP ?? '.', 'aerolink-api-packing-shadow-'))
  const output = join(directory, 'out')
  try {
    const discovery = makeDiscovery()
    const observations = makeObservations(discovery)
    const discoveryPath = join(directory, 'discovery.json')
    const observationsPath = join(directory, 'observations.json')
    writeFileSync(discoveryPath, `${JSON.stringify(discovery)}\n`)
    writeFileSync(observationsPath, `${JSON.stringify(observations)}\n`)
    const result = spawnSync(process.execPath, [bin, discoveryPath, observationsPath, output, '3'], { encoding: 'utf8' })
    assert.equal(result.status, 0, result.stderr)
    const report = JSON.parse(readFileSync(join(output, 'api-packing-shadow.json'), 'utf8'))
    assert.equal(report.schemaVersion, 'aerolink-api-packing-shadow/v1')
    assert.equal(report.proposedPlan.coverage.complete, true)
    assert.match(readFileSync(join(output, 'api-packing-shadow.md'), 'utf8'), /Proposed composite shadow plan/)
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})
