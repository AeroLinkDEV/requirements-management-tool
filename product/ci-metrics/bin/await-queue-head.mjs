// #1156: first step of the protected-main binding job. Holds a protected-diff candidate that is not yet first
// in the merge queue until the entries ahead merge, so it is judged at the head instead of being refused.
// Runs with the ordinary read-only workflow token, before any App token is minted.
import { appendFileSync, readFileSync } from 'node:fs'
import { compareTrustedSurfaces, createGitHubRequest, fetchDefaultBranch } from '../lib/merge-authority-github.mjs'
import { MAINTENANCE_REPOSITORY as repository } from '../lib/maintenance-preflight.mjs'
import { awaitQueueHead, QUEUE_BRANCH_PATTERN } from '../lib/queue-head-wait.mjs'

// Below the job timeout and the queue's own check timeout; an exhausted budget falls back to today's verifier.
const BUDGET_MS = 60 * 60 * 1000
const INTERVAL_MS = 60 * 1000

function emit(outcome) {
  if (process.env.GITHUB_OUTPUT) appendFileSync(process.env.GITHUB_OUTPUT, `superseded=${outcome === 'superseded'}\n`)
}

async function main() {
  const event = JSON.parse(readFileSync(process.env.GITHUB_EVENT_PATH, 'utf8'))
  const trigger = event?.workflow_run
  const match = QUEUE_BRANCH_PATTERN.exec(trigger?.head_branch ?? '')
  if (event?.action !== 'completed' || trigger?.event !== 'merge_group' || !match) {
    emit('evaluate')
    return
  }
  const prNumber = Number(match[1])
  const candidateSha = trigger.head_sha
  const request = createGitHubRequest({ token: process.env.GITHUB_TOKEN, apiUrl: process.env.GITHUB_API_URL || 'https://api.github.com' })
  const query = `query { repository(owner: "AeroLinkDEV", name: "requirements-management-tool") { pullRequest(number: ${prNumber}) { mergeQueueEntry { position state headCommit { oid } } } } }`
  const result = await awaitQueueHead({
    candidateSha,
    budgetMs: BUDGET_MS,
    intervalMs: INTERVAL_MS,
    readProtectedDiff: async () => {
      const main = await fetchDefaultBranch({ request, repository })
      return compareTrustedSurfaces({ request, repository, candidateSha, baseSha: main.sha })
    },
    readEntry: async () => {
      const response = await request('/graphql', { method: 'POST', body: { query } })
      if (Array.isArray(response?.errors) && response.errors.length) throw new Error('Queue query failed.')
      const pr = response?.data?.repository?.pullRequest
      if (!pr) throw new Error('Pull request not found.')
      return pr.mergeQueueEntry ?? null
    },
    log: message => console.log(`[queue-head] ${message}`),
  })
  console.log(`[queue-head] ${result.outcome}: ${result.reason}`)
  emit(result.outcome)
}

main().catch(() => {
  // Never block the verifier: any failure here hands the decision back to it unchanged.
  console.error('[queue-head] Wait step failed; handing the candidate to the verifier.')
  emit('evaluate')
})
