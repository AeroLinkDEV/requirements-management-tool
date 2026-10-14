// The FMS performance harness orchestrator (#1510 I0a, #1502 D10). Run with Node 24 from product/client:
//
//   node perf/run-perf.ts prepare  --work <dir> --sha A=<commit> --sha B=<commit>
//   node perf/run-perf.ts headless --out <dir> --arm A=<checkout> --arm B=<checkout> --blocks <n> [--workloads W1,W3]
//   node perf/run-perf.ts browser  --out <dir> --arm B=<checkout> --rounds <n> [--rates 1,64] [--configurations ...]
//   node perf/run-perf.ts report   --out <dir> [--pilot]
//
// Every run is its own `playwright test --config=perf/playwright.perf.config.ts` invocation (a fresh process), taken
// in ABBA order for two arms after an unscored priming pass (headless, protocol revision 3), and kept whatever its
// outcome. Arm checkouts are detached worktrees at their SHAs; their client source, lock file and Vite config tree
// hashes are recorded with every run. Output holds no user paths, hostnames or environment values: environment
// variables are recorded by name only.

import { execFileSync, spawnSync } from 'node:child_process'
import { createHash, randomUUID } from 'node:crypto'
import { appendFileSync, cpSync, existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from 'node:fs'
import { arch, cpus, platform, release, totalmem } from 'node:os'
import { basename, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { parseArgs } from 'node:util'
import { blockZeroCheck, comparisonKind, failedRunRule, headlessSchedule, headlessVerdict, holm, judgeFamily, medianLevel, nearestRank, permutationTest, plannedRuns, primingPlan, relativeEffect, scoredRuns, type Arm, type RunValue, type SessionEvidence } from './stats.ts'
import { browserQualification } from './browser-accounting.ts'
import { browserCapturePlan, captureDisposition, coldPayload, copyColdPayload, digest, fileDigest, requireClosure, CAPTURE_SCHEMA } from './browser-capture.ts'
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
    configurations: { type: 'string' }, hours: { type: 'string' }, pilot: { type: 'boolean' }, 'dirty-smoke': { type: 'boolean' },
    'cold-input': { type: 'string' }, host: { type: 'string' }, topology: { type: 'string' },
  },
})

const git = (cwd: string, ...args: string[]) => execFileSync('git', ['-C', cwd, ...args], { encoding: 'utf8' }).trim()
const pairs = (list: string[] | undefined) => Object.fromEntries((list ?? []).map(entry => {
  const at = entry.indexOf('=')
  if (at < 1) throw new Error(`expected NAME=value, got ${entry}`)
  return [entry.slice(0, at), entry.slice(at + 1)]
})) as Record<string, string>

/** Versioned work declarations used by planning and the actual child dispatcher, never a simulation adapter. */
function workloadIds(defaults: string[]) {
  const ids = options.workloads?.split(',') ?? defaults
  if (!ids.length || new Set(ids).size !== ids.length) throw new Error('workloads must be nonempty and distinct')
  for (const id of ids) if (!protocol.routeContract.workloads.some((entry: { id: string }) => entry.id === id)) throw new Error(`unknown workload ${id}`)
  return ids
}
function route(workload: string, host: string, topology: string, implemented = true) {
  const family = protocol.routeContract.workloads.find((entry: { id: string }) => entry.id === workload)
  if (!family) throw new Error(`unknown workload ${workload}`)
  const owner = protocol.routeContract.hosts[host]
  if (!owner) throw new Error(`unknown route host ${host}`)
  if (!owner.topologies.includes(topology)) throw new Error(`topology ${topology} is not available for ${host}`)
  const declared = family.routes[host]
  if (implemented && declared.status !== 'IMPLEMENTED') throw new Error(`route ${workload}/${host}/${topology} is NOT_IMPLEMENTED`)
  const source = protocol.headless.workloads.find((entry: { id: string }) => entry.id === workload)
  if (source?.scenario !== family.scenario) throw new Error(`route scenario disagrees with executable workload ${workload}`)
  if (host === 'headless' && (owner.warmup.value !== protocol.warmupSimSeconds ||
    (source.windowSimSeconds ?? null) !== (declared.scored.kind === 'fixed-total-after-warmup' ? declared.scored.maxTotalSimSeconds : null)))
    throw new Error(`route extent disagrees with executable workload ${workload}`)
  if (host === 'browser-paced' && declared.status === 'IMPLEMENTED' && workload === 'W2' && (family.scenario !== protocol.browser.scenario ||
    declared.warmup.value !== protocol.browser.warmupWallSeconds || JSON.stringify(declared.scored.simSecondsByRate) !== JSON.stringify(protocol.browser.windowSimSeconds)))
    throw new Error(`route extent disagrees with executable browser workload ${workload}`)
  return { schema: protocol.routeContract.schema, id: `${workload}/${host}/${topology}/v${declared.version ?? 1}`, workload,
    scenario: family.scenario, source: family.source, host, topology, ...owner, ...declared,
    metrics: declared.status === 'IMPLEMENTED' ? owner.metrics.filter((metric: string) => metric !== 'sync-residency-dual-only' || topology === 'dual') : [], role: declared.roleByTopology?.[topology] ?? null }
}
function browserRoute(paired = false) {
  const ids = workloadIds(['W2'])
  if (ids.length !== 1) throw new Error('one browser workload is required')
  const selected = route(ids[0], options.host ?? 'browser-paced', options.topology ?? 'dual')
  if (selected.host !== 'browser-paced') throw new Error('the product UI dispatcher requires browser-paced')
  if (paired && selected.workload !== 'W2') throw new Error('paired browser planning/capture supports W2 only')
  return selected
}
function routePlan() {
  const ids = workloadIds(protocol.routeContract.workloads.map((entry: { id: string }) => entry.id))
  const hosts = options.host ? [options.host] : Object.keys(protocol.routeContract.hosts)
  const routes = ids.flatMap(id => hosts.flatMap(host => {
    const owner = protocol.routeContract.hosts[host]
    if (!owner) throw new Error(`unknown route host ${host}`)
    return (options.topology ? [options.topology] : owner.topologies).map((topology: string) => route(id, host, topology, false))
  }))
  if (options.arm && hosts.some(host => host !== 'browser-unpaced')) throw new Error('ARM capability planning requires explicit browser-unpaced host')
  const armCapabilities = options.arm ? Object.entries(pairs(options.arm)).map(([name, root]) => ({ name, ...unpacedCapability(resolve(root)) })) : undefined
  console.log(JSON.stringify({ schema: 'aerolink.fms-perf-route-plan.v1', inference: 'NOT_CALIBRATED', routes, armCapabilities }, null, 2))
}

/** Static tracked-file admission, not proof that the declared ARM's browser modules execute. */
function unpacedCapability(root: string) {
  const prefix = 'product/client/src/fmsCdu/'
  const tracked = new Set(git(root, 'ls-files', '--', prefix).split('\n'))
  const has = (name: string) => tracked.has(prefix + name) && existsSync(join(root, prefix, name))
  const common = ['scenario.ts', 'profile.ts', 'gps.ts']
  const kernel = ['kernel/legacyPlantAdapter.ts', 'kernel/kernel.ts']
  const legacy = ['scriptedFms.ts', 'flight.ts', 'dualFms.ts']
  if (!common.every(has)) throw new Error('unpaced ARM capability refuses incomplete public modules')
  const kernelCount = kernel.filter(has).length
  if (kernelCount === 1) throw new Error('unpaced ARM capability refuses partial kernel pair')
  const kind = kernelCount === 2 ? 'CURRENT_KERNEL' : 'LEGACY_PUBLIC_ADVANCE_TICKS'
  const required = [...common, ...(kernelCount === 2 ? kernel : legacy)]
  if (!required.every(has)) throw new Error('unpaced ARM capability refuses incomplete legacy public modules')
  const identity = armIdentity(root)
  return { kind, identity, modules: required.map(name => ({ path: name, blob: git(root, 'rev-parse', `HEAD:${prefix}${name}`),
    sha256: createHash('sha256').update(readFileSync(join(root, prefix, name))).digest('hex') })) }
}

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
  // The browser runs and their seed start the API with --no-build, so they run the last harness build. The
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
function quietHost(remainingMs = Infinity) {
  const { sampleSeconds, maxBusyPercent, maxWaitSeconds } = protocol.quietHost
  const started = Date.now()
  const began = performance.now(), samples: { utc: string; monotonicMs: number; busyPercent: number }[] = []
  for (;;) {
    if (performance.now() - began + sampleSeconds * 1000 > remainingMs) throw new Error('whole capture deadline prevents quiet admission')
    const before = cpus()
    wait(sampleSeconds * 1000)
    const busyPercent = busy(before, cpus())
    samples.push({ utc: new Date().toISOString(), monotonicMs: performance.now(), busyPercent })
    const waitedSeconds = (Date.now() - started) / 1000
    if (busyPercent <= maxBusyPercent || waitedSeconds >= maxWaitSeconds)
      return { busyPercent, waitedSeconds, quiet: busyPercent <= maxBusyPercent, samples }
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
function run(dir: string, record: Record<string, unknown>, env: Record<string, string>, deadline = Infinity, childCapMs = 40 * 60_000) {
  const index = nextIndex(dir)
  const name = `run-${String(index).padStart(4, '0')}`
  const resultPath = join(dir, 'runs', `${name}.json`)
  const quiet = quietHost(deadline - performance.now())
  const before = cpus(), started = Date.now(), startedAt = new Date(started).toISOString()
  const monotonicStartedAt = performance.now()
  const child = spawnSync(process.execPath, [cli, 'test', '--config=perf/playwright.perf.config.ts'], {
    cwd: client, encoding: 'utf8', timeout: Math.max(1, Math.min(childCapMs, deadline - performance.now())), maxBuffer: 64 * 1024 * 1024,
    env: { ...process.env, ...env, AEROLINK_PERF_RESULT: resultPath, AEROLINK_PERF_OUTPUT_DIR: join(dir, 'logs', name) },
  })
  const wallSeconds = (Date.now() - started) / 1000
  const monotonicEndedAt = performance.now()
  writeFileSync(join(dir, 'logs', `${name}.log`), `${child.stdout ?? ''}\n${child.stderr ?? ''}`)
  const result = existsSync(resultPath) ? JSON.parse(readFileSync(resultPath, 'utf8')) : null
  const declaredRoute = record.route as { id: string; workload: string; scenario: string; topology: string } | undefined
  const routeMatches = !declaredRoute || (result?.scenario === declaredRoute.scenario && result?.topology === declaredRoute.topology &&
    (record.kind === 'headless' ? result?.workload === declaredRoute.workload : result?.route?.id === declaredRoute.id))
  const entry: RunRecord = {
    index, ...record, startedAt, wallSeconds, monotonicStartedAt, monotonicEndedAt, exitCode: child.status, status: child.status === 0 && result && routeMatches ? 'passed' : 'failed',
    routeIdentityRefusal: routeMatches ? null : 'actual child workload/scenario/topology disagrees with dispatched route',
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
  const workloads = workloadIds(protocol.headless.workloads.map((w: { id: string }) => w.id))
  if (options.topology || (options.host && options.host !== 'headless')) throw new Error('headless host/topologies are fixed by protocol; use route-plan to inspect them')
  const routes = workloads.flatMap(workload => protocol.headless.topologies.map((topology: string) => route(workload, 'headless', topology)))
  const dir = out(), list = arms()
  if (list.length !== 2 || !list.some(a => a.arm === 'A') || !list.some(a => a.arm === 'B')) throw new Error('headless runs need --arm A=... and --arm B=...')
  const blocks = Number(options.blocks)
  const schedule = headlessSchedule(blocks, workloads, protocol.headless.topologies)
  const priming = primingPlan(workloads, protocol.headless.topologies)
  const session = begin(dir, 'headless', { arms: list.map(({ arm, identity }) => ({ arm, ...identity })), blocks, workloads, priming, routes })
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
      const entry = run(dir, { kind: 'headless', primed, block, slot, position, arm: arm.arm, armIdentity: arm.identity, workload, topology, route: route(workload, 'headless', topology) }, {
        AEROLINK_PERF_MODE: 'headless', AEROLINK_PERF_ARM: join(arm.root, 'product', 'client'), AEROLINK_PERF_WORKLOAD: workload,
        AEROLINK_PERF_TOPOLOGY: topology, NODE_OPTIONS: `${process.env.NODE_OPTIONS ?? ''} --expose-gc`.trim(),
      })
      if (primed) primedRuns.push({ index: entry.index, status: entry.status })
    }
    if (!primed && slot % 4 === 3) blockSeconds.push((Date.now() - blockStart) / 1000)
  }
  writeFileSync(join(dir, `session-end-${Date.now()}.json`), JSON.stringify({ ...session, endedAt: new Date().toISOString(), harnessAtEnd: harnessIdentity(), primedRuns, stopped }, null, 2))
}

/** The served bytes: SHA-256 over every file of the arm's built dist (sorted relative paths and contents). */
function distIdentity(dist: string) {
  if (!existsSync(join(dist, 'index.html'))) throw new Error('the browser arm has no built dist (run prepare first)')
  const hash = createHash('sha256')
  let files = 0
  const walk = (folder: string, prefix: string) => {
    for (const entry of readdirSync(folder, { withFileTypes: true }).sort((a, b) => a.name < b.name ? -1 : a.name > b.name ? 1 : 0)) {
      const relative = `${prefix}${entry.name}`
      if (entry.isDirectory()) walk(join(folder, entry.name), `${relative}/`)
      else { hash.update(`${relative}\0`).update(createHash('sha256').update(readFileSync(join(folder, entry.name))).digest()); files += 1 }
    }
  }
  walk(dist, '')
  return { distSha256: hash.digest('hex'), distFiles: files }
}

/** One diagnostic browser-engine run. No product API/seed, paired allocation or inherited statistical judge. */
function browserUnpaced() {
  const ids = workloadIds([])
  if (ids.length !== 1 || !options.topology) throw new Error('browser-unpaced requires one workload and explicit single/dual topology')
  const selected = route(ids[0], options.host ?? 'browser-unpaced', options.topology)
  if (selected.host !== 'browser-unpaced') throw new Error('browser-unpaced requires its own host')
  if (options.blocks || options.rounds || options.rates || options.configurations || options.hours || options['cold-input'] || options['dirty-smoke'] || options.pilot)
    throw new Error('browser-unpaced has no paired, rate, view, subset, order or statistical overrides')
  if ((options.arm?.length ?? 0) !== 1) throw new Error('browser-unpaced takes exactly one declared arm')
  const port = Number(process.env.AEROLINK_PERF_PORT)
  if (!Number.isSafeInteger(port) || port < 1024 || port > 65535) throw new Error('browser-unpaced requires an explicit private AEROLINK_PERF_PORT')
  const capability = unpacedCapability(resolve(Object.values(pairs(options.arm))[0]))
  const root = git(client, 'rev-parse', '--show-toplevel')
  const harness = { commit: git(root, 'rev-parse', 'HEAD'), perfTree: git(root, 'rev-parse', 'HEAD:product/client/perf'),
    lockBlob: git(root, 'rev-parse', 'HEAD:product/client/package-lock.json'), dirtyEntries: git(root, 'status', '--porcelain').split('\n').filter(Boolean).length }
  if (harness.dirtyEntries) throw new Error('browser-unpaced requires a clean declared harness')
  const [arm] = arms(), dir = out()
  const cohort = { harness, protocol, capability, runtime: environment(), vite: JSON.parse(readFileSync(join(client, 'node_modules/vite/package.json'), 'utf8')).version }
  writeFileSync(join(dir, 'unpaced-session.json'), JSON.stringify({ kind: 'browser-unpaced', route: selected, cohort, arm: arm.identity,
    startedAt: new Date().toISOString(), inference: 'DISABLED', qualification: 'NOT_CALIBRATED' }, null, 2), { flag: 'wx' })
  run(dir, { kind: 'browser-unpaced', route: selected, workload: selected.workload, topology: selected.topology, arm: arm.arm, armIdentity: arm.identity, cohort }, {
    AEROLINK_PERF_MODE: 'browser-unpaced', AEROLINK_PERF_ARM: join(arm.root, 'product/client'),
    AEROLINK_PERF_WORKLOAD: selected.workload, AEROLINK_PERF_TOPOLOGY: selected.topology,
    AEROLINK_PERF_ARM_CAPABILITY: JSON.stringify(capability),
  })
  writeFileSync(join(dir, 'unpaced-end.json'), JSON.stringify({ endedAt: new Date().toISOString(), armIdentity: armIdentity(arm.root),
    harnessCommit: git(root, 'rev-parse', 'HEAD'), dirtyEntries: git(root, 'status', '--porcelain').split('\n').filter(Boolean).length,
    inference: 'DISABLED', qualification: 'NOT_CALIBRATED', nativeClosure: 'UNPROVEN' }, null, 2), { flag: 'wx' })
}

function browser() {
  const selectedRoute = browserRoute()
  const dir = out(), list = arms()
  if (list.length !== 1) throw new Error('browser runs take one arm (--arm B=<checkout>)')
  const [arm] = list
  const dist = join(arm.root, 'product', 'client', 'dist')
  const served = distIdentity(dist)
  const rounds = Number(options.rounds)
  const rates: number[] = options.rates?.split(',').map(Number) ?? protocol.browser.rates
  const configurations: string[] = options.configurations?.split(',') ?? protocol.browser.configurations.map((c: { id: string }) => c.id)
  const cells = configurations.flatMap(configuration => rates.map(rate => ({ configuration, rate })))
  const session = begin(dir, 'browser', { arms: [{ arm: arm.arm, ...arm.identity, ...served }], rounds, cells, route: selectedRoute })

  // One template database, seeded once per session; every run's fresh API process starts on its own copy of it. The
  // seed's files are named per session, so a reused --out keeps an earlier session's seed evidence, and its SHA-256 is
  // recorded with every run and in the session's end record.
  const templateId = `perf-template-${Date.now()}`
  const seedPath = join(dir, `seed-${templateId}.json`), seedLog = join(dir, 'logs', `seed-${templateId}.log`)
  if (existsSync(seedPath) || existsSync(seedLog)) throw new Error(`seed evidence for ${templateId} already exists in --out`)
  const seed = spawnSync(process.execPath, [cli, 'test', '--config=perf/playwright.perf.config.ts'], {
    cwd: client, encoding: 'utf8', timeout: 20 * 60_000, maxBuffer: 64 * 1024 * 1024,
    env: { ...process.env, AEROLINK_PERF_MODE: 'seed', AEROLINK_PERF_DIST: dist, AEROLINK_E2E_RUN_ID: templateId, AEROLINK_PERF_RESULT: seedPath, AEROLINK_PERF_OUTPUT_DIR: join(dir, 'logs', `seed-${templateId}`) },
  })
  writeFileSync(seedLog, `${seed.stdout ?? ''}\n${seed.stderr ?? ''}`, { flag: 'wx' })
  if (seed.status !== 0 || !existsSync(seedPath)) throw new Error(`seeding the template database failed; see ${seedLog}`)
  const seedRecord = { file: `seed-${templateId}.json`, sha256: createHash('sha256').update(readFileSync(seedPath)).digest('hex') }
  const template = browserStoragePath(templateId)

  // No priming pass here (protocol.json headless.priming): one arm, so no A/B for a cold arm to bias.
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
          // The served dist digest is part of the browser arm's identity: the report never pools two builds in a cell.
          run(dir, { kind: 'browser', sessionStartedAt: session.startedAt, block: round, round, position, arm: arm.arm, armIdentity: { ...arm.identity, ...served }, seed: seedRecord, configuration, rate, route: selectedRoute }, {
            AEROLINK_PERF_MODE: 'browser', AEROLINK_PERF_DIST: dist, AEROLINK_E2E_RUN_ID: runId,
            ...(selectedRoute.workload !== 'W2' ? { AEROLINK_PERF_ARM: join(arm.root, 'product/client') } : {}),
            AEROLINK_PERF_WORKLOAD: selectedRoute.workload, AEROLINK_PERF_TOPOLOGY: selectedRoute.topology,
            AEROLINK_SHOWCASE_SEED: readFileSync(seedPath, 'utf8'), AEROLINK_PERF_RATE: String(rate), AEROLINK_PERF_CONFIGURATION: configuration,
          })
        } finally { removeBrowserStorage(runId) }
      })
      roundSeconds.push((Date.now() - roundStart) / 1000)
    }
  } finally { removeBrowserStorage(templateId) }
  // The end record repeats the served-bytes digest, so a dist rebuilt during the session is visible.
  writeFileSync(join(dir, `session-end-${Date.now()}.json`), JSON.stringify({ ...session, endedAt: new Date().toISOString(), harnessAtEnd: harnessIdentity(), servedAtEnd: distIdentity(dist), seed: seedRecord }, null, 2))
}

/** One fixed same-source instrument study. Actual execution still requires the separately reviewed 446d outer
 * owner and quiet-window admission. This process is not the independent deadline/closure authority. */
function browserPlan() {
  browserRoute(true)
  const plan = browserCapturePlan()
  console.log(JSON.stringify({ ...plan, manifestSha256: digest(plan) }, null, 2))
}
function browserCapture() {
  const selectedRoute = browserRoute(true)
  const startedAt = new Date().toISOString(), began = performance.now(), deadline = began + protocol.browser.capture.bounds.measurementSeconds * 1000
  if (!options['cold-input'] || options['dirty-smoke'] || options.rounds || options.rates || options.configurations || options.hours)
    throw new Error('paired capture requires a sealed --cold-input; no dirty, order, subset or cap override')
  const dir = out(), list = arms(), plan = browserCapturePlan()
  if (nextIndex(dir) || existsSync(join(dir, 'paired-session.json'))) throw new Error('paired capture needs a fresh output')
  if (list.length !== 2 || !list.some(arm => arm.arm === 'A') || !list.some(arm => arm.arm === 'B')) throw new Error('paired capture requires arms A and B')
  const servedArms = Object.fromEntries(list.map(arm => [arm.arm, { ...arm.identity, ...distIdentity(join(arm.root, 'product/client/dist')) }]))
  if (digest(servedArms.A) !== digest(servedArms.B)) throw new Error('capture-only A/A requires identical source/lock/Vite/dist identities and overlays')
  const input = resolve(options['cold-input']), payload = join(input, 'payload')
  const manifest = JSON.parse(readFileSync(join(input, 'cold.json'), 'utf8'))
  const closure = JSON.parse(readFileSync(join(input, 'closure.json'), 'utf8'))
  requireClosure(closure)
  const entries = coldPayload(payload)
  if (digest(entries) !== manifest.payloadSha256 || fileDigest(join(input, 'seed.json')) !== manifest.seedSha256 || fileDigest(join(input, 'closure.json')) !== manifest.closureSha256)
    throw new Error('sealed cold payload, seed or writer-closure bytes disagree')
  // Archive exact observed payload bytes after the externally proven seed-writer closure, before any API starts.
  const archive = join(dir, 'cold', 'payload')
  mkdirSync(archive, { recursive: true })
  copyColdPayload(payload, archive, entries)
  cpSync(join(input, 'seed.json'), join(dir, 'cold', 'seed.json'))
  cpSync(join(input, 'closure.json'), join(dir, 'cold', 'closure.json'))
  const cold = { payloadSha256: digest(entries), seedSha256: manifest.seedSha256, closureSha256: manifest.closureSha256 }
  writeFileSync(join(dir, 'cold', 'manifest.json'), JSON.stringify({ schema: CAPTURE_SCHEMA, ...cold, entries }, null, 2), { flag: 'wx' })
  const harness = harnessIdentity()
  if (harness.dirtyEntries || !harness.apiAssemblySha256) throw new Error('paired capture needs a clean harness and exact built API provenance')
  if (closure.apiTree !== harness.apiTree || closure.apiAssemblySha256 !== harness.apiAssemblySha256) throw new Error('cold seed writer API identity disagrees with the harness')
  const cohort = { harness, runtime: environment(), protocol }
  const session = { id: randomUUID(), plan, route: selectedRoute, kind: 'browser-capture', startedAt,
    monotonicStart: began, deadline, cohort, arms: servedArms, cold, closure: 'UNPROVEN' }
  writeFileSync(join(dir, 'paired-session.json'), JSON.stringify(session, null, 2), { flag: 'wx' })
  let stopped: string | null = null
  try {
    for (const slot of plan.slots) {
      if (performance.now() >= deadline) throw new Error('whole capture deadline reached')
      const arm = list.find(arm => arm.arm === slot.arm)!, runId = `paired-${session.id}-${slot.slot}`
      const current = { ...armIdentity(arm.root), ...distIdentity(join(arm.root, 'product/client/dist')) }
      if (digest(current) !== digest(servedArms[slot.arm]) || digest(harnessIdentity()) !== digest(harness)) throw new Error('per-run build identity changed')
      const storage = createBrowserStorage(runId)
      copyColdPayload(archive, storage.root, entries)
      const copiedInputs = coldPayload(storage.root), copySha256 = digest(copiedInputs)
      // Preserve every raw failed record and disposable run input. Outer owner closes all writers before cleanup.
      const entry = run(dir, { kind: 'browser-capture', route: selectedRoute, lineage: { session: session.id, manifest: digest(plan), ...slot },
        arm: slot.arm, armIdentity: current, servedAtEnd: null, cold, copySha256, copiedInputs, cohort, storageRoot: storage.root }, {
        AEROLINK_PERF_MODE: 'browser', AEROLINK_PERF_DIST: join(arm.root, 'product/client/dist'), AEROLINK_E2E_RUN_ID: runId,
        AEROLINK_PERF_WORKLOAD: selectedRoute.workload, AEROLINK_PERF_TOPOLOGY: selectedRoute.topology,
        AEROLINK_SHOWCASE_SEED: readFileSync(join(dir, 'cold/seed.json'), 'utf8'),
        AEROLINK_PERF_RATE: String(slot.rate), AEROLINK_PERF_CONFIGURATION: slot.configuration,
      }, deadline, 180_000)
      appendFileSync(join(dir, 'paired-completions.jsonl'), `${JSON.stringify({ index: entry.index,
        servedAtEnd: { ...armIdentity(arm.root), ...distIdentity(join(arm.root, 'product/client/dist')) } })}\n`)
    }
  } catch (error) { stopped = error instanceof Error ? error.message : 'capture interrupted'; throw error }
  finally {
    const cohortAtEnd = { ...cohort, harness: harnessIdentity() }
    const armsAtEnd = Object.fromEntries(list.map(arm => [arm.arm, { ...armIdentity(arm.root), ...distIdentity(join(arm.root, 'product/client/dist')) }]))
    const coldAtEnd = { ...cold, payloadSha256: digest(coldPayload(archive)) }
    // These observations are measurement work. Stamp their actual completion, not their pre-hash start.
    // The separately held whole owner must also bound the following file I/O and actual producer exit.
    const end = { ...session, cohort: cohortAtEnd, arms: armsAtEnd, cold: coldAtEnd, closure: 'UNPROVEN', stopped,
      endedAt: new Date().toISOString(), monotonicEnd: performance.now() }
    writeFileSync(join(dir, 'paired-end.json'), JSON.stringify(end, null, 2), { flag: 'wx' })
  }
}
function browserCaptureReport() {
  const dir = out(), session = JSON.parse(readFileSync(join(dir, 'paired-session.json'), 'utf8'))
  const end = existsSync(join(dir, 'paired-end.json')) ? JSON.parse(readFileSync(join(dir, 'paired-end.json'), 'utf8')) : null
  // The outer owner writes this only after the measured job actually exits/drains/closes. Without it, partial.
  if (end && existsSync(join(dir, 'closure.json'))) end.closure = JSON.parse(readFileSync(join(dir, 'closure.json'), 'utf8'))
  const readLines = (name: string) => existsSync(join(dir, name)) ? readFileSync(join(dir, name), 'utf8').trim().split('\n').filter(Boolean).map(line => JSON.parse(line)) : []
  const rows = readLines('runs.jsonl'), completions = readLines('paired-completions.jsonl')
  rows.forEach(row => { const matches = completions.filter(item => item.index === row.index); if (matches.length === 1) row.servedAtEnd = matches[0].servedAtEnd })
  const disposition = captureDisposition(session.plan, session, rows, end)
  // Re-observe retained archive, sidecars, seed and closure bytes instead of trusting declared hashes in JSON.
  try {
    const seedClosure = JSON.parse(readFileSync(join(dir, 'cold/closure.json'), 'utf8'))
    requireClosure(seedClosure)
    if (seedClosure.apiTree !== session.cohort.harness.apiTree || seedClosure.apiAssemblySha256 !== session.cohort.harness.apiAssemblySha256) throw new Error('seed writer changed')
    if (Date.parse(seedClosure.closedAt) > Date.parse(session.startedAt)) throw new Error('seed writer closure does not precede the capture')
    if (digest(coldPayload(join(dir, 'cold/payload'))) !== session.cold.payloadSha256 || fileDigest(join(dir, 'cold/seed.json')) !== session.cold.seedSha256 ||
      fileDigest(join(dir, 'cold/closure.json')) !== session.cold.closureSha256) throw new Error('changed')
  } catch {
    disposition.reasons.push('retained cold archive, seed or writer-closure bytes are missing or changed')
    disposition.status = 'CAPTURE_PARTIAL'; disposition.qualification = 'NOT_QUALIFIED'
  }
  writeFileSync(join(dir, 'paired-report.json'), JSON.stringify(disposition, null, 2))
  console.log(JSON.stringify(disposition, null, 2))
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
// Browser accounting revision 2 (#1557): completed-frame throughput beside the selected rate; simulation cost
// (slices) separate from continuation cost (yield gaps, task tails); responsiveness and rendering separately.
const browserMeasures: Record<string, (result: Record<string, any>) => number> = {
  throughput: r => r.accounting.achievedRate, 'achieved / selected': r => r.accounting.achievedFraction,
  'slice p95': r => r.accounting.simulation.slices.p95 ?? NaN, 'slice p99': r => r.accounting.simulation.slices.p99 ?? NaN,
  'yield gap p95': r => r.accounting.continuation.yieldGaps.p95 ?? NaN, 'task tail p95': r => r.accounting.continuation.taskTails.p95 ?? NaN,
  'long tasks': r => r.longTasks.count, 'rAF interval p95': r => r.raf?.p95 ?? NaN,
  'interaction p50': r => interactionDurations(r)[0], 'interaction max': r => interactionDurations(r)[1], heap: r => r.heapUsedBytesAfterGc,
}

/** Event Timing per interaction (interactionId > 0): the longest entry of each; returns [nearest-rank p50, max] over interactions. */
function interactionDurations(result: Record<string, any>): [number, number] {
  const longest = new Map<number, number>()
  for (const entry of result.interaction.entries) if (entry.interaction > 0) longest.set(entry.interaction, Math.max(longest.get(entry.interaction) ?? 0, entry.duration))
  const values = [...longest.values()].sort((a, b) => a - b)
  return values.length ? [nearestRank(values, 50), values[values.length - 1]] : [NaN, NaN]
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
  const starts = records(/^session-\d+\.json$/)
  const sessions: SessionEvidence[] = starts.map(session => {
    const end = ends.find(record => record.startedAt === session.startedAt && record.kind === session.kind)
    return { ...session, committedStatistics: committed(session.harness?.commit), end: end?.harnessAtEnd ?? null, primedRuns: end?.primedRuns ?? null }
  })
  const browserSessions = starts.filter(session => session.kind === 'browser').map(session => ({ ...session,
    endRecords: ends.filter(end => end.startedAt === session.startedAt && end.kind === 'browser').map(end => {
      const seed = end.seed
      let seedValid = false
      try {
        seedValid = typeof seed?.file === 'string' && basename(seed.file) === seed.file && /^[0-9a-f]{64}$/.test(seed?.sha256 ?? '') &&
          existsSync(join(dir, seed.file)) && createHash('sha256').update(readFileSync(join(dir, seed.file))).digest('hex') === seed.sha256
      } catch { /* unreadable retained evidence is not qualified */ }
      return { ...end, seedValid }
    }),
  }))
  const declaredFamilySize = protocol.headless.workloads.length * protocol.headless.topologies.length * aaMeasures.length
  const lines: string[] = []
  const json: Record<string, unknown> = {
    schema: 'aerolink.fms-perf-report.v1', protocol, runs: entries.length, failedRuns: entries.filter(e => e.status !== 'passed').length,
    primedRuns: priming.length, failedPrimedRuns: priming.filter(e => e.status !== 'passed').length,
  }
  const passed = entries.filter(e => e.status === 'passed' && e.result)
  const loud = (list: Entry[]) => list.filter(e => !(e.host as { quietBefore: { quiet: boolean } }).quietBefore.quiet).length
  lines.push(`Runs: ${entries.length} recorded non-priming (${json.failedRuns} failed, all kept); runs that started on a host above the quiet threshold: ${loud(entries)}.`,
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
    const { kind, title, verdict } = headlessVerdict(comparisonKind(head), judged)
    lines.push(`## Headless A/B (B/A - 1 of arm medians; within-block permutation test, R = ${permutations}; Holm across ${family.length} comparisons)`, '',
      `${title}: **${verdict}** (family-wise alpha ${aaFamilyAlpha}; ${significant.length} significant after Holm; smallest attainable p ${smallest.toPrecision(3)}, alpha / m = ${(aaFamilyAlpha / declaredFamilySize).toPrecision(3)}; ${family.length} of ${declaredFamilySize} declared comparisons present).`, '',
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
    json.headless = { comparison: kind, aa: aa.map(({ rows: _rows, ...row }) => ({ ...row, holm: family.findIndex(f => f.cell === row.cell && f.measure === row.measure) >= 0 ? adjusted[family.findIndex(f => f.cell === row.cell && f.measure === row.measure)] : null })), verdict: judged, excluded, smallestAttainableP: smallest }

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
    const { accepted, ...qualification } = browserQualification(protocol, web, browserSessions)
    json.browserQualification = { ...qualification, accepted: accepted.map(e => e.index) }
    if (accepted.length) {
      const rows = accepted as (Entry & { browserIdentity: Record<string, any> })[]
      // Complete immutable recorded identities decide comparability. Prefixes below are display only.
      const armOf = (e: Entry) => {
        const id = e.armIdentity as { commit?: string; srcTree?: string; distSha256?: string } | undefined
        return `${e.arm}@${id?.commit?.slice(0, 12) ?? '?'}/${id?.srcTree?.slice(0, 12) ?? '?'}/${id?.distSha256?.slice(0, 12) ?? '?'}`
      }
      const key = (e: typeof rows[number]) => JSON.stringify([e.configuration, e.rate, e.browserIdentity])
      const cells = [...new Set(rows.map(key))]
      lines.push('## Browser matrix (accounting revision 2, completed frames; median over comparable runs, 95% CI; half-width as % of median)', '',
        'Descriptive levels only. No browser budget verdict is made under this revision: #1542 first needs a counterbalanced browser protocol revision (its run order still starts every round at 1x and ends it at 64x), then a quiet-host browser A/A under it.', '')
      const browserJson: Record<string, unknown>[] = []
      const plannedBrowser: Record<string, number> = {}
      for (const cell of cells) {
        const cellRows = rows.filter(e => key(e) === cell)
        const first = cellRows[0], { configuration, rate } = first, arm = armOf(first), identity = first.browserIdentity
        const cellProtocol = identity.protocol, cellStats = { iterations: cellProtocol.statistics.iterations, seed: cellProtocol.statistics.seed }
        const levels = Object.fromEntries(Object.entries(browserMeasures).map(([name, take]) => {
          const values = cellRows.map(e => take(e.result!)).filter(value => Number.isFinite(value))
          return [name, values.length ? medianLevel(values, cellStats) : null]
        }))
        browserJson.push({ arm, configuration, rate, identity, runs: cellRows.length, levels })
        plannedBrowser[cell] = Math.max(...['slice p95', 'throughput'].map(name => levels[name]
          ? plannedRuns({ halfWidthPp: levels[name]!.halfWidthPct ?? 0, runsPerArm: cellRows.length }, cellProtocol.statistics.targetHalfWidthPp, cellProtocol.caps.browserRunsPerCell) : 0))
        const runtime = identity.runtime
        const protocolDigest = createHash('sha256').update(JSON.stringify(cellProtocol)).digest('hex')
        lines.push(`### Browser cohort ${browserJson.length}: ${identity.scenario}`, '',
          `Protocol SHA-256 ${protocolDigest}; harness ${identity.harness.commit} / API assembly ${identity.harness.apiAssemblySha256}. Chromium ${runtime.version}, ${runtime.headless ? 'headless' : 'headed'}, renderer: ${runtime.renderer}, viewport ${runtime.viewport.width}x${runtime.viewport.height} at DPR ${runtime.viewport.deviceScaleFactor}. Full build, protocol, harness and environment identities are retained in report.json.`, '',
          `| Arm (commit/src/dist) | Configuration | Rate | runs | ${Object.keys(browserMeasures).join(' | ')} |`, `|---|---|---|---|${Object.keys(browserMeasures).map(() => '---').join('|')}|`)
        lines.push(`| ${arm} | ${configuration} | ${rate}x | ${cellRows.length} | ${Object.keys(browserMeasures).map(name => {
          const level = levels[name]; const scale = name === 'heap' ? 2 ** 20 : 1; const digits = name === 'achieved / selected' ? 3 : 1
          return level ? `${fixed(level.estimate / scale, digits)} [${fixed(level.low / scale, digits)}, ${fixed(level.high / scale, digits)}] (±${width(level, 1)})` : 'n/a'
        }).join(' | ')} |`, '')
      }
      const steps = [...new Set(rows.map(e => (e.result as Record<string, any>).interaction.resolutionStepMs))]
      lines.push('', `Throughput is completed simulated seconds per wall second; achieved / selected divides it by the selected rate. Slice times are the simulation's own frame attempts per task (ms). Yield gap: wall time from one slice's end to the next slice of its batch, which the bench yields to rendering, input and other tasks; it contains the earlier task's tail, so gaps and tails are not added. Task tail: the rest of the slice's timer task after the slice ends, through the microtask queued after it (sync work and the render a published frame causes). Interaction = Event Timing, key -> next paint, longest entry per interaction (observed duration step ${steps.join('/')} ms; exempt from the 1% target). Heap in MiB after a forced GC. Worker backlog: not applicable before I3.`, '')
      if (options.pilot) {
        lines.push('### Pilot: runs per cohort cell for its declared half-width on slice p95 and throughput (capped)', '', '| Cohort | planned N |', '|---|---|', ...Object.values(plannedBrowser).map((n, i) => `| ${i + 1} | ${n} |`), '')
        json.browserPilotPlanned = plannedBrowser
      }
      json.browser = browserJson
    }
    if (qualification.notQualified.length) lines.push('## Browser records NOT_QUALIFIED', '',
      `${qualification.notQualified.length} browser records did not pass, use unsupported accounting, or lack complete consistent session evidence, and are not scored: interval count x selected rate (v1) is not completed simulation, and a failed run includes an accounting refusal. They remain in their original input files and identities.`, '',
      ...Object.entries(Object.groupBy(qualification.notQualified, record => record.compatibility)).map(([compatibility, list]) => `- ${compatibility}: ${list!.length} (runs ${list!.map(record => record.index).join(', ')})`), '')
  }
  writeFileSync(join(dir, 'report.md'), `${lines.join('\n')}\n`)
  writeFileSync(join(dir, 'report.json'), JSON.stringify(json, null, 2))
  console.log(lines.join('\n'))
}

const commands: Record<string, () => void> = { prepare, headless, browser, report, 'browser-unpaced': browserUnpaced, 'route-plan': routePlan, 'browser-plan': browserPlan, 'browser-capture': browserCapture, 'browser-capture-report': browserCaptureReport }
const command = commands[positionals[0] ?? '']
if (!command) throw new Error(`usage: node perf/run-perf.ts <${Object.keys(commands).join('|')}> ...`)
command()
