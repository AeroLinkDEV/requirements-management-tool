// Offline reporter for the #942 API packing shadow analysis.
// Usage: node report-api-packing-shadow.mjs <discovery.json> <observations.json> <output-dir> [shard-count]

import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { join, resolve } from 'node:path'
import process from 'node:process'
import { buildApiPackingShadowReport, renderApiPackingShadowMarkdown } from '../lib/api-packing-shadow.mjs'

const [discoveryPath, observationsPath, outputPath, shardCountText] = process.argv.slice(2)
if (!discoveryPath || !observationsPath || !outputPath || (shardCountText !== undefined && !/^\d+$/.test(shardCountText))) {
  console.error('usage: node report-api-packing-shadow.mjs <discovery.json> <observations.json> <output-dir> [shard-count]')
  process.exit(2)
}
if (!existsSync(discoveryPath) || !existsSync(observationsPath)) {
  console.error('discovery.json and observations.json must both exist.')
  process.exit(2)
}

let discovery
let observations
try {
  discovery = JSON.parse(readFileSync(discoveryPath, 'utf8'))
  observations = JSON.parse(readFileSync(observationsPath, 'utf8'))
} catch {
  console.error('Input files must contain valid JSON.')
  process.exit(2)
}

let report
try {
  report = buildApiPackingShadowReport({
    discovery,
    observations,
    shardCount: shardCountText === undefined ? undefined : Number(shardCountText),
  })
} catch (error) {
  // An incomplete live discovery cannot produce a safe coverage plan.  Keep the CLI failure bounded and
  // free of raw input text, paths or credentials; callers must repair discovery before trying again.
  console.error(`API packing shadow refused: ${error instanceof Error ? error.message : 'invalid input'}`)
  process.exit(1)
}

const outputDir = resolve(outputPath)
mkdirSync(outputDir, { recursive: true })
writeFileSync(join(outputDir, 'api-packing-shadow.json'), `${JSON.stringify(report, null, 2)}\n`, 'utf8')
writeFileSync(join(outputDir, 'api-packing-shadow.md'), `${renderApiPackingShadowMarkdown(report)}\n`, 'utf8')
console.log(`[ci-metrics] API packing shadow: ${report.discovery.testCount} tests across ${report.discovery.classCount} classes; evidence=${report.evidence.valid ? 'validated' : 'fallback'}; adoptionEligible=${report.evidence.adoptionEligible}; noSpeedupClaim=true`)
