// The FMS performance harness orchestrator (#1510 I0a, #1502 D10). Run with Node 24 from product/client:
//
//   node perf/run-perf.ts prepare  --work <dir> --sha A=<commit> --sha B=<commit>
//   node perf/run-perf.ts headless --out <dir> --arm A=<checkout> --arm B=<checkout> --blocks <n> [--workloads W1,W3]
//   node perf/run-perf.ts browser  --out <dir> --arm B=<checkout> --rounds <n> [--rates 1,64] [--configurations ...]
//   node perf/run-perf.ts report   --out <dir> [--pilot]
//
// Every run is its own `playwright test --config=perf/playwright.perf.config.ts` invocation (a fresh process), taken
// in ABBA order for two arms, and kept whatever its outcome. Arm checkouts are detached worktrees at their SHAs; their
// client source, lock file and Vite config tree hashes are recorded with every run. Output holds no user paths,
// hostnames or environment values: environment variables are recorded by name only.

import { execFileSync, spawnSync } from 'node:child_process'
import { appendFileSync, cpSync, existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from 'node:fs'
import { arch, cpus, platform, release, totalmem } from 'node:os'
import { join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { parseArgs } from 'node:util'
import { abbaOrder, holm, medianLevel, plannedRuns, relativeEffect, type Arm, type RunValue } from './stats.ts'
// @ts-expect-error a JavaScript module without declarations
import { browserStoragePath, createBrowserStorage, removeBrowserStorage } from '../scripts/browser-storage.mjs'

const client = resolve(fileURLToPath(new URL('..', import.meta.url)))
const protocol = JSON.parse(readFileSync(join(client, 'perf', 'protocol.json'), 'utf8'))
const cli = join(client, 'node_modules', '@playwright', 'test', 'cli.js')

const { values: options, positionals } = parseArgs({
  allowPositionals: true,
  options: {
    out: { type: 'string' }, work: { type: 'string' }, arm: { type: 'string', multiple: true }, sha: { type: 'string', multiple: true },
    blocks: { type: 'string' }, rounds: { type: 'string' }, workloads: { type: 'string' }, rates: { type: 'string' },
    configurations: { type: 'string' }, hours: { type: 'string' }, pilot: { type: 'boolean' },
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
  return { commit: git(root, 'rev-parse', 'HEAD'), apiTree: git(root, 'rev-parse', 'HEAD:product/src'), dirtyEntries: git(root, 'status', '--porcelain').split('\n').filter(Boolean).length }
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
  const record = { kind, startedAt: new Date().toISOString(), harness: harnessIdentity(), environment: environment(), protocol, ...extra }
  writeFileSync(join(dir, `session-${Date.now()}.json`), JSON.stringify(record, null, 2))
  return record
}

function headless() {
  const dir = out(), list = arms()
  if (list.length !== 2 || !list.some(a => a.arm === 'A') || !list.some(a => a.arm === 'B')) throw new Error('headless runs need --arm A=... and --arm B=...')
  const blocks = Number(options.blocks)
  const workloads = (options.workloads?.split(',') ?? protocol.headless.workloads.map((w: { id: string }) => w.id))
  const session = begin(dir, 'headless', { arms: list.map(({ arm, identity }) => ({ arm, ...identity })), blocks, workloads })
  const order = abbaOrder(blocks), started = Date.now(), blockSeconds: number[] = []
  let blockStart = started
  for (let slot = 0; slot < order.length; slot += 1) {
    const block = Math.floor(slot / 4)
    if (slot % 4 === 0) {
      // A cap stops only between blocks, so every block kept is a whole ABBA block.
      const average = blockSeconds.length ? blockSeconds.reduce((a, b) => a + b, 0) / blockSeconds.length : 0
      if (block > 0 && (Date.now() - started) / 1000 + average > hoursCap() * 3600) { console.log(`stopped at the ${hoursCap()} h cap after ${block} blocks`); break }
      blockStart = Date.now()
    }
    const arm = list.find(a => a.arm === order[slot])!
    // Topology order alternates by slot, so neither topology always runs first.
    const topologies = slot % 2 ? ['dual', 'single'] : ['single', 'dual']
    let position = 0
    for (const workload of workloads) for (const topology of topologies) {
      run(dir, { kind: 'headless', block, slot, position: position++, arm: arm.arm, armIdentity: arm.identity, workload, topology }, {
        AEROLINK_PERF_MODE: 'headless', AEROLINK_PERF_ARM: join(arm.root, 'product', 'client'), AEROLINK_PERF_WORKLOAD: workload,
        AEROLINK_PERF_TOPOLOGY: topology, NODE_OPTIONS: `${process.env.NODE_OPTIONS ?? ''} --expose-gc`.trim(),
      })
    }
    if (slot % 4 === 3) blockSeconds.push((Date.now() - blockStart) / 1000)
  }
  writeFileSync(join(dir, `session-end-${Date.now()}.json`), JSON.stringify({ ...session, endedAt: new Date().toISOString(), harnessAtEnd: harnessIdentity() }, null, 2))
}

function browser() {
  const dir = out(), list = arms()
  if (list.length !== 1) throw new Error('browser runs take one arm (--arm B=<checkout>)')
  const [arm] = list
  const dist = join(arm.root, 'product', 'client', 'dist')
  const rounds = Number(options.rounds)
  const rates: number[] = options.rates?.split(',').map(Number) ?? protocol.browser.rates
  const configurations: string[] = options.configurations?.split(',') ?? protocol.browser.configurations.map((c: { id: string }) => c.id)
  const cells = configurations.flatMap(configuration => rates.map(rate => ({ configuration, rate })))
  const session = begin(dir, 'browser', { arms: [{ arm: arm.arm, ...arm.identity }], rounds, cells })

  // One seeded template database; every run's fresh API process gets its own copy of it.
  const templateId = `perf-template-${Date.now()}`
  const seedPath = join(dir, 'seed.json')
  const seed = spawnSync(process.execPath, [cli, 'test', '--config=perf/playwright.perf.config.ts'], {
    cwd: client, encoding: 'utf8', timeout: 20 * 60_000, maxBuffer: 64 * 1024 * 1024,
    env: { ...process.env, AEROLINK_PERF_MODE: 'seed', AEROLINK_PERF_DIST: dist, AEROLINK_E2E_RUN_ID: templateId, AEROLINK_PERF_RESULT: seedPath, AEROLINK_PERF_OUTPUT_DIR: join(dir, 'logs', 'seed') },
  })
  writeFileSync(join(dir, 'logs', 'seed.log'), `${seed.stdout ?? ''}\n${seed.stderr ?? ''}`)
  if (seed.status !== 0 || !existsSync(seedPath)) throw new Error('seeding the template database failed; see logs/seed.log')
  const template = browserStoragePath(templateId)

  const started = Date.now(), roundSeconds: number[] = []
  try {
    for (let round = 0; round < rounds; round += 1) {
      if (round > 0) {
        const average = roundSeconds.reduce((a, b) => a + b, 0) / roundSeconds.length
        if ((Date.now() - started) / 1000 + average > hoursCap() * 3600) { console.log(`stopped at the ${hoursCap()} h cap after ${round} rounds`); break }
      }
      const roundStart = Date.now()
      // Each round starts one cell later, and odd rounds run backwards, so no cell always runs first or last.
      const rotated = cells.map((_, i) => cells[(i + round) % cells.length])
      const order = round % 2 ? rotated.reverse() : rotated
      order.forEach(({ configuration, rate }, position) => {
        const runId = `perf-${Date.now()}-${round}-${position}`
        const storage = createBrowserStorage(runId)
        for (const file of readdirSync(template)) if (file.startsWith('aerolink.db')) cpSync(join(template, file), join(storage.root, file))
        cpSync(join(template, 'evidence'), storage.evidence, { recursive: true })
        try {
          run(dir, { kind: 'browser', block: round, round, position, arm: arm.arm, armIdentity: arm.identity, configuration, rate }, {
            AEROLINK_PERF_MODE: 'browser', AEROLINK_PERF_DIST: dist, AEROLINK_E2E_RUN_ID: runId,
            AEROLINK_SHOWCASE_SEED: readFileSync(seedPath, 'utf8'), AEROLINK_PERF_RATE: String(rate), AEROLINK_PERF_CONFIGURATION: configuration,
          })
        } finally { removeBrowserStorage(runId) }
      })
      roundSeconds.push((Date.now() - roundStart) / 1000)
    }
  } finally { removeBrowserStorage(templateId) }
  writeFileSync(join(dir, `session-end-${Date.now()}.json`), JSON.stringify({ ...session, endedAt: new Date().toISOString(), harnessAtEnd: harnessIdentity() }, null, 2))
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

type Entry = RunRecord & { kind: string; block: number; arm: string; workload?: string; topology?: string; configuration?: string; rate?: number }

const measures: Record<string, (result: Record<string, unknown>) => number> = {
  p50: r => r.p50 as number, p95: r => r.p95 as number, p99: r => r.p99 as number, throughput: r => r.throughput as number,
  heap: r => r.heapUsedBytesAfterGc as number,
}
const browserMeasures: Record<string, (result: Record<string, any>) => number> = {
  'callback p50': r => r.callbacks.p50, 'callback p95': r => r.callbacks.p95, 'callback p99': r => r.callbacks.p99,
  throughput: r => r.throughput, 'long tasks': r => r.longTasks.count, 'rAF interval p95': r => r.raf?.p95 ?? NaN,
  'interaction p50': r => interactionDurations(r)[0], 'interaction max': r => interactionDurations(r)[1], heap: r => r.heapUsedBytesAfterGc,
}

/** Event Timing per interaction (interactionId > 0): the longest entry of each; returns [median, max] over interactions. */
function interactionDurations(result: Record<string, any>): [number, number] {
  const longest = new Map<number, number>()
  for (const entry of result.interaction.entries) if (entry.interaction > 0) longest.set(entry.interaction, Math.max(longest.get(entry.interaction) ?? 0, entry.duration))
  const values = [...longest.values()].sort((a, b) => a - b)
  return values.length ? [values[Math.floor((values.length - 1) / 2)], values[values.length - 1]] : [NaN, NaN]
}

const pct = (value: number) => `${value >= 0 ? '+' : ''}${(value * 100).toFixed(2)}%`
const fixed = (value: number, digits = 3) => Number.isFinite(value) ? value.toFixed(digits) : 'n/a'

function report() {
  const dir = out()
  const entries: Entry[] = readFileSync(join(dir, 'runs.jsonl'), 'utf8').split('\n').filter(Boolean).map(line => JSON.parse(line))
  const { iterations, seed, targetHalfWidthPp, aaFamilyAlpha, aaMeasures } = protocol.statistics
  const stats = { iterations, seed }
  const lines: string[] = []
  const json: Record<string, unknown> = { schema: 'aerolink.fms-perf-report.v1', protocol, runs: entries.length, failedRuns: entries.filter(e => e.status !== 'passed').length }
  const passed = entries.filter(e => e.status === 'passed' && e.result)
  const notQuiet = entries.filter(e => !(e.host as { quietBefore: { quiet: boolean } }).quietBefore.quiet).length
  lines.push(`Runs: ${entries.length} (${json.failedRuns} failed, all kept); runs that started on a host above the quiet threshold: ${notQuiet}.`, '')

  const head = passed.filter(e => e.kind === 'headless')
  if (head.length) {
    const cells = [...new Set(head.map(e => `${e.workload}|${e.topology}`))]
    const aa: { cell: string; measure: string; effect: ReturnType<typeof relativeEffect> }[] = []
    const planned: Record<string, number> = {}
    for (const cell of cells) {
      const [workload, topology] = cell.split('|')
      const rows = head.filter(e => e.workload === workload && e.topology === topology)
      for (const measure of [...aaMeasures, 'heap']) {
        const runs: RunValue[] = rows.map(e => ({ block: e.block, arm: e.arm as Arm, value: measures[measure](e.result!) }))
        if (!runs.some(r => r.arm === 'A') || !runs.some(r => r.arm === 'B')) continue
        const effect = relativeEffect(runs, stats)
        aa.push({ cell, measure, effect })
        if (aaMeasures.includes(measure)) {
          const perArm = rows.filter(e => e.arm === 'A').length
          planned[cell] = Math.max(planned[cell] ?? 0, plannedRuns({ halfWidthPp: effect.halfWidthPp, runsPerArm: perArm }, targetHalfWidthPp, protocol.caps.headlessRunsPerArmPerCell))
        }
      }
    }
    const family = aa.filter(row => aaMeasures.includes(row.measure))
    const adjusted = holm(family.map(row => row.effect.p))
    const significant = family.filter((_, i) => adjusted[i] <= aaFamilyAlpha)
    const verdict = options.pilot ? 'pilot (not judged)' : significant.length ? 'FAILED' : 'passed'
    lines.push(`## Headless A/B (B/A - 1 of arm medians; 95% CI unadjusted; Holm across ${family.length} comparisons)`, '', `A/A result: **${verdict}** (family-wise alpha ${aaFamilyAlpha}; ${significant.length} significant after Holm).`, '',
      '| Workload | Topology | Measure | runs A/B | median A | median B | B/A - 1 | 95% CI | half-width (pp) | p | Holm p |', '|---|---|---|---|---|---|---|---|---|---|---|')
    for (const row of aa) {
      const [workload, topology] = row.cell.split('|')
      const rows = head.filter(e => e.workload === workload && e.topology === topology)
      const level = (arm: string) => medianLevel(rows.filter(e => e.arm === arm).map(e => ({ block: e.block, value: measures[row.measure](e.result!) })), stats).estimate
      const i = family.indexOf(row)
      lines.push(`| ${workload} | ${topology} | ${row.measure} | ${rows.filter(e => e.arm === 'A').length}/${rows.filter(e => e.arm === 'B').length} | ${fixed(level('A'))} | ${fixed(level('B'))} | ${pct(row.effect.estimate)} | ${pct(row.effect.low)} to ${pct(row.effect.high)} | ${row.effect.halfWidthPp.toFixed(2)} | ${row.effect.p.toFixed(4)} | ${i >= 0 ? adjusted[i].toFixed(4) : 'descriptive'} |`)
    }
    lines.push('', 'Frame times in ms; throughput in simulated s per wall s; heap in bytes after GC.', '')
    if (options.pilot) {
      lines.push('### Pilot: runs per arm per cell for a 1 pp half-width (capped)', '', '| Cell | planned N |', '|---|---|', ...Object.entries(planned).map(([cell, n]) => `| ${cell.replace('|', ' ')} | ${n} |`), '')
      json.pilotPlanned = planned
    }
    json.headless = { aa: aa.map((row) => ({ ...row, holm: family.includes(row) ? adjusted[family.indexOf(row)] : null })), verdict }

    // Comparison 5: single -> dual on arm B (current main), blocked by the same ABBA blocks.
    const mainRuns = head.filter(e => e.arm === 'B')
    const workloads = [...new Set(mainRuns.map(e => e.workload!))]
    const comparison: Record<string, unknown>[] = []
    lines.push('## Comparison 5: single -> dual topology (arm B; reference single)', '', '| Workload | Measure | single | dual | dual/single - 1 | 95% CI | over 5%? |', '|---|---|---|---|---|---|---|')
    for (const workload of workloads) for (const measure of ['p50', 'p95', 'p99', 'throughput']) {
      const runs: RunValue[] = mainRuns.filter(e => e.workload === workload).map(e => ({ block: e.block, arm: e.topology === 'single' ? 'A' : 'B', value: measures[measure](e.result!) }))
      if (!runs.some(r => r.arm === 'A') || !runs.some(r => r.arm === 'B')) continue
      const effect = relativeEffect(runs, stats)
      const level = (arm: Arm) => medianLevel(runs.filter(r => r.arm === arm), stats).estimate
      const over = measure === 'throughput' ? effect.estimate < -0.05 : effect.estimate > 0.05
      comparison.push({ workload, measure, single: level('A'), dual: level('B'), effect, over })
      lines.push(`| ${workload} | ${measure} | ${fixed(level('A'))} | ${fixed(level('B'))} | ${pct(effect.estimate)} | ${pct(effect.low)} to ${pct(effect.high)} | ${over ? '**yes**' : 'no'} |`)
    }
    json.comparison5 = comparison
    lines.push('')

    // Absolute baselines and the dual topology's SYNC record, arm B.
    lines.push('## Absolute baselines (arm B; median over runs, 95% CI, half-width as % of median)', '', '| Workload | Topology | runs | p50 ms | p95 ms | p99 ms | throughput | heap MiB | SYNC residency | first SYNC drop |', '|---|---|---|---|---|---|---|---|---|---|')
    for (const cell of cells) {
      const [workload, topology] = cell.split('|')
      const rows = mainRuns.filter(e => e.workload === workload && e.topology === topology)
      if (!rows.length) continue
      const show = (measure: string, scale = 1) => {
        const level = medianLevel(rows.map(e => ({ block: e.block, value: measures[measure](e.result!) / scale })), stats)
        return `${fixed(level.estimate)} [${fixed(level.low)}, ${fixed(level.high)}] (±${level.halfWidthPct.toFixed(2)}%)`
      }
      const sync = rows[0].result!.sync as { residency: number; firstDrop: { simSeconds: number; reason: string | null } | null } | null
      const drops = [...new Set(rows.map(e => JSON.stringify((e.result!.sync as typeof sync)?.firstDrop ?? null)))]
      lines.push(`| ${workload} | ${topology} | ${rows.length} | ${show('p50')} | ${show('p95')} | ${show('p99')} | ${show('throughput')} | ${show('heap', 2 ** 20)} | ${sync ? sync.residency.toFixed(4) : 'n/a'} | ${sync ? drops.map(d => d === 'null' ? 'none' : `${JSON.parse(d).simSeconds} s ${JSON.parse(d).reason ?? ''}`).join('; ') : 'n/a'} |`)
    }
    lines.push('')
  }

  const web = passed.filter(e => e.kind === 'browser')
  if (web.length) {
    const cells = [...new Set(web.map(e => `${e.configuration}|${e.rate}`))]
    lines.push('## Browser matrix (87n-offshore-sar; median over runs, 95% CI; half-width as % of median)', '',
      `| Configuration | Rate | runs | ${Object.keys(browserMeasures).join(' | ')} |`, `|---|---|---|${Object.keys(browserMeasures).map(() => '---').join('|')}|`)
    const browserJson: Record<string, unknown>[] = []
    const plannedBrowser: Record<string, number> = {}
    for (const cell of cells) {
      const [configuration, rate] = cell.split('|')
      const rows = web.filter(e => e.configuration === configuration && String(e.rate) === rate)
      const levels = Object.fromEntries(Object.entries(browserMeasures).map(([name, take]) => {
        const values = rows.map(e => ({ block: e.block, value: take(e.result!) })).filter(v => Number.isFinite(v.value))
        return [name, values.length ? medianLevel(values, stats) : null]
      }))
      browserJson.push({ configuration, rate: Number(rate), runs: rows.length, levels })
      plannedBrowser[cell] = Math.max(...['callback p95', 'throughput'].map(name => levels[name]
        ? plannedRuns({ halfWidthPp: levels[name]!.halfWidthPct, runsPerArm: rows.length }, targetHalfWidthPp, protocol.caps.browserRunsPerCell) : 0))
      lines.push(`| ${configuration} | ${rate}x | ${rows.length} | ${Object.keys(browserMeasures).map(name => {
        const level = levels[name]; const scale = name === 'heap' ? 2 ** 20 : 1
        return level ? `${fixed(level.estimate / scale, 1)} [${fixed(level.low / scale, 1)}, ${fixed(level.high / scale, 1)}] (±${level.halfWidthPct.toFixed(1)}%)` : 'n/a'
      }).join(' | ')} |`)
    }
    const sample = web[0].result as Record<string, any>
    const steps = [...new Set(web.map(e => (e.result as Record<string, any>).interaction.resolutionStepMs))]
    lines.push('', `Callback and interaction times in ms (interaction = Event Timing, key -> next paint, longest entry per interaction; observed duration step ${steps.join('/')} ms; exempt from the 1% target). Throughput in simulated s per wall s (paced: the bench has no unpaced mode). Heap in MiB after a forced GC. Chromium ${sample.browser.version}, headless, renderer: ${sample.browser.renderer}, viewport ${sample.browser.viewport.width}x${sample.browser.viewport.height} at DPR ${sample.browser.viewport.deviceScaleFactor}.`, '')
    if (options.pilot) {
      lines.push('### Pilot: runs per cell for a 1% half-width on callback p95 and throughput (capped)', '', '| Cell | planned N |', '|---|---|', ...Object.entries(plannedBrowser).map(([cell, n]) => `| ${cell.replace('|', ' ')}x | ${n} |`), '')
      json.browserPilotPlanned = plannedBrowser
    }
    json.browser = browserJson
  }
  writeFileSync(join(dir, 'report.md'), `${lines.join('\n')}\n`)
  writeFileSync(join(dir, 'report.json'), JSON.stringify(json, null, 2))
  console.log(lines.join('\n'))
}

const commands: Record<string, () => void> = { prepare, headless, browser, report }
const command = commands[positionals[0] ?? '']
if (!command) throw new Error(`usage: node perf/run-perf.ts <${Object.keys(commands).join('|')}> ...`)
command()
