import { defineConfig, devices } from '@playwright/test'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

const dotnet = `${process.env.USERPROFILE?.replaceAll('\\', '/')}/.dotnet/dotnet.exe`
const runId = process.env.AEROLINK_E2E_RUN_ID ?? `${Date.now()}`
process.env.AEROLINK_E2E_RUN_ID = runId
const e2eDatabase = join(tmpdir(), `aerolink-e2e-${runId}.db`).replaceAll('\\', '/')
const e2eApiPort = process.env.AEROLINK_E2E_API_PORT ?? '5082'
process.env.AEROLINK_E2E_API_BASE = `http://127.0.0.1:${e2eApiPort}`

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
      command: `powershell.exe -NoProfile -Command "$env:Database__Provider='Sqlite'; $env:DemoData__Enabled='false'; $env:ConnectionStrings__AeroLink='Data Source=${e2eDatabase}'; & '${dotnet}' run --configuration Release --project ../src/AeroLink.Api --urls http://127.0.0.1:${e2eApiPort}"`,
      url: `http://127.0.0.1:${e2eApiPort}/health`,
      reuseExistingServer: false,
      timeout: 120_000,
    },
    {
      command: `powershell.exe -NoProfile -Command "$env:VITE_API_URL='http://127.0.0.1:${e2eApiPort}'; npm.cmd run dev -- --host 127.0.0.1 --port 5174 --strictPort"`,
      url: 'http://127.0.0.1:5174',
      reuseExistingServer: false,
      timeout: 60_000,
    },
  ],
})
