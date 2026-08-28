// #566 criterion 1: classify every API test by primary intent and hosted invocation evidence.
//
// The inventory is deliberately conservative. A test whose method body does not show a host operation,
// but whose class contains a host fixture, is `unknown` rather than silently inheriting class-level host
// evidence. Unknown rows cannot be used to claim the criterion-7 escape clause.

import { readFileSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import {
  buildIntentArtifact,
  INVENTORY_SUMMARY_END,
  INVENTORY_SUMMARY_START,
  renderInventorySummary,
} from '../lib/test-intent.mjs'

const repoRoot = fileURLToPath(new URL('../../../', import.meta.url))
const ROOT = join(repoRoot, 'product/tests/AeroLink.Api.Tests')
const artifact = buildIntentArtifact(ROOT)
const summary = { ...artifact.totals, intents: artifact.intents }

function replaceGeneratedSummary(document, rendered) {
  const starts = document.split(INVENTORY_SUMMARY_START).length - 1
  const ends = document.split(INVENTORY_SUMMARY_END).length - 1
  if (starts !== 1 || ends !== 1) {
    throw new Error(`Expected exactly one generated inventory summary (${INVENTORY_SUMMARY_START} ... ${INVENTORY_SUMMARY_END})`)
  }
  const start = document.indexOf(INVENTORY_SUMMARY_START)
  const end = document.indexOf(INVENTORY_SUMMARY_END)
  if (end <= start) throw new Error('Generated inventory summary markers are out of order')
  return `${document.slice(0, start)}${INVENTORY_SUMMARY_START}\n${rendered}\n${INVENTORY_SUMMARY_END}${document.slice(end + INVENTORY_SUMMARY_END.length)}`
}

console.log(`Classified ${summary.tests} test methods (${summary.cases} known cases) across ${summary.classes} classes\n`)
console.log('intent                tests   cases   classes   level')
for (const [key, entry] of Object.entries(summary.intents)) {
  console.log(`${key.padEnd(20)}${String(entry.tests).padStart(6)}${String(entry.cases).padStart(8)}${String(entry.classes).padStart(10)}   ${entry.level}`)
}

console.log('')
console.log(`Explicitly hosted:     ${summary.hostedTests} methods / ${summary.hostedCases} cases`)
console.log(`Hosted candidates:     ${summary.hostedCandidateTests} methods / ${summary.hostedCandidateCases} cases (${((summary.hostedCandidateCases / summary.hostedCases) * 100).toFixed(1)}%)`)
console.log(`Explicitly not hosted:  ${summary.nonHostedTests} methods / ${summary.nonHostedCases} cases`)
console.log(`Host use unknown:       ${summary.unknownHostTests} methods / ${summary.unknownHostCases} cases`)
console.log(`Unknown candidate use:  ${summary.unknownCandidateTests} methods / ${summary.unknownCandidateCases} cases`)
console.log(`#566 criterion 7 target: >= 20% of hosted test invocations removed or consolidated`)
console.log(`  -> static evidence status: ${summary.criterion7}`)

const outputPath = process.argv[2] ?? join(repoRoot, 'product/test-contracts/api-test-intent.json')
const documentationPath = join(repoRoot, 'product/docs/API_TEST_INTENT_INVENTORY.md')
const documentation = readFileSync(documentationPath, 'utf8')
const updatedDocumentation = replaceGeneratedSummary(documentation, renderInventorySummary(artifact))
writeFileSync(outputPath, `${JSON.stringify({
  ...artifact,
}, null, 2)}\n`, 'utf8')
writeFileSync(documentationPath, updatedDocumentation, 'utf8')
console.log(`\nwrote ${outputPath}`)
console.log(`wrote ${documentationPath}`)
