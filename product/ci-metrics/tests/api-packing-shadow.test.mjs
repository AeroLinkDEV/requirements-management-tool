import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
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
  parseVstestList,
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
    provenance: 'offline-shadow-claim',
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
  const assignments = (plan) => new Map(plan.shards.flatMap((shard) => shard.classes.map((className) => [className, shard.shard])))
  const currentAssignments = assignments(report.currentPlan)
  const proposedAssignments = assignments(report.proposedPlan)
  assert.notEqual(currentAssignments.get('AeroLink.Api.Tests.AlphaApiTests'), currentAssignments.get('AeroLink.Api.Tests.BetaApiTests'))
  assert.equal(proposedAssignments.get('AeroLink.Api.Tests.AlphaApiTests'), proposedAssignments.get('AeroLink.Api.Tests.BetaApiTests'))
  for (const plan of [report.currentPlan, report.proposedPlan]) {
    const all = plan.shards.flatMap((shard) => shard.tests)
    assert.equal(new Set(all).size, 9)
    assert.ok(plan.shards.every((shard) => shard.filter.length > 0))
  }
  assert.equal(report.currentPlan.grouping.collectionConstraintsApplied, false)
  assert.equal(report.proposedPlan.grouping.collectionGroupsUnverified, true)
  assert.deepEqual(report.comparison.collectionGrouping[0].currentShards.length, 2)
  assert.deepEqual(report.comparison.collectionGrouping[0].proposedShards.length, 1)
  assert.equal(report.comparison.collectionGrouping[0].changed, true)
  const markdown = renderApiPackingShadowMarkdown(report)
  assert.match(markdown, /Class duration sums are rank signals only/)
  assert.match(markdown, /hypothetical claims/)
  assert.match(markdown, /speedup claim: \*\*none\*\*/)
  assert.equal(report.executionSelectorChanged, false)
  assert.equal(report.noSpeedupClaim, true)
})

test('eight offline run claims create a deterministic shadow candidate without authority claims', () => {
  const discovery = makeDiscovery()
  const observations = makeObservations(discovery)
  const first = buildApiPackingShadowReport({ discovery, observations, shardCount: 3 })
  const second = buildApiPackingShadowReport({ discovery, observations: JSON.parse(JSON.stringify(observations)), shardCount: 3 })
  assert.deepEqual(first.currentPlan, second.currentPlan)
  assert.deepEqual(first.proposedPlan, second.proposedPlan)
  assert.equal(first.evidence.structurallyCompatible, true)
  assert.equal(first.evidence.matchedRunCount, 8)
  assert.equal(first.evidence.minimumEvidenceCountMet, true)
  assert.equal(first.evidence.adoptionEligible, false)
  assert.equal(first.discovery.authenticity, 'unverified-offline-claim')
  assert.equal(first.discovery.freshDiscoveryClaim, false)
  assert.equal(first.evidence.authenticity, 'unverified-offline-claims')
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
    assert.equal(report.evidence.structurallyCompatible, false, name)
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
  assert.equal(report.evidence.structurallyCompatible, true)
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
  assert.equal(report.evidence.structurallyCompatible, true)
  assert.equal(report.evidence.minimumEvidenceCountMet, false)
  assert.equal(report.evidence.adoptionEligible, false)
  assert.equal(report.proposedPlan.algorithm, 'composite-duration-and-case-shadow')
  assert.match(report.evidence.reasons[0], /2 matched source runs/)
  assert.match(report.evidence.reasons[0], /minimum evidence count/)
  assert.equal(report.noSpeedupClaim, true)
})

test('collection metadata must opt into preserving the entire group', () => {
  const discovery = makeDiscovery({ collections: [{ name: 'shared-showcase', classNames: ['AeroLink.Api.Tests.AlphaApiTests'], preserveTogether: false }] })
  assert.throws(() => normalizeApiDiscovery(discovery), /preserveTogether=true/)
})

test('VSTest fixture parsing preserves parameterized and custom-Fact display names', () => {
  const fixture = readFileSync(join(dirname(fileURLToPath(import.meta.url)), 'fixtures', 'api-vstest-list.txt'), 'utf8')
  const tests = parseVstestList(fixture)
  assert.equal(tests.length, 7)
  assert.ok(tests.some((name) => name.includes('(value: "one")')))
  assert.ok(tests.some((name) => name.includes('Custom_fact_name_with a display label')))
  assert.throws(() => parseVstestList('The following Tests are available:\r\n    NotAeroLink.Test'), /no AeroLink API tests/)
  assert.throws(() => normalizeApiDiscovery({ ...makeDiscovery(), tests: parseVstestList('    AeroLinkMalformed') }), /Cannot derive a test class/)
})

test('current count plan preserves the synthetic edge-case fixture assignments', () => {
  const testDirectory = join(dirname(fileURLToPath(import.meta.url)), 'fixtures')
  const fixture = JSON.parse(readFileSync(join(testDirectory, 'api-packing-count-parity.json'), 'utf8'))
  const tests = parseVstestList(readFileSync(join(testDirectory, fixture.vstestList), 'utf8'))
  const discovery = {
    schemaVersion: API_DISCOVERY_SCHEMA_VERSION,
    repository: API_REPOSITORY,
    source: API_DISCOVERY_SOURCE,
    project: API_PROJECT,
    commitSha: sha('a'),
    treeSha: sha('b'),
    tests,
    collections: fixture.collections,
  }
  const plan = buildCurrentCountPlan(discovery, fixture.shardCount)
  const actualAssignments = Object.fromEntries(plan.shards.flatMap((shard) => shard.classes.map((className) => [className, shard.shard])))
  assert.deepEqual(actualAssignments, fixture.expectedClassShards)
  assert.equal(plan.grouping.collectionConstraintsApplied, false)
  const workflow = readFileSync(join(dirname(fileURLToPath(import.meta.url)), '../../../.github/workflows/ci.yml'), 'utf8')
  const start = workflow.indexOf('- name: Run this shard of the API test suite')
  const end = workflow.indexOf('- name: Upload API test diagnostics', start)
  assert.ok(start >= 0 && end > start)
  const packer = workflow.slice(start, end)
  assert.match(packer, /grep -E '\^    AeroLink'/)
  assert.match(packer, /sort -k1,1rn -k2,2/)
  assert.match(packer, /Lightest shard takes the next heaviest class/)
  assert.doesNotMatch(packer, /collection/i)
})

test('exact maintained Bash packer agrees on counts and filter text for real and display-name captures', () => {
  const root = dirname(fileURLToPath(import.meta.url))
  const workflow = readFileSync(join(root, '../../../.github/workflows/ci.yml'), 'utf8')
  const shardMatrix = workflow.slice(workflow.indexOf('  backend-api:'), workflow.indexOf('  backend-core-domain:'))
  assert.match(shardMatrix, /shard: \[1, 2, 3\]/)
  const start = workflow.indexOf("          grep -E '^    AeroLink'")
  const end = workflow.indexOf('          expected=', start)
  assert.ok(start > 0 && end > start)
  const bash = process.platform === 'win32' ? join(process.env.ProgramFiles ?? 'C:/Program Files', 'Git/bin/bash.exe') : 'bash'
  if (process.platform === 'win32') assert.ok(existsSync(bash), 'Git Bash is required for current CI packer parity')
  const directory = mkdtempSync(join(process.env.TEMP ?? process.env.TMP ?? '/tmp', 'api-packer-parity-'))
  try {
    const captures = ['api-vstest-real-list.txt', 'api-vstest-list.txt'].map((name) => join(root, 'fixtures', name))
    if (process.env.API_PACKING_PARITY_CAPTURE) captures.push(process.env.API_PACKING_PARITY_CAPTURE)
    for (const fixture of captures) {
      const capture = readFileSync(fixture, 'utf8')
      writeFileSync(join(directory, 'listed.txt'), capture)
      const discovery = { ...makeDiscovery(), tests: parseVstestList(capture) }
      const plan = buildCurrentCountPlan(discovery)
      for (const shard of plan.shards) {
        const script = 'set -euo pipefail\n' + workflow.slice(start, end).replace(/\r/g, '')
          .replaceAll('${{ matrix.shard }}', String(shard.shard)).replaceAll('${{ strategy.job-total }}', '3') + '\ncat partition.txt\n'
        const result = spawnSync(bash, ['--noprofile', '--norc', '-c', script], { cwd: directory, encoding: 'utf8' })
        assert.equal(result.status, 0, result.stderr)
        const [count, filter] = result.stdout.trim().split(/\r?\n/)
        assert.equal(Number(count), shard.caseCount, fixture)
        assert.equal(filter, shard.filter, fixture)
      }
    }
  } finally { rmSync(directory, { recursive: true, force: true }) }
})

test('discovery refuses empty, duplicate, ambiguous, unsafe and empty-shard plans', () => {
  for (const tests of [[], ['AeroLink.Api.Tests.A.One', 'AeroLink.Api.Tests.A.One'],
    ['AeroLink.Api.Tests.A.One', 'AeroLink.Api.Tests.A.Nested.Two'],
    ['AeroLink.Api.Tests.A|FullyQualifiedName~B.One'], ['AeroLink.Api.Tests.A & B.One']]) {
    assert.throws(() => buildCurrentCountPlan({ ...makeDiscovery(), tests }))
  }
  assert.throws(() => buildCurrentCountPlan({ ...makeDiscovery(), tests: ['AeroLink.Api.Tests.A.One'] }), /empty shard/)
  assert.throws(() => parseVstestList(''), /non-empty/)
})

test('invalid duration units and duplicate or unknown weights visibly fall back without losing tests', () => {
  for (const durationMs of [0, -1, NaN, Infinity, '100ms', 1e13]) {
    const discovery = makeDiscovery()
    const observations = makeObservations(discovery)
    observations.weights[0].durationMs = durationMs
    const report = buildApiPackingShadowReport({ discovery, observations })
    assert.equal(report.evidence.fallbackUsed, true)
    assert.equal(report.currentPlan.coverage.complete, true)
    assert.equal(report.evidence.adoptionEligible, false)
  }
  for (const mutate of [(o) => o.weights.push(o.weights[0]), (o) => { o.weights[0].className = 'AeroLink.Api.Tests.Unknown' }]) {
    const discovery = makeDiscovery()
    const observations = makeObservations(discovery); mutate(observations)
    const report = buildApiPackingShadowReport({ discovery, observations })
    assert.equal(report.evidence.fallbackUsed, true)
    assert.equal(report.proposedPlan.coverage.complete, true)
  }
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
