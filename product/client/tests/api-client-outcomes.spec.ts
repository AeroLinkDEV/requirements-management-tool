import { expect, logicTest as test } from './isolated-client-test'
import { ApiError, apiRequest } from '../src/apiClient'

// These transport fixtures prove client wording and diagnostics, not whether a server committed work.
test('an unexplained HTTP failure leaves the outcome unconfirmed and retains its status', async () => {
  const originalFetch = globalThis.fetch
  globalThis.fetch = async () => new Response('gateway unavailable', { status: 502 })
  try {
    const failure = await apiRequest('/api/example', { method: 'POST' }).catch(error => error)
    expect(failure).toBeInstanceOf(ApiError)
    expect(failure.status).toBe(502)
    expect(failure.details).toBeUndefined()
    expect(failure.message).toBe('AeroLink could not confirm the outcome of this request.')
  } finally {
    globalThis.fetch = originalFetch
  }
})

test('a rejected fetch leaves the outcome unconfirmed without claiming reachability, retained input or safe retry', async () => {
  const originalFetch = globalThis.fetch
  globalThis.fetch = async () => { throw new TypeError('Failed to fetch') }
  try {
    const failure = await apiRequest('/api/example', { method: 'POST' }).catch(error => error)
    expect(failure).toBeInstanceOf(ApiError)
    expect(failure.status).toBe(0)
    expect(failure.message).toBe('AeroLink did not receive a response. The outcome of this request could not be confirmed.')
  } finally {
    globalThis.fetch = originalFetch
  }
})

test('an explanatory HTTP response retains the server message and structured diagnostics', async () => {
  const originalFetch = globalThis.fetch
  const body = { error: 'The operation is still being finalized.', code: 'in_progress', operationId: 'operation-1' }
  globalThis.fetch = async () => Response.json(body, { status: 503 })
  try {
    const failure = await apiRequest('/api/example', { method: 'POST' }).catch(error => error)
    expect(failure).toBeInstanceOf(ApiError)
    expect(failure.message).toBe(body.error)
    expect(failure.status).toBe(503)
    expect(failure.code).toBe('in_progress')
    expect(failure.details).toEqual(body)
  } finally {
    globalThis.fetch = originalFetch
  }
})
