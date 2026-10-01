import { expect, logicTest as test } from './isolated-client-test'
import type { Request } from '@playwright/test'
import { BUFFER_SPACE, BUFFER_SPACE_ANNOTATION, BUFFER_SPACE_BACKOFF_MS, retryOnBufferSpace, type Annotation, type RetryDeps } from './bootstrap-retry'

// #986 guard: the login bootstrap is retried only when the failed attempt saw net::ERR_NO_BUFFER_SPACE, at most twice,
// each retry annotated and logged; every other failure is thrown at once and unchanged.

/** A page that only delivers requestfailed events, and counts its listeners. */
function fakePage() {
  const listeners = new Set<(request: Request) => void>()
  return {
    on: (event: string, listener: (request: Request) => void) => { if (event === 'requestfailed') listeners.add(listener) },
    off: (event: string, listener: (request: Request) => void) => { if (event === 'requestfailed') listeners.delete(listener) },
    fail: (url: string, errorText: string) => { for (const listener of listeners) listener({ url: () => url, failure: () => ({ errorText }) } as unknown as Request) },
    listeners: () => listeners.size,
  }
}
function deps() {
  const annotations: Annotation[] = [], logs: string[] = [], sleeps: number[] = []
  const injected: RetryDeps = { annotate: a => annotations.push(a), log: line => logs.push(line), sleep: async ms => { sleeps.push(ms) }, snapshot: () => ({ total: 94 }) }
  return { injected, annotations, logs, sleeps }
}
const bootstrapFailed = () => new Error('expect(locator).toBeVisible() failed: Sign out or Username not visible')

test('a bootstrap resource failing with ERR_NO_BUFFER_SPACE is retried, each retry annotated and logged with the host snapshot', async () => {
  const page = fakePage(), d = deps()
  let attempts = 0
  const result = await retryOnBufferSpace(page as never, 'login bootstrap', async () => {
    attempts += 1
    if (attempts < 3) { page.fail('http://127.0.0.1:5174/@react-refresh', BUFFER_SPACE); throw bootstrapFailed() }
    return 'signed in'
  }, d.injected)
  expect(result).toBe('signed in')
  expect(attempts).toBe(3)
  expect(d.sleeps).toEqual([...BUFFER_SPACE_BACKOFF_MS])
  expect(d.annotations.map(a => a.type)).toEqual([BUFFER_SPACE_ANNOTATION, BUFFER_SPACE_ANNOTATION])
  expect(d.annotations[0].description).toContain('login bootstrap, attempt 1: net::ERR_NO_BUFFER_SPACE on http://127.0.0.1:5174/@react-refresh; retrying in 500 ms')
  expect(d.annotations[0].description).toContain('host {"total":94}')
  expect(d.logs).toEqual(d.annotations.map(a => `[#986] ${a.description}`))
  expect(page.listeners()).toBe(0)
})

test('page.goto rejecting with ERR_NO_BUFFER_SPACE is retried', async () => {
  const page = fakePage(), d = deps()
  let attempts = 0
  await retryOnBufferSpace(page as never, 'login bootstrap', async () => {
    attempts += 1
    if (attempts === 1) throw new Error(`page.goto: ${BUFFER_SPACE} at http://127.0.0.1:5174/`)
  }, d.injected)
  expect(attempts).toBe(2)
  expect(d.annotations[0].description).toContain('on the navigation itself')
})

test('any other failure is thrown at once, unchanged and unannotated: another net error, a plain assertion, a refused connection', async () => {
  for (const errorText of ['net::ERR_CONNECTION_REFUSED', 'net::ERR_ABORTED', 'net::ERR_NO_BUFFER_SPACE_OTHER', 'NS_ERROR_NO_BUFFER_SPACE']) {
    const page = fakePage(), d = deps()
    const original = bootstrapFailed()
    let attempts = 0
    await expect(retryOnBufferSpace(page as never, 'login bootstrap', async () => { attempts += 1; page.fail('http://127.0.0.1:5174/', errorText); throw original }, d.injected)).rejects.toBe(original)
    expect(attempts, errorText).toBe(1)
    expect(d.annotations, errorText).toEqual([])
    expect(d.sleeps, errorText).toEqual([])
    expect(page.listeners()).toBe(0)
  }
  // An assertion failure with no failed request at all.
  const page = fakePage(), d = deps()
  const original = bootstrapFailed()
  await expect(retryOnBufferSpace(page as never, 'login bootstrap', async () => { throw original }, d.injected)).rejects.toBe(original)
  expect(d.annotations).toEqual([])
})

test('a buffer failure in one attempt does not excuse a different failure in the next', async () => {
  const page = fakePage(), d = deps()
  const different = new Error('expect(locator).toBeVisible() failed: Projects heading not visible')
  let attempts = 0
  await expect(retryOnBufferSpace(page as never, 'login bootstrap', async () => {
    attempts += 1
    if (attempts === 1) { page.fail('http://127.0.0.1:5086/assets/index.js', BUFFER_SPACE); throw bootstrapFailed() }
    throw different
  }, d.injected)).rejects.toBe(different)
  expect(attempts).toBe(2)
  expect(d.annotations).toHaveLength(1)
})

test('after two retries the failure stands: the last error is thrown, and the report says it was not retried again', async () => {
  const page = fakePage(), d = deps()
  const errors: Error[] = []
  await expect(retryOnBufferSpace(page as never, 'login bootstrap', async () => {
    page.fail('http://127.0.0.1:5174/node_modules/.vite/deps/react.js', BUFFER_SPACE)
    const error = bootstrapFailed(); errors.push(error); throw error
  }, d.injected)).rejects.toThrow('Sign out or Username')
  expect(errors).toHaveLength(3)
  expect(d.sleeps).toEqual([...BUFFER_SPACE_BACKOFF_MS])
  expect(d.annotations).toHaveLength(3)
  expect(d.annotations[2].description).toContain('attempt 3: net::ERR_NO_BUFFER_SPACE')
  expect(d.annotations[2].description).toContain('not retried again, the failure stands')
  expect(page.listeners()).toBe(0)
})

test('a success at the first attempt is untouched: no retry, no annotation, no listener left behind', async () => {
  const page = fakePage(), d = deps()
  expect(await retryOnBufferSpace(page as never, 'login bootstrap', async () => 7, d.injected)).toBe(7)
  // A buffer failure after the attempt succeeded belongs to nothing here.
  page.fail('http://127.0.0.1:5174/late.js', BUFFER_SPACE)
  expect(d.annotations).toEqual([])
  expect(page.listeners()).toBe(0)
})
