// Explicit operator diagnostic only. This command never changes workflow outputs or checks.
import { mkdirSync, writeFileSync } from 'node:fs'
import { resolve, join } from 'node:path'
import { collectQueueReuseShadow } from '../lib/queue-reuse-observer.mjs'
import { renderQueueReuseShadow } from '../lib/queue-reuse-shadow.mjs'

const [runId, prNumber, fallbackRunId, output] = process.argv.slice(2)
if (![runId, prNumber, fallbackRunId].every(v => /^[1-9][0-9]*$/.test(v ?? '')) || !output || process.argv.length !== 6) {
  console.error('Usage: node report-queue-reuse-shadow.mjs <native-queue-run> <merged-pr> <main-diagnostic-run> <new-output-directory>')
  process.exit(2)
}
const directory = resolve(output)
mkdirSync(directory) // Existing output is evidence, never overwrite it.
const { report, packet } = await collectQueueReuseShadow({ runId: Number(runId), prNumber: Number(prNumber), fallbackRunId: Number(fallbackRunId) })
writeFileSync(join(directory, 'queue-reuse-shadow.json'), JSON.stringify(report, null, 2) + '\n')
writeFileSync(join(directory, 'queue-reuse-evidence.json'), JSON.stringify(packet, null, 2) + '\n')
writeFileSync(join(directory, 'queue-reuse-shadow.md'), renderQueueReuseShadow(report))
console.log(`${report.outcome}; shadow-only; canSkip=false; ${report.observerMs} ms`)
