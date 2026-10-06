import { expect } from '../../tests/isolated-client-test'
import { nativeTest as test } from './native-server'

test('an upstream failure the guard cannot recover reaches the page truthfully and fails teardown', async ({ page }) => {
  await page.goto('/')
  const results = await page.evaluate(async () => {
    const attempt = (path: string, init: RequestInit = {}) => fetch(path, { cache: 'no-store', signal: AbortSignal.timeout(5000), ...init })
      .then(async response => `${response.status} ${await response.text()}`)
      .catch((error: Error) => error.name === 'TimeoutError' ? 'hung' : 'network error')
    const seen = await Promise.all([attempt('/reset-always'), attempt('/truncated')])
    // Each check below follows a request that leaves a used connection in the guard's upstream pool.
    // A reset reused connection is not replayed for a non-idempotent request, nor once a response has begun.
    await attempt('/warm')
    // Bodyless, so only the method limit stops the replay.
    seen.push(await attempt('/reset-reused', { method: 'POST' }))
    await attempt('/warm')
    seen.push(await attempt('/partial-reused'))
    return seen
  })
  // A reset is a 502, and a body cut short is a network error rather than a short "complete" body.
  expect(results).toEqual(['502 ', 'network error', '502 ', '502 '])
  // Deliberately swallowed by the page: the rendered fixture's teardown must still fail and name each cause.
})
