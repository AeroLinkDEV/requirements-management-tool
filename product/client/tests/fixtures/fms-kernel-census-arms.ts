// Runs the #1517 census against one arm's checkout and compares arms (#1502 D7 9.3). Node 24, from product/client:
//
//   node tests/fixtures/fms-kernel-census-arms.ts run --arm <arm product/client> --out <result.json> [--node-modes 1h] [--chromium-modes 1h,4r,off]
//        [--shard <k>/<n>] [--port <n>] [--runs <substring>]
//   node tests/fixtures/fms-kernel-census-arms.ts merge <out.json> <shard.json>...
//   node tests/fixtures/fms-kernel-census-arms.ts compare <a.json> <b.json>
//   node tests/fixtures/fms-kernel-census-arms.ts golden <reference result.json> <fms-kernel-golden-digests.ts> --basis <text>
//
// Regenerating the golden fixture, from source, never by hand (the golden command refuses a record that lacks any census
// run, and writes the runs in census order, so its bytes do not depend on how the record was sharded):
// (a) An I1 pull request whose base moved: rebase, take the rebased commit that has no kernel change (its I1-0 / golden
//     commit) as the reference arm, run the whole census there (--node-modes 1h at least), and regenerate. Then compare
//     that arm with the head as usual.
// (b) A pull request that changes simulation behaviour: compare its base with its head, classify every difference in that
//     pull request (#1502 D4 6.3), then run the census on its head and regenerate with --basis naming the pull request.
//
// An arm is a worktree at one commit with its own node_modules. Its census, encoder and simulation are loaded from that
// checkout through its own Vite configuration: in Node by Vite's module loader, and in Chromium on the headless kernel
// page served on a private port. Both arms of a comparison must run in the same engine build (the qualified
// environment); a Node run is reported, not claimed. The golden fixture is written only from a whole-census Node run
// of a reference arm chosen by rule (a) or (b) below, which --basis must name: under (a) it has no kernel change;
// under (b) it is the behaviour pull request's own head, after every difference from its base has been classified.

import { execFileSync } from 'node:child_process'
import { readFileSync, writeFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { parseArgs } from 'node:util'

type Mode = { rate: 1 | 4; rendered: boolean; digest: boolean }
type Result = { id: string; frames: string[]; end: string; sha256: string; bytes: number; outcome: string | null; results: unknown; endedAfter: number | null; ms: number }
type Entry = { engine: string; mode: Mode; result: Result }
type Arm = { commit: string; srcTree: string; dirty: string[] }
type CensusRecord = { schema: string; arm: Arm; censusIds: string[]; engines: { node: string; chromium: string | null }; entries: Entry[] }

const MODES: Mode[] = [
  { rate: 1, rendered: false, digest: true }, { rate: 4, rendered: false, digest: true },
  { rate: 1, rendered: true, digest: true }, { rate: 4, rendered: true, digest: true },
  // The self-check: no frame digest, only the end state. Its outcome, step results and end state must equal the others'.
  { rate: 1, rendered: false, digest: false },
]
/** Mode codes: 1h, 4h (1x or 4x, headless), 1r, 4r (rendered), off (1x headless, digest off). An empty list skips the engine. */
const MODE_CODES: Readonly<Record<string, Mode>> = { '1h': MODES[0], '4h': MODES[1], '1r': MODES[2], '4r': MODES[3], off: MODES[4] }
const modesOf = (list: string | undefined, fallback: string) => (list ?? fallback).split(',').filter(Boolean).map(code => {
  const mode = MODE_CODES[code]
  if (!mode) throw new Error(`unknown mode ${code}`)
  return mode
})
const modeKey = (mode: Mode) => `${mode.rate}x ${mode.rendered ? 'rendered' : 'headless'}${mode.digest ? '' : ' digest-off'}`

const { values: options, positionals } = parseArgs({
  allowPositionals: true,
  options: {
    arm: { type: 'string' }, out: { type: 'string' }, port: { type: 'string' }, runs: { type: 'string' }, shard: { type: 'string' },
    'node-modes': { type: 'string' }, 'chromium-modes': { type: 'string' }, basis: { type: 'string' },
  },
})

const git = (cwd: string, ...args: string[]) => execFileSync('git', ['-C', cwd, ...args], { encoding: 'utf8' }).trim()

async function run() {
  if (!options.arm || !options.out) throw new Error('run needs --arm and --out')
  const arm = resolve(options.arm)
  const nodeModes = modesOf(options['node-modes'], '1h'), chromiumModes = modesOf(options['chromium-modes'], '1h,4r,off')
  const [shard, shards] = (options.shard ?? '1/1').split('/').map(Number)
  if (!(shards >= 1 && shard >= 1 && shard <= shards)) throw new Error(`bad --shard ${options.shard}`)
  // The arm as it is now; read again when the runs end, and the evidence is refused if it moved meanwhile.
  const identity = (): Arm => ({
    commit: git(arm, 'rev-parse', 'HEAD'), srcTree: git(arm, 'rev-parse', 'HEAD:product/client/src'),
    dirty: git(arm, 'status', '--porcelain', '--', 'src', 'tests/fixtures', 'package-lock.json', 'vite.config.ts').split('\n').filter(Boolean),
  })
  const record: CensusRecord = { schema: 'aerolink.fms-kernel-census.v2', arm: identity(), censusIds: [], engines: { node: process.version, chromium: null }, entries: [] }
  const census = (ids: string[]) => {
    if (record.censusIds.length && JSON.stringify(record.censusIds) !== JSON.stringify(ids)) throw new Error('Node and Chromium loaded different censuses')
    record.censusIds = ids
  }
  const { createServer } = await import('vite')
  const port = Number(options.port ?? 5797)
  const server = await createServer({ root: arm, configFile: resolve(arm, 'vite.config.ts'), logLevel: 'error', server: { host: '127.0.0.1', port, strictPort: true, hmr: false } })
  // Shards take the runs round-robin in census order.
  const pick = (ids: string[]) => ids.filter((id, i) => i % shards === shard - 1 && (!options.runs || id.includes(options.runs)))
  try {
    if (nodeModes.length) {
      const loaded = await server.ssrLoadModule('/tests/fixtures/fms-kernel-census.ts')
      census(loaded.CENSUS_RUNS.map((r: { id: string }) => r.id))
      for (const id of pick(record.censusIds)) {
        for (const mode of nodeModes) {
          const begin = performance.now()
          const result = loaded.runCensus(loaded.CENSUS_RUNS.find((r: { id: string }) => r.id === id), mode)
          record.entries.push({ engine: 'node', mode, result: { ...result, ms: performance.now() - begin } })
          console.log(`node ${id} ${modeKey(mode)}: ${result.frames.length} frames ${result.sha256.slice(0, 12)} ${Math.round(performance.now() - begin)} ms`)
        }
      }
    }
    if (chromiumModes.length) {
      await server.listen()
      const { chromium } = await import('@playwright/test')
      const browser = await chromium.launch()
      record.engines.chromium = browser.version()
      try {
        const page = await browser.newPage()
        await page.goto(`http://127.0.0.1:${port}/tests/fixtures/fms-kernel-headless.html`)
        await page.waitForFunction(() => document.getElementById('status')?.textContent === 'ready', null, { timeout: 120_000 })
        census(await page.evaluate(() => window.fmsKernelCensus.ids))
        for (const id of pick(record.censusIds)) {
          for (const mode of chromiumModes) {
            const result: Result = await page.evaluate(([i, m]) => window.fmsKernelCensus.run(i as string, m as Mode), [id, mode] as const)
            record.entries.push({ engine: 'chromium', mode, result })
            console.log(`chromium ${id} ${modeKey(mode)}: ${result.frames.length} frames ${result.sha256.slice(0, 12)} ${Math.round(result.ms)} ms`)
          }
        }
      } finally { await browser.close() }
    }
  } finally { await server.close() }
  const after = identity()
  if (JSON.stringify(after) !== JSON.stringify(record.arm)) throw new Error(`the arm changed during the run (${JSON.stringify(record.arm)} -> ${JSON.stringify(after)}); no evidence written`)
  writeFileSync(options.out, JSON.stringify(record))
}

/** Every difference between two arms, per engine, run and mode; and whether each arm's modes agree with one another. */
function compare(aPath: string, bPath: string) {
  const a: CensusRecord = JSON.parse(readFileSync(aPath, 'utf8')), b: CensusRecord = JSON.parse(readFileSync(bPath, 'utf8'))
  // Bit-identity is claimed only within one engine build (#1502 D7 9.5): arms from different builds are not compared.
  for (const engine of ['node', 'chromium'] as const) {
    const ran = (record: CensusRecord) => record.entries.some(e => e.engine === engine)
    if (ran(a) && ran(b) && a.engines[engine] !== b.engines[engine]) throw new Error(`the arms ran in different ${engine} builds: ${a.engines[engine]} vs ${b.engines[engine]}`)
  }
  const key = (e: Entry) => `${e.engine} | ${e.result.id} | ${modeKey(e.mode)}`
  const bByKey = new Map(b.entries.map(e => [key(e), e]))
  const differences: string[] = []
  let compared = 0, frames = 0
  for (const entry of a.entries) {
    const other = bByKey.get(key(entry))
    if (!other) { differences.push(`${key(entry)}: missing from B`); continue }
    compared += 1; frames += entry.result.frames.length
    const x = entry.result, y = other.result
    const first = x.frames.findIndex((frame, i) => frame !== y.frames[i])
    if (x.frames.length !== y.frames.length || first >= 0) differences.push(`${key(entry)}: frames ${x.frames.length} vs ${y.frames.length}, first difference at frame ${first}`)
    if (x.sha256 !== y.sha256 || x.bytes !== y.bytes) differences.push(`${key(entry)}: SHA-256 ${x.sha256} vs ${y.sha256} (${x.bytes} vs ${y.bytes} bytes)`)
    if (x.end !== y.end) differences.push(`${key(entry)}: end state differs`)
    if (JSON.stringify([x.outcome, x.results, x.endedAfter]) !== JSON.stringify([y.outcome, y.results, y.endedAfter])) differences.push(`${key(entry)}: outcome or step results differ`)
  }
  for (const entry of b.entries) if (!a.entries.some(e => key(e) === key(entry))) differences.push(`${key(entry)}: missing from A`)
  // Within one arm and engine, every mode of a run must agree: the frame digests at 1x and 4x, rendered or not (#1518),
  // and the outcome, step results and end state with the digest off (the I1-0 self-check).
  const modesAgree = (record: CensusRecord) => {
    const states = new Map<string, Set<string>>(), digests = new Map<string, Set<string>>()
    for (const e of record.entries) {
      const k = `${e.engine} | ${e.result.id}`
      states.set(k, (states.get(k) ?? new Set()).add(`${e.result.end}|${e.result.outcome}|${e.result.endedAfter}|${JSON.stringify(e.result.results)}`))
      if (e.mode.digest) digests.set(k, (digests.get(k) ?? new Set()).add(`${e.result.sha256}|${e.result.frames.join(',')}`))
    }
    return [...[...states].filter(([, values]) => values.size > 1).map(([k]) => `${k}: outcome, results or end state differ between modes`),
      ...[...digests].filter(([, values]) => values.size > 1).map(([k]) => `${k}: frame digests differ between modes`)]
  }
  const engines = (record: CensusRecord) => {
    const node = new Map(record.entries.filter(e => e.engine === 'node').map(e => [`${e.result.id} | ${modeKey(e.mode)}`, e.result.sha256]))
    return record.entries.filter(e => e.engine === 'chromium' && node.has(`${e.result.id} | ${modeKey(e.mode)}`) && node.get(`${e.result.id} | ${modeKey(e.mode)}`) !== e.result.sha256).map(e => `${e.result.id} | ${modeKey(e.mode)}`)
  }
  const modesDisagree = { a: modesAgree(a), b: modesAgree(b) }
  const reference = (engine: string) => a.entries.filter(e => e.engine === engine && e.mode.rate === 1 && !e.mode.rendered && e.mode.digest)
  console.log(JSON.stringify({
    a: { ...a.arm, engines: a.engines }, b: { ...b.arm, engines: b.engines }, compared, frames, differences,
    modesDisagree, nodeVsChromium: { a: engines(a), b: engines(b) },
    runs: (reference('chromium').length ? reference('chromium') : reference('node')).map(e => ({ engine: e.engine, id: e.result.id, frames: e.result.frames.length, sha256: e.result.sha256 })),
  }, null, 2))
  if (differences.length || modesDisagree.a.length || modesDisagree.b.length) process.exitCode = 1
}

/** The golden fixture: per run, the SHA-256, the frame count, every 100th frame digest and the end digest. */
function golden(reference: string, target: string) {
  const record: CensusRecord = JSON.parse(readFileSync(reference, 'utf8'))
  if (record.schema !== 'aerolink.fms-kernel-census.v2' || !record.censusIds?.length) throw new Error('the reference record does not name its census; run it with this tool')
  if (record.arm.dirty.length) throw new Error('the reference arm has local changes')
  const base = new Map(record.entries.filter(e => e.engine === 'node' && e.mode.rate === 1 && !e.mode.rendered && e.mode.digest).map(e => [e.result.id, e]))
  const missing = record.censusIds.filter(id => !base.has(id))
  if (missing.length) throw new Error(`the reference record lacks ${missing.length} of ${record.censusIds.length} census runs (Node, 1x headless): ${missing.join(', ')}`)
  // Census order, so the fixture's bytes are the same however the record was sharded.
  const runs = Object.fromEntries(record.censusIds.map(id => base.get(id)!).map(({ result }) => [result.id, {
    sha256: result.sha256, frames: result.frames.length, every100th: result.frames.filter((_, i) => i % 100 === 0), end: result.end,
    outcome: result.outcome, endedAfter: result.endedAfter,
  }]))
  // Required: the header's Basis line must say which rule chose the reference arm, never a default.
  const basis = options.basis?.trim()
  if (!basis) throw new Error('golden needs --basis: rule (a) (an I1 reference arm with no kernel change) or rule (b) (naming the behaviour pull request)')
  const text = [
    '// GENERATED by tests/fixtures/fms-kernel-census-arms.ts golden. Never edit or merge by hand (#1517 I1-0).',
    `// Reference arm: commit ${record.arm.commit}, product/client/src tree ${record.arm.srcTree}; Node ${record.engines.node}; every census run, 1x headless.`,
    '// The tree identifies the reference once its commit is squashed away.',
    `// Basis: ${basis}.`,
    '// Authority: only the entries fms-kernel-legacy-equivalence.spec.ts checks are enforced; they are authoritative. Every',
    '// other entry is pull-request evidence: true of the reference arm, enforced by nothing, so it may go stale until the',
    '// next regeneration. (Enforcing all of them costs a whole Node census per CI run, about 31 CPU-minutes locally.)',
    '// Regenerate from source (the tool refuses a partial record and writes census order):',
    '// (a) an I1 pull request whose base moved: its rebased commit with no kernel change is the reference arm;',
    '// (b) a pull request that changes simulation behaviour: compare base with head, classify every difference in that',
    '//     pull request (#1502 D4 6.3), then regenerate from its head with --basis naming it.',
    `export const GOLDEN_GENERATOR = ${JSON.stringify(record.arm.commit)}`,
    `export const GOLDEN_SOURCE_TREE = ${JSON.stringify(record.arm.srcTree)}`,
    `export const GOLDEN_DIGESTS: Readonly<Record<string, { sha256: string; frames: number; every100th: readonly string[]; end: string; outcome: string | null; endedAfter: number | null }>> = ${JSON.stringify(runs, null, 2).replaceAll('"', '\'')}`,
    '',
  ].join('\n')
  writeFileSync(target, text)
}

/** One record from a run's shards: one arm, one engine build each. */
function merge(target: string, parts: string[]) {
  const records: CensusRecord[] = parts.map(part => JSON.parse(readFileSync(part, 'utf8')))
  const key = (r: CensusRecord) => JSON.stringify([r.schema, r.arm, r.censusIds, r.engines.node])
  if (new Set(records.map(key)).size !== 1) throw new Error('the shards are not from one arm, one census and one Node')
  const chromium = [...new Set(records.map(r => r.engines.chromium).filter(Boolean))]
  if (chromium.length > 1) throw new Error('the shards ran in different Chromium builds')
  writeFileSync(target, JSON.stringify({ ...records[0], engines: { node: records[0].engines.node, chromium: chromium[0] ?? null }, entries: records.flatMap(r => r.entries) }))
}

const command = positionals[0]
if (command === 'run') await run()
else if (command === 'compare') compare(positionals[1], positionals[2])
else if (command === 'merge') merge(positionals[1], positionals.slice(2))
else if (command === 'golden') golden(positionals[1], positionals[2])
else throw new Error('usage: run | merge <out> <shards...> | compare <a> <b> | golden <reference> <target>')
