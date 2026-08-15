// The CI half of #568: the `changes` job's classifier, reading the same rules as the local planner.
//
// This replaces roughly fifty lines of inline bash. The bash was correct, and it is not being replaced
// because it was wrong — it is being replaced because it was the *only* copy, so nothing outside the
// workflow could answer "what will CI run for this change?", and a local answer could drift from the
// real one with nothing to detect it.
//
// Writes GitHub Actions outputs; prints the decision and its reasoning to the job log.

import { appendFileSync } from 'node:fs'
import { execFileSync } from 'node:child_process'
import { classify, explain, BROAD_EVENTS } from '../lib/classify.mjs'

const env = (name) => process.env[name] ?? ''

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
  const output = execFileSync('git', ['diff', '--name-only', `${baseSha}...${headSha}`], { encoding: 'utf8' })
  paths = output.split('\n').map((line) => line.trim()).filter(Boolean)
  for (const path of paths) console.log(path)
}

const result = classify(paths, { event })

console.log('')
console.log(`Event: ${event}`)
console.log(`Changed files: ${paths.length}`)
for (const row of explain(paths).slice(0, 100)) {
  console.log(`  ${row.path} -> ${row.areas.length > 0 ? row.areas.join(', ') : (row.product ? '(no area matched)' : '(not product code)')}`)
}
console.log('')
console.log(`docs_only=${result.docsOnly} backend=${result.backend} client=${result.client} browser=${result.browser} postgresql=${result.postgresql}`)
if (result.reason) console.log(result.reason)

if (!outputPath) {
  console.error('::error::GITHUB_OUTPUT is not set; the classification could not be published.')
  process.exit(1)
}

appendFileSync(outputPath, [
  `docs_only=${result.docsOnly}`,
  `backend=${result.backend}`,
  `client=${result.client}`,
  `browser=${result.browser}`,
  `postgresql=${result.postgresql}`,
  '',
].join('\n'), 'utf8')
