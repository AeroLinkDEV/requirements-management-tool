// Explicit operator report (#1152 B4). Read-only: it lists runs and jobs through the GitHub CLI and writes a
// report into a new directory. It never changes workflows, checks, labels or the merge queue.
//
//   node product/ci-metrics/bin/report-fast-leak.mjs <since YYYY-MM-DD> <new-output-directory>

import { execFileSync } from 'node:child_process'
import { mkdirSync, writeFileSync } from 'node:fs'
import { join, resolve } from 'node:path'
import { summarizeFastLeak, renderFastLeak } from '../lib/fast-leak.mjs'

const REPOSITORY = 'AeroLinkDEV/requirements-management-tool'
const [since, output] = process.argv.slice(2)
if (!/^\d{4}-\d{2}-\d{2}$/.test(since ?? '') || !output || process.argv.length !== 4) {
  console.error('Usage: node report-fast-leak.mjs <since YYYY-MM-DD> <new-output-directory>')
  process.exit(2)
}

function get(path) {
  const text = execFileSync('gh', ['api', '--hostname', 'github.com', '--method', 'GET', path], {
    encoding: 'utf8', maxBuffer: 64 * 1024 * 1024, timeout: 120_000, stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true,
  })
  return JSON.parse(text)
}

function allPages(path, key) {
  const rows = []
  for (let page = 1; page <= 30; page += 1) {
    const body = get(`${path}${path.includes('?') ? '&' : '?'}per_page=100&page=${page}`)
    const batch = body?.[key]
    if (!Array.isArray(batch)) throw new Error(`Malformed ${key} page ${page}`)
    rows.push(...batch)
    if (batch.length < 100) return rows
  }
  throw new Error('More than 30 pages; narrow the window.')
}

const prefix = `/repos/${REPOSITORY}/actions/workflows`
const readinessRuns = allPages(`${prefix}/ci.yml/runs?event=workflow_dispatch&created=%3E%3D${since}`, 'workflow_runs')
const fastRuns = allPages(`${prefix}/fast-pr-feedback.yml/runs?created=%3E%3D${since}`, 'workflow_runs')
const failedJobsByRun = {}
for (const run of readinessRuns.filter((r) => r.status === 'completed' && r.conclusion === 'failure')) {
  const jobs = allPages(`/repos/${REPOSITORY}/actions/runs/${run.id}/jobs?filter=latest`, 'jobs')
  failedJobsByRun[run.id] = jobs.filter((job) => job.conclusion === 'failure').map((job) => job.name)
}

const summary = summarizeFastLeak({ readinessRuns, fastRuns, failedJobsByRun })
const directory = resolve(output)
mkdirSync(directory) // Existing output is evidence; never overwrite it.
const until = new Date().toISOString()
writeFileSync(join(directory, 'fast-leak.json'), `${JSON.stringify({ since, until, repository: REPOSITORY, ...summary }, null, 2)}\n`)
writeFileSync(join(directory, 'fast-leak.md'), renderFastLeak(summary, { since, until }))
console.log(`failed readiness ${summary.failedReadiness}; caught ${summary.caught}; leaked ${summary.leaked}; no Fast ${summary.noFast}; leak rate ${summary.leakRate === null ? 'n/a' : (summary.leakRate * 100).toFixed(1) + '%'}`)
