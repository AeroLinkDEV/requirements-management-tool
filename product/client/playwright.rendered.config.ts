import { defineConfig, devices } from '@playwright/test'
import tiers from './fast-client-tests.json' with { type: 'json' }

const port = process.env.AEROLINK_FAST_CLIENT_PORT ?? '5188'
const baseURL = `http://127.0.0.1:${port}`

// These fixtures provide their own raw response data and exercise real components.
// Integrated pages and persistence continue to be proved by the complete Full suite.
export default defineConfig({
  testDir: './tests',
  testMatch: tiers.rendered,
  workers: 1,
  fullyParallel: false,
  retries: 0,
  expect: { timeout: 15_000 },
  outputDir: 'test-results/fast/rendered',
  reporter: [
    ['list'],
    ['json', { outputFile: 'test-results/fast/rendered.json' }],
    ['html', { open: 'never', outputFolder: 'playwright-report/fast' }],
  ],
  use: { baseURL, trace: 'retain-on-failure', screenshot: 'only-on-failure' },
  projects: [{ name: 'rendered', use: { ...devices['Desktop Chrome'] } }],
  webServer: {
    command: `npm run dev -- --host 127.0.0.1 --port ${port} --strictPort`,
    url: baseURL,
    reuseExistingServer: false,
    timeout: 60_000,
  },
})
