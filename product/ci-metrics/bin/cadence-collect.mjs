// Trusted CI cadence metrics collector for #561.
//
// Queries only GitHub API metadata from default-branch code. It records the before/after cadence metrics
// required by #561 without influencing merge authority or executing pull-request code.

import { mkdirSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { cadenceEntries, buildCadenceReport, CADENCE_WINDOW_DAYS } from '../lib/cadence.mjs'

const env = (name) => process.env[name] ?? ''
const DAY_MS = 24 * 60 * 60 * 1000

async function api(path, { token, apiUrl }) {
  const response = await fetch(`${apiUrl}${path}`, {
    headers: {
      Authorization: `Bearer ${token}`,
      Accept: 'application/vnd.github+json',
      'X-GitHub-Api-Version': '2022-11-28',
    },
  })
  if (!response.ok) throw new Error(`GitHub API ${path} returned ${response.status}.`)
  return response.json()
}

async function listAll(path, options) {
  const items = []
  for (let page = 1; page <= 10; page += 1) {
    const body = await api(`${path}${path.includes('?') ? '&' : '?'}per_page=100&page=${page}`, options)
    const rows = Array.isArray(body) ? body : body.items ?? body.workflow_runs ?? body.jobs ?? []
    items.push(...rows)
    if (rows.length < 100) break
  }
  return items
}

function isoMs(value) {
  if (typeof value !== 'string') return null
  const parsed = Date.parse(value)
  return Number.isFinite(parsed) ? parsed : null
}

function cancelledConsumedMs(jobs) {
  return (Array.isArray(jobs) ? jobs : [])
    .filter((job) => job?.conclusion === 'cancelled')
    .reduce((sum, job) => {
      const start = isoMs(job.started_at)
      const end = isoMs(job.completed_at)
      return sum + (start !== null && end !== null && end >= start ? end - start : 0)
    }, 0)
}

async function main() {
  const token = env('GITHUB_TOKEN')
  const repository = env('GITHUB_REPOSITORY')
  const apiUrl = env('GITHUB_API_URL') || 'https://api.github.com'
  const outputDir = env('CADENCE_OUTPUT_DIR')
  if (!token || !repository || !outputDir) {
    console.error('[ci-cadence] GITHUB_TOKEN, GITHUB_REPOSITORY, and CADENCE_OUTPUT_DIR are required.')
    process.exit(2)
  }

  const options = { token, apiUrl }
  const [productRuns, fastRuns, closedPrs] = await Promise.all([
    listAll(`/repos/${repository}/actions/workflows/ci.yml/runs`, options),
    listAll(`/repos/${repository}/actions/workflows/fast-pr-feedback.yml/runs`, options),
    listAll(`/repos/${repository}/pulls?state=closed&sort=updated&direction=desc`, options),
  ])

  const now = Date.now()
  const mergedPrs = closedPrs.filter((pr) => {
    const merged = isoMs(pr?.merged_at)
    return merged !== null && now - merged <= CADENCE_WINDOW_DAYS * DAY_MS
  })

  // Cancellation cost needs job metadata, but only cancelled Product runs can contribute. Querying jobs for
  // successful runs would add API cost without changing the result.
  const earliest = now - (CADENCE_WINDOW_DAYS + 2) * DAY_MS
  const cancelledRuns = productRuns.filter((run) => run?.conclusion === 'cancelled' && (isoMs(run.created_at) ?? 0) >= earliest)
  const cancelledByRun = new Map()
  for (const run of cancelledRuns) {
    const jobs = await listAll(`/repos/${repository}/actions/runs/${run.id}/jobs?filter=all`, options)
    cancelledByRun.set(run.id, cancelledConsumedMs(jobs))
  }

  const entries = cadenceEntries(mergedPrs, productRuns, fastRuns, cancelledByRun)
  const report = buildCadenceReport(entries)
  mkdirSync(outputDir, { recursive: true })
  writeFileSync(join(outputDir, 'cadence-metrics.json'), `${JSON.stringify(report, null, 2)}\n`, 'utf8')
  writeFileSync(join(outputDir, 'cadence-metrics.md'), `${report.markdown}\n`, 'utf8')
  console.log(`[ci-cadence] Recorded ${entries.length} merged PR(s): ${report.summary.before.merges} before / ${report.summary.after.merges} after #655.`)
}

main().catch((error) => {
  console.error(`[ci-cadence] Collector failed: ${error.message}`)
  process.exit(1)
})
