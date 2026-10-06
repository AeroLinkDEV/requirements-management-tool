import { expect } from '../../tests/isolated-client-test'
import { nativeTest as test } from './native-server'

test('a reused upstream socket reset before any response is retried once on a new connection', async ({ page, networkGuard, nativeServer }) => {
  await page.goto('/')
  // Sequential requests let the guard's keep-alive agent reuse a pooled connection the server then closes.
  const { seen, connects } = await page.evaluate(async () => {
    const seen: string[] = []
    for (let index = 0; index < 4; index++) {
      const response = await fetch(`/reset-reused?${index}`, { cache: 'no-store' })
      seen.push(`${response.status} ${await response.text()}`)
    }
    // A request that opened a new browser connection to the guard spends time between connectStart and connectEnd.
    const connects = (performance.getEntriesByType('resource') as PerformanceResourceTiming[])
      .filter(entry => entry.name.includes('/reset-reused?')).map(entry => entry.connectEnd - entry.connectStart)
    return { seen, connects }
  })
  expect(seen).toEqual(['200 fresh', '200 fresh', '200 fresh', '200 fresh'])
  // The scenario must actually have happened, and each recovery must be named, not silent.
  expect(nativeServer.reusedResets).toBeGreaterThan(0)
  expect(networkGuard.recovered).toHaveLength(nativeServer.reusedResets)
  for (const entry of networkGuard.recovered) expect(entry).toMatch(/^GET \/reset-reused: ECONNRESET .* on a reused socket after \d+ ms; retried once on a new connection$/)
  // A recovery's upstream `Connection: close` is not relayed, so the browser keeps its loopback connection (#986).
  expect(connects).toEqual([0, 0, 0, 0])
})
