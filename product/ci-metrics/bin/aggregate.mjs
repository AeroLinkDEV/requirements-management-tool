// Aggregates one run's fragments into run-metrics.json and run-metrics.md.
//
// Usage: node bin/aggregate.mjs <fragments-directory> <output-directory> [run-meta.json]
// The optional run-meta.json may contain {"queueDelayMs": <number|null>} from a trusted default-branch
// source; values are only numbers, never commands or paths.

import { mkdirSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { readFragments, aggregateFragments, renderMarkdown } from '../lib/aggregate.mjs'

const [fragmentsDir, outputDir, runMetaPath] = process.argv.slice(2)
if (!fragmentsDir || !outputDir) {
  console.error('usage: aggregate.mjs <fragments-directory> <output-directory> [run-meta.json]')
  process.exit(2)
}

let runMeta = null
if (runMetaPath) {
  try {
    runMeta = JSON.parse(await import('node:fs').then((fs) => fs.readFileSync(runMetaPath, 'utf8')))
    if (runMeta.queueDelayMs !== undefined && runMeta.queueDelayMs !== null && !Number.isInteger(runMeta.queueDelayMs)) {
      throw new Error('runMeta.queueDelayMs must be an integer or null.')
    }
  } catch (error) {
    console.error(`[ci-metrics] run-meta ignored because it could not be read safely: ${error.message}`)
    runMeta = null
  }
}

const { fragments, missing, truncated } = readFragments(fragmentsDir)
const missingWithTruncation = truncated
  ? [...missing, { job: 'fragments-directory', reason: 'Fragment count exceeded the bounded limit; the remainder was not read.' }]
  : missing
const merged = aggregateFragments({ fragments, missing: missingWithTruncation, runMeta })
const markdown = renderMarkdown(merged)

mkdirSync(outputDir, { recursive: true })
writeFileSync(join(outputDir, 'run-metrics.json'), `${JSON.stringify(merged, null, 2)}\n`, 'utf8')
writeFileSync(join(outputDir, 'run-metrics.md'), `${markdown}\n`, 'utf8')

console.log(`[ci-metrics] Aggregated ${fragments.length} valid fragments; ${missing.length} missing/unreadable.`)
console.log(`[ci-metrics] Critical path: ${merged.criticalPath.job ?? 'unavailable'} (${merged.criticalPath.durationMs === null ? 'unknown' : `${Math.round(merged.criticalPath.durationMs / 1000)}s`}).`)
console.log(`[ci-metrics] Wrote ${join(outputDir, 'run-metrics.json')} and ${join(outputDir, 'run-metrics.md')}.`)
