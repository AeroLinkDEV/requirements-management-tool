// GitHub-facing evidence collection for the trusted merge-authority verifier (#549).
//
// This module deliberately keeps network I/O separate from merge-authority.mjs's pure decision logic.
// Callers inject a request function, which makes the exact endpoints, pagination, and field mapping
// contract testable without credentials or live GitHub state.

import { TRUSTED_SURFACE_PREFIXES } from './merge-authority.mjs'

const SHA_PATTERN = /^[0-9a-f]{40}$/

function requireObject(value, label) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    throw new Error(`${label} was not an object.`)
  }
  return value
}

function requireSha(value, label) {
  if (typeof value !== 'string' || !SHA_PATTERN.test(value)) {
    throw new Error(`${label} was not a lowercase 40-character commit SHA.`)
  }
  return value
}

function encodeRef(value) {
  return String(value).split('/').map(encodeURIComponent).join('/')
}

export function createGitHubRequest({ token, apiUrl = 'https://api.github.com', fetchImpl = fetch }) {
  if (!token) throw new Error('A GitHub token is required.')
  return async function request(path, { method = 'GET', body } = {}) {
    const response = await fetchImpl(`${apiUrl}${path}`, {
      method,
      headers: {
        Authorization: `Bearer ${token}`,
        Accept: 'application/vnd.github+json',
        'X-GitHub-Api-Version': '2022-11-28',
        ...(typeof body === 'undefined' ? {} : { 'Content-Type': 'application/json' }),
      },
      ...(typeof body === 'undefined' ? {} : { body: JSON.stringify(body) }),
    })
    if (!response.ok) {
      throw new Error(`GitHub API ${method} ${path} returned ${response.status}.`)
    }
    return response.status === 204 ? null : response.json()
  }
}

export async function fetchWorkflowRun({ request, repository, runId }) {
  const body = requireObject(
    await request(`/repos/${repository}/actions/runs/${encodeURIComponent(runId)}`),
    'Workflow run response',
  )
  return {
    repository: body.repository?.full_name,
    workflowName: body.name,
    workflowPath: body.path,
    event: body.event,
    headSha: body.head_sha,
    headBranch: body.head_branch,
    runId: body.id,
    runAttempt: body.run_attempt,
    status: body.status,
    detailsUrl: body.html_url,
  }
}

/**
 * Read every page from the one authoritative endpoint and map its fields directly.
 *
 * `filter=latest` is intentionally present in every request. GitHub's partial-rerun model retains
 * earlier successful jobs in that response while selecting the latest execution for rerun jobs.
 * Reading filter=all or assembling records from attempt endpoints could mix incompatible evidence.
 */
export async function fetchLatestRunJobs({ request, repository, runId }) {
  const jobs = []
  let totalCount = null
  let page = 1
  while (true) {
    const path = `/repos/${repository}/actions/runs/${encodeURIComponent(runId)}/jobs?filter=latest&per_page=100&page=${page}`
    const body = requireObject(await request(path), 'Workflow jobs response')
    if (!Array.isArray(body.jobs)) throw new Error('Workflow jobs response did not contain a jobs array.')
    if (!Number.isInteger(body.total_count) || body.total_count < 0) {
      throw new Error('Workflow jobs response did not contain a valid total_count.')
    }
    if (totalCount === null) totalCount = body.total_count
    if (body.total_count !== totalCount) throw new Error('Workflow jobs total_count changed during pagination.')
    jobs.push(...body.jobs.map((job) => ({
      runId: job?.run_id,
      runAttempt: job?.run_attempt,
      name: job?.name,
      conclusion: job?.conclusion,
    })))
    if (body.jobs.length < 100) break
    page += 1
  }
  if (jobs.length !== totalCount) {
    throw new Error(`Workflow jobs pagination returned ${jobs.length} of ${totalCount} records.`)
  }
  return jobs
}

async function commitTreeSha({ request, repository, ref }) {
  const body = requireObject(
    await request(`/repos/${repository}/git/commits/${encodeRef(ref)}`),
    `Git commit response for ${ref}`,
  )
  return requireSha(body.tree?.sha, `Root tree for ${ref}`)
}

async function childTreeSha({ request, repository, treeSha, name }) {
  const body = requireObject(
    await request(`/repos/${repository}/git/trees/${treeSha}`),
    `Git tree response for ${treeSha}`,
  )
  if (body.truncated === true) throw new Error(`Git tree ${treeSha} was truncated.`)
  if (!Array.isArray(body.tree)) throw new Error(`Git tree ${treeSha} did not contain an entry array.`)
  const matches = body.tree.filter((entry) => entry?.path === name)
  if (matches.length !== 1 || matches[0].type !== 'tree') {
    throw new Error(`Trusted directory '${name}' was missing or ambiguous in tree ${treeSha}.`)
  }
  return requireSha(matches[0].sha, `Tree entry ${name}`)
}

async function subtreeSha({ request, repository, rootTreeSha, prefix }) {
  const segments = prefix.replace(/\/$/, '').split('/')
  let treeSha = rootTreeSha
  for (const segment of segments) {
    treeSha = await childTreeSha({ request, repository, treeSha, name: segment })
  }
  return treeSha
}

/**
 * Compare complete Git subtrees instead of GitHub's paged changed-file list. Equal tree SHAs mean
 * names, modes, blobs, and descendants are byte-identical; no file-count or rename pagination gap
 * can hide a candidate edit. A differing subtree is represented by its trusted prefix, which the
 * pure evaluator treats as a trusted-surface modification.
 */
export async function compareTrustedSurfaces({ request, repository, candidateSha, baseSha }) {
  requireSha(candidateSha, 'Candidate SHA')
  requireSha(baseSha, 'Default-branch SHA')
  const candidateRoot = await commitTreeSha({ request, repository, ref: candidateSha })
  const baseRoot = await commitTreeSha({ request, repository, ref: baseSha })
  const changedPaths = []
  for (const prefix of TRUSTED_SURFACE_PREFIXES) {
    const [candidateTree, baseTree] = await Promise.all([
      subtreeSha({ request, repository, rootTreeSha: candidateRoot, prefix }),
      subtreeSha({ request, repository, rootTreeSha: baseRoot, prefix }),
    ])
    if (candidateTree !== baseTree) changedPaths.push(prefix)
  }
  return changedPaths
}

export async function fetchDefaultBranch({ request, repository }) {
  const repositoryBody = requireObject(await request(`/repos/${repository}`), 'Repository response')
  const name = repositoryBody.default_branch
  if (typeof name !== 'string' || name.length === 0) throw new Error('Repository default branch was missing.')
  const refBody = requireObject(
    await request(`/repos/${repository}/git/ref/heads/${encodeRef(name)}`),
    'Default-branch ref response',
  )
  if (refBody.object?.type !== 'commit') throw new Error('Repository default branch did not resolve to a commit.')
  return { name, sha: requireSha(refBody.object?.sha, 'Default-branch SHA') }
}

export async function publishMergeAuthorityCheck({ request, repository, headSha, decision, reasons, detailsUrl }) {
  requireSha(headSha, 'Check-run head SHA')
  if (!['PASS', 'REFUSE', 'PENDING'].includes(decision)) {
    throw new Error(`Unsupported merge-authority decision '${decision ?? 'unknown'}'.`)
  }
  const pending = decision === 'PENDING'
  const passed = decision === 'PASS'
  const safeReasons = Array.isArray(reasons)
    ? reasons.slice(0, 50).map((reason) => String(reason).replace(/\r?\n/g, ' ').slice(0, 500))
    : ['The verifier returned no reason list.']
  const summary = pending
    ? 'A Product quality gate attempt is in progress. Any earlier authority for this candidate is invalid until the latest attempt completes.'
    : passed
      ? 'Default-branch verification bound the complete Product quality gate evidence to this merge-queue candidate.'
      : `The trusted verifier refused to bind this merge-queue candidate:\n\n${safeReasons.map((reason) => `- ${reason}`).join('\n')}`
  return request(`/repos/${repository}/check-runs`, {
    method: 'POST',
    body: {
      name: 'Trusted merge-queue binding',
      head_sha: headSha,
      status: pending ? 'in_progress' : 'completed',
      ...(!pending ? { conclusion: passed ? 'success' : 'failure' } : {}),
      ...(detailsUrl ? { details_url: detailsUrl } : {}),
      external_id: `aerolink-merge-authority:${headSha}`,
      output: {
        title: pending
          ? 'Trusted merge-queue evidence pending'
          : passed
            ? 'Trusted merge-queue evidence bound'
            : 'Trusted merge-queue evidence refused',
        summary,
      },
    },
  })
}
