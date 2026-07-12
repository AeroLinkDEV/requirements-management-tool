import { defineConfig, devices } from '@playwright/test'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

const dotnet = `${process.env.USERPROFILE?.replaceAll('\\', '/')}/.dotnet/dotnet.exe`
const runId = process.env.AEROLINK_E2E_RUN_ID ?? `${Date.now()}`
process.env.AEROLINK_E2E_RUN_ID = runId
const e2eDatabase = join(tmpdir(), `aerolink-e2e-${runId}.db`).replaceAll('\\', '/')

export default defineConfig({
  testDir: './tests',
  fullyParallel: false,
  workers: 1,
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    baseURL: 'http://127.0.0.1:5174',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: [
    {
      command: `powershell.exe -NoProfile -Command "$env:Database__Provider='Sqlite'; $env:ConnectionStrings__AeroLink='Data Source=${e2eDatabase}'; & '${dotnet}' run --project ../src/AeroLink.Api --urls http://127.0.0.1:5080"`,
      url: 'http://127.0.0.1:5080/health',
      reuseExistingServer: true,
      timeout: 120_000,
    },
    {
      command: 'npm run dev -- --host 127.0.0.1 --port 5174 --strictPort',
      url: 'http://127.0.0.1:5174',
      reuseExistingServer: false,
      timeout: 60_000,
    },
  ],
})
