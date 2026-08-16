// #563 criterion 2: classify every API test class conservatively as fresh-host, reusable-host,
// converted, or a candidate for non-hosted migration.

import { readFileSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { buildIntentArtifact } from '../lib/test-intent.mjs'
import { buildHostArtifact } from '../lib/host-classification.mjs'

const repoRoot = fileURLToPath(new URL('../../../', import.meta.url))
const testsDirectory = join(repoRoot, 'product/tests/AeroLink.Api.Tests')
const inventory = buildIntentArtifact(testsDirectory)
const overrides = JSON.parse(readFileSync(join(repoRoot, 'product/test-contracts/api-host-classification-overrides.json'), 'utf8')).classes
const artifact = buildHostArtifact({ testsDirectory, inventory, overrides })
const result = { totals: artifact.totals, summary: artifact.summary }

console.log(`Classified ${result.totals.classes} API test classes (${result.totals.tests} methods, ${result.totals.knownCases} known cases)\n`)
console.log('classification         classes   methods   known cases   unknown-case methods   share of methods')
for (const [key, entry] of Object.entries(result.summary).sort((a, b) => b[1].tests - a[1].tests)) {
  console.log(`${key.padEnd(22)}${String(entry.classes).padStart(8)}${String(entry.tests).padStart(9)}${String(entry.knownCases).padStart(8)}${String(entry.unknownCaseTests).padStart(17)}   ${((entry.tests / result.totals.tests) * 100).toFixed(1)}%`)
}

const reusable = result.summary['reusable-host'] ?? { classes: 0, tests: 0 }
const converted = result.summary.converted ?? { classes: 0, tests: 0 }
console.log('')
console.log(`Remaining reuse headroom: ${reusable.classes} classes, ${reusable.tests} methods, ${reusable.knownCases} known cases`)
console.log(`Already converted:        ${converted.classes} classes, ${converted.tests} methods, ${converted.knownCases} known cases`)

const outputPath = process.argv[2] ?? join(repoRoot, 'product/test-contracts/api-host-classification.json')
writeFileSync(outputPath, `${JSON.stringify(artifact, null, 2)}\n`, 'utf8')
console.log(`\nwrote ${outputPath}`)
