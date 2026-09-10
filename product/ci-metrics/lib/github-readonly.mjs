// Strict read-only GitHub Actions client used by authenticated CI observation tooling.
//
// Every request is a GET against the configured GitHub API origin and the fixed AeroLink repository. Artifact
// redirects are followed with Authorization only while they remain on the API origin; cross-origin downloads
// never receive the bearer token. All list endpoints require complete pagination and bounded result counts.

import { readNamedEntryFromZip, readNamedJsonFromZip, ZipParseError } from './zip.mjs'

export const GITHUB_REPOSITORY = 'AeroLinkDEV/requirements-management-tool'
export const GITHUB_API_ORIGIN = 'https://api.github.com'
export const MAX_PAGE_COUNT = 20
export const MAX_LIST_ITEMS = 2_000
export const MAX_JSON_BYTES = 5 * 1024 * 1024
export const MAX_ARTIFACT_BYTES = 50 * 1024 * 1024

function requireObject(value, label) {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) throw new Error(`${label} must be an object.`)
  return value
}

function apiOrigin(value) {
  const url = new URL(value ?? GITHUB_API_ORIGIN)
  if (url.protocol !== 'https:' || url.username || url.password || url.search || url.hash) throw new Error('GitHub API URL must be an HTTPS origin without credentials or query text.')
  if (url.origin !== GITHUB_API_ORIGIN) throw new Error(`GitHub API URL must use the fixed ${GITHUB_API_ORIGIN} origin.`)
  url.pathname = url.pathname.replace(/\/$/, '')
  return url
}

function repoPath(repository, path) {
  if (repository !== GITHUB_REPOSITORY) throw new Error(`GitHub observation collection only supports ${GITHUB_REPOSITORY}.`)
  if (typeof path !== 'string' || !path.startsWith(`/repos/${GITHUB_REPOSITORY}/`)) throw new Error('GitHub request path is outside the fixed repository API scope.')
  if (/\r|\n/.test(path)) throw new Error('GitHub request path contains a line break.')
  return path
}

/** Read a fetch body without allocating beyond its declared safety ceiling. */
export async function readBoundedResponseBody(response, maxBytes, label) {
  if (!Number.isSafeInteger(maxBytes) || maxBytes < 1 || maxBytes > MAX_ARTIFACT_BYTES) throw new Error('Response byte limit is outside the bounded range.')
  const contentLength = Number(response.headers?.get?.('content-length') ?? NaN)
  if (Number.isFinite(contentLength) && contentLength > maxBytes) throw new Error(`${label} exceeds the bounded size.`)
  const reader = response.body?.getReader?.()
  if (!reader) throw new Error(`${label} did not provide a readable response body.`)
  const chunks = []
  let total = 0
  try {
    while (true) {
      const result = await reader.read()
      if (!result || typeof result !== 'object') throw new Error(`${label} returned an invalid streamed chunk.`)
      if (result.done) break
      const chunk = Buffer.from(result.value ?? [])
      if (chunk.length > maxBytes - total) {
        try { await reader.cancel('response exceeds bounded size') } catch { /* cancellation is best effort */ }
        throw new Error(`${label} exceeds the bounded size.`)
      }
      chunks.push(chunk)
      total += chunk.length
    }
  } finally {
    try { reader.releaseLock?.() } catch { /* no-op */ }
  }
  return Buffer.concat(chunks, total)
}

async function responseJson(response, path) {
  const bytes = await readBoundedResponseBody(response, MAX_JSON_BYTES, `GitHub API response for ${path}`)
  try {
    return JSON.parse(bytes.toString('utf8'))
  } catch {
    throw new Error(`GitHub API response for ${path} was not valid JSON.`)
  }
}

/** Create a GET-only request function. This wrapper cannot be used to publish checks or mutate GitHub. */
export function createReadOnlyGitHubRequest({ token, repository = GITHUB_REPOSITORY, apiUrl = GITHUB_API_ORIGIN, fetchImpl = fetch }) {
  if (typeof token !== 'string' || token.length < 1 || token.length > 500) throw new Error('A GitHub token is required.')
  if (typeof fetchImpl !== 'function') throw new Error('fetchImpl must be a function.')
  const origin = apiOrigin(apiUrl)
  repoPath(repository, `/repos/${repository}/probe`)
  return async function request(path) {
    const safePath = repoPath(repository, path)
    const url = new URL(safePath, origin)
    const response = await fetchImpl(url, {
      method: 'GET',
      headers: {
        Authorization: `Bearer ${token}`,
        Accept: 'application/vnd.github+json',
        'X-GitHub-Api-Version': '2022-11-28',
      },
      redirect: 'error',
    })
    if (!response.ok) throw new Error(`GitHub API GET ${safePath} returned ${response.status}.`)
    if (response.status === 204) return null
    return responseJson(response, safePath)
  }
}

function appendPage(path, page) {
  const separator = path.includes('?') ? '&' : '?'
  return `${path}${separator}per_page=100&page=${page}`
}

/** Fetch a complete paged GitHub list with strict total-count, duplicate, and page bounds. */
export async function listGitHubPages({ request, path, key, idOf = (value) => value?.id, maxItems = MAX_LIST_ITEMS }) {
  if (typeof request !== 'function') throw new Error('request is required.')
  if (typeof key !== 'string' || key.length === 0) throw new Error('A response array key is required.')
  if (!Number.isSafeInteger(maxItems) || maxItems < 1 || maxItems > MAX_LIST_ITEMS) throw new Error('maxItems is outside the bounded range.')
  const rows = []
  const seen = new Set()
  let total = null
  for (let page = 1; page <= MAX_PAGE_COUNT; page += 1) {
    const body = requireObject(await request(appendPage(path, page)), `GitHub list ${key}`)
    if (!Number.isSafeInteger(body.total_count) || body.total_count < 0 || body.total_count > maxItems) throw new Error(`GitHub list ${key} has no bounded total_count.`)
    if (!Array.isArray(body[key]) || body[key].length > 100) throw new Error(`GitHub list ${key} has an invalid page.`)
    if (total === null) total = body.total_count
    if (body.total_count !== total) throw new Error(`GitHub list ${key} total_count changed during pagination.`)
    for (const row of body[key]) {
      const id = idOf(row)
      if (id === null || id === undefined || id === '') throw new Error(`GitHub list ${key} contains an item without identity.`)
      const identity = String(id)
      if (seen.has(identity)) throw new Error(`GitHub list ${key} contains duplicate identity ${identity}.`)
      seen.add(identity)
      rows.push(row)
      if (rows.length > maxItems) throw new Error(`GitHub list ${key} exceeded the bounded item count.`)
    }
    if (rows.length === total) break
    if (body[key].length < 100) throw new Error(`GitHub list ${key} pagination returned ${rows.length} of ${total} records.`)
  }
  if (rows.length !== total) throw new Error(`GitHub list ${key} exceeded the bounded page count (${rows.length} of ${total}).`)
  return rows
}

export async function fetchWorkflow({ request, repository = GITHUB_REPOSITORY }) {
  const body = requireObject(await request(repoPath(repository, `/repos/${repository}/actions/workflows/ci.yml`)), 'Workflow response')
  if (!Number.isSafeInteger(body.id) || body.id < 1) throw new Error('Workflow response did not contain a valid id.')
  if (body.path !== '.github/workflows/ci.yml' || body.name !== 'Product quality gate') throw new Error('The requested workflow is not Product quality gate at .github/workflows/ci.yml.')
  return { id: body.id, name: body.name, path: body.path, state: body.state ?? null, url: body.url ?? null }
}

export async function fetchWorkflowRuns({ request, repository = GITHUB_REPOSITORY }) {
  return listGitHubPages({ request, path: repoPath(repository, `/repos/${repository}/actions/workflows/ci.yml/runs`), key: 'workflow_runs', idOf: (run) => run?.id, maxItems: MAX_LIST_ITEMS })
}

export async function fetchWorkflowRun({ request, repository = GITHUB_REPOSITORY, runId }) {
  const id = Number(runId)
  if (!Number.isSafeInteger(id) || id < 1) throw new Error('runId must be a positive integer.')
  return requireObject(await request(repoPath(repository, `/repos/${repository}/actions/runs/${encodeURIComponent(id)}`)), 'Workflow run response')
}

/** Fetch one authenticated workflow-attempt record, including its start/end timestamps. */
export async function fetchWorkflowRunAttempt({ request, repository = GITHUB_REPOSITORY, runId, attempt }) {
  const id = Number(runId)
  const number = Number(attempt)
  if (!Number.isSafeInteger(id) || id < 1) throw new Error('runId must be a positive integer.')
  if (!Number.isSafeInteger(number) || number < 1 || number > 1000) throw new Error('attempt must be a positive integer bounded by 1000.')
  return requireObject(await request(repoPath(repository, `/repos/${repository}/actions/runs/${id}/attempts/${number}`)), 'Workflow run attempt response')
}

export async function fetchRunJobs({ request, repository = GITHUB_REPOSITORY, runId, filter = 'latest' }) {
  if (!['latest', 'all'].includes(filter)) throw new Error('Workflow job filter must be latest or all.')
  const id = Number(runId)
  if (!Number.isSafeInteger(id) || id < 1) throw new Error('runId must be a positive integer.')
  return listGitHubPages({ request, path: repoPath(repository, `/repos/${repository}/actions/runs/${id}/jobs?filter=${filter}`), key: 'jobs', idOf: (job) => job?.id, maxItems: MAX_LIST_ITEMS })
}

export async function fetchRunArtifacts({ request, repository = GITHUB_REPOSITORY, runId }) {
  const id = Number(runId)
  if (!Number.isSafeInteger(id) || id < 1) throw new Error('runId must be a positive integer.')
  return listGitHubPages({ request, path: repoPath(repository, `/repos/${repository}/actions/runs/${id}/artifacts`), key: 'artifacts', idOf: (artifact) => artifact?.id, maxItems: MAX_LIST_ITEMS })
}

export async function fetchCommitTree({ request, repository = GITHUB_REPOSITORY, commitSha }) {
  if (typeof commitSha !== 'string' || !/^[0-9a-f]{40}$/i.test(commitSha)) throw new Error('commitSha must be a 40-character SHA.')
  const body = requireObject(await request(repoPath(repository, `/repos/${repository}/git/commits/${commitSha.toLowerCase()}`)), 'Commit response')
  if (typeof body.sha !== 'string' || body.sha.toLowerCase() !== commitSha.toLowerCase() || !/^[0-9a-f]{40}$/i.test(body.tree?.sha ?? '')) throw new Error('Commit response did not bind the requested commit tree.')
  return body.tree.sha.toLowerCase()
}

export async function fetchWorkflowDefinition({ request, repository = GITHUB_REPOSITORY, commitSha }) {
  if (typeof commitSha !== 'string' || !/^[0-9a-f]{40}$/i.test(commitSha)) throw new Error('commitSha must be a 40-character SHA.')
  const ref = encodeURIComponent(commitSha.toLowerCase())
  const body = requireObject(await request(repoPath(repository, `/repos/${repository}/contents/.github/workflows/ci.yml?ref=${ref}`)), 'Workflow definition response')
  if (body.type !== 'file' || body.path !== '.github/workflows/ci.yml' || typeof body.sha !== 'string' || !/^[0-9a-f]{40}$/i.test(body.sha)) throw new Error('Workflow definition response was missing a file blob identity.')
  return { path: body.path, sha: body.sha.toLowerCase(), commitSha: commitSha.toLowerCase() }
}

function safeArtifactLocation(location, origin) {
  let url
  try { url = new URL(location, origin) } catch { throw new Error('GitHub artifact redirect location was invalid.') }
  // GitHub's signed blob redirects carry a query string. It is needed to download the object, but is never
  // copied into diagnostics or combined with the bearer token. Userinfo and fragments remain forbidden.
  if (url.protocol !== 'https:' || url.username || url.password || url.hash || url.toString().length > 8_000) throw new Error('GitHub artifact redirect was not a safe HTTPS URL.')
  return url
}

/** Download one GitHub artifact with bounded redirects and no bearer token on cross-origin locations. */
export async function downloadArtifactZip({ fetchImpl = fetch, token, apiUrl = GITHUB_API_ORIGIN, repository = GITHUB_REPOSITORY, artifactId, maxBytes = MAX_ARTIFACT_BYTES }) {
  if (typeof token !== 'string' || token.length < 1 || token.length > 500) throw new Error('A GitHub token is required.')
  if (typeof fetchImpl !== 'function') throw new Error('fetchImpl must be a function.')
  const origin = apiOrigin(apiUrl)
  if (repository !== GITHUB_REPOSITORY) throw new Error(`GitHub observation collection only supports ${GITHUB_REPOSITORY}.`)
  const id = Number(artifactId)
  if (!Number.isSafeInteger(id) || id < 1) throw new Error('artifactId must be a positive integer.')
  let url = new URL(`/repos/${repository}/actions/artifacts/${id}/zip`, origin)
  for (let redirects = 0; redirects <= 3; redirects += 1) {
    const sameOrigin = url.origin === origin.origin
    const response = await fetchImpl(url, {
      method: 'GET',
      headers: {
        ...(sameOrigin ? { Authorization: `Bearer ${token}` } : {}),
        Accept: sameOrigin ? 'application/vnd.github+json' : 'application/octet-stream',
        'X-GitHub-Api-Version': '2022-11-28',
      },
      redirect: 'manual',
    })
    if (response.status >= 300 && response.status < 400) {
      if (redirects === 3) throw new Error('GitHub artifact download exceeded the redirect limit.')
      const location = response.headers?.get?.('location')
      if (!location) throw new Error('GitHub artifact redirect did not include a location.')
      url = safeArtifactLocation(location, url)
      continue
    }
    if (!response.ok) throw new Error(`GitHub artifact ${id} download returned ${response.status}.`)
    return readBoundedResponseBody(response, maxBytes, `GitHub artifact ${id} download`)
  }
  throw new Error('GitHub artifact download did not complete.')
}

export function readObservationArtifact(zip) {
  try {
    const artifact = readNamedJsonFromZip(zip, 'api-observation.json')
    const trxText = readNamedEntryFromZip(zip, 'shard.trx').toString('utf8')
    return { artifact, trxText }
  } catch (error) {
    if (error instanceof ZipParseError) throw new Error(`API observation artifact was incomplete or unreadable: ${error.message}`)
    throw error
  }
}
