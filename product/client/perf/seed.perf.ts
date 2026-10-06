import { test } from '@playwright/test'
import { writeFileSync } from 'node:fs'
import { showcaseSeed } from '../tests/auth'

// Seeds the template database every browser run copies (#1510 I0a), and records the seed's identities.
test('seed the template database', async ({ request }) => {
  test.setTimeout(10 * 60_000)
  const result = process.env.AEROLINK_PERF_RESULT
  if (!result) throw new Error('AEROLINK_PERF_RESULT is required')
  writeFileSync(result, JSON.stringify(await showcaseSeed(request)))
})
