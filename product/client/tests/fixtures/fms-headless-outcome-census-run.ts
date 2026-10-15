// Drives the #1517 I1d outcome census (fms-headless-outcome-census.ts) at one checkout. Node 24, from product/client:
//
//   node tests/fixtures/fms-headless-outcome-census-run.ts scenarios --out <dir>
//   node tests/fixtures/fms-headless-outcome-census-run.ts sites --out <dir> [--arms control,single,dual,single-pure,dual-pure]
//
// scenarios: every library scenario and the 87N mission, flown single and dual in lockstep (scenarios.json). Each run
//   is anchored: its single end state must equal today's runHeadless, and both must match the I1 golden end digests.
// sites: every runHeadless call in the specs. The specs that call it are run with Playwright, one worker: unchanged
//   (control), and as temporary copies whose scenario import is the census stand-in (fms-headless-outcome-census-shim.ts)
//   on each arm: single and dual instrumented (each call's END state, and the lockstep divergence on dual), and
//   single-pure and dual-pure (the arm's run alone; the assertion results of record). In the copies every expect is
//   soft, so each test reports every assertion that fails, not only the first. Writes sites-<arm>.jsonl, a Playwright
//   JSON report and log per arm, and sites-inventory.json; the copies and their config are removed afterwards.
//
// No production code changes: the stand-in replaces runHeadless only in the copies.

import { execFileSync, spawnSync } from 'node:child_process'
import { existsSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs'
import { join, resolve } from 'node:path'
import { parseArgs } from 'node:util'

const { values: options, positionals } = parseArgs({ allowPositionals: true, options: { out: { type: 'string' }, only: { type: 'string' }, files: { type: 'string' }, arms: { type: 'string' } } })
const client = resolve(import.meta.dirname, '..', '..')
const git = (...args: string[]) => execFileSync('git', ['-C', client, ...args], { encoding: 'utf8' }).trim()
const identity = () => ({
  commit: git('rev-parse', 'HEAD'), srcTree: git('rev-parse', 'HEAD:product/client/src'),
  dirty: git('status', '--porcelain', '--', 'src', 'tests', 'package-lock.json', 'vite.config.ts').split('\n').filter(Boolean),
})
const outDir = () => {
  if (!options.out) throw new Error('--out is required')
  const dir = resolve(options.out)
  mkdirSync(dir, { recursive: true })
  return dir
}

async function scenarios() {
  const dir = outDir()
  const before = identity()
  const { createServer } = await import('vite')
  // SSR loading only: no port is opened.
  const server = await createServer({ root: client, configFile: resolve(client, 'vite.config.ts'), logLevel: 'error', server: { middlewareMode: true, hmr: false } })
  const runs: unknown[] = []
  try {
    const census = await server.ssrLoadModule('/tests/fixtures/fms-headless-outcome-census.ts')
    const scenario = await server.ssrLoadModule('/src/fmsCdu/scenario.ts')
    const { SCENARIO_LIBRARY } = await server.ssrLoadModule('/src/fmsCdu/scenarioLibrary.ts')
    const { MISSION_87N_OFFSHORE_SAR } = await server.ssrLoadModule('/src/fmsCdu/heliDemo.ts')
    const { GOLDEN_DIGESTS, GOLDEN_GENERATOR } = await server.ssrLoadModule('/tests/fixtures/fms-kernel-golden-digests.ts')
    const list = [...SCENARIO_LIBRARY.map((s: { id: string }) => ({ census: s.id, scenario: s })), { census: `W2 ${MISSION_87N_OFFSHORE_SAR.id}`, scenario: MISSION_87N_OFFSHORE_SAR }]
      .filter(entry => !options.only || entry.census.includes(options.only))
    for (const { census: id, scenario: s } of list) {
      const begin = performance.now()
      const compared = census.lockstep(s)
      const real = scenario.runHeadless(s)
      const realEnd = census.unitDigest({ fms: real.fms, sim: real.sim })
      const goldenSingle = GOLDEN_DIGESTS[`${id}/single`], goldenDual = GOLDEN_DIGESTS[`${id}/dual`]
      const anchors = {
        lockstepSingleEqualsRunHeadless: compared.single.end === realEnd && compared.single.outcome === real.runner.outcome
          && JSON.stringify(compared.single.results) === JSON.stringify(real.runner.results),
        goldenGenerator: GOLDEN_GENERATOR,
        singleEqualsGolden: goldenSingle ? goldenSingle.end === compared.single.end && goldenSingle.outcome === compared.single.outcome : null,
        dualEqualsGolden: goldenDual ? goldenDual.end === compared.dualSystemEnd && goldenDual.outcome === compared.dual.outcome : null,
      }
      runs.push({ id, scenarioId: s.id, maxSeconds: s.maxSeconds, anchors, ...compared, ms: Math.round(performance.now() - begin) })
      const differs = compared.single.outcome !== compared.dual.outcome || JSON.stringify(compared.single.results) !== JSON.stringify(compared.dual.results)
      console.log(`${id}: single ${compared.single.outcome} dual ${compared.dual.outcome}${differs ? ' RESULTS DIFFER' : ''}; first frame ${compared.firstFrame}; `
        + `${compared.divergentFrames}/${compared.comparedFrames} frames differ; SYNC ${compared.sync.residency.toFixed(4)}; anchors ${JSON.stringify(anchors)}; ${Math.round(performance.now() - begin)} ms`)
    }
  } finally { await server.close() }
  const after = identity()
  if (JSON.stringify(after) !== JSON.stringify(before)) throw new Error(`the checkout changed during the run (${JSON.stringify(before)} -> ${JSON.stringify(after)}); no evidence written`)
  writeFileSync(join(dir, 'scenarios.json'), JSON.stringify({ schema: 'aerolink.fms-i1d-outcome-census-scenarios.v1', checkout: before, node: process.version, runs }, null, 1))
}

/** Every spec line that calls runHeadless (a call, not the import or a title). */
function inventory() {
  const tests = join(client, 'tests')
  const files = readdirSync(tests).filter(name => name.endsWith('.spec.ts') && !name.startsWith('i1d-')).sort()
  const sites: { file: string; line: number; calls: number; text: string }[] = []
  for (const file of files) {
    readFileSync(join(tests, file), 'utf8').split(/\r?\n/).forEach((text, i) => {
      const calls = (text.match(/\brunHeadless\(/g) ?? []).length
      if (calls && !/^\s*import\b/.test(text)) sites.push({ file, line: i + 1, calls, text: text.trim() })
    })
  }
  return { files: [...new Set(sites.map(site => site.file))], sites, calls: sites.reduce((sum, site) => sum + site.calls, 0) }
}

const SCENARIO_IMPORT = /from '\.\.\/src\/fmsCdu\/scenario'/g
// A global expression keeps lastIndex between test() calls; test with a fresh one.
const HAS_SCENARIO_IMPORT = new RegExp(SCENARIO_IMPORT.source)

function sites() {
  const dir = outDir()
  const before = identity()
  const all = inventory()
  // --files narrows a trial run to spec files whose name contains the text; evidence runs take every file.
  const found = options.files ? { ...all, files: all.files.filter(file => file.includes(options.files!)) } : all
  writeFileSync(join(dir, 'sites-inventory.json'), JSON.stringify(found, null, 1))
  console.log(`${found.calls} runHeadless calls on ${found.sites.length} lines in ${found.files.length} spec files`)
  const tests = join(client, 'tests')
  const config = join(client, 'playwright.i1d-census.config.ts')
  const copies: string[] = []
  const playwright = (label: string, match: string[], env: Record<string, string>) => {
    writeFileSync(config, [
      "import { defineConfig } from '@playwright/test'",
      '// Temporary: written and removed by tests/fixtures/fms-headless-outcome-census-run.ts.',
      `export default defineConfig({ testDir: './tests', testMatch: ${JSON.stringify(match)}, workers: 1, fullyParallel: false, retries: 0, timeout: 30 * 60_000,`,
      `  outputDir: ${JSON.stringify(join(dir, `pw-${label}-output`).replaceAll('\\', '/'))}, reporter: [['line'], ['json', { outputFile: ${JSON.stringify(join(dir, `pw-${label}.json`).replaceAll('\\', '/'))} }]] })`,
      '',
    ].join('\n'))
    const begin = Date.now()
    // The CLI by path, with no shell: the checkout path may contain spaces.
    const result = spawnSync(process.execPath, [join(client, 'node_modules', '@playwright', 'test', 'cli.js'), 'test', '--config', config], {
      cwd: client, env: { ...process.env, ...env }, encoding: 'utf8', maxBuffer: 256 * 1024 * 1024,
    })
    writeFileSync(join(dir, `pw-${label}.log`), `${result.stdout}\n--- stderr ---\n${result.stderr}`)
    console.log(`${label}: exit ${result.status}, ${Math.round((Date.now() - begin) / 1000)} s`)
  }
  const ARMS = { single: { arm: 'single', pure: false }, dual: { arm: 'dual', pure: false }, 'single-pure': { arm: 'single', pure: true }, 'dual-pure': { arm: 'dual', pure: true } } as const
  const chosen = (options.arms ?? 'control,single,dual,single-pure,dual-pure').split(',').filter(Boolean)
  for (const label of chosen) if (label !== 'control' && !(label in ARMS)) throw new Error(`unknown arm ${label}`)
  const labels = chosen.filter((label): label is keyof typeof ARMS => label in ARMS)
  try {
    for (const arm of labels) {
      for (const file of found.files) {
        const source = readFileSync(join(tests, file), 'utf8')
        if (!HAS_SCENARIO_IMPORT.test(source)) throw new Error(`${file} does not import the scenario module by its usual path`)
        // Same lines, same columns where it matters: the import is renamed in place, and every expect made soft.
        const copy = source.replace(SCENARIO_IMPORT, "from './fixtures/fms-headless-outcome-census-shim'").replace(/\bexpect\(/g, 'expect.soft(')
        const target = join(tests, `i1d-${arm}--${file}`)
        writeFileSync(target, copy); copies.push(target)
      }
    }
    if (chosen.includes('control')) playwright('control', found.files, {})
    for (const label of labels) {
      const jsonl = join(dir, `sites-${label}.jsonl`)
      if (existsSync(jsonl)) rmSync(jsonl)
      playwright(label, found.files.map(file => `i1d-${label}--${file}`),
        { AEROLINK_I1D_ARM: ARMS[label].arm, AEROLINK_I1D_OUT: jsonl, AEROLINK_I1D_PURE: ARMS[label].pure ? '1' : '0' })
    }
  } finally {
    for (const copy of copies) rmSync(copy, { force: true })
    rmSync(config, { force: true })
  }
  const after = identity()
  if (JSON.stringify(after) !== JSON.stringify(before)) throw new Error(`the checkout changed during the run (${JSON.stringify(before)} -> ${JSON.stringify(after)})`)
  writeFileSync(join(dir, `sites-checkout-${chosen.join('+')}.json`), JSON.stringify({ checkout: before, node: process.version, arms: chosen }, null, 1))
}

const command = positionals[0]
if (command === 'scenarios') await scenarios()
else if (command === 'sites') sites()
else if (command === 'inventory') console.log(JSON.stringify(inventory(), null, 1))
else throw new Error('usage: scenarios --out <dir> | sites --out <dir> | inventory')
