// Appends one named timestamp marker to the job's metrics timing file.
//
// Metrics are never merge authority, so a missing environment or an unwritable timing file warns and exits
// zero rather than failing the product job that happens to be instrumented.

import { appendFileSync, mkdirSync } from 'node:fs'
import { dirname } from 'node:path'

const name = process.argv[2]
const timingFile = process.env.METRICS_TIMING_FILE

if (!name) {
  console.error('[ci-metrics] mark.mjs requires a marker name.')
  process.exit(2)
}
if (!timingFile) {
  console.error('[ci-metrics] METRICS_TIMING_FILE is not set; marker skipped (metrics are non-authoritative).')
  process.exit(0)
}

try {
  mkdirSync(dirname(timingFile), { recursive: true })
  appendFileSync(timingFile, `${JSON.stringify({ name, at: Date.now() })}\n`, 'utf8')
} catch (error) {
  console.error(`[ci-metrics] Could not write timing marker "${name}": ${error.message}`)
}
process.exit(0)
