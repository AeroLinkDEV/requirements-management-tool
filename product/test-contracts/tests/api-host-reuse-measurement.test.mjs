import assert from 'node:assert/strict'
import { existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs'
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
  assert.match(source, /Get-Item -LiteralPath \$candidate/)
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
  assert.match(source, /No expected process identity was available; no process was killed/)
  assert.match(source, /Final verification is against every known identity/)
  assert.match(source, /Process-tree refresh failed/)
  assert.match(source, /Assert-WorktreeStable/)
  assert.match(source, /different API test-case names/)
  assert.match(source, /telemetry JSONL was missing or empty/)
  assert.match(source, /telemetry reported zero factories/)
  assert.match(source, /TestListPath is allowed only for Plan contract smoke/)
  assert.match(source, /Run mode requires exactly 10 measured observations/)
  assert.match(source, /summary must contain exactly 10 required runs/)
  assert.match(source, /full sorted case-name manifest/)
  assert.match(source, /live discovery/)
  assert.match(source, /recorded condition worktree paths to exist/)
  assert.match(source, /Refusing to reuse non-empty observation directory/)
  assert.ok(source.indexOf('Assert-EmptyOutput $OutputRoot') < source.indexOf("New-Plan $OutputRoot $baselineInfo $treatmentInfo $manifest $partitions 'Run'"))
})

function clone(value) {
  return JSON.parse(JSON.stringify(value))
}

function ensureBuilt(worktree) {
  const dll = join(worktree, 'product', 'tests', 'AeroLink.Api.Tests', 'bin', 'Release', 'net10.0', 'AeroLink.Api.Tests.dll')
  if (existsSync(dll)) return true
  const result = spawnSync('dotnet', ['build', 'product/AeroLink.slnx', '--configuration', 'Release', '--nologo'], {
    cwd: worktree,
    encoding: 'utf8',
    windowsHide: true,
    timeout: 180000,
    maxBuffer: 8 * 1024 * 1024,
  })
  return result.status === 0 && existsSync(dll)
}

function findLivePlan(t) {
  const candidates = [repoRoot, 'C:\\Sean Project\\Requirements Management Tool']
    .filter((path, index, all) => all.indexOf(path) === index && existsSync(path))
  for (const baselinePath of candidates) {
    for (const treatmentPath of candidates) {
      if (baselinePath === treatmentPath || !ensureBuilt(baselinePath) || !ensureBuilt(treatmentPath)) continue
      const output = mkdtempSync(join(tmpdir(), 'aerolink-563-live-plan-'))
      const result = runPowerShellRaw([
        '-Mode', 'Plan', '-BaselinePath', baselinePath, '-TreatmentPath', treatmentPath,
        '-OutputRoot', output, '-Runs', '10', '-Seeds', '563000,563001,563002,563003,563004,563005,563006,563007,563008,563009',
      ])
      if (result.status === 0) {
        const plan = JSON.parse(readFileSync(join(output, 'plan.json'), 'utf8'))
        rmSync(output, { recursive: true, force: true })
        if (plan.baseline.clean && plan.treatment.clean && plan.baseline.head !== plan.treatment.head) return plan
      }
      rmSync(output, { recursive: true, force: true })
    }
  }
  t.skip('No two distinct clean, built current worktrees with an identical live API test manifest were available.')
  return null
}

function manifestFacts(manifest) {
  return {
    manifestHash: manifest.manifestHash,
    caseCount: manifest.caseCount,
    classCount: manifest.classCount,
    classFacts: manifest.classFacts,
    caseNames: manifest.cases.map((item) => item.name).sort(),
  }
}

function makeLiveSummary(plan, condition, wall) {
  const manifest = manifestFacts(plan[condition === 'baseline' ? 'baselineManifest' : 'treatmentManifest'])
  const metadata = clone(plan[condition === 'baseline' ? 'baselineConditionMetadata' : 'treatmentConditionMetadata'])
  metadata.manifest = clone(manifest)
  const observations = plan.observations.map((planned, index) => {
    const shards = planned.partition.shards.map((shard) => ({
      shard: shard.shard,
      classes: shard.classes,
      expectedCases: shard.expectedCases,
      exitCode: 0,
      wallMs: wall,
      cpuMs: wall,
      diskReadBytes: 10,
      diskWriteBytes: 10,
      counts: { total: shard.expectedCases, failed: 0, skipped: 0, other: 0 },
      telemetryHasRecords: true,
      malformedTelemetry: 0,
      telemetryTruncated: false,
      telemetry: { tests: shard.expectedCases, factories: 1, summedFactoryStartupMs: 1 },
      cpuAvailable: true,
      ioAvailable: true,
      processTreeAvailable: true,
      processTreeError: null,
      successfulSamples: 1,
      cleanupFailure: null,
      waitError: null,
      errorSignals: [],
    }))
    return {
      schemaVersion: 'aerolink-api-host-reuse-measurement/v1',
      condition,
      valid: true,
      seed: planned.seed,
      metricsComplete: true,
      invalidReasons: [],
      conditionMetadata: clone(metadata),
      finalWorktree: clone(plan[condition === 'baseline' ? 'baseline' : 'treatment']),
      partition: clone(planned.partition),
      shards,
      metrics: {
        worstShardWallMs: wall,
        summedShardWallMs: wall * shards.length,
        cpuMs: wall * shards.length,
        diskReadBytes: 10 * shards.length,
        diskWriteBytes: 10 * shards.length,
        factories: shards.length,
        startupMs: shards.length,
        testCount: manifest.caseCount,
      },
      run: index + 1,
    }
  })
  return {
    schemaVersion: 'aerolink-api-host-reuse-measurement/v1',
    condition,
    requiredRuns: 10,
    observationCount: 10,
    validObservationCount: 10,
    allValid: true,
    metricsComplete: true,
    manifest,
    conditionMetadata: metadata,
    observations,
  }
}

function evaluateLive(plan, baseline, treatment) {
  const directory = mkdtempSync(join(tmpdir(), 'aerolink-563-evaluate-'))
  const summaries = join(directory, 'summaries')
  const output = join(directory, 'output')
  mkdirSync(summaries, { recursive: true })
  writeFileSync(join(summaries, 'baseline.json'), JSON.stringify(baseline))
  writeFileSync(join(summaries, 'treatment.json'), JSON.stringify(treatment))
  const result = runPowerShellRaw([
    '-Mode', 'Evaluate', '-BaselineSummaryPath', join(summaries, 'baseline.json'),
    '-TreatmentSummaryPath', join(summaries, 'treatment.json'), '-OutputRoot', output,
  ])
  return { directory, output, result }
}

test('evaluate authenticates a positive ten-run decision against live manifests and exact worktrees', (t) => {
  const plan = findLivePlan(t)
  if (!plan) return
  const baseline = makeLiveSummary(plan, 'baseline', 100)
  const treatment = makeLiveSummary(plan, 'treatment', 80)
  const evaluated = evaluateLive(plan, baseline, treatment)
  t.after(() => rmSync(evaluated.directory, { recursive: true, force: true }))
  assert.equal(evaluated.result.status, 0, `${evaluated.result.stdout}\n${evaluated.result.stderr}`)
  const decision = JSON.parse(readFileSync(join(evaluated.output, 'decision.json'), 'utf8'))
  assert.equal(decision.status, 'pass')
  assert.ok(Math.abs(decision.aggregateImprovement - 0.2) < 1e-12)
  assert.ok(Math.abs(decision.pairedImprovement.median - 0.2) < 1e-12)
})

test('evaluate rejects forged matching manifests and missing final worktree evidence', (t) => {
  const plan = findLivePlan(t)
  if (!plan) return
  const baseline = makeLiveSummary(plan, 'baseline', 100)
  const treatment = makeLiveSummary(plan, 'treatment', 80)
  const forgedFacts = { manifestHash: '0'.repeat(64), caseCount: 1, classCount: 1, classFacts: [{ name: 'AeroLink.Api.Tests.Forged', cases: 1 }], caseNames: ['AeroLink.Api.Tests.Forged.Forged'] }
  for (const summary of [baseline, treatment]) {
    summary.manifest = clone(forgedFacts)
    summary.conditionMetadata.manifest = clone(forgedFacts)
    for (const observation of summary.observations) observation.conditionMetadata.manifest = clone(forgedFacts)
  }
  const forged = evaluateLive(plan, baseline, treatment)
  t.after(() => rmSync(forged.directory, { recursive: true, force: true }))
  assert.notEqual(forged.result.status, 0)
  assert.equal(existsSync(join(forged.output, 'decision.json')), false)

  const validBaseline = makeLiveSummary(plan, 'baseline', 100)
  const validTreatment = makeLiveSummary(plan, 'treatment', 80)
  delete validBaseline.observations[0].finalWorktree
  const missingFinal = evaluateLive(plan, validBaseline, validTreatment)
  t.after(() => rmSync(missingFinal.directory, { recursive: true, force: true }))
  assert.equal(missingFinal.result.status, 0, `${missingFinal.result.stdout}\n${missingFinal.result.stderr}`)
  const decision = JSON.parse(readFileSync(join(missingFinal.output, 'decision.json'), 'utf8'))
  assert.equal(decision.status, 'inconclusive')
  assert.ok(decision.validationErrors.some((error) => error.includes('final worktree state')))
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

test('plan rejects an NTFS junction resolving into a condition worktree', (t) => {
  const parent = mkdtempSync(join(tmpdir(), 'aerolink-563-junction-'))
  const junction = join(parent, 'output-junction')
  const quotePs = (value) => `'${value.replaceAll("'", "''")}'`
  const create = spawnSync(pwsh, ['-NoProfile', '-Command', `New-Item -ItemType Junction -Path ${quotePs(junction)} -Target ${quotePs(repoRoot)} | Out-Null`], {
    encoding: 'utf8',
    windowsHide: true,
  })
  if (create.status !== 0) {
    rmSync(parent, { recursive: true, force: true })
    t.skip(`NTFS junction creation unavailable: ${create.stderr || create.stdout}`)
    return
  }
  t.after(() => {
    spawnSync(pwsh, ['-NoProfile', '-Command', `Remove-Item -LiteralPath ${quotePs(junction)} -Force -ErrorAction SilentlyContinue`], { encoding: 'utf8', windowsHide: true })
    rmSync(parent, { recursive: true, force: true })
  })
  const result = runPowerShellRaw([
    '-Mode', 'Plan', '-BaselinePath', repoRoot, '-TreatmentPath', repoRoot,
    '-OutputRoot', junction, '-Runs', '2', '-Seeds', '41,42', '-TestListPath', fixture,
  ])
  assert.notEqual(result.status, 0)
  assert.match(`${result.stdout}\n${result.stderr}`, /Output must be outside the condition worktrees/)
})

test('run rejects a non-authoritative one-run configuration before touching worktrees', () => {
  const result = runPowerShellRaw(['-Mode', 'Run', '-Runs', '1', '-BaselinePath', repoRoot, '-TreatmentPath', repoRoot])
  assert.notEqual(result.status, 0)
  assert.match(`${result.stdout}\n${result.stderr}`, /requires exactly 10 measured observations/)
})
