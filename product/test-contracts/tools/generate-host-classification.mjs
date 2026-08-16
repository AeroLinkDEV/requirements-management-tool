// #563 criterion 2: classify every API test class conservatively as fresh-host, reusable-host,
// converted, or a candidate for non-hosted migration.

import { writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { buildIntentArtifact } from '../lib/test-intent.mjs'
import { buildHostArtifact } from '../lib/host-classification.mjs'

const repoRoot = fileURLToPath(new URL('../../../', import.meta.url))
const testsDirectory = join(repoRoot, 'product/tests/AeroLink.Api.Tests')
const inventory = buildIntentArtifact(testsDirectory)
const artifact = buildHostArtifact({ testsDirectory, inventory })
const result = { totals: artifact.totals, summary: artifact.summary }

console.log(`Classified ${result.totals.classes} API test classes (${result.totals.tests} tests)\n`)
console.log('classification         classes   tests   share of tests')
for (const [key, entry] of Object.entries(result.summary).sort((a, b) => b[1].tests - a[1].tests)) {
  console.log(`${key.padEnd(22)}${String(entry.classes).padStart(8)}${String(entry.tests).padStart(8)}   ${((entry.tests / result.totals.tests) * 100).toFixed(1)}%`)
}

const reusable = result.summary['reusable-host'] ?? { classes: 0, tests: 0 }
const converted = result.summary.converted ?? { classes: 0, tests: 0 }
console.log('')
console.log(`Remaining reuse headroom: ${reusable.classes} classes, ${reusable.tests} tests`)
console.log(`Already converted:        ${converted.classes} classes, ${converted.tests} tests`)

const outputPath = process.argv[2] ?? join(repoRoot, 'product/test-contracts/api-host-classification.json')
writeFileSync(outputPath, `${JSON.stringify(artifact, null, 2)}\n`, 'utf8')
console.log(`\nwrote ${outputPath}`)
