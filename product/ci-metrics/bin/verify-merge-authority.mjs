// Trusted default-branch verifier and App-bound check publisher for merge-queue candidates (#549).

import { readFileSync } from 'node:fs'
import { evaluateMergeGroupCandidate, TRUSTED_SURFACE_PREFIXES } from '../lib/merge-authority.mjs'
import {
  compareTrustedSurfaces,
  createGitHubRequest,
  fetchDefaultBranch,
  fetchLatestRunJobs,
  fetchWorkflowRun,
  publishMergeAuthorityCheck,
} from '../lib/merge-authority-github.mjs'

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
    decision = evaluateMergeGroupCandidate({
      run,
      jobs,
      changedPaths,
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
