// Answers, for the branch you are on: what should I run before I push, and what will GitHub run after?
//
// This is the local half of #568. The classification comes from the same module the workflow uses, so
// the two answers cannot disagree — which was the other half of the problem, since a path recognised in
// one place and not the other is invisible until a gate skips something it should have run.
//
// Usage:
//   node product/test-planner/bin/plan.mjs                 # against origin/main
//   node product/test-planner/bin/plan.mjs --base <ref>
//   node product/test-planner/bin/plan.mjs --files a.cs b.ts
//   node product/test-planner/bin/plan.mjs --json
//   node product/test-planner/bin/plan.mjs --event merge_group

import { execFileSync } from 'node:child_process'
import { classify, explain, localPlan, ciSelection } from '../lib/classify.mjs'

function arg(name, fallback = null) {
  const index = process.argv.indexOf(`--${name}`)
  return index >= 0 && process.argv[index + 1] && !process.argv[index + 1].startsWith('--')
    ? process.argv[index + 1]
    : fallback
}

function changedPaths() {
  const explicit = process.argv.indexOf('--files')
  if (explicit >= 0) return process.argv.slice(explicit + 1).filter((value) => !value.startsWith('--'))

  const base = arg('base', 'origin/main')
  try {
    // Three dots. A two-dot diff compares the two trees directly, so once the base moves ahead every
    // file changed there appears here as though this branch had touched it — which is exactly how a
    // pull request once classified as client-only and skipped the backend suites it was about.
    const output = execFileSync('git', ['diff', '--name-only', `${base}...HEAD`], { encoding: 'utf8' })
    return output.split('\n').map((line) => line.trim()).filter(Boolean)
  } catch (error) {
    console.error(`Could not diff against ${base}: ${error.message.split('\n')[0]}`)
    console.error('Pass --base <ref> or --files <paths...> instead.')
    process.exit(2)
  }
}

const paths = changedPaths()
const event = arg('event', 'pull_request')
const result = classify(paths, { event })

if (process.argv.includes('--json')) {
  console.log(JSON.stringify({ event, changedPaths: paths, classification: result, local: localPlan(result), ci: ciSelection(result) }, null, 2))
  process.exit(0)
}

const tick = (value) => (value ? 'yes' : 'no ')

console.log(`Changed files: ${paths.length}${paths.length === 0 ? ' (nothing to classify)' : ''}`)
if (paths.length > 0 && paths.length <= 20) {
  for (const row of explain(paths)) {
    const areas = row.areas.length > 0 ? row.areas.join(', ') : (row.product ? '(no area matched)' : '(not product code)')
    console.log(`  ${row.path}  ->  ${areas}`)
  }
} else if (paths.length > 20) {
  console.log('  (too many to list individually; use --json for the full breakdown)')
}

console.log('')
console.log(`Classification (event: ${event})`)
console.log(`  docs only    ${tick(result.docsOnly)}`)
console.log(`  backend      ${tick(result.backend)}`)
console.log(`  client       ${tick(result.client)}`)
console.log(`  browser      ${tick(result.browser)}`)
console.log(`  postgresql   ${tick(result.postgresql)}`)
if (result.reason) console.log(`  note: ${result.reason}`)

console.log('')
console.log('Before you push:')
for (const step of localPlan(result)) {
  console.log(`  - ${step.label}`)
  if (step.command) console.log(`      ${step.command}`)
  console.log(`      ${step.why}`)
}

console.log('')
console.log('GitHub will then run:')
for (const job of ciSelection(result)) console.log(`  - ${job}`)

if (result.unclassified) {
  console.log('')
  console.log('One or more changed paths matched no area rule, so backend and client were selected as a')
  console.log('precaution. If this path should map to a narrower set, add it to AREA_PATTERNS in')
  console.log('product/test-planner/lib/classify.mjs — the workflow reads the same definitions.')
}
