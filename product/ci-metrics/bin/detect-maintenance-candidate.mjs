import { readFileSync, appendFileSync } from 'node:fs'
import {
  compareTrustedSurfacePaths,
  createGitHubRequest,
  fetchDefaultBranch,
  fetchLatestRunJobs,
  fetchWorkflowRun,
} from '../lib/merge-authority-github.mjs'
import { evaluateMergeGroupCandidate } from '../lib/merge-authority.mjs'
import { shouldMintMaintenanceEvidence } from '../lib/maintenance-candidate.mjs'
import { MAINTENANCE_REPOSITORY as repository } from '../lib/maintenance-preflight.mjs'

const output = process.env.GITHUB_OUTPUT
const emit = (name, value) => {
  if (typeof output !== 'string' || output.length === 0) return
  appendFileSync(output, name + '=' + value + '\n')
}
const safe = value => typeof value === 'string' && /^[0-9a-f]{40}$/.test(value)

function queueQuery(number) {
  return `query { repository(owner: "AeroLinkDEV", name: "requirements-management-tool") { pullRequest(number: ${number}) { number headRefOid mergeQueueEntry { position state headCommit { oid } baseCommit { oid } } } } }`
}

async function main() {
  emit('maintenance-needed', 'false')
  try {
    const event = JSON.parse(readFileSync(process.env.GITHUB_EVENT_PATH, 'utf8'))
    const trigger = event?.workflow_run
    if (event?.action !== 'completed' || trigger?.event !== 'merge_group' ||
        trigger?.status !== 'completed' || !safe(trigger?.head_sha) ||
        !Number.isSafeInteger(trigger?.id) || !Number.isSafeInteger(trigger?.run_attempt)) return
    const match = /^gh-readonly-queue\/main\/pr-([1-9][0-9]*)-[0-9a-f]{40}$/.exec(trigger.head_branch || '')
    if (!match) return
    const request = createGitHubRequest({ token: process.env.GITHUB_TOKEN, apiUrl: process.env.GITHUB_API_URL || 'https://api.github.com' })
    const [run, jobs, main, pr, queueResponse] = await Promise.all([
      fetchWorkflowRun({ request, repository, runId: trigger.id }),
      fetchLatestRunJobs({ request, repository, runId: trigger.id }),
      fetchDefaultBranch({ request, repository }),
      request('/repos/' + repository + '/pulls/' + match[1]),
      request('/graphql', { method: 'POST', body: { query: queueQuery(match[1]) } }),
    ])
    const queuePr = queueResponse?.data?.repository?.pullRequest
    const queueEntry = queuePr?.mergeQueueEntry
    const queue = queuePr && queueEntry ? {
      prNumber: queuePr.number,
      prHeadSha: queuePr.headRefOid,
      position: queueEntry.position,
      state: queueEntry.state,
      headSha: queueEntry.headCommit?.oid,
      baseSha: queueEntry.baseCommit?.oid,
    } : null
    const changedPaths = await compareTrustedSurfacePaths({
      request, repository, candidateSha: trigger.head_sha, baseSha: main.sha,
    })
    const expected = { repository, headSha: trigger.head_sha, baseBranch: main.name, runId: trigger.id, runAttempt: trigger.run_attempt }
    const ordinary = evaluateMergeGroupCandidate({ run, jobs, changedPaths, expected })
    if (shouldMintMaintenanceEvidence({
      eventAction: event.action, trigger, run, pr, queue, main, ordinary, changedPaths,
    })) emit('maintenance-needed', 'true')
  } catch {
    // Missing or stale evidence leaves the privileged token absent; the protected verifier then refuses.
  }
}

main()
