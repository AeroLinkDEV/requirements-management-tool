// The FMS performance harness orchestrator (#1510 I0a, #1502 D10). Run with Node 24 from product/client:
//
//   node perf/run-perf.ts prepare  --work <dir> --sha A=<commit> --sha B=<commit>
//   node perf/run-perf.ts headless --out <dir> --arm A=<checkout> --arm B=<checkout> --blocks <n> [--workloads W1,W3]
//   node perf/run-perf.ts browser  --out <dir> --arm B=<checkout> --rounds <n> [--rates 1,64] [--configurations ...]
//   node perf/run-perf.ts report   --out <dir> [--pilot]
//
// Every admitted headless run is its own `playwright test --config=perf/playwright.perf.config.ts` invocation (a fresh process), taken
// in ABBA order for two arms after an unscored priming pass (headless, protocol revision 3), and kept whatever its
// outcome. Arm checkouts are detached worktrees at their SHAs; their client source, lock file and Vite config tree
// hashes are recorded with every run. Output holds no user paths, hostnames or environment values: environment
// variables are recorded by name only.

import { execFileSync, spawnSync } from 'node:child_process'
import { createHash } from 'node:crypto'
import { appendFileSync, existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from 'node:fs'
import { arch, cpus, platform, release, totalmem } from 'node:os'
import { join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { parseArgs } from 'node:util'
import { blockZeroCheck, failedRunRule, headlessSchedule, holm, judgeFamily, medianLevel, permutationTest, plannedRuns, primingPlan, relativeEffect, scoredRuns, type Arm, type RunValue, type SessionEvidence } from './stats.ts'
import { assertBrowserScoringAvailable, browserQualification, browserScoringRefusal } from './browser-accounting.ts'

const client = resolve(fileURLToPath(new URL('..', import.meta.url)))
const protocol = JSON.parse(readFileSync(join(client, 'perf', 'protocol.json'), 'utf8'))
const cli = join(client, 'node_modules', '@playwright', 'test', 'cli.js')

const { values: options, positionals } = parseArgs({
  allowPositionals: true,
  options: {
    out: { type: 'string' }, work: { type: 'string' }, arm: { type: 'string', multiple: true }, sha: { type: 'string', multiple: true },
    blocks: { type: 'string' }, rounds: { type: 'string' }, workloads: { type: 'string' }, rates: { type: 'string' },
    configurations: { type: 'string' }, hours: { type: 'string' }, pilot: { type: 'boolean' }, 'dirty-smoke': { type: 'boolean' },
  },
})

const git = (cwd: string, ...args: string[]) => execFileSync('git', ['-C', cwd, ...args], { encoding: 'utf8' }).trim()
const pairs = (list: string[] | undefined) => Object.fromEntries((list ?? []).map(entry => {
  const at = entry.indexOf('=')
  if (at < 1) throw new Error(`expected NAME=value, got ${entry}`)
  return [entry.slice(0, at), entry.slice(at + 1)]
})) as Record<string, string>

/** An arm's identity: commit and the tree hashes of what decides the client's behaviour. Refuses a dirty arm. */
function armIdentity(root: string) {
  const dirty = git(root, 'status', '--porcelain', '--', 'product/client/src', 'product/client/package-lock.json', 'product/client/vite.config.ts')
  if (dirty) throw new Error('an arm checkout has local changes under its client source, lock file or Vite config')
  return {
    commit: git(root, 'rev-parse', 'HEAD'),
    srcTree: git(root, 'rev-parse', 'HEAD:product/client/src'),
    lockBlob: git(root, 'rev-parse', 'HEAD:product/client/package-lock.json'),
    viteBlob: git(root, 'rev-parse', 'HEAD:product/client/vite.config.ts'),
  }
}

function harnessIdentity() {
  const root = git(client, 'rev-parse', '--show-toplevel')
  // The optional seed configuration starts the API with --no-build, so it runs the last harness build. The
  // commit and source tree are recorded with the hash of the built assembly; the hash identifies the binary, but it
  // cannot by itself prove that binary was built from that tree: build it from this checkout before a session.
  const release = join(root, 'product', 'src', 'AeroLink.Api', 'bin', 'Release')
  const framework = existsSync(release) ? readdirSync(release).sort().at(-1) : undefined
  const assembly = framework ? join(release, framework, 'AeroLink.Api.dll') : undefined
  return {
    commit: git(root, 'rev-parse', 'HEAD'), apiTree: git(root, 'rev-parse', 'HEAD:product/src'), dirtyEntries: git(root, 'status', '--porcelain').split('\n').filter(Boolean).length,
    apiAssemblySha256: assembly && existsSync(assembly) ? createHash('sha256').update(readFileSync(assembly)).digest('hex') : null,
  }
}

function busy(samples: ReturnType<typeof cpus>, later: ReturnType<typeof cpus>) {
  let idle = 0, total = 0
  later.forEach((cpu, i) => {
    const before = samples[i].times, after = cpu.times
    const delta = (key: keyof typeof after) => after[key] - before[key]
    idle += delta('idle'); total += delta('user') + delta('nice') + delta('sys') + delta('irq') + delta('idle')
  })
  return total ? (1 - idle / total) * 100 : 0
}

function wait(ms: number) { Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms) }

/** The quiet-host criterion (declared in protocol.json): sampled CPU busy %, never os.loadavg (zeros on Windows). */
function quietHost() {
  const { sampleSeconds, maxBusyPercent, maxWaitSeconds } = protocol.quietHost
  const started = Date.now()
  for (;;) {
    const before = cpus()
    wait(sampleSeconds * 1000)
    const busyPercent = busy(before, cpus())
    const waitedSeconds = (Date.now() - started) / 1000
    if (busyPercent <= maxBusyPercent || waitedSeconds >= maxWaitSeconds)
      return { busyPercent, waitedSeconds, quiet: busyPercent <= maxBusyPercent }
  }
}

function powerPlan() {
  if (platform() !== 'win32') return null
  try { return /\(([^)]+)\)\s*$/.exec(execFileSync('powercfg', ['/getactivescheme'], { encoding: 'utf8' }).trim())?.[1] ?? null } catch { return null }
}

function environment() {
  const cpu = cpus()
  return {
    node: process.versions.node, v8: process.versions.v8,
    playwright: JSON.parse(readFileSync(join(client, 'node_modules', '@playwright', 'test', 'package.json'), 'utf8')).version,
    os: { platform: platform(), release: release(), arch: arch() }, cpu: { model: cpu[0]?.model ?? null, logicalCores: cpu.length },
    memoryGiB: Math.round(totalmem() / 2 ** 30), powerPlan: powerPlan(),
    environmentVariableNames: Object.keys(process.env).sort(),
  }
}

function out() {
  if (!options.out) throw new Error('--out <dir> is required')
  const dir = resolve(options.out)
  mkdirSync(join(dir, 'runs'), { recursive: true })
  mkdirSync(join(dir, 'logs'), { recursive: true })
  return dir
}

type RunRecord = Record<string, unknown> & { index: number; status: 'passed' | 'failed'; result: Record<string, unknown> | null }

function nextIndex(dir: string) {
  const index = join(dir, 'runs.jsonl')
  return existsSync(index) ? readFileSync(index, 'utf8').split('\n').filter(Boolean).length : 0
}

/** One run: a fresh `playwright test` process. The record is appended whatever happens. */
function run(dir: string, record: Record<string, unknown>, env: Record<string, string>) {
  const index = nextIndex(dir)
  const name = `run-${String(index).padStart(4, '0')}`
  const resultPath = join(dir, 'runs', `${name}.json`)
  const quiet = quietHost()
  const before = cpus(), started = Date.now(), startedAt = new Date(started).toISOString()
  const child = spawnSync(process.execPath, [cli, 'test', '--config=perf/playwright.perf.config.ts'], {
    cwd: client, encoding: 'utf8', timeout: 40 * 60_000, maxBuffer: 64 * 1024 * 1024,
    env: { ...process.env, ...env, AEROLINK_PERF_RESULT: resultPath, AEROLINK_PERF_OUTPUT_DIR: join(dir, 'logs', name) },
  })
  const wallSeconds = (Date.now() - started) / 1000
  writeFileSync(join(dir, 'logs', `${name}.log`), `${child.stdout ?? ''}\n${child.stderr ?? ''}`)
  const result = existsSync(resultPath) ? JSON.parse(readFileSync(resultPath, 'utf8')) : null
  const entry: RunRecord = {
    index, ...record, startedAt, wallSeconds, exitCode: child.status, status: child.status === 0 && result ? 'passed' : 'failed',
    host: { quietBefore: quiet, busyPercentDuringRun: busy(before, cpus()), powerPlan: powerPlan() }, result,
  }
  appendFileSync(join(dir, 'runs.jsonl'), `${JSON.stringify(entry)}\n`)
  console.log(`${name} ${entry.status} ${JSON.stringify(record)} ${wallSeconds.toFixed(1)} s`)
  return entry
}

function arms() {
  const named = pairs(options.arm)
  const list = Object.entries(named).map(([arm, root]) => ({ arm, root: resolve(root), identity: armIdentity(resolve(root)) }))
  if (!list.length) throw new Error('--arm NAME=<checkout> is required')
  return list
}

function hoursCap() { return Number(options.hours ?? protocol.caps.hostHoursPerBaselineSet) }

function begin(dir: string, kind: string, extra: Record<string, unknown>) {
  // A run set is evidence of the committed harness only: a dirty harness tree refuses to start. --dirty-smoke overrides
  // that for a smoke run; the session records it, and the report never judges a run set that carries it.
  const harness = harnessIdentity()
  if (harness.dirtyEntries > 0 && !options['dirty-smoke'])
    throw new Error(`the harness tree has ${harness.dirtyEntries} uncommitted entries; commit them, or pass --dirty-smoke for a smoke run that is never judged`)
  const record = { kind, startedAt: new Date().toISOString(), harness, dirtyOverride: options['dirty-smoke'] === true, environment: environment(), protocol, ...extra }
  writeFileSync(join(dir, `session-${Date.now()}.json`), JSON.stringify(record, null, 2))
  return record
}

function headless() {
  const dir = out(), list = arms()
  if (list.length !== 2 || !list.some(a => a.arm === 'A') || !list.some(a => a.arm === 'B')) throw new Error('headless runs need --arm A=... and --arm B=...')
  const blocks = Number(options.blocks)
  const workloads = (options.workloads?.split(',') ?? protocol.headless.workloads.map((w: { id: string }) => w.id))
  const schedule = headlessSchedule(blocks, workloads, protocol.headless.topologies)
  const priming = primingPlan(workloads, protocol.headless.topologies)
  const session = begin(dir, 'headless', { arms: list.map(({ arm, identity }) => ({ arm, ...identity })), blocks, workloads, priming })
  const started = Date.now(), blockSeconds: number[] = [], primedRuns: { index: number; status: string }[] = []
  let blockStart = started, stopped: string | null = null
  for (const { primed, block, slot, arm: name, runs } of schedule) {
    // A failed priming run leaves the set not judged (judgeFamily) and is never retried, so the scored blocks would spend
    // the quiet window on a set that cannot be judged: the session ends after the priming pass and says why.
    if (!primed && slot === 0 && primedRuns.some(r => r.status !== 'passed')) {
      stopped = `priming run(s) ${primedRuns.filter(r => r.status !== 'passed').map(r => r.index).join(', ')} failed; no scored block was run, and the set is not judged`
      console.log(`stopped after the priming pass: ${stopped}`)
      break
    }
    if (!primed && slot % 4 === 0) {
      // A cap stops only between blocks, so every block kept is a whole ABBA block. The priming pass is not a block.
      const average = blockSeconds.length ? blockSeconds.reduce((a, b) => a + b, 0) / blockSeconds.length : 0
      if (block! > 0 && (Date.now() - started) / 1000 + average > hoursCap() * 3600) {
        stopped = `the ${hoursCap()} h cap after ${block} blocks`
        console.log(`stopped at ${stopped}`)
        break
      }
      blockStart = Date.now()
    }
    const arm = list.find(a => a.arm === name)!
    for (const { position, workload, topology } of runs) {
      const entry = run(dir, { kind: 'headless', primed, block, slot, position, arm: arm.arm, armIdentity: arm.identity, workload, topology }, {
        AEROLINK_PERF_MODE: 'headless', AEROLINK_PERF_ARM: join(arm.root, 'product', 'client'), AEROLINK_PERF_WORKLOAD: workload,
        AEROLINK_PERF_TOPOLOGY: topology, NODE_OPTIONS: `${process.env.NODE_OPTIONS ?? ''} --expose-gc`.trim(),
      })
      if (primed) primedRuns.push({ index: entry.index, status: entry.status })
    }
    if (!primed && slot % 4 === 3) blockSeconds.push((Date.now() - blockStart) / 1000)
  }
  writeFileSync(join(dir, `session-end-${Date.now()}.json`), JSON.stringify({ ...session, endedAt: new Date().toISOString(), harnessAtEnd: harnessIdentity(), primedRuns, stopped }, null, 2))
}

function browser() {
  const dir = out(), list = arms()
  if (list.length !== 1) throw new Error('browser runs take one arm (--arm B=<checkout>)')
  // Deliberate compatibility refusal before session/seeding/API work, independent of a timer-count assertion.
  const refusalPath = join(dir, 'browser-accounting-refusal.json')
  try {
    // Refusal evidence is immutable: a reused output directory must not replace the earlier arm/harness record.
    writeFileSync(refusalPath, JSON.stringify({ ...browserScoringRefusal(protocol),
      arm: { name: list[0].arm, ...list[0].identity }, harness: harnessIdentity() }, null, 2), { flag: 'wx' })
  } catch (error) {
    if (error instanceof Error && 'code' in error && error.code === 'EEXIST') {
      throw new Error(`FMS_BROWSER_REFUSAL_EVIDENCE_EXISTS: prior refusal evidence retained at ${refusalPath}; choose a fresh --out directory.`)
    }
    throw error
  }
  assertBrowserScoringAvailable(protocol)
}

function prepare() {
  if (!options.work) throw new Error('--work <dir> is required')
  const work = resolve(options.work), root = git(client, 'rev-parse', '--show-toplevel')
  for (const [arm, sha] of Object.entries(pairs(options.sha))) {
    const checkout = join(work, arm)
    if (!existsSync(checkout)) execFileSync('git', ['-C', root, '-c', 'core.longpaths=true', 'worktree', 'add', '--detach', checkout, sha], { stdio: 'inherit' })
    const clientDir = join(checkout, 'product', 'client')
    execFileSync(process.platform === 'win32' ? 'npm.cmd' : 'npm', ['ci', '--no-audit', '--no-fund'], { cwd: clientDir, stdio: 'inherit', shell: process.platform === 'win32' })
    execFileSync(process.platform === 'win32' ? 'npm.cmd' : 'npm', ['run', 'build'], { cwd: clientDir, stdio: 'inherit', shell: process.platform === 'win32' })
    console.log(`${arm}: ${JSON.stringify(armIdentity(checkout))}`)
  }
}

// ------------------------------------------------------------------------------------------------------ report

type Entry = RunRecord & { kind: string; primed?: boolean; block: number | null; arm: string; workload?: string; topology?: string; configuration?: string; rate?: number }

const measures: Record<string, (result: Record<string, unknown>) => number> = {
  p50: r => r.p50 as number, p95: r => r.p95 as number, p99: r => r.p99 as number, throughput: r => r.throughput as number,
  heap: r => r.heapUsedBytesAfterGc as number,
}
const width = (level: { halfWidthPct: number | null }, digits: number) => level.halfWidthPct === null ? 'n/a' : `${level.halfWidthPct.toFixed(digits)}%`
const pct = (value: number) => `${value >= 0 ? '+' : ''}${(value * 100).toFixed(2)}%`
const fixed = (value: number, digits = 3) => Number.isFinite(value) ? value.toFixed(digits) : 'n/a'

function report() {
  const dir = out()
  const recorded: Entry[] = readFileSync(join(dir, 'runs.jsonl'), 'utf8').split('\n').filter(Boolean).map(line => JSON.parse(line))
  // Priming runs (protocol revision 3) are counted here and never analysed: every table below uses the scored runs only.
  const entries = scoredRuns(recorded), priming = recorded.filter(e => !entries.includes(e))
  const { iterations, seed, targetHalfWidthPp, aaFamilyAlpha, aaMeasures, permutations } = protocol.statistics
  const stats = { iterations, seed }
  const testing = { permutations, seed }
  // A run set is judged only when every one of its sessions recorded exactly these statistics (judgeFamily). Sessions
  // declared under revision 1 (a bootstrap p) keep their recorded verdict; the test here is then descriptive context.
  // Each session is also checked against protocol.json as committed at its harness commit, and against its end record.
  const records = (pattern: RegExp) => readdirSync(dir).filter(name => pattern.test(name)).map(name => JSON.parse(readFileSync(join(dir, name), 'utf8')))
  const ends = records(/^session-end-\d+\.json$/)
  const root = git(client, 'rev-parse', '--show-toplevel')
  const committed = (commit: unknown) => {
    if (typeof commit !== 'string' || !/^[0-9a-f]{40}$/.test(commit)) return null
    try { return JSON.parse(git(root, 'show', `${commit}:product/client/perf/protocol.json`)).statistics ?? null } catch { return null }
  }
  const sessions: SessionEvidence[] = records(/^session-\d+\.json$/).map(session => {
    const end = ends.find(record => record.startedAt === session.startedAt && record.kind === session.kind)
    return { ...session, committedStatistics: committed(session.harness?.commit), end: end?.harnessAtEnd ?? null, primedRuns: end?.primedRuns ?? null }
  })
  const declaredFamilySize = protocol.headless.workloads.length * protocol.headless.topologies.length * aaMeasures.length
  const lines: string[] = []
  const json: Record<string, unknown> = {
    schema: 'aerolink.fms-perf-report.v1', protocol, runs: entries.length, failedRuns: entries.filter(e => e.status !== 'passed').length,
    primedRuns: priming.length, failedPrimedRuns: priming.filter(e => e.status !== 'passed').length,
  }
  const passed = entries.filter(e => e.status === 'passed' && e.result)
  const loud = (list: Entry[]) => list.filter(e => !(e.host as { quietBefore: { quiet: boolean } }).quietBefore.quiet).length
  lines.push(`Runs: ${entries.length} recorded non-priming (${json.failedRuns} failed, all kept); runs that started on a host above the quiet threshold: ${loud(entries)}. Browser records below are not qualified for scoring.`,
    `Priming runs: ${priming.length} (${json.failedPrimedRuns} failed; ${loud(priming)} started on a host above the quiet threshold), recorded and never analysed: no table, test, interval or failed-run exclusion below uses them.`,
    ...ends.filter(end => end.stopped).map(end => `Session ${end.startedAt} stopped early: ${end.stopped}.`), '')

  const head = passed.filter(e => e.kind === 'headless')
  if (head.length) {
    const cells = [...new Set(head.map(e => `${e.workload}|${e.topology}`))]
    const aa: { cell: string; measure: string; rows: Entry[]; effect: ReturnType<typeof relativeEffect>; test: ReturnType<typeof permutationTest> }[] = []
    const planned: Record<string, number> = {}
    const excluded: string[] = []
    for (const cell of cells) {
      const [workload, topology] = cell.split('|')
      // Declared rule (protocol.json statistics.failedRun): a block in which any run of this cell failed is excluded
      // whole from this cell's analysis, both arms, and the exclusion is reported.
      const { rows, excludedBlocks } = failedRunRule(recorded, e => e.kind === 'headless' && e.workload === workload && e.topology === topology)
      excludedBlocks.forEach(block => excluded.push(`${cell.replace('|', ' ')} block ${block}`))
      for (const measure of [...aaMeasures, 'heap']) {
        const runs: RunValue[] = rows.map(e => ({ block: e.block!, arm: e.arm as Arm, value: measures[measure](e.result!) }))
        if (!runs.some(r => r.arm === 'A') || !runs.some(r => r.arm === 'B')) continue
        const effect = relativeEffect(runs, stats)
        aa.push({ cell, measure, rows, effect, test: permutationTest(runs, testing) })
        if (aaMeasures.includes(measure)) {
          const perArm = rows.filter(e => e.arm === 'A').length
          planned[cell] = Math.max(planned[cell] ?? 0, plannedRuns({ halfWidthPp: effect.halfWidthPp, runsPerArm: perArm }, targetHalfWidthPp, protocol.caps.headlessRunsPerArmPerCell))
        }
      }
    }
    const family = aa.filter(row => aaMeasures.includes(row.measure))
    const adjusted = holm(family.map(row => row.test.p))
    const smallest = family.length ? Math.max(...family.map(row => row.test.smallestP)) : Infinity
    const judged = judgeFamily({ pilot: options.pilot === true, sessions, statistics: protocol.statistics, topologies: protocol.headless.topologies, adjusted, smallestP: family.map(row => row.test.smallestP), alpha: aaFamilyAlpha, declaredFamilySize })
    const significant = { length: judged.significant }
    const verdict = `${judged.verdict}${judged.reason ? `: ${judged.reason}` : ''}`
    lines.push(`## Headless A/B (B/A - 1 of arm medians; within-block permutation test, R = ${permutations}; Holm across ${family.length} comparisons)`, '',
      `A/A result: **${verdict}** (family-wise alpha ${aaFamilyAlpha}; ${significant.length} significant after Holm; smallest attainable p ${smallest.toPrecision(3)}, alpha / m = ${(aaFamilyAlpha / declaredFamilySize).toPrecision(3)}; ${family.length} of ${declaredFamilySize} declared comparisons present).`, '',
      `Blocks excluded for a failed run: ${excluded.length ? excluded.join('; ') : 'none'}. The 95% intervals are descriptive bootstrap intervals (k - 1 draws within blocks), not the test.`, '',
      '| Workload | Topology | Measure | runs A/B | median A | median B | B/A - 1 | 95% CI (descriptive) | half-width (pp) | permutation p | Holm p |', '|---|---|---|---|---|---|---|---|---|---|---|')
    for (const row of aa) {
      const [workload, topology] = row.cell.split('|')
      // The rows the test used: the failed-run rule already applied.
      const rows = row.rows
      const level = (arm: string) => medianLevel(rows.filter(e => e.arm === arm).map(e => measures[row.measure](e.result!)), stats).estimate
      const i = family.indexOf(row)
      lines.push(`| ${workload} | ${topology} | ${row.measure} | ${rows.filter(e => e.arm === 'A').length}/${rows.filter(e => e.arm === 'B').length} | ${fixed(level('A'))} | ${fixed(level('B'))} | ${pct(row.effect.estimate)} | ${pct(row.effect.low)} to ${pct(row.effect.high)} | ${row.effect.halfWidthPp.toFixed(2)} | ${row.test.p.toFixed(4)} | ${i >= 0 ? adjusted[i].toFixed(4) : 'descriptive'} |`)
    }
    lines.push('', 'Frame times in ms; throughput in simulated s per wall s; heap in bytes after GC.', '')

    // Descriptive only (#1536): does block 0 still stand apart once the priming pass has run? Same rows as the test.
    const zero = family.map(row => ({ cell: row.cell, measure: row.measure, ...blockZeroCheck(row.rows.map(e => ({ block: e.block!, arm: e.arm as Arm, value: measures[row.measure](e.result!) }))) }))
    lines.push('### Block-0 check (descriptive, not a test)', '', 'd = B/A - 1 of one block\'s arm medians. A cold start left in the set shows as a block-0 d far outside the later blocks\' RMS.', '',
      '| Workload | Topology | Measure | block 0 d | RMS d, later blocks | later blocks | abs(d0) / RMS |', '|---|---|---|---|---|---|---|',
      ...zero.map(row => `| ${row.cell.replace('|', ' | ')} | ${row.measure} | ${row.d0 === null ? 'n/a' : pct(row.d0)} | ${row.rmsLater === null ? 'n/a' : `${(row.rmsLater * 100).toFixed(2)}%`} | ${row.laterBlocks} | ${row.d0 !== null && row.rmsLater ? (Math.abs(row.d0) / row.rmsLater).toFixed(2) : 'n/a'} |`), '')
    json.blockZero = zero
    if (options.pilot) {
      lines.push('### Pilot: runs per arm per cell for a 1 pp half-width (capped)', '', '| Cell | planned N |', '|---|---|', ...Object.entries(planned).map(([cell, n]) => `| ${cell.replace('|', ' ')} | ${n} |`), '')
      json.pilotPlanned = planned
    }
    json.headless = { aa: aa.map(({ rows: _rows, ...row }) => ({ ...row, holm: family.findIndex(f => f.cell === row.cell && f.measure === row.measure) >= 0 ? adjusted[family.findIndex(f => f.cell === row.cell && f.measure === row.measure)] : null })), verdict: judged, excluded, smallestAttainableP: smallest }

    // Comparison 5: single -> dual on arm B (current main), blocked by the same ABBA blocks. The failed-run rule applies
    // here too: a block in which any arm-B run of the workload failed (either topology) is excluded for that workload.
    const mainRuns = head.filter(e => e.arm === 'B')
    const workloads = [...new Set(mainRuns.map(e => e.workload!))]
    const comparison: Record<string, unknown>[] = []
    const excluded5: string[] = []
    lines.push('## Comparison 5: single -> dual topology (arm B; reference single)', '', '| Workload | Measure | single | dual | dual/single - 1 | 95% CI (descriptive) | permutation p | over 5%? |', '|---|---|---|---|---|---|---|---|')
    for (const workload of workloads) {
      const { rows, excludedBlocks } = failedRunRule(recorded, e => e.kind === 'headless' && e.arm === 'B' && e.workload === workload)
      excludedBlocks.forEach(block => excluded5.push(`${workload} block ${block}`))
      for (const measure of ['p50', 'p95', 'p99', 'throughput']) {
        const runs: RunValue[] = rows.map(e => ({ block: e.block!, arm: e.topology === 'single' ? 'A' : 'B', value: measures[measure](e.result!) }))
        if (!runs.some(r => r.arm === 'A') || !runs.some(r => r.arm === 'B')) continue
        const effect = relativeEffect(runs, stats), test = permutationTest(runs, testing)
        const level = (arm: Arm) => medianLevel(runs.filter(r => r.arm === arm).map(r => r.value), stats).estimate
        const over = measure === 'throughput' ? effect.estimate < -0.05 : effect.estimate > 0.05
        comparison.push({ workload, measure, single: level('A'), dual: level('B'), effect, test, over })
        lines.push(`| ${workload} | ${measure} | ${fixed(level('A'))} | ${fixed(level('B'))} | ${pct(effect.estimate)} | ${pct(effect.low)} to ${pct(effect.high)} | ${test.p.toPrecision(3)} | ${over ? '**yes**' : 'no'} |`)
      }
    }
    json.comparison5 = { rows: comparison, excluded: excluded5 }
    lines.push('', `Blocks excluded for a failed run: ${excluded5.length ? excluded5.join('; ') : 'none'}.`, '')

    // Absolute baselines and the dual topology's SYNC record, arm B. These levels are not blocked (one group of runs),
    // so a failed run leaves no degenerate stratum: they use every passed run, and the runs column shows the count.
    lines.push('## Absolute baselines (arm B; median over runs, 95% CI, half-width as % of median)', '', '| Workload | Topology | runs | p50 ms | p95 ms | p99 ms | throughput | heap MiB | SYNC residency | first SYNC drop |', '|---|---|---|---|---|---|---|---|---|---|')
    for (const cell of cells) {
      const [workload, topology] = cell.split('|')
      const rows = mainRuns.filter(e => e.workload === workload && e.topology === topology)
      if (!rows.length) continue
      const show = (measure: string, scale = 1) => {
        const level = medianLevel(rows.map(e => measures[measure](e.result!) / scale), stats)
        return `${fixed(level.estimate)} [${fixed(level.low)}, ${fixed(level.high)}] (±${width(level, 2)})`
      }
      const sync = rows[0].result!.sync as { residency: number; firstDrop: { simSeconds: number; reason: string | null } | null } | null
      const drops = [...new Set(rows.map(e => JSON.stringify((e.result!.sync as typeof sync)?.firstDrop ?? null)))]
      lines.push(`| ${workload} | ${topology} | ${rows.length} | ${show('p50')} | ${show('p95')} | ${show('p99')} | ${show('throughput')} | ${show('heap', 2 ** 20)} | ${sync ? sync.residency.toFixed(4) : 'n/a'} | ${sync ? drops.map(d => d === 'null' ? 'none' : `${JSON.parse(d).simSeconds} s ${JSON.parse(d).reason ?? ''}`).join('; ') : 'n/a'} |`)
    }
    lines.push('')
  }

  const web = recorded.filter(e => e.kind === 'browser')
  if (web.length) {
    const qualification = browserQualification(protocol, web)
    json.browserQualification = qualification
    lines.push('## Browser accounting: NOT_QUALIFIED', '', qualification.reason,
      `${web.length} browser records remain in their original input files and identities. Historical interval-inferred metrics are not rescored by this report. No browser levels, confidence intervals, planned sample count or budget verdict is emitted. Headless analysis is separate.`, '')
  }
  writeFileSync(join(dir, 'report.md'), `${lines.join('\n')}\n`)
  writeFileSync(join(dir, 'report.json'), JSON.stringify(json, null, 2))
  console.log(lines.join('\n'))
}

const commands: Record<string, () => void> = { prepare, headless, browser, report }
const command = commands[positionals[0] ?? '']
if (!command) throw new Error(`usage: node perf/run-perf.ts <${Object.keys(commands).join('|')}> ...`)
command()
