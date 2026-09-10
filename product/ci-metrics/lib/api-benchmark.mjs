// Controlled local Windows API-packing benchmark for #942.
//
// The benchmark runs the same source and complete inventory through the current count plan and the
// duration candidate. It is an explicit diagnostic command, uses three shards in both cohorts, retains
// TRX reconciliation, and never changes CI selectors or protected gate behavior.

import { existsSync, mkdirSync, readFileSync } from 'node:fs'
import { isAbsolute, join, relative, resolve } from 'node:path'
import { tmpdir } from 'node:os'
import * as os from 'node:os'
import { execFileSync, spawn } from 'node:child_process'
import { buildCurrentCountPlan, buildDurationCandidatePlan, normalizeApiDiscovery } from './api-packing-shadow.mjs'
import { classDurationWeights } from './api-observations.mjs'
import { parseTrx } from './trx.mjs'

export const API_BENCHMARK_SCHEMA = 'aerolink-api-packing-benchmark/v1'
export const BENCHMARK_SHARD_COUNT = 3
export const BENCHMARK_TIMEOUT_MS = 30 * 60 * 1000
const MAX_OUTPUT_BYTES = 32 * 1024

function requireObject(value, label) {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) throw new Error(`${label} must be an object.`)
  return value
}

function boundedOutput(value) {
  const text = String(value ?? '')
  return text.length > MAX_OUTPUT_BYTES ? `${text.slice(0, MAX_OUTPUT_BYTES)}…` : text
}

function protectedDatabaseEnvironment(environment) {
  for (const [key, value] of Object.entries(environment ?? {})) {
    if (/(?:AEROLINK_)?(?:MIGRATIONS|POSTGRES|DATABASE|CONNECTION)|^(?:PG|POSTGRES)/i.test(key) || /54329/.test(String(value))) {
      throw new Error(`Benchmark refuses protected database environment variable '${key}'.`)
    }
  }
}

function safeChildEnvironment(environment) {
  protectedDatabaseEnvironment(environment)
  const safe = {}
  for (const [key, value] of Object.entries(environment ?? {})) {
    if (/TOKEN|SECRET|PASSWORD|PASSWD|PRIVATE_KEY|ACCESS_KEY|API_KEY|CREDENTIAL|AUTH|GITHUB_|CONNECTION|DATABASE|POSTGRES|^PG/i.test(key)) continue
    safe[key] = value
  }
  safe.AEROLINK_BENCHMARK_MODE = 'disposable-local'
  safe.AEROLINK_BENCHMARK_OUTPUT = 'owned-temp-output'
  return safe
}

function terminateOwnedProcessTree(pid) {
  if (!Number.isSafeInteger(pid) || pid < 1) return
  try {
    if (process.platform === 'win32') execFileSync('taskkill', ['/PID', String(pid), '/T', '/F'], { windowsHide: true, timeout: 5_000, stdio: 'ignore' })
    else execFileSync('kill', ['-TERM', String(pid)], { timeout: 5_000, stdio: 'ignore' })
  } catch { /* the child close/error handler still records the bounded timeout */ }
}

function runCommand({ command, args, cwd, environment, timeoutMs = BENCHMARK_TIMEOUT_MS, runner = null }) {
  if (runner) return runner({ command, args, cwd, environment, timeoutMs })
  return new Promise((resolveResult) => {
    const started = Date.now()
    const child = spawn(command, args, { cwd, env: environment, shell: false, windowsHide: true })
    let stdout = ''
    let stderr = ''
    const append = (current, chunk) => boundedOutput(`${current}${chunk.toString()}`)
    child.stdout?.on('data', (chunk) => { stdout = append(stdout, chunk) })
    child.stderr?.on('data', (chunk) => { stderr = append(stderr, chunk) })
    let timedOut = false
    const timer = setTimeout(() => {
      timedOut = true
      // dotnet test starts a testhost child on Windows. Terminate only the tree rooted at
      // the PID spawned by this benchmark, so a timeout cannot leave owned work running or
      // disturb another user's process with a broad process-name kill.
      terminateOwnedProcessTree(child.pid)
      child.kill()
    }, timeoutMs)
    child.on('error', (error) => {
      clearTimeout(timer)
      resolveResult({ exitCode: null, signal: null, durationMs: Date.now() - started, timedOut, stdout, stderr: boundedOutput(error.message) })
    })
    child.on('close', (exitCode, signal) => {
      clearTimeout(timer)
      resolveResult({ exitCode, signal, durationMs: Date.now() - started, timedOut, stdout, stderr })
    })
  })
}

function sourceIdentity(sourceDir, git = null) {
  if (git) {
    const value = requireObject(git(sourceDir), 'source identity')
    if (typeof value.commitSha !== 'string' || typeof value.treeSha !== 'string') throw new Error('Source identity callback did not return commit/tree SHAs.')
    if (value.dirty === true) throw new Error('Source tree is dirty; benchmark requires the exact committed discovery tree.')
    if (!/^[0-9a-f]{40}$/i.test(value.commitSha) || !/^[0-9a-f]{40}$/i.test(value.treeSha)) throw new Error('Source tree identity could not be resolved.')
    return { commitSha: value.commitSha.toLowerCase(), treeSha: value.treeSha.toLowerCase(), dirty: value.dirty }
  }
  const commitSha = execFileSync('git', ['rev-parse', 'HEAD'], { cwd: sourceDir, encoding: 'utf8' }).trim()
  const treeSha = execFileSync('git', ['rev-parse', 'HEAD^{tree}'], { cwd: sourceDir, encoding: 'utf8' }).trim()
  const status = execFileSync('git', ['status', '--porcelain', '--untracked-files=all'], { cwd: sourceDir, encoding: 'utf8' }).trim()
  if (status) throw new Error('Source tree is dirty; benchmark requires the exact committed discovery tree.')
  if (!/^[0-9a-f]{40}$/i.test(commitSha) || !/^[0-9a-f]{40}$/i.test(treeSha)) throw new Error('Source tree identity could not be resolved.')
  return { commitSha: commitSha.toLowerCase(), treeSha: treeSha.toLowerCase(), dirty: false }
}

function toolchainSnapshot() {
  let dotnetVersion = null
  try { dotnetVersion = boundedOutput(execFileSync('dotnet', ['--version'], { encoding: 'utf8', timeout: 5_000 }).trim()) || null } catch { /* command failure remains visible in cohort build output */ }
  return {
    platform: process.platform,
    arch: process.arch,
    osRelease: boundedOutput(os.release()),
    cpuCount: typeof os.availableParallelism === 'function' ? os.availableParallelism() : os.cpus().length,
    dotnetVersion,
  }
}

function inventoryFromInput(input) {
  const value = requireObject(input, 'discovery input')
  const discovery = value.schemaVersion === 'aerolink-api-discovery/v1' ? value : value.discovery
  return normalizeApiDiscovery(discovery)
}

function durationsFromInput(input, discovery) {
  const value = requireObject(input, 'observation input')
  const authenticatedCollectorReport = value.schemaVersion === 'aerolink-api-observations/v1' && value.collector?.mode === 'authenticated-read-only'
  const candidates = []
  if (Array.isArray(value.runs)) candidates.push(...value.runs.filter((run) => run?.artifactAssertions?.reconciled === true).map((run) => ({ candidate: run, provenance: authenticatedCollectorReport && run.sourceMetadata?.authenticated === true ? (run.comparability?.eligible === true ? 'authenticated-run-report' : 'authenticated-recovered-run') : 'caller-supplied-unverified-run' })))
  if (value.sourceMetadata && Array.isArray(value.shards)) candidates.push({ candidate: value, provenance: value.sourceMetadata.authenticated === true && value.artifactAssertions?.reconciled === true ? 'authenticated-reconciled-run' : 'caller-supplied-unverified-run' })
  if (Array.isArray(value.classDurations)) candidates.push({ candidate: value, provenance: 'caller-supplied-unverified-durations' })
  for (const { candidate, provenance } of candidates) {
    if (candidate.inventory?.digest && candidate.inventory.digest !== discovery.digest) continue
    if (candidate.sourceMetadata?.run?.treeSha && candidate.sourceMetadata.run.treeSha !== discovery.treeSha) continue
    const weights = classDurationWeights(candidate)
    if (weights.size > 0) return {
      weights,
      sourceRunId: candidate.sourceMetadata?.run?.id ?? null,
      provenance,
      inventoryDigest: candidate.inventory?.digest ?? discovery.digest,
      sourceCommitSha: candidate.sourceMetadata?.run?.commitSha ?? discovery.commitSha,
      sourceTreeSha: candidate.sourceMetadata?.run?.treeSha ?? discovery.treeSha,
      metadataAuthenticated: provenance.startsWith('authenticated-'),
      artifactReconciled: provenance.startsWith('authenticated-') && candidate.artifactAssertions?.reconciled === true,
    }
  }
  throw new Error('Observation input contains no comparable class durations for the supplied inventory.')
}

function planCoverage(plan, discovery, label) {
  if (!plan || plan.shardCount !== BENCHMARK_SHARD_COUNT || plan.shards.length !== BENCHMARK_SHARD_COUNT) throw new Error(`${label} does not contain exactly three shards.`)
  const counts = new Map()
  const classCounts = new Map()
  for (const shard of plan.shards) {
    if (!shard.filter || !Array.isArray(shard.tests) || shard.tests.length === 0) throw new Error(`${label} contains an empty shard filter.`)
    for (const test of shard.tests) counts.set(test, (counts.get(test) ?? 0) + 1)
    for (const className of shard.classes) classCounts.set(className, (classCounts.get(className) ?? 0) + 1)
  }
  const missing = discovery.tests.filter((test) => counts.get(test) !== 1)
  const split = [...classCounts.entries()].filter(([, count]) => count !== 1)
  if (missing.length > 0 || split.length > 0) throw new Error(`${label} does not cover each discovered test exactly once and preserve class atomicity.`)
  return true
}

function classNameForTest(name) {
  const withoutArguments = String(name).split('(', 1)[0]
  const dot = withoutArguments.lastIndexOf('.')
  return dot > 0 ? withoutArguments.slice(0, dot) : null
}

function reconcileTrx(trx, plannedTests, label) {
  const names = trx.tests.map((test) => test.name)
  const counts = new Map()
  for (const name of names) counts.set(name, (counts.get(name) ?? 0) + 1)
  const expected = new Set(plannedTests)
  const missing = plannedTests.filter((name) => counts.get(name) !== 1)
  const extra = names.filter((name) => !expected.has(name))
  if (missing.length > 0 || extra.length > 0 || new Set(names).size !== names.length) {
    return { ok: false, missing: missing.slice(0, 20), extra: extra.slice(0, 20), duplicateCount: names.length - new Set(names).size }
  }
  if (trx.tests.some((test) => classNameForTest(test.name) !== test.className)) {
    return { ok: false, reason: 'TRX identities were present, but a result class did not match its planned test identity.', missing: [], extra: [], duplicateCount: 0 }
  }
  if (trx.totals.total !== plannedTests.length || trx.totals.executed !== plannedTests.length || trx.totals.passed !== plannedTests.length || trx.totals.failed !== 0 || trx.totals.skipped !== 0 || trx.tests.some((test) => test.outcome !== 'Passed')) {
    return { ok: false, reason: 'TRX identities reconciled, but first-pass outcomes were not all Passed.', missing: [], extra: [], duplicateCount: 0 }
  }
  return { ok: true, missing: [], extra: [], duplicateCount: 0 }
}

async function executeCohort({ label, plan, sourceDir, outputDir, project, environment, runner }) {
  planCoverage(plan, { tests: plan.shards.flatMap((shard) => shard.tests) }, `${label} plan`)
  const cohortDir = join(outputDir, label)
  mkdirSync(cohortDir, { recursive: true })
  const cohortEnvironment = { ...environment, TEMP: cohortDir, TMP: cohortDir, TMPDIR: cohortDir }
  const buildArgs = ['build', project, '--configuration', 'Release', '--nologo']
  const build = await runCommand({
    command: 'dotnet',
    args: buildArgs,
    cwd: sourceDir,
    environment: cohortEnvironment,
    runner,
    timeoutMs: BENCHMARK_TIMEOUT_MS,
  })
  const setupMs = build.durationMs
  const shards = []
  if (build.exitCode !== 0 || build.timedOut) {
    return {
      label, algorithm: plan.algorithm, setupMs, build: { ...build, command: 'dotnet', args: buildArgs, stdout: boundedOutput(build.stdout), stderr: boundedOutput(build.stderr) },
      shards: plan.shards.map((shard) => ({ shard: shard.shard, filter: shard.filter, plannedTests: shard.tests.length, status: 'not-started', reason: 'Build failed before shard execution.' })),
      success: false, retryCount: 0, slowestShardWallMs: null, totalRunnerMinutes: 0,
    }
  }
  const shardRuns = await Promise.all(plan.shards.map(async (shard) => {
    const shardDir = join(cohortDir, `shard-${shard.shard}`)
    mkdirSync(shardDir, { recursive: true })
    const shardEnvironment = { ...environment, TEMP: shardDir, TMP: shardDir, TMPDIR: shardDir }
    const trxPath = join(shardDir, 'shard.trx')
    const started = Date.now()
    const testArgs = ['test', project, '--configuration', 'Release', '--no-build', '--nologo', '--filter', shard.filter,
      '--logger', 'trx;LogFileName=shard.trx', '--results-directory', shardDir, '--blame-hang-timeout', '15m']
    const result = await runCommand({
      command: 'dotnet',
      args: testArgs,
      cwd: sourceDir,
      environment: shardEnvironment,
      runner,
      timeoutMs: BENCHMARK_TIMEOUT_MS,
    })
    const wallMs = Date.now() - started
    let trx = null
    let reconciliation = { ok: false, reason: 'TRX file was not produced.' }
    if (existsSync(trxPath)) {
      try {
        trx = parseTrx(readFileSync(trxPath, 'utf8'))
        reconciliation = reconcileTrx(trx, shard.tests, `Cohort ${label} shard ${shard.shard}`)
      } catch (error) {
        reconciliation = { ok: false, reason: `TRX parse failed: ${error.message}` }
      }
    }
    return {
      shard: shard.shard,
      filter: shard.filter,
      plannedTests: shard.tests.length,
      attempt: 1,
      wallMs,
      process: { ...result, command: 'dotnet', args: testArgs, stdout: boundedOutput(result.stdout), stderr: boundedOutput(result.stderr) },
      status: result.exitCode === 0 && !result.timedOut && reconciliation.ok ? 'success' : 'failure',
      trx: trx ? { totals: trx.totals, tests: trx.tests } : null,
      outcomes: trx ? { passed: trx.totals.passed, failed: trx.totals.failed, skipped: trx.totals.skipped } : null,
      reconciliation,
    }
  }))
  shards.push(...shardRuns.sort((a, b) => a.shard - b.shard))
  const validWalls = shards.map((shard) => shard.wallMs).filter((value) => Number.isFinite(value))
  const success = shards.every((shard) => shard.status === 'success')
  return {
    label, algorithm: plan.algorithm, setupMs, build: { ...build, command: 'dotnet', args: buildArgs, stdout: boundedOutput(build.stdout), stderr: boundedOutput(build.stderr) },
    shards, success, retryCount: 0, slowestShardWallMs: validWalls.length > 0 ? Math.max(...validWalls) : null,
    totalRunnerMinutes: validWalls.reduce((sum, value) => sum + value, 0) / 60_000,
  }
}

export async function runApiPackingBenchmark({ sourceDir, discovery: rawDiscovery, observations: rawObservations, outputDir, platform = process.platform, git = null, runner = null, project = 'product/tests/AeroLink.Api.Tests/AeroLink.Api.Tests.csproj' }) {
  if (platform !== 'win32') throw new Error('The API packing benchmark is LOCAL WINDOWS only (process.platform must be win32).')
  if (!isAbsolute(sourceDir) || !existsSync(sourceDir)) throw new Error('sourceDir must be an existing absolute source directory.')
  if (!isAbsolute(outputDir)) throw new Error('outputDir must be an absolute owned temp directory.')
  const source = resolve(sourceDir)
  const output = resolve(outputDir)
  const temp = resolve(tmpdir())
  const outputRelativeToTemp = relative(temp, output)
  if (outputRelativeToTemp.startsWith('..') || outputRelativeToTemp.includes(':')) throw new Error('outputDir must be under the Windows temp directory.')
  const sourceRelative = relative(source, output)
  if (sourceRelative === '' || (!sourceRelative.startsWith('..') && !sourceRelative.includes(':'))) throw new Error('outputDir must be outside the source tree.')
  if (source.toLowerCase() === resolve('C:\\Sean Project\\AeroLink Production').toLowerCase()) throw new Error('Benchmark refuses the dedicated production source.')
  protectedDatabaseEnvironment(process.env)
  const discovery = inventoryFromInput(rawDiscovery)
  const identity = sourceIdentity(source, git)
  if (identity.commitSha !== discovery.commitSha || identity.treeSha !== discovery.treeSha) throw new Error('Source HEAD/tree does not match the supplied complete inventory.')
  const durationInput = durationsFromInput(rawObservations, discovery)
  const toolchain = toolchainSnapshot()
  const current = buildCurrentCountPlan(discovery, BENCHMARK_SHARD_COUNT)
  const proposed = buildDurationCandidatePlan(discovery, durationInput.weights, BENCHMARK_SHARD_COUNT)
  planCoverage(current, discovery, 'Current plan')
  planCoverage(proposed, discovery, 'Proposed plan')
  mkdirSync(output, { recursive: true })
  const environment = safeChildEnvironment(process.env)
  const cohorts = {
    current: await executeCohort({ label: 'current', plan: current, sourceDir: source, outputDir: output, project, environment, runner }),
    proposed: await executeCohort({ label: 'proposed', plan: proposed, sourceDir: source, outputDir: output, project, environment, runner }),
  }
  const afterIdentity = sourceIdentity(source, git)
  if (afterIdentity.commitSha !== identity.commitSha || afterIdentity.treeSha !== identity.treeSha) throw new Error('Source HEAD/tree changed during the benchmark.')
  const currentWall = cohorts.current.slowestShardWallMs
  const proposedWall = cohorts.proposed.slowestShardWallMs
  let verdict = 'insufficient-evidence'
  if (cohorts.current.success && cohorts.proposed.success && currentWall !== null && proposedWall !== null) {
    if (proposedWall < currentWall * 0.95) verdict = 'promising-but-not-proven'
    else if (proposedWall > currentWall * 1.05) verdict = 'regression'
    else verdict = 'no-measured-improvement'
  }
  return {
    schemaVersion: API_BENCHMARK_SCHEMA,
    mode: 'local-windows-diagnostic',
    source: { directory: source, commitSha: identity.commitSha, treeSha: identity.treeSha, cleanBefore: identity.dirty === false, cleanAfter: afterIdentity.dirty === false },
    toolchain,
    inventory: { digest: discovery.digest, testCount: discovery.tests.length, classCount: discovery.classes.length },
    configuration: {
      project,
      shardCount: BENCHMARK_SHARD_COUNT,
      sameInventory: true,
      ordinaryCiChanged: false,
      protectedGateEligible: false,
      durationObservation: {
        runId: durationInput.sourceRunId,
        provenance: durationInput.provenance,
        inventoryDigest: durationInput.inventoryDigest,
        sourceCommitSha: durationInput.sourceCommitSha,
        sourceTreeSha: durationInput.sourceTreeSha,
        metadataAuthenticated: durationInput.metadataAuthenticated,
        artifactReconciled: durationInput.artifactReconciled,
      },
      collectionTopology: 'unknown-unobserved; no regrouping inferred from VSTest output',
    },
    cohorts,
    comparison: {
      slowestShardWallMs: { current: currentWall, proposed: proposedWall },
      verdict,
      verdictScope: 'single controlled current/proposed pair; not an eight-per-configuration conclusion',
      sampleCounts: { current: 1, proposed: 1 },
      ordinaryPerformanceConclusionEligible: false,
      minimumRepresentativeSamplesPerConfiguration: 8,
      cohortOrder: ['current', 'proposed'],
      orderAndCacheConfounder: 'Current runs before proposed; process timing is retained, but this single pair does not remove cache or runner-order effects.',
      gateWallClaim: 'not-measured',
    },
    safety: { outputOwnedTemp: true, sourceOutsideProduction: true, childEnvironment: 'credentials-and-database connection variables removed; protected port refused', data: 'API tests own disposable SQLite databases per factory; no canonical database fallback' },
    limits: ['This is a local diagnostic experiment and does not enable duration packing.', 'Summed test durations and runner-minutes are observed process timings; they are not a whole-gate forecast.', 'A performance conclusion requires separately declared comparable cohorts and at least eight observations per configuration.', 'The three local shard processes share one machine; this is not equivalent to three hosted runners, and cold-cache equality is not guaranteed.', 'Current always runs before proposed; cache and order effects remain a limitation of this single pair.'],
  }
}

export function renderApiBenchmarkMarkdown(report) {
  const lines = ['# API packing benchmark', '', `- Mode: **${report.mode}**; source commit \`${report.source.commitSha}\`; tree \`${report.source.treeSha}\``, `- Toolchain: ${report.toolchain.platform}/${report.toolchain.arch}, OS ${report.toolchain.osRelease}, ${report.toolchain.cpuCount} logical CPUs, .NET ${report.toolchain.dotnetVersion ?? 'unavailable'}`, `- Inventory: ${report.inventory.testCount} tests across ${report.inventory.classCount} classes; digest \`${report.inventory.digest}\`; same inventory: **${report.configuration.sameInventory}**`, `- Current CI selection changed: **${report.configuration.ordinaryCiChanged}**; protected gate eligible: **${report.configuration.protectedGateEligible}**`, `- Verdict: **${report.comparison.verdict}**; complete gate timing: **not measured**`, '', '| Cohort | Setup/build | Slowest shard | Runner-minutes | Successful |', '|---|---:|---:|---:|---|']
  for (const label of ['current', 'proposed']) {
    const cohort = report.cohorts[label]
    lines.push(`| ${label} (${cohort.algorithm}) | ${cohort.setupMs} ms | ${cohort.slowestShardWallMs ?? '—'} ms | ${cohort.totalRunnerMinutes.toFixed(2)} | ${cohort.success} |`)
  }
  lines.push('', '## Shards', '', '| Cohort | Shard | Planned tests | Wall | Status | TRX reconciliation |', '|---|---:|---:|---:|---|---|')
  for (const label of ['current', 'proposed']) for (const shard of report.cohorts[label].shards) lines.push(`| ${label} | ${shard.shard} | ${shard.plannedTests} | ${shard.wallMs ?? '—'} ms | ${shard.status} | ${shard.reconciliation?.ok ? 'exact' : 'FAILED'} |`)
  lines.push('', '## Limits', '')
  for (const limit of report.limits) lines.push(`- ${limit}`)
  lines.push('')
  return lines.join('\n')
}
