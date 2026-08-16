// Answers, for the branch you are on: what should I run before I push, and what will GitHub run after?
//
// The classification comes from the same module the workflow uses. This command is intentionally plan-only;
// the Windows wrapper owns optional execution and safety prompts.

import { execFileSync } from 'node:child_process'
import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { classify, explain, localPlan, selectJobs } from '../lib/classify.mjs'
import { PLANNER_VERSION, plannerHash } from '../lib/planner-meta.mjs'

const VALUE_OPTIONS = new Set(['base', 'head', 'event'])
const LIST_OPTIONS = new Set(['files'])
const FLAG_OPTIONS = new Map([
  ['json', 'json'],
  ['help', 'help'],
  ['since-origin-main', 'sinceOriginMain'],
  ['dry-run', 'dryRun'],
])

const USAGE = 'Usage: node plan.mjs [--base <ref>|--since-origin-main] [--head <ref>] [--event <name>] [--files <path>...] [--json] [--dry-run] [--help]'

/** Parse options explicitly, stopping a file list at the next option. */
function parseArgs(argv) {
  const options = { files: null, base: null, head: null, event: null, json: false, help: false, sinceOriginMain: false, dryRun: false }
  for (let i = 0; i < argv.length; i += 1) {
    const token = argv[i]
    if (!token.startsWith('--')) throw new Error(`Unexpected argument "${token}"; options must start with --.`)
    const name = token.slice(2)
    if (FLAG_OPTIONS.has(name)) { options[FLAG_OPTIONS.get(name)] = true; continue }
    if (VALUE_OPTIONS.has(name)) {
      const value = argv[i + 1]
      if (value === undefined || value.startsWith('--')) throw new Error(`--${name} requires a value.`)
      options[name] = value
      i += 1
      continue
    }
    if (LIST_OPTIONS.has(name)) {
      const values = []
      while (i + 1 < argv.length && !argv[i + 1].startsWith('--')) { values.push(argv[i + 1]); i += 1 }
      if (argv[i + 1] === '--') { i += 1; while (i + 1 < argv.length) { values.push(argv[i + 1]); i += 1 } }
      options[name] = values
      continue
    }
    throw new Error(`Unknown option --${name}.`)
  }
  if (options.sinceOriginMain && options.base) throw new Error('--since-origin-main cannot be combined with --base.')
  if (options.files !== null && (options.base || options.head || options.sinceOriginMain)) throw new Error('--files cannot be combined with --base, --head or --since-origin-main.')
  return options
}

let options
try {
  options = parseArgs(process.argv.slice(2))
} catch (error) {
  console.error(error.message)
  console.error(USAGE)
  process.exit(2)
}

if (options.help) {
  console.log(USAGE)
  process.exit(0)
}

function gitText(args) {
  return execFileSync('git', args, { encoding: 'utf8' }).trim()
}

function gitCommitSha(ref) {
  return gitText(['rev-parse', '--verify', `${ref}^{commit}`])
}

function changedPathsFromDiff(base, head) {
  const output = execFileSync('git', ['diff', '--name-status', '--find-renames', '--find-copies', '-z', `${base}...${head}`], { encoding: 'utf8' })
  const fields = output.split('\0').filter(Boolean)
  const paths = []
  for (let index = 0; index < fields.length;) {
    const status = fields[index++]
    if (/^[RC]/.test(status)) paths.push(fields[index++], fields[index++])
    else paths.push(fields[index++])
  }
  return paths.filter((path) => typeof path === 'string' && path.length > 0)
}

const range = { base: null, head: options.head ?? 'HEAD', baseSha: null, headSha: null, mergeBase: null }
function changedPaths() {
  if (options.files !== null) return options.files

  range.base = options.sinceOriginMain ? 'origin/main' : (options.base ?? 'origin/main')
  try {
    range.baseSha = gitCommitSha(range.base)
    range.headSha = gitCommitSha(range.head)
    range.mergeBase = gitText(['merge-base', range.base, range.head])
    // Three dots compare from the merge base. Name-status with rename detection is parsed into both old
    // and new paths so sensitive coverage cannot disappear when a file moves out of a guarded area.
    return changedPathsFromDiff(range.base, range.head)
  } catch (error) {
    console.error(`Could not diff against ${range.base}...${range.head}: ${error.message.split('\n')[0]}`)
    if (range.base === 'origin/main') {
      console.error('A local origin/main ref is required for this mode. Pass --base <available-ref> or --files <paths...>; no fetch or rebase is performed.')
    }
    else {
      console.error('Pass --base/--head or --files <paths...> instead; no fetch or rebase is performed.')
    }
    process.exit(2)
  }
}

const paths = changedPaths()
const event = options.event ?? 'pull_request'
const result = classify(paths, { event })
const repoRoot = fileURLToPath(new URL('../../../', import.meta.url))
const workflowText = readFileSync(join(repoRoot, '.github/workflows/ci.yml'), 'utf8')
const jobs = selectJobs(workflowText, result, { event })
const hash = plannerHash(repoRoot)
const unknownPaths = explain(paths).filter((row) => row.product && row.areas.length === 0 && !row.broad).map((row) => row.path)
const compact = {
  planner: { version: PLANNER_VERSION, hash },
  source: {
    base: range.base,
    head: range.head,
    baseSha: range.baseSha,
    headSha: range.headSha,
    mergeBase: range.mergeBase,
    paths: options.files !== null ? 'explicit' : 'git-diff',
  },
  event,
  areas: { docsOnly: result.docsOnly, backend: result.backend, client: result.client, browser: result.browser, postgresql: result.postgresql },
  unknownPaths,
  ci: { selected: jobs.selected.map((job) => job.id), skipped: jobs.skipped.map((job) => job.id) },
}

if (options.json) {
  console.log(JSON.stringify({
    event,
    changedPaths: paths,
    baseSha: range.baseSha,
    headSha: range.headSha,
    mergeBase: range.mergeBase,
    explain: explain(paths),
    classification: result,
    local: localPlan(result),
    ci: jobs,
    safety: {
      planOnly: true,
      dryRun: options.dryRun || options.json,
      persistentDatabaseTouched: false,
      evidenceRootTouched: false,
      fetchedOrRebased: false,
      remainingFullEvidence: 'GitHub Actions full gate remains authoritative.',
    },
    compact,
  }, null, 2))
  process.exit(0)
}

const tick = (value) => (value ? 'yes' : 'no ')
console.log(`AeroLink test planner ${PLANNER_VERSION}`)
console.log(`Planner hash: ${hash}`)
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
console.log(`  merge base   ${range.mergeBase ?? '(explicit paths; no Git diff)'}`)

console.log('')
console.log(`Safety: ${options.dryRun ? 'dry run; ' : ''}persistent PostgreSQL and evidence roots are untouched; no fetch or rebase is performed.`)
console.log('Full merge evidence remains with the GitHub Actions gate; a local Fast plan never substitutes for it.')
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
console.log('')
console.log(`AEROLINK_TEST_PLAN_RESULT=${JSON.stringify(compact)}`)

if (result.unclassified) {
  console.log('')
  console.log('One or more changed paths matched no area rule, so broad backend, client, browser and PostgreSQL validation was selected as a precaution.')
  console.log('If this path should map to a narrower set, add it to AREA_PATTERNS in product/test-planner/lib/classify.mjs.')
}
