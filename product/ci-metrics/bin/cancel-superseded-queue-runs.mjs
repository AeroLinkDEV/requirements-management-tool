// Cancels Product runs whose merge-queue candidate ref was deleted (#1147, #1152 A2).
//
// Runs from the protected default branch on GitHub's `delete` event. It reads only GitHub's own records and
// cancels only runs that `selectSupersededRuns` selects. See lib/queue-supersession.mjs for the rules.

import { appendFileSync } from 'node:fs'
import { selectSupersededRuns, isQueueRef } from '../lib/queue-supersession.mjs'

const env = (name) => process.env[name] ?? ''

async function request(method, path, { token, apiUrl, allow404 = false } = {}) {
  const response = await fetch(`${apiUrl}${path}`, {
    method,
    headers: {
      Authorization: `Bearer ${token}`,
      Accept: 'application/vnd.github+json',
      'X-GitHub-Api-Version': '2022-11-28',
    },
  })
  if (allow404 && response.status === 404) return null
  // 409: the run finished between listing and cancelling, which is the outcome we wanted anyway.
  if (method === 'POST' && response.status === 409) return { alreadyFinished: true }
  if (!response.ok) throw new Error(`GitHub API ${method} ${path} returned ${response.status}.`)
  return response.status === 202 || response.status === 204 ? {} : response.json()
}

async function main() {
  const token = env('GITHUB_TOKEN')
  const apiUrl = env('GITHUB_API_URL') || 'https://api.github.com'
  const repository = env('GITHUB_REPOSITORY')
  const deletedRef = env('DELETED_REF')
  const ownRunId = env('GITHUB_RUN_ID')
  const dryRun = env('CANCEL_DRY_RUN') === 'true'
  if (!token || !repository || !ownRunId) {
    console.error('[queue] GITHUB_TOKEN, GITHUB_REPOSITORY, and GITHUB_RUN_ID are required.')
    process.exit(2)
  }
  if (!isQueueRef(deletedRef)) {
    console.log(`[queue] ${deletedRef.slice(0, 120)} is not a merge-queue candidate ref; nothing to do.`)
    return
  }

  // This run was created by the deletion, so anything created after it is a newer candidate.
  const own = await request('GET', `/repos/${repository}/actions/runs/${ownRunId}`, { token, apiUrl })
  const encodedRef = deletedRef.split('/').map(encodeURIComponent).join('/')
  const ref = await request('GET', `/repos/${repository}/git/ref/heads/${encodedRef}`, { token, apiUrl, allow404: true })
  const currentRefSha = ref?.object?.sha ?? null
  const runsBody = await request('GET', `/repos/${repository}/actions/workflows/ci.yml/runs?event=merge_group&branch=${encodeURIComponent(deletedRef)}&per_page=100`, { token, apiUrl })
  const runs = Array.isArray(runsBody?.workflow_runs) ? runsBody.workflow_runs : []

  const { cancel, keep } = selectSupersededRuns({ deletedRef, runs, currentRefSha, triggeredAt: own.created_at })
  const lines = [`## Superseded merge-queue candidate`, '', `- Deleted ref: \`${deletedRef}\``, `- Ref now: ${currentRefSha ? `\`${currentRefSha}\`` : 'absent'}`]
  for (const run of cancel) {
    if (dryRun) {
      lines.push(`- Would cancel run ${run.id} (\`${run.head_sha}\`, ${run.status})`)
      continue
    }
    const result = await request('POST', `/repos/${repository}/actions/runs/${run.id}/cancel`, { token, apiUrl })
    lines.push(result?.alreadyFinished
      ? `- Run ${run.id} finished before it could be cancelled`
      : `- Cancelled run ${run.id} (\`${run.head_sha}\`, was ${run.status})`)
  }
  for (const entry of keep) lines.push(`- Kept run ${entry.id}: ${entry.reason}`)
  if (cancel.length === 0 && keep.length === 0) lines.push('- No Product run was found for this ref.')

  const summary = lines.join('\n')
  console.log(summary)
  const summaryPath = env('GITHUB_STEP_SUMMARY')
  if (summaryPath) appendFileSync(summaryPath, `${summary}\n`, 'utf8')
}

main().catch((error) => {
  console.error(`[queue] Cancelling superseded candidates failed: ${error.message}`)
  process.exit(1)
})
