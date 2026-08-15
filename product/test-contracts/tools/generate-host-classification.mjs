// #563 criterion 2: classify every API test class as fresh-host, reusable-host, converted, or a
// candidate for non-hosted migration.
//
// The classification is derived, not hand-maintained. It reads the intent inventory produced by
// generate-test-intent.mjs and the current source, so it cannot drift from what the tests actually do —
// a hand-written list would be correct on the day it was written and quietly wrong afterwards.

import { readFileSync, readdirSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'

const repoRoot = fileURLToPath(new URL('../../../', import.meta.url))
const TESTS = join(repoRoot, 'product/tests/AeroLink.Api.Tests')
const intentPath = join(repoRoot, 'product/test-contracts/api-test-intent.json')

const inventory = JSON.parse(readFileSync(intentPath, 'utf8'))

/** Classes already sharing a host, from the #563 pilot tranches. Read from source, not listed here. */
const converted = new Set()
/** Classes whose tests require an isolated host: their intent set includes state the host owns. */
const NEEDS_FRESH = new Set(['filesystem', 'startup-config'])

const sourceByClass = new Map()
for (const entry of readdirSync(TESTS)) {
  if (!entry.endsWith('.cs')) continue
  const cls = entry.replace(/\.cs$/, '')
  const source = readFileSync(join(TESTS, entry), 'utf8')
  sourceByClass.set(cls, source)
  if (/SharedApiHost/.test(source) && cls !== 'SharedApiHost') converted.add(cls)
}

const intentsByClass = new Map()
for (const row of inventory.tests) {
  const set = intentsByClass.get(row.cls) ?? new Set()
  set.add(row.intent)
  intentsByClass.set(row.cls, set)
}

const MIGRATABLE = new Set(['in-process-logic', 'rule-matrix'])

const rows = []
for (const [cls, intents] of intentsByClass) {
  const tests = inventory.tests.filter((row) => row.cls === cls).length
  let classification
  let reason
  if (converted.has(cls)) {
    classification = 'converted'
    reason = 'Already sharing a host through the #563 pilot.'
  } else if ([...intents].every((intent) => MIGRATABLE.has(intent))) {
    classification = 'migration-candidate'
    reason = 'Every test is in-process logic or a rule matrix; it needs a database or nothing, not a host.'
  } else if ([...intents].some((intent) => NEEDS_FRESH.has(intent))) {
    classification = 'fresh-host'
    reason = `Owns host-scoped state (${[...intents].filter((i) => NEEDS_FRESH.has(i)).join(', ')}); sharing would leak it between tests.`
  } else {
    classification = 'reusable-host'
    reason = 'Exercises the HTTP surface with no host-scoped state; a shared host is safe with per-test clients.'
  }
  rows.push({ cls, tests, classification, reason, intents: [...intents].sort() })
}

rows.sort((a, b) => a.classification.localeCompare(b.classification) || b.tests - a.tests || a.cls.localeCompare(b.cls))

const summary = {}
for (const row of rows) {
  summary[row.classification] = summary[row.classification] ?? { classes: 0, tests: 0 }
  summary[row.classification].classes += 1
  summary[row.classification].tests += row.tests
}

const totalClasses = rows.length
const totalTests = rows.reduce((sum, r) => sum + r.tests, 0)

console.log(`Classified ${totalClasses} API test classes (${totalTests} tests)\n`)
console.log('classification         classes   tests   share of tests')
for (const [key, entry] of Object.entries(summary).sort((a, b) => b[1].tests - a[1].tests)) {
  console.log(`${key.padEnd(22)}${String(entry.classes).padStart(8)}${String(entry.tests).padStart(8)}   ${((entry.tests / totalTests) * 100).toFixed(1)}%`)
}

const reusable = summary['reusable-host'] ?? { classes: 0, tests: 0 }
const convertedSummary = summary.converted ?? { classes: 0, tests: 0 }
console.log('')
console.log(`Remaining reuse headroom: ${reusable.classes} classes, ${reusable.tests} tests`)
console.log(`Already converted:        ${convertedSummary.classes} classes, ${convertedSummary.tests} tests`)

const outputPath = process.argv[2] ?? join(repoRoot, 'product/test-contracts/api-host-classification.json')
writeFileSync(outputPath, `${JSON.stringify({
  schemaVersion: 'aerolink-api-host-classification/v1',
  totals: { classes: totalClasses, tests: totalTests },
  summary,
  classes: rows,
}, null, 2)}\n`, 'utf8')
console.log(`\nwrote ${outputPath}`)
