import { defineConfig, devices } from '@playwright/test'

const port = process.env.AEROLINK_FAST_CLIENT_PORT ?? '5188'

export default defineConfig({
  testDir: './test-support/isolation-probes',
  testMatch: '*.spec.ts',
  workers: 1,
  retries: 0,
  outputDir: 'test-results/isolation-probe',
  reporter: [['line']],
  use: { baseURL: `http://127.0.0.1:${port}` },
  projects: [{ name: 'isolation-probe', use: { ...devices['Desktop Chrome'] } }],
})
