// Answers, for the branch you are on: what should I run before I push, and what will GitHub run after?
//
// This is the local half of #568. The classification comes from the same module the workflow uses, so
// the two answers cannot disagree — which was the other half of the problem, since a path recognised in
// one place and not the other is invisible until a gate skips something it should have run.
//
// Usage:
//   node product/test-planner/tools/plan.mjs                 # against origin/main
//   node product/test-planner/tools/plan.mjs --base <ref>
//   node product/test-planner/tools/plan.mjs --files a.cs b.ts
//   node product/test-planner/tools/plan.mjs --json
//   node product/test-planner/tools/plan.mjs --event merge_group

import { execFileSync } from 'node:child_process'
import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { classify, explain, localPlan, selectJobs } from '../lib/classify.mjs'

/**
 * Parse options explicitly, stopping a list at the next option.
 *
 * The first version sliced everything after `--files` and dropped only tokens starting with `--`, so
 * `--files README.md --event pull_request` classified the literal path `pull_request` — turning a
 * documentation-only change into an unclassified one and printing a plan for work that was not needed.
 * A parser that silently absorbs another option's value produces confident wrong answers.
 */
const VALUE_OPTIONS = new Set(['base', 'event'])
const LIST_OPTIONS = new Set(['files'])
const FLAG_OPTIONS = new Set(['json', 'help'])

function parseArgs(argv) {
  const options = { files: null, base: null, event: null, json: false, help: false }
  for (let i = 0; i < argv.length; i += 1) {
    const token = argv[i]
    if (!token.startsWith('--')) throw new Error(`Unexpected argument "${token}"; options must start with --.`)
    const name = token.slice(2)
    if (FLAG_OPTIONS.has(name)) { options[name] = true; continue }
    if (VALUE_OPTIONS.has(name)) {
      const value = argv[i + 1]
      if (value === undefined || value.startsWith('--')) throw new Error(`--${name} requires a value.`)
      options[name] = value
      i += 1
      continue
    }
    if (LIST_OPTIONS.has(name)) {
      const values = []
      // Stops at the next option. A path that genuinely begins with a dash can be passed after `--`.
      while (i + 1 < argv.length && !argv[i + 1].startsWith('--')) { values.push(argv[i + 1]); i += 1 }
      if (argv[i + 1] === '--') { i += 1; while (i + 1 < argv.length) { values.push(argv[i + 1]); i += 1 } }
      options[name] = values
      continue
    }
    throw new Error(`Unknown option --${name}.`)
  }
  return options
}

let options
try {
  options = parseArgs(process.argv.slice(2))
} catch (error) {
  console.error(error.message)
  console.error('Usage: node plan.mjs [--base <ref>] [--event <name>] [--files <path>...] [--json]')
  process.exit(2)
}

function changedPaths() {
  if (options.files !== null) return options.files

  const base = options.base ?? 'origin/main'
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
const event = options.event ?? 'pull_request'
const result = classify(paths, { event })

const repoRoot = fileURLToPath(new URL('../../../', import.meta.url))
const workflowText = readFileSync(join(repoRoot, '.github/workflows/ci.yml'), 'utf8')
const jobs = selectJobs(workflowText, result, { event })

if (options.json) {
  console.log(JSON.stringify({ event, changedPaths: paths, classification: result, local: localPlan(result), ci: jobs }, null, 2))
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
console.log('GitHub will then run (read from ci.yml, not restated):')
for (const job of jobs.selected) console.log(`  - ${job.name ?? job.id}${job.always ? '  (always)' : ''}`)

if (result.unclassified) {
  console.log('')
  console.log('One or more changed paths matched no area rule, so backend and client were selected as a')
  console.log('precaution. If this path should map to a narrower set, add it to AREA_PATTERNS in')
  console.log('product/test-planner/lib/classify.mjs — the workflow reads the same definitions.')
}
