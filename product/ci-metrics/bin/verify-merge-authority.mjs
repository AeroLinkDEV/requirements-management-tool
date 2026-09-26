// Trusted default-branch verifier and App-bound check publisher for merge-queue candidates (#549).

import { appendFileSync, readFileSync } from 'node:fs'
import { collectMaintenanceReview } from '../lib/maintenance-approval-github.mjs'
import { createMaintenanceRulesetReader } from '../lib/maintenance-evidence-reader.mjs'
import { announceMaintenanceReview } from '../lib/maintenance-approval.mjs'
import { trustedMaintenanceContext } from '../lib/maintenance-runtime.mjs'
import { evaluateMergeGroupCandidate, TRUSTED_SURFACE_PREFIXES } from '../lib/merge-authority.mjs'
import {
  compareTrustedSurfaces,
  createGitHubRequest,
  deriveDocumentationOnlyCandidate,
  fetchDefaultBranch,
  fetchLatestRunJobs,
  fetchWorkflowRun,
  publishMergeAuthorityCheck,
} from '../lib/merge-authority-github.mjs'
import { QUEUE_BRANCH_PATTERN } from '../lib/queue-head-wait.mjs'
// The protected classifier from this default-branch checkout, never the candidate's (#1152 A3).
import { isDocumentationOnlyChange } from '../../test-planner/lib/classify.mjs'

/**
 * The candidate's documentation-only status, or false on any doubt. The queue entry's base commit comes
 * from GitHub's merge-queue record for the pull request named by the queue ref, so the diff is pinned to
 * what the queue composed.
 */
async function documentationOnlyEvidence({ request, repository, trigger, headSha }) {
  try {
    const match = QUEUE_BRANCH_PATTERN.exec(trigger?.head_branch ?? '')
    if (!match) return { documentationOnly: false, reason: 'not a main queue ref' }
    const query = `query { repository(owner: "AeroLinkDEV", name: "requirements-management-tool") { pullRequest(number: ${match[1]}) { mergeQueueEntry { headCommit { oid } baseCommit { oid } } } } }`
    const response = await request('/graphql', { method: 'POST', body: { query } })
    const entry = response?.data?.repository?.pullRequest?.mergeQueueEntry
    if (!entry || entry.headCommit?.oid !== headSha) return { documentationOnly: false, reason: 'the queue entry is gone or holds a different candidate' }
    return await deriveDocumentationOnlyCandidate({
      request, repository, candidateSha: headSha, queueBaseSha: entry.baseCommit?.oid, isDocumentationOnlyChange,
    })
  } catch (error) {
    return { documentationOnly: false, reason: `documentation-only derivation failed: ${safeMessage(error)}` }
  }
}

const env = (name) => process.env[name] ?? ''
const SHA_PATTERN = /^[0-9a-f]{40}$/

function requiredEnv(name) {
  const value = env(name)
  if (!value) throw new Error(`${name} is required.`)
  return value
}

function safeMessage(error) {
  return String(error?.message ?? error ?? 'unknown error').replace(/\r?\n/g, ' ').slice(0, 500)
}

async function main() {
  const repository = requiredEnv('GITHUB_REPOSITORY')
  const event = JSON.parse(readFileSync(requiredEnv('GITHUB_EVENT_PATH'), 'utf8'))
  const trigger = event?.workflow_run
  const action = event?.action
  const headSha = trigger?.head_sha
  if (typeof headSha !== 'string' || !SHA_PATTERN.test(headSha)) {
    throw new Error('The workflow_run payload did not contain a publishable candidate head SHA.')
  }

  const authorityRequest = createGitHubRequest({
    token: requiredEnv('MERGE_AUTHORITY_TOKEN'),
    apiUrl: env('GITHUB_API_URL') || 'https://api.github.com',
  })

  // workflow_run emits in_progress for both an initial run and a rerun (requested is omitted for reruns).
  // Replace any earlier success before reading or waiting on the new attempt, so old authority cannot bridge
  // the interval in which a newer Product attempt is active.
  if (action === 'in_progress') {
    if (trigger?.status !== 'in_progress') {
      throw new Error('The workflow_run in_progress action did not carry in-progress run status.')
    }
    await publishMergeAuthorityCheck({
      request: authorityRequest,
      repository,
      headSha,
      decision: 'PENDING',
      reasons: [],
      detailsUrl: trigger?.html_url,
    })
    console.log('[merge-authority] PENDING: a Product attempt is active; prior authority was invalidated')
    return
  }
  if (action !== 'completed') {
    throw new Error(`Unsupported workflow_run action '${action ?? 'unknown'}'.`)
  }

  // A completed-event handler may start while an earlier success is still the latest App check. Replace it
  // before collecting evidence; if a rerun begins during collection, the in-progress workflow preempts this
  // handler and the live recheck below prevents a stale-attempt PASS even if cancellation arrives late.
  await publishMergeAuthorityCheck({
    request: authorityRequest,
    repository,
    headSha,
    decision: 'PENDING',
    reasons: [],
    detailsUrl: trigger?.html_url,
  })

  const evidenceRequest = createGitHubRequest({
    token: requiredEnv('GITHUB_TOKEN'),
    apiUrl: env('GITHUB_API_URL') || 'https://api.github.com',
  })

  let decision
  let detailsUrl = trigger?.html_url
  try {
    if (typeof trigger?.id !== 'number' || typeof trigger?.run_attempt !== 'number') {
      throw new Error('The workflow_run payload did not bind a numeric run id and run attempt.')
    }
    const [run, jobs, defaultBranch] = await Promise.all([
      fetchWorkflowRun({ request: evidenceRequest, repository, runId: trigger.id }),
      fetchLatestRunJobs({ request: evidenceRequest, repository, runId: trigger.id }),
      fetchDefaultBranch({ request: evidenceRequest, repository }),
    ])
    detailsUrl = run.detailsUrl || detailsUrl
    const changedPaths = await compareTrustedSurfaces({
      request: evidenceRequest,
      repository,
      candidateSha: headSha,
      baseSha: defaultBranch.sha,
    })
    const documentation = await documentationOnlyEvidence({ request: evidenceRequest, repository, trigger, headSha })
    console.log(documentation.documentationOnly
      ? `[merge-authority] Documentation-only candidate: ${documentation.paths.length} path(s); the documentation topology applies.`
      : `[merge-authority] Full gate set required: ${documentation.reason}`)
    decision = evaluateMergeGroupCandidate({
      run,
      jobs,
      changedPaths,
      documentationOnlyCandidate: documentation.documentationOnly,
      expected: {
        repository,
        headSha,
        baseBranch: defaultBranch.name,
        runId: trigger.id,
        runAttempt: trigger.run_attempt,
      },
    })
  } catch (error) {
    // Evidence collection is part of the authorization decision. An API/schema/tree error cannot be
    // interpreted as an empty diff or an absent job; it becomes an explicit refusal instead.
    decision = {
      decision: 'REFUSE',
      reasons: [
        `evidence-collection-failed: ${safeMessage(error)}`,
        ...TRUSTED_SURFACE_PREFIXES.map((prefix) => `trusted-surface-unverified: '${prefix}' could not be compared`),
      ],
    }
  }

  const currentRun = await fetchWorkflowRun({
    request: evidenceRequest,
    repository,
    runId: trigger.id,
  })
  if (currentRun.status !== 'completed' || currentRun.runAttempt !== trigger.run_attempt) {
    console.log(
      `[merge-authority] PENDING: Product run advanced from completed attempt ${trigger.run_attempt} ` +
      `to status=${currentRun.status ?? 'unknown'} attempt=${currentRun.runAttempt ?? 'unknown'}`,
    )
    return
  }

  // Protected changes remain refused by the ordinary evaluator. Only a complete, opt-in maintenance
  // packet can start a separate owner review; the App check stays pending and no PASS is published here.
  if (decision.decision === 'REFUSE' && decision.reasons.length > 0 &&
      decision.reasons.every(reason => reason.startsWith('trusted-surface-modified:'))) {
    try {
      const context = trustedMaintenanceContext(event)
      const evidenceAppId = Number(requiredEnv('MAINTENANCE_EVIDENCE_APP_ID'))
      const evidenceInstallationId = Number(requiredEnv('MAINTENANCE_EVIDENCE_INSTALLATION_ID'))
      const rulesetReader = createMaintenanceRulesetReader({
        token: requiredEnv('MAINTENANCE_EVIDENCE_TOKEN'),
        expectedAppId: evidenceAppId,
        expectedInstallationId: evidenceInstallationId,
        expectedAppSlug: requiredEnv('MAINTENANCE_EVIDENCE_APP_SLUG'),
        actionAppSlug: requiredEnv('MAINTENANCE_EVIDENCE_ACTION_APP_SLUG'),
        actionInstallationId: Number(requiredEnv('MAINTENANCE_EVIDENCE_ACTION_INSTALLATION_ID')),
        apiUrl: env('GITHUB_API_URL') || 'https://api.github.com',
      })
      const review = await collectMaintenanceReview({ ...context, read: evidenceRequest, rulesetReader,
        graphql: query => evidenceRequest('/graphql', { method: 'POST', body: { query } }) })
      const summaryPath = requiredEnv('GITHUB_STEP_SUMMARY')
      const outputPath = requiredEnv('GITHUB_OUTPUT')
      announceMaintenanceReview(review, {
        appendSummary: text => appendFileSync(summaryPath, text),
        appendOutput: text => appendFileSync(outputPath, text),
        log: line => console.log(line),
      })
      return
    } catch (error) {
      // Missing configuration, request, evidence or current identity is a refusal, never an exception. The log
      // names the cause (our own error text, never a response body) so a refusal can be diagnosed (#1164).
      console.log(`[merge-authority] maintenance review not prepared: ${safeMessage(error)}`)
      decision.reasons.push('maintenance-review-not-prepared: opt-in, configuration or current evidence failed verification')
    }
  }

  await publishMergeAuthorityCheck({
    request: authorityRequest,
    repository,
    headSha,
    decision: decision.decision,
    reasons: decision.reasons,
    detailsUrl,
  })
  console.log(`[merge-authority] ${decision.decision}: ${decision.reasons.join('; ') || 'all required evidence is bound'}`)
  if (decision.decision !== 'PASS') process.exitCode = 1
}

main().catch((error) => {
  console.error(`[merge-authority] Verifier failed closed: ${safeMessage(error)}`)
  process.exit(1)
})
