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
  const result = spawnSync(pwsh, ['-NoProfile', '-File', script, ...args], {
    cwd: repoRoot,
    encoding: 'utf8',
    windowsHide: true,
    maxBuffer: 8 * 1024 * 1024,
  })
  assert.equal(result.error, undefined, result.error?.message)
  assert.equal(result.status, 0, `${result.stdout}\n${result.stderr}`)
  return result
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
})

test('evaluate mode applies the 15 percent aggregate and paired decision rule', (t) => {
  const directory = mkdtempSync(join(tmpdir(), 'aerolink-563-evaluate-'))
  t.after(() => rmSync(directory, { recursive: true, force: true }))
  const observations = (wall) => Array.from({ length: 10 }, (_, index) => ({
    valid: true,
    seed: 100 + index,
    metricsComplete: true,
    metrics: {
      worstShardWallMs: wall,
      summedShardWallMs: wall * 2,
      cpuMs: wall * 3,
      diskReadBytes: 100,
      diskWriteBytes: 100,
      factories: 10,
      startupMs: 100,
    },
  }))
  const summary = (condition, wall) => ({
    condition,
    requiredRuns: 10,
    observationCount: 10,
    validObservationCount: 10,
    allValid: true,
    metricsComplete: true,
    observations: observations(wall),
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
})
