import { test } from 'node:test'
import assert from 'node:assert/strict'
import { createReadOnlyGitHubRequest, downloadArtifactZip, fetchWorkflowRunAttempt, fetchWorkflowRuns, listGitHubPages, readBoundedResponseBody } from '../lib/github-readonly.mjs'

const repository = 'AeroLinkDEV/requirements-management-tool'
const apiRoot = `/repos/${repository}`

function response({ status = 200, body = {}, headers = {} } = {}) {
  const text = JSON.stringify(body)
  const bytes = Buffer.from(text)
  return {
    status,
    ok: status >= 200 && status < 300,
    headers: { get: (name) => headers[name.toLowerCase()] ?? null },
    body: streamedBody([bytes]),
  }
}

function streamedBody(chunks) {
  let index = 0
  let cancelled = false
  return {
    getReader() {
      return {
        read: async () => index < chunks.length ? { done: false, value: chunks[index++] } : { done: true, value: undefined },
        cancel: async () => { cancelled = true },
        releaseLock: () => {},
        get cancelled() { return cancelled },
      }
    },
  }
}

test('read-only GitHub client sends only GET requests to the fixed repository', async () => {
  const calls = []
  const request = createReadOnlyGitHubRequest({
    token: 'token-value',
    repository,
    fetchImpl: async (url, options) => {
      calls.push({ url: String(url), options })
      return response({ body: { ok: true } })
    },
  })
  assert.deepEqual(await request(`${apiRoot}/actions/workflows/ci.yml`), { ok: true })
  assert.equal(calls[0].options.method, 'GET')
  assert.equal(calls[0].options.headers.Authorization, 'Bearer token-value')
  await assert.rejects(() => request('/repos/another/project/actions/runs'), /fixed repository API scope/)
  assert.throws(() => createReadOnlyGitHubRequest({ token: 'x', repository: 'another/project' }), /only supports/)
  assert.throws(() => createReadOnlyGitHubRequest({ token: 'x', repository, apiUrl: 'https://example.test' }), /fixed/)
})

test('REST JSON bodies enforce the cumulative limit when content length is missing or lies', async () => {
  const oversized = Buffer.alloc(5 * 1024 * 1024 + 1, 65)
  const requestWith = (contentLength) => createReadOnlyGitHubRequest({ token: 'token-value', repository, fetchImpl: async () => ({
    status: 200,
    ok: true,
    headers: { get: (name) => name === 'content-length' ? contentLength : null },
    body: streamedBody([oversized]),
  }) })
  await assert.rejects(() => requestWith(null)(`${apiRoot}/actions/workflows/ci.yml`), /bounded size/)
  await assert.rejects(() => requestWith('1')(`${apiRoot}/actions/workflows/ci.yml`), /bounded size/)
})

test('paged GitHub lists require complete totals and refuse duplicate identities', async () => {
  const seen = []
  const request = async (path) => {
    seen.push(path)
    const page = Number(new URL(`https://example.test${path}`).searchParams.get('page'))
    return { total_count: 101, rows: page === 1 ? Array.from({ length: 100 }, (_, index) => ({ id: index + 1 })) : [{ id: 101 }] }
  }
  const rows = await listGitHubPages({ request, path: `${apiRoot}/actions/runs`, key: 'rows', maxItems: 200 })
  assert.deepEqual(rows.map((row) => row.id), Array.from({ length: 101 }, (_, index) => index + 1))
  assert.equal(seen.length, 2)
  await assert.rejects(() => listGitHubPages({
    request: async () => ({ total_count: 2, rows: [{ id: 1 }, { id: 1 }] }),
    path: `${apiRoot}/actions/runs`, key: 'rows', maxItems: 10,
  }), /duplicate identity/)
  await assert.rejects(() => listGitHubPages({
    request: async () => ({ total_count: 2, rows: [{ id: 1 }] }),
    path: `${apiRoot}/actions/runs`, key: 'rows', maxItems: 10,
  }), /returned 1 of 2/)
})

test('workflow run pagination preserves all runs and does not cap at the first page', async () => {
  const request = async (path) => {
    const page = Number(new URL(`https://example.test${path}`).searchParams.get('page'))
    return { total_count: 101, workflow_runs: page === 1 ? Array.from({ length: 100 }, (_, index) => ({ id: index + 1 })) : [{ id: 101 }] }
  }
  const runs = await fetchWorkflowRuns({ request, repository })
  assert.equal(runs.length, 101)
  assert.equal(runs.at(-1).id, 101)
})

test('workflow attempt reads stay within the fixed repository scope', async () => {
  const calls = []
  const request = async (path) => { calls.push(path); return { id: 42, run_attempt: 2, run_started_at: '2026-09-09T01:00:00Z', updated_at: '2026-09-09T01:02:00Z' } }
  const attempt = await fetchWorkflowRunAttempt({ request, repository, runId: 42, attempt: 2 })
  assert.equal(attempt.run_attempt, 2)
  assert.equal(calls[0], `${apiRoot}/actions/runs/42/attempts/2`)
  await assert.rejects(() => fetchWorkflowRunAttempt({ request, repository, runId: 42, attempt: 0 }), /positive integer/)
})

test('artifact redirects strip bearer authorization after leaving the API origin', async () => {
  const calls = []
  const fetchImpl = async (url, options) => {
    calls.push({ url: String(url), options })
    if (calls.length === 1) return response({ status: 302, headers: { location: 'https://objects.example.test/a.zip?sig=opaque' } })
    return {
      status: 200,
      ok: true,
      headers: { get: () => null },
      body: streamedBody([Buffer.from('PK-test')]),
    }
  }
  const bytes = await downloadArtifactZip({ token: 'secret-token', repository, artifactId: 42, fetchImpl })
  assert.equal(bytes.toString(), 'PK-test')
  assert.equal(calls[0].options.headers.Authorization, 'Bearer secret-token')
  assert.equal(calls[1].options.headers.Authorization, undefined)
  assert.equal(calls[1].options.headers.Accept, 'application/octet-stream')
  await assert.rejects(() => downloadArtifactZip({ token: 'x', repository, artifactId: 42, fetchImpl: async () => response({ status: 302, headers: { location: 'http://objects.example.test/a.zip' } }) }), /safe HTTPS URL/)
})

test('streamed GitHub responses enforce cumulative limits and cancel on overflow', async () => {
  let cancelled = false
  let reads = 0
  const stream = {
    getReader() {
      return {
        read: async () => {
          reads += 1
          return reads <= 2 ? { done: false, value: Buffer.from('ab') } : { done: true, value: undefined }
        },
        cancel: async () => { cancelled = true },
        releaseLock: () => {},
      }
    },
  }
  await assert.rejects(() => readBoundedResponseBody({ body: stream, headers: { get: () => null } }, 3, 'test response'), /exceeds the bounded size/)
  assert.equal(cancelled, true)
  assert.equal(reads, 2)

  let lyingCancelled = false
  const lyingLengthStream = {
    getReader() {
      let index = 0
      return {
        read: async () => index++ === 0 ? { done: false, value: Buffer.from('abcd') } : { done: true, value: undefined },
        cancel: async () => { lyingCancelled = true },
        releaseLock: () => {},
      }
    },
  }
  await assert.rejects(() => readBoundedResponseBody({ body: lyingLengthStream, headers: { get: (name) => name === 'content-length' ? '1' : null } }, 3, 'lying response'), /exceeds the bounded size/)
  assert.equal(lyingCancelled, true)
})

test('artifact download applies the streamed limit before retaining oversized chunks', async () => {
  let cancelled = false
  let reads = 0
  const fetchImpl = async () => ({
    status: 200,
    ok: true,
    headers: { get: () => null },
    body: {
      getReader: () => ({
        read: async () => {
          reads += 1
          return reads <= 2 ? { done: false, value: Buffer.from('ab') } : { done: true, value: undefined }
        },
        cancel: async () => { cancelled = true },
        releaseLock: () => {},
      }),
    },
  })
  await assert.rejects(() => downloadArtifactZip({ token: 'token', repository, artifactId: 42, maxBytes: 3, fetchImpl }), /bounded size/)
  assert.equal(cancelled, true)
  assert.equal(reads, 2)
})
