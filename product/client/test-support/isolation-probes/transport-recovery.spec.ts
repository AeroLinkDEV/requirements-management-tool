import { expect } from '../../tests/isolated-client-test'
import { nativeTest as test } from './native-server'

test('a reused upstream socket reset before any response is retried once on a new connection', async ({ page, networkGuard, nativeServer }) => {
  await page.goto('/')
  // Sequential requests let the guard's keep-alive agent reuse a pooled connection the server then closes.
  const results = await page.evaluate(async () => {
    const seen: string[] = []
    for (let index = 0; index < 3; index++) {
      const response = await fetch(`/reset-reused?${index}`, { cache: 'no-store' })
      seen.push(`${response.status} ${await response.text()}`)
    }
    return seen
  })
  expect(results).toEqual(['200 fresh', '200 fresh', '200 fresh'])
  // The scenario must actually have happened, and each recovery must be named, not silent.
  expect(nativeServer.reusedResets).toBeGreaterThan(0)
  expect(networkGuard.recovered).toHaveLength(nativeServer.reusedResets)
  for (const entry of networkGuard.recovered) expect(entry).toMatch(/^GET \/reset-reused: ECONNRESET .* on a reused socket; retried once on a new connection$/)
})
