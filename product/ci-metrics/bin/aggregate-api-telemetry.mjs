// Combines factory JSONL telemetry with the TRX report into the #563 startup-floor summary.
//
// Usage: node bin/aggregate-api-telemetry.mjs <telemetry.jsonl> <trx> <output-dir>

import { readFileSync, writeFileSync, mkdirSync, existsSync } from 'node:fs'
import { join } from 'node:path'
import { parseTelemetryLines, aggregateApiTelemetry, renderApiTelemetryMarkdown } from '../lib/api-telemetry.mjs'
import { parseTrx } from '../lib/trx.mjs'

const [jsonlPath, trxPath, outputDir] = process.argv.slice(2)
if (!jsonlPath || !trxPath || !outputDir || !existsSync(jsonlPath) || !existsSync(trxPath)) {
  console.error('usage: aggregate-api-telemetry.mjs <telemetry.jsonl> <trx> <output-dir>')
  process.exit(2)
}

const parsed = parseTelemetryLines(readFileSync(jsonlPath, 'utf8'))
const trx = parseTrx(readFileSync(trxPath, 'utf8'))
const report = aggregateApiTelemetry({ factoryRecords: parsed.records, trxTests: trx.tests })
const markdown = renderApiTelemetryMarkdown(report)
mkdirSync(outputDir, { recursive: true })
writeFileSync(join(outputDir, 'api-telemetry.json'), `${JSON.stringify(report, null, 2)}\n`, 'utf8')
writeFileSync(join(outputDir, 'api-telemetry.md'), `${markdown}\n`, 'utf8')
console.log(`[ci-metrics] API telemetry: ${report.totals.tests} tests, ${report.totals.factories} factories, startup ${Math.round(report.totals.startupFraction * 100)}% of wall (${parsed.malformed.length} malformed lines, truncated=${parsed.truncated}).`)
