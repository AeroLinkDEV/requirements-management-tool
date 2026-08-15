// Updates the single durable CI regression tracker issue.
//
// Reads the rolling report produced by rolling-collect.mjs. If no sustained regressions exist, the issue
// is corrected to say so when one already exists, and none is created when one does not (no issue spam).
// If regressions exist, one fixed-title issue is created when missing and its body is replaced with the
// current evidence. Requires `issues: write`.

import { readFileSync } from 'node:fs'
import { trackerBody, decideTrackerAction } from '../lib/rolling.mjs'

const env = (name) => process.env[name] ?? ''
const TRACKER_TITLE = 'CI rolling regression tracker'

async function api(path, options = {}, method = 'GET') {
  const response = await fetch(`${env('GITHUB_API_URL') || 'https://api.github.com'}${path}`, {
    method,
    headers: {
      Authorization: `Bearer ${env('GITHUB_TOKEN')}`,
      Accept: 'application/vnd.github+json',
      'X-GitHub-Api-Version': '2022-11-28',
    },
    body: options.body ? JSON.stringify(options.body) : undefined,
  })
  if (!response.ok && response.status !== 404) throw new Error(`GitHub API ${path} returned ${response.status}.`)
  return response.status === 404 ? null : response.json()
}

async function main() {
  const reportPath = env('ROLLING_REPORT_PATH')
  if (!reportPath || !env('GITHUB_TOKEN') || !env('GITHUB_REPOSITORY')) {
    console.error('[ci-metrics] ROLLING_REPORT_PATH, GITHUB_TOKEN, and GITHUB_REPOSITORY are required.')
    process.exit(2)
  }
  const report = JSON.parse(readFileSync(reportPath, 'utf8'))
  const repository = env('GITHUB_REPOSITORY')
  // The search runs before the decision now. It used to be skipped whenever there were no regressions,
  // which is what made a cleared regression unreportable: the code never learned a tracker was there.
  const search = await api(`/search/issues?q=repo:${repository}+in:title+"${encodeURIComponent(TRACKER_TITLE)}"+type:issue+state:open`)
  const existing = search?.items?.[0] ?? null
  const decision = decideTrackerAction({ regressions: report.regressions, trackerExists: existing !== null })
  if (decision.action === 'none') {
    console.log(`[ci-metrics] ${decision.reason}`)
    return
  }
  const body = trackerBody(report)
  if (decision.action === 'update') {
    await api(`/repos/${repository}/issues/${existing.number}`, { body: { body } }, 'PATCH')
    console.log(`[ci-metrics] Updated regression tracker issue #${existing.number}. ${decision.reason}`)
  } else {
    const created = await api(`/repos/${repository}/issues`, { body: { title: TRACKER_TITLE, body } }, 'POST')
    console.log(`[ci-metrics] Created regression tracker issue #${created.number}. ${decision.reason}`)
  }
}

main().catch((error) => {
  console.error(`[ci-metrics] Regression tracker update failed: ${error.message}`)
  process.exit(1)
})
