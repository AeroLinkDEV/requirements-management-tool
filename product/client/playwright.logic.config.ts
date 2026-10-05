import { defineConfig } from '@playwright/test'
import tiers from './fast-client-tests.json' with { type: 'json' }
import { fastShard } from './fast-shard'

// Pure behavior checks do not start Chromium, Vite, an API, or a database.
// Tests, not files, are the unit of distribution: two FMS scenario files held half the suite's time, so file-level
// workers finished minutes apart and the hosted Fast lane hit its ten-minute cap (#1456). Fast also shards by test.
export default defineConfig({
  testDir: './tests',
  testMatch: tiers.logic,
  workers: 3,
  fullyParallel: true,
  shard: fastShard(),
  retries: 0,
  outputDir: 'test-results/fast/logic',
  reporter: [['list'], ['json', { outputFile: 'test-results/fast/logic.json' }]],
  projects: [{ name: 'logic' }],
})
