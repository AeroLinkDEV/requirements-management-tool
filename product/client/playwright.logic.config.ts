import { defineConfig } from '@playwright/test'
import tiers from './fast-client-tests.json' with { type: 'json' }

// Pure behavior checks do not start Chromium, Vite, an API, or a database.
export default defineConfig({
  testDir: './tests',
  testMatch: tiers.logic,
  workers: 1,
  fullyParallel: false,
  retries: 0,
  outputDir: 'test-results/fast/logic',
  reporter: [['list'], ['json', { outputFile: 'test-results/fast/logic.json' }]],
  projects: [{ name: 'logic' }],
})
