// The CI half of #568: the `changes` job's classifier, reading the same rules as the local planner.
//
// This replaces roughly fifty lines of inline bash. The bash was correct, and it is not being replaced
// because it was wrong — it is being replaced because it was the *only* copy, so nothing outside the
// workflow could answer "what will CI run for this change?", and a local answer could drift from the
// real one with nothing to detect it.
//
// Writes GitHub Actions outputs; prints the decision and its reasoning to the job log.

import { appendFileSync, readFileSync } from 'node:fs'
import { execFileSync } from 'node:child_process'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { classify, explain, BROAD_EVENTS, selectJobs } from '../lib/classify.mjs'
import { PLANNER_VERSION, plannerHash } from '../lib/planner-meta.mjs'

const env = (name) => process.env[name] ?? ''

// GitHub's environment-file parser treats each physical line as a separate output assignment. Paths and
// classifier reasons originate in the PR tree, so a filename containing CR/LF must never be able to add or
// overwrite another output. Keep the values readable while making every emitted value one physical line.
function outputSafe(value) {
  return String(value ?? '').replace(/[\u0000-\u001f\u007f-\u009f\u2028\u2029]/g, (character) => {
    return `\\u${character.codePointAt(0).toString(16).padStart(4, '0')}`
  })
}

function outputLine(name, value) {
  const safeValue = outputSafe(value)
  if (/[\r\n]/.test(safeValue)) throw new Error(`Output value for ${name} was not reduced to one line.`)
  return `${name}=${safeValue}`
}

const event = env('EVENT_NAME')
const baseSha = env('BASE_SHA')
const headSha = env('HEAD_SHA')
const outputPath = env('GITHUB_OUTPUT')

let paths = []
if (!BROAD_EVENTS.has(event)) {
  if (!baseSha || !headSha) {
    console.error(`::error::Event ${event} supplied no base (${baseSha || 'empty'}) or head (${headSha || 'empty'}) to diff against.`)
    process.exit(1)
  }
  // Three dots, not two. A two-dot diff compares the two trees directly, so once main moves ahead of the
  // branch every file changed on main appears here as though the pull request had touched it. That is
  // not cosmetic: on 2026-08-13 it silently changed which gates ran, and a pull request that modified
  // only the workflow classified as client-only on its second run — the backend suites skipped and the
  // gate went green having never run the tests the change was about.
  // Include both sides of renames. A rename out of a migration or identity area must keep the old
  // sensitive path in the classification or the real-provider gate can silently disappear.
  const output = execFileSync('git', ['diff', '--name-status', '--find-renames', '--find-copies', '-z', `${baseSha}...${headSha}`], { encoding: 'utf8' })
  const fields = output.split('\0').filter(Boolean)
  for (let index = 0; index < fields.length;) {
    const status = fields[index++]
    if (/^[RC]/.test(status)) {
      paths.push(fields[index++], fields[index++])
    } else {
      paths.push(fields[index++])
    }
  }
  for (const path of paths) console.log(path)
}

const result = classify(paths, { event })

const repoRoot = fileURLToPath(new URL('../../../', import.meta.url))
const workflowText = readFileSync(join(repoRoot, '.github/workflows/ci.yml'), 'utf8')
const jobs = selectJobs(workflowText, result, { event })
const decisions = {
  selected: jobs.selected.map((job) => ({ id: job.id, name: job.name ?? job.id, reason: job.always ? 'always-running reporting job' : `condition matched: ${job.condition ?? 'none'}` })),
  skipped: jobs.skipped.map((job) => ({ id: job.id, name: job.name ?? job.id, reason: `condition not matched: ${job.condition ?? 'none'}` })),
}
const unknownPaths = explain(paths).filter((row) => row.product && row.areas.length === 0 && !row.broad).map((row) => row.path)
const plannerHashValue = plannerHash(repoRoot)

console.log('')
console.log(`Event: ${event}`)
console.log(`Changed files: ${paths.length}`)
for (const row of explain(paths).slice(0, 100)) {
  console.log(`  ${row.path} -> ${row.areas.length > 0 ? row.areas.join(', ') : (row.product ? '(no area matched)' : '(not product code)')}`)
}
console.log('')
console.log(`docs_only=${result.docsOnly} backend=${result.backend} client=${result.client} browser=${result.browser} postgresql=${result.postgresql}`)
if (result.reason) console.log(result.reason)
console.log(`planner_version=${PLANNER_VERSION}`)
console.log(`planner_hash=${plannerHashValue}`)
console.log(`planner_unknown_paths=${unknownPaths.length > 0 ? unknownPaths.join(', ') : '(none)'}`)
console.log(`planner_selected_jobs=${decisions.selected.map((job) => job.id).join(', ') || '(none)'}`)
console.log(`planner_skipped_jobs=${decisions.skipped.map((job) => job.id).join(', ') || '(none)'}`)

if (!outputPath) {
  console.error('::error::GITHUB_OUTPUT is not set; the classification could not be published.')
  process.exit(1)
}

appendFileSync(outputPath, [
  outputLine('docs_only', result.docsOnly),
  outputLine('backend', result.backend),
  outputLine('client', result.client),
  outputLine('browser', result.browser),
  outputLine('postgresql', result.postgresql),
  outputLine('launchers_only', result.launchersOnly === true),
  outputLine('planner_version', PLANNER_VERSION),
  outputLine('planner_hash', plannerHashValue),
  outputLine('planner_unknown_paths', unknownPaths.join(', ')),
  outputLine('planner_reason', result.reason ?? ''),
  outputLine('planner_selected_jobs', decisions.selected.map((job) => job.id).join(',')),
  outputLine('planner_skipped_jobs', decisions.skipped.map((job) => job.id).join(',')),
  outputLine('planner_decisions', JSON.stringify(decisions)),
  '',
].join('\n'), 'utf8')
