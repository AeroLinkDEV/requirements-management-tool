import { expect } from '../../tests/isolated-client-test'
import { nativeTest as test } from './native-server'

test('an upstream failure the guard cannot recover reaches the page truthfully and fails teardown', async ({ page, networkGuard, nativeServer }) => {
  await page.goto('/')
  const results = await page.evaluate(async () => {
    // Chromium can reject a timed-out fetch as TimeoutError or AbortError; either means the request hung.
    const attempt = (path: string, init: RequestInit = {}) => fetch(path, { cache: 'no-store', signal: AbortSignal.timeout(5000), ...init })
      .then(async response => `${response.status} ${await response.text()}`)
      .catch((error: Error) => ['TimeoutError', 'AbortError'].includes(error.name) ? 'hung' : 'network error')
    // More concurrent resets than the guard's pool holds idle connections, so at least one starts on a new connection.
    const seen = await Promise.all([attempt('/reset-always?1'), attempt('/reset-always?2'), attempt('/reset-always?3'), attempt('/truncated')])
    // Each check below follows a request that leaves a used connection in the guard's upstream pool.
    // A reset reused connection is not replayed for a non-idempotent request, nor once a response has begun.
    await attempt('/warm')
    // Bodyless, so only the method limit stops the replay.
    seen.push(await attempt('/reset-reused', { method: 'POST' }))
    await attempt('/warm')
    seen.push(await attempt('/partial-reused'))
    return seen
  })
  // A reset is a 502, and a body cut short is a network error rather than a hang or a short "complete" body.
  expect(results).toEqual(['502 ', '502 ', '502 ', 'network error', '502 ', '502 '])
  // Only a reset on a reused connection may be replayed; a reset on a new connection never is.
  expect(nativeServer.alwaysResets.new, 'a /reset-always request must start on a new connection').toBeGreaterThan(nativeServer.alwaysResets.reused)
  expect(networkGuard.recovered.filter(entry => entry.startsWith('GET /reset-always:'))).toHaveLength(nativeServer.alwaysResets.reused)
  // The outer runner requires this line, so a failure above cannot pass as the expected teardown failure.
  console.log('page outcomes verified')
  // Deliberately swallowed by the page: the rendered fixture's teardown must still fail and name each cause.
})
