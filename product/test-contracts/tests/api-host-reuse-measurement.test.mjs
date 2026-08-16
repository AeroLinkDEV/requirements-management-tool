import assert from 'node:assert/strict'
import { mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join, dirname } from 'node:path'
import { spawnSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import { test } from 'node:test'

const here = dirname(fileURLToPath(import.meta.url))
const repoRoot = join(here, '..', '..', '..')
const script = join(repoRoot, 'product', 'tools', 'measure-api-host-reuse.ps1')
const fixture = join(here, 'fixtures', 'api-test-list.txt')
const pwsh = process.env.PWSH_EXE ?? 'pwsh'

function runPowerShell(args) {
  const result = runPowerShellRaw(args)
  assert.equal(result.error, undefined, result.error?.message)
  assert.equal(result.status, 0, `${result.stdout}\n${result.stderr}`)
  return result
}

function runPowerShellRaw(args) {
  return spawnSync(pwsh, ['-NoProfile', '-File', script, ...args], {
    cwd: repoRoot,
    encoding: 'utf8',
    windowsHide: true,
    maxBuffer: 8 * 1024 * 1024,
  })
}

function planAt(output) {
  runPowerShell([
    '-Mode', 'Plan',
    '-BaselinePath', repoRoot,
    '-TreatmentPath', repoRoot,
    '-OutputRoot', output,
    '-Runs', '2',
    '-Seeds', '41,42',
    '-TestListPath', fixture,
  ])
  return JSON.parse(readFileSync(join(output, 'plan.json'), 'utf8'))
}

test('plan-only mode is safe, deterministic, and produces disjoint complete shards', (t) => {
  const first = mkdtempSync(join(tmpdir(), 'aerolink-563-plan-'))
  const second = mkdtempSync(join(tmpdir(), 'aerolink-563-plan-'))
  t.after(() => { rmSync(first, { recursive: true, force: true }); rmSync(second, { recursive: true, force: true }) })

  const one = planAt(first)
  const two = planAt(second)
  assert.equal(one.planOnly, true)
  assert.match(one.baselineManifest.manifestHash, /^[0-9a-f]{64}$/)
  assert.equal(one.baselineManifest.manifestHash, one.treatmentManifest.manifestHash)
  assert.equal(one.execution.includes('does not build'), true)
  assert.deepEqual(one.observations.map((x) => x.partition), two.observations.map((x) => x.partition))
  assert.deepEqual(one.observations.map((x) => x.order), [['baseline', 'treatment'], ['treatment', 'baseline']])
  assert.deepEqual(readdirSync(first).sort(), ['plan.json', 'plan.md'])

  for (const observation of one.observations) {
    const classes = observation.partition.shards.flatMap((shard) => shard.classes)
    assert.equal(new Set(classes).size, one.baselineManifest.classCount)
    assert.equal(observation.partition.totalCases, one.baselineManifest.caseCount)
    assert.equal(observation.partition.shards.reduce((sum, shard) => sum + shard.expectedCases, 0), one.baselineManifest.caseCount)
  }
})

test('script contract keeps telemetry aggregation, isolated evidence, and alternating order', () => {
  const source = readFileSync(script, 'utf8')
  assert.match(source, /AEROLINK_API_TELEMETRY_JSONL/)
  assert.match(source, /aggregate-api-telemetry\.mjs/)
  assert.match(source, /Start-Sleep -Milliseconds 500/)
  assert.match(source, /if \(\(\$index % 2\) -eq 0\)/)
  assert.match(source, /metricsComplete/)
  assert.match(source, /allValid/)
  assert.match(source, /cpuAvailable/)
  assert.match(source, /order = @\(\$Order\)/)
  assert.match(source, /telemetry aggregator exit code/)
  assert.match(source, /SQLITE_BUSY\|SQLITE_LOCKED/)
  assert.match(source, /Stop-ProcessSafely/)
  assert.match(source, /finally \{/)
  assert.match(source, /Assert-EmptyOutput/)
  assert.match(source, /Assert-OutputOutsideWorktrees/)
  assert.match(source, /Get-CanonicalPath/)
  assert.match(source, /Resolve-Path -LiteralPath \$cursor/)
  assert.match(source, /MaxProcessTreeCount/)
  assert.match(source, /Get-ProcessIdentity/)
  assert.match(source, /Test-ProcessIdentity/)
  assert.match(source, /process-tree enumeration unavailable/)
  assert.match(source, /manifestHash/)
  assert.match(source, /environmentFingerprint/)
  assert.match(source, /finalWorktree/)
  assert.doesNotMatch(source, /Stop-Process -Id/)
  assert.match(source, /\.Kill\(\)/)
  assert.match(source, /PID identity changed; no kill was attempted/)
  assert.match(source, /Known owned process records exceeded MaxProcessTreeCount/)
  assert.match(source, /Assert-WorktreeStable/)
  assert.match(source, /different API test-case names/)
  assert.match(source, /telemetry JSONL was missing or empty/)
  assert.match(source, /telemetry reported zero factories/)
  assert.match(source, /TestListPath is allowed only for Plan contract smoke/)
  assert.match(source, /Run mode requires exactly 10 measured observations/)
  assert.match(source, /summary must contain exactly 10 required runs/)
  assert.match(source, /Refusing to reuse non-empty observation directory/)
  assert.ok(source.indexOf('Assert-EmptyOutput $OutputRoot') < source.indexOf("New-Plan $OutputRoot $baselineInfo $treatmentInfo $manifest $partitions 'Run'"))
})

test('evaluate mode applies the 15 percent aggregate and paired decision rule', (t) => {
  const directory = mkdtempSync(join(tmpdir(), 'aerolink-563-evaluate-'))
  t.after(() => rmSync(directory, { recursive: true, force: true }))
  const manifest = { manifestHash: 'manifest-hash', caseCount: 1, classCount: 1, classFacts: [{ name: 'AeroLink.Api.Tests.SampleTests', cases: 1 }] }
  const metadata = (condition) => ({
    condition,
    path: `C:\\synthetic-${condition}`,
    head: `${condition}-sha`,
    cleanAtStart: true,
    manifest,
    environmentFingerprint: 'synthetic-environment',
  })
  const observations = (condition, wall) => Array.from({ length: 10 }, (_, index) => ({
    schemaVersion: 'aerolink-api-host-reuse-measurement/v1',
    condition,
    valid: true,
    seed: 100 + index,
    metricsComplete: true,
    invalidReasons: [],
    conditionMetadata: metadata(condition),
    finalWorktree: { path: `C:\\synthetic-${condition}`, head: `${condition}-sha`, clean: true },
    partition: { algorithm: 'synthetic', seed: 100 + index, shardCount: 1, totalCases: 1, shards: [{ shard: 1, expectedCases: 1, classes: ['AeroLink.Api.Tests.SampleTests'], filters: 'FullyQualifiedName~AeroLink.Api.Tests.SampleTests.' }] },
    shards: [{
      exitCode: 0,
      expectedCases: 1,
      wallMs: wall,
      cpuMs: wall * 3,
      diskReadBytes: 10,
      diskWriteBytes: 10,
      counts: { total: 1, failed: 0, skipped: 0, other: 0 },
      telemetryHasRecords: true,
      malformedTelemetry: 0,
      telemetryTruncated: false,
      telemetry: { tests: 1, factories: 1, summedFactoryStartupMs: 100 },
      cpuAvailable: true,
      ioAvailable: true,
      processTreeAvailable: true,
      processTreeError: null,
      successfulSamples: 1,
      cleanupFailure: null,
      waitError: null,
      errorSignals: [],
    }],
    metrics: {
      worstShardWallMs: wall,
      summedShardWallMs: wall,
      cpuMs: wall * 3,
      diskReadBytes: 10,
      diskWriteBytes: 10,
      factories: 1,
      startupMs: 100,
      testCount: 1,
    },
  }))
  const summary = (condition, wall) => ({
    schemaVersion: 'aerolink-api-host-reuse-measurement/v1',
    condition,
    requiredRuns: 10,
    observationCount: 10,
    validObservationCount: 10,
    allValid: true,
    metricsComplete: true,
    manifest,
    conditionMetadata: metadata(condition),
    observations: observations(condition, wall),
  })
  const baseline = join(directory, 'baseline.json')
  const treatment = join(directory, 'treatment.json')
  writeFileSync(baseline, JSON.stringify(summary('baseline', 100)))
  writeFileSync(treatment, JSON.stringify(summary('treatment', 80)))
  runPowerShell([
    '-Mode', 'Evaluate',
    '-BaselineSummaryPath', baseline,
    '-TreatmentSummaryPath', treatment,
    '-OutputRoot', directory,
  ])
  const decision = JSON.parse(readFileSync(join(directory, 'decision.json'), 'utf8'))
  assert.equal(decision.status, 'pass')
  assert.ok(Math.abs(decision.aggregateImprovement - 0.2) < 1e-12)
  assert.ok(Math.abs(decision.pairedImprovement.median - 0.2) < 1e-12)

  const oneRun = JSON.parse(readFileSync(baseline, 'utf8'))
  oneRun.requiredRuns = 1
  oneRun.observationCount = 1
  oneRun.observations = [oneRun.observations[0]]
  writeFileSync(baseline, JSON.stringify(oneRun))
  runPowerShell(['-Mode', 'Evaluate', '-BaselineSummaryPath', baseline, '-TreatmentSummaryPath', treatment, '-OutputRoot', directory])
  const oneRunDecision = JSON.parse(readFileSync(join(directory, 'decision.json'), 'utf8'))
  assert.equal(oneRunDecision.status, 'inconclusive')
  assert.ok(oneRunDecision.validationErrors.some((error) => error.includes('exactly 10')))

  writeFileSync(baseline, JSON.stringify(summary('baseline', 100)))
  const zeroTreatment = JSON.parse(readFileSync(treatment, 'utf8'))
  zeroTreatment.observations[0].metrics.worstShardWallMs = 0
  zeroTreatment.observations[0].shards[0].wallMs = 0
  writeFileSync(treatment, JSON.stringify(zeroTreatment))
  runPowerShell(['-Mode', 'Evaluate', '-BaselineSummaryPath', baseline, '-TreatmentSummaryPath', treatment, '-OutputRoot', directory])
  const zeroTreatmentDecision = JSON.parse(readFileSync(join(directory, 'decision.json'), 'utf8'))
  assert.equal(zeroTreatmentDecision.status, 'inconclusive')
  assert.ok(zeroTreatmentDecision.validationErrors.some((error) => error.includes('non-positive')))
  writeFileSync(treatment, JSON.stringify(summary('treatment', 80)))

  const manifestMismatch = JSON.parse(readFileSync(treatment, 'utf8'))
  manifestMismatch.manifest.manifestHash = 'different-manifest'
  writeFileSync(treatment, JSON.stringify(manifestMismatch))
  runPowerShell(['-Mode', 'Evaluate', '-BaselineSummaryPath', baseline, '-TreatmentSummaryPath', treatment, '-OutputRoot', directory])
  const manifestDecision = JSON.parse(readFileSync(join(directory, 'decision.json'), 'utf8'))
  assert.equal(manifestDecision.status, 'inconclusive')
  assert.ok(manifestDecision.validationErrors.some((error) => error.includes('manifest hashes differ')))
  writeFileSync(treatment, JSON.stringify(summary('treatment', 80)))

  const shaMismatch = JSON.parse(readFileSync(treatment, 'utf8'))
  shaMismatch.conditionMetadata.head = 'baseline-sha'
  for (const observation of shaMismatch.observations) {
    observation.conditionMetadata.head = 'baseline-sha'
    observation.finalWorktree.head = 'baseline-sha'
  }
  writeFileSync(treatment, JSON.stringify(shaMismatch))
  runPowerShell(['-Mode', 'Evaluate', '-BaselineSummaryPath', baseline, '-TreatmentSummaryPath', treatment, '-OutputRoot', directory])
  const shaDecision = JSON.parse(readFileSync(join(directory, 'decision.json'), 'utf8'))
  assert.equal(shaDecision.status, 'inconclusive')
  assert.ok(shaDecision.validationErrors.some((error) => error.includes('SHAs must be distinct')))
  writeFileSync(treatment, JSON.stringify(summary('treatment', 80)))

  const environmentMismatch = JSON.parse(readFileSync(treatment, 'utf8'))
  environmentMismatch.conditionMetadata.environmentFingerprint = 'different-environment'
  for (const observation of environmentMismatch.observations) observation.conditionMetadata.environmentFingerprint = 'different-environment'
  writeFileSync(treatment, JSON.stringify(environmentMismatch))
  runPowerShell(['-Mode', 'Evaluate', '-BaselineSummaryPath', baseline, '-TreatmentSummaryPath', treatment, '-OutputRoot', directory])
  const environmentDecision = JSON.parse(readFileSync(join(directory, 'decision.json'), 'utf8'))
  assert.equal(environmentDecision.status, 'inconclusive')
  assert.ok(environmentDecision.validationErrors.some((error) => error.includes('environment fingerprints differ')))
  writeFileSync(treatment, JSON.stringify(summary('treatment', 80)))

  const partitionMismatch = JSON.parse(readFileSync(treatment, 'utf8'))
  partitionMismatch.observations[0].partition.shards[0].expectedCases = 99
  writeFileSync(treatment, JSON.stringify(partitionMismatch))
  runPowerShell(['-Mode', 'Evaluate', '-BaselineSummaryPath', baseline, '-TreatmentSummaryPath', treatment, '-OutputRoot', directory])
  const partitionDecision = JSON.parse(readFileSync(join(directory, 'decision.json'), 'utf8'))
  assert.equal(partitionDecision.status, 'inconclusive')
  assert.ok(partitionDecision.validationErrors.some((error) => error.includes('Partition differs')))
  writeFileSync(treatment, JSON.stringify(summary('treatment', 80)))

  const dirtyFinal = JSON.parse(readFileSync(baseline, 'utf8'))
  dirtyFinal.observations[0].finalWorktree.clean = false
  writeFileSync(baseline, JSON.stringify(dirtyFinal))
  runPowerShell(['-Mode', 'Evaluate', '-BaselineSummaryPath', baseline, '-TreatmentSummaryPath', treatment, '-OutputRoot', directory])
  const dirtyDecision = JSON.parse(readFileSync(join(directory, 'decision.json'), 'utf8'))
  assert.equal(dirtyDecision.status, 'inconclusive')
  assert.ok(dirtyDecision.validationErrors.some((error) => error.includes('final worktree state')))
  writeFileSync(baseline, JSON.stringify(summary('baseline', 100)))

  const zeroBaseline = JSON.parse(readFileSync(baseline, 'utf8'))
  zeroBaseline.observations[0].metrics.worstShardWallMs = 0
  zeroBaseline.observations[0].shards[0].wallMs = 0
  writeFileSync(baseline, JSON.stringify(zeroBaseline))
  runPowerShell(['-Mode', 'Evaluate', '-BaselineSummaryPath', baseline, '-TreatmentSummaryPath', treatment, '-OutputRoot', directory])
  const zeroDecision = JSON.parse(readFileSync(join(directory, 'decision.json'), 'utf8'))
  assert.equal(zeroDecision.status, 'inconclusive')
  assert.ok(zeroDecision.validationErrors.some((error) => error.includes('non-positive')))

  zeroBaseline.observations[0].metrics.worstShardWallMs = 100
  zeroBaseline.observations[0].shards[0].wallMs = 100
  writeFileSync(baseline, JSON.stringify(zeroBaseline))
  const forged = JSON.parse(readFileSync(baseline, 'utf8'))
  forged.observations[0].valid = false
  writeFileSync(baseline, JSON.stringify(forged))
  runPowerShell(['-Mode', 'Evaluate', '-BaselineSummaryPath', baseline, '-TreatmentSummaryPath', treatment, '-OutputRoot', directory])
  const forgedDecision = JSON.parse(readFileSync(join(directory, 'decision.json'), 'utf8'))
  assert.equal(forgedDecision.status, 'inconclusive')
  assert.ok(forgedDecision.validationErrors.some((error) => error.includes('valid flag')))

  const restored = JSON.parse(readFileSync(baseline, 'utf8'))
  restored.observations[0].valid = true
  writeFileSync(baseline, JSON.stringify(restored))
  const treatmentReordered = JSON.parse(readFileSync(treatment, 'utf8'))
  treatmentReordered.observations.reverse()
  writeFileSync(treatment, JSON.stringify(treatmentReordered))
  runPowerShell(['-Mode', 'Evaluate', '-BaselineSummaryPath', baseline, '-TreatmentSummaryPath', treatment, '-OutputRoot', directory])
  const reorderedDecision = JSON.parse(readFileSync(join(directory, 'decision.json'), 'utf8'))
  assert.equal(reorderedDecision.status, 'pass')

  const duplicate = JSON.parse(readFileSync(treatment, 'utf8'))
  duplicate.observations[1].seed = duplicate.observations[0].seed
  writeFileSync(treatment, JSON.stringify(duplicate))
  runPowerShell(['-Mode', 'Evaluate', '-BaselineSummaryPath', baseline, '-TreatmentSummaryPath', treatment, '-OutputRoot', directory])
  const duplicateDecision = JSON.parse(readFileSync(join(directory, 'decision.json'), 'utf8'))
  assert.equal(duplicateDecision.status, 'inconclusive')
  assert.ok(duplicateDecision.validationErrors.some((error) => error.includes('repeats seed')))

  const mismatched = JSON.parse(readFileSync(treatment, 'utf8'))
  mismatched.observations[1].seed = 9999
  writeFileSync(treatment, JSON.stringify(mismatched))
  runPowerShell(['-Mode', 'Evaluate', '-BaselineSummaryPath', baseline, '-TreatmentSummaryPath', treatment, '-OutputRoot', directory])
  const mismatchedDecision = JSON.parse(readFileSync(join(directory, 'decision.json'), 'utf8'))
  assert.equal(mismatchedDecision.status, 'inconclusive')
  assert.ok(mismatchedDecision.validationErrors.some((error) => error.includes('same seed set')))
})

test('plan rejects output inside a condition worktree', () => {
  const result = runPowerShellRaw([
    '-Mode', 'Plan',
    '-BaselinePath', repoRoot,
    '-TreatmentPath', repoRoot,
    '-OutputRoot', repoRoot,
    '-Runs', '2',
    '-Seeds', '41,42',
    '-TestListPath', fixture,
  ])
  assert.notEqual(result.status, 0)
  assert.match(`${result.stdout}\n${result.stderr}`, /Output must be outside the condition worktrees/)
})

test('run rejects a non-authoritative one-run configuration before touching worktrees', () => {
  const result = runPowerShellRaw(['-Mode', 'Run', '-Runs', '1', '-BaselinePath', repoRoot, '-TreatmentPath', repoRoot])
  assert.notEqual(result.status, 0)
  assert.match(`${result.stdout}\n${result.stderr}`, /requires exactly 10 measured observations/)
})
