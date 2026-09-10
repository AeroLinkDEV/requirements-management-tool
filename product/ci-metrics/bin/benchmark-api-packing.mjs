// Explicit LOCAL WINDOWS benchmark for the #942 API packing investigation.
//
// Usage:
//   node benchmark-api-packing.mjs --source <source-tree> --discovery <discovery.json> \
//     --observations <api-observations.json> --output <owned-temp-dir>
//
// The command builds the supplied source once per cohort, runs the exact same complete inventory through
// three current/candidate filters, and writes an attributable report. It is never used by ci.yml or the
// protected Product quality gate.

import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { runApiPackingBenchmark, renderApiBenchmarkMarkdown } from '../lib/api-benchmark.mjs'

function args(argv) {
  const result = { source: null, discovery: null, observations: null, output: null }
  for (let index = 0; index < argv.length; index += 1) {
    const flag = argv[index]
    if (flag === '--source') result.source = argv[++index] ?? null
    else if (flag === '--discovery') result.discovery = argv[++index] ?? null
    else if (flag === '--observations') result.observations = argv[++index] ?? null
    else if (flag === '--output') result.output = argv[++index] ?? null
    else throw new Error(`Unknown option '${flag}'.`)
  }
  if (Object.values(result).some((value) => !value)) throw new Error('All of --source, --discovery, --observations, and --output are required.')
  return result
}

async function main() {
  const input = args(process.argv.slice(2))
  for (const [label, path] of [['discovery', input.discovery], ['observations', input.observations]]) if (!existsSync(path)) throw new Error(`${label} input does not exist.`)
  const report = await runApiPackingBenchmark({
    sourceDir: input.source,
    discovery: JSON.parse(readFileSync(input.discovery, 'utf8')),
    observations: JSON.parse(readFileSync(input.observations, 'utf8')),
    outputDir: input.output,
  })
  mkdirSync(input.output, { recursive: true })
  writeFileSync(join(input.output, 'api-packing-benchmark.json'), `${JSON.stringify(report, null, 2)}\n`, 'utf8')
  writeFileSync(join(input.output, 'api-packing-benchmark.md'), `${renderApiBenchmarkMarkdown(report)}\n`, 'utf8')
  console.log(`[ci-metrics] API packing benchmark: verdict=${report.comparison.verdict}; current=${report.comparison.slowestShardWallMs.current ?? 'unavailable'}ms; proposed=${report.comparison.slowestShardWallMs.proposed ?? 'unavailable'}ms`)
  if (report.comparison.verdict === 'insufficient-evidence') process.exitCode = 1
}

main().catch((error) => {
  console.error(`[ci-metrics] API packing benchmark refused: ${error.message}`)
  process.exitCode = 2
})
