import { defineConfig, devices } from '@playwright/test'
import { existsSync } from 'node:fs'
import { homedir, tmpdir } from 'node:os'
import { join } from 'node:path'

// The journeys must be runnable wherever the product is developed, not only on Windows. Both servers are
// therefore launched as plain commands with their configuration supplied through webServer.env, rather
// than through a PowerShell prologue. Playwright runs each command in the platform shell, so bare `npm`
// resolves to npm.cmd on Windows and to npm on Linux and macOS without any per-platform branching here.
const windowsDotnet = process.env.USERPROFILE
  ? join(process.env.USERPROFILE, '.dotnet', 'dotnet.exe')
  : undefined
const posixDotnet = join(homedir(), '.dotnet', 'dotnet')
const localDotnet = [windowsDotnet, posixDotnet].find(candidate => candidate && existsSync(candidate))
const dotnet = process.env.AEROLINK_DOTNET ?? localDotnet ?? 'dotnet'
const runId = process.env.AEROLINK_E2E_RUN_ID ?? `${Date.now()}`
process.env.AEROLINK_E2E_RUN_ID = runId
const e2eDatabase = join(tmpdir(), `aerolink-e2e-${runId}.db`).replaceAll('\\', '/')
const e2eApiPort = process.env.AEROLINK_E2E_API_PORT ?? '5082'
const e2eClientPort = process.env.AEROLINK_E2E_CLIENT_PORT ?? '5174'
const skipApiBuild = process.env.AEROLINK_E2E_SKIP_BUILD === 'true'
const outputDir = process.env.AEROLINK_E2E_OUTPUT_DIR ?? 'test-results'
const reportDir = process.env.AEROLINK_E2E_REPORT_DIR ?? 'playwright-report'
process.env.AEROLINK_E2E_API_BASE = `http://127.0.0.1:${e2eApiPort}`

export default defineConfig({
  testDir: './tests',
  globalSetup: './tests/global-setup.ts',
  outputDir,
  fullyParallel: false,
  workers: 1,
  reporter: [['list'], ['./tests/slow-test-reporter.ts'], ['html', { open: 'never', outputFolder: reportDir }]],
  use: {
    baseURL: `http://127.0.0.1:${e2eClientPort}`,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: [
    {
      command: `"${dotnet}" run --configuration Release ${skipApiBuild ? '--no-build ' : ''}--project ../src/AeroLink.Api --urls http://127.0.0.1:${e2eApiPort}`,
      env: {
        Database__Provider: 'Sqlite',
        DemoData__Enabled: 'false',
        Identity__SeedDemoAccounts: 'true',
        Identity__AllowDemoAccounts: 'true',
        Identity__CookieSecure: 'false',
        Identity__LoginRateLimitPerMinute: '500',
        Cors__AllowedOrigins__0: `http://127.0.0.1:${e2eClientPort}`,
        ConnectionStrings__AeroLink: `Data Source=${e2eDatabase}`,
      },
      url: `http://127.0.0.1:${e2eApiPort}/health`,
      reuseExistingServer: false,
      timeout: 120_000,
    },
    {
      command: `npm run dev -- --host 127.0.0.1 --port ${e2eClientPort} --strictPort`,
      env: { VITE_API_URL: `http://127.0.0.1:${e2eApiPort}` },
      url: `http://127.0.0.1:${e2eClientPort}`,
      reuseExistingServer: false,
      timeout: 60_000,
    },
  ],
})
