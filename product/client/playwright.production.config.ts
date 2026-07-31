import { defineConfig, devices } from '@playwright/test'
import { existsSync } from 'node:fs'
import { homedir, tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'

/**
 * The journeys, run against the artifact a demonstration or a deployment actually serves.
 *
 * `playwright.config.ts` serves the client with `npm run dev`. So did `START_AEROLINK.bat`, and so did every
 * gate in CI — which meant the production bundle was compiled on every pull request and never once rendered.
 * `vite dev` hands over unbundled modules and injects each stylesheet as the module evaluating it is reached;
 * a build chunks the code, extracts the CSS, hashes and minifies. Those are different artifacts, and the
 * client has documented cascade rules that win by load order (see CAPABILITY_ROADMAP.md), so "it works in
 * dev" was never evidence about the thing being shipped.
 *
 * One server here, not two: the API serves the built client, so there is a single origin and no CORS — the
 * shape an on-premises deployment wants and the shape the demonstration launcher uses.
 */
const clientDir = fileURLToPath(new URL('.', import.meta.url))
const windowsDotnet = process.env.USERPROFILE
  ? join(process.env.USERPROFILE, '.dotnet', 'dotnet.exe')
  : undefined
const posixDotnet = join(homedir(), '.dotnet', 'dotnet')
const localDotnet = [windowsDotnet, posixDotnet].find(candidate => candidate && existsSync(candidate))
const dotnet = process.env.AEROLINK_DOTNET ?? localDotnet ?? 'dotnet'
const runId = process.env.AEROLINK_E2E_RUN_ID ?? `production-${Date.now()}`
process.env.AEROLINK_E2E_RUN_ID = runId
const database = join(tmpdir(), `aerolink-production-${runId}.db`).replaceAll('\\', '/')
const port = process.env.AEROLINK_E2E_PRODUCTION_PORT ?? '5086'
const origin = `http://127.0.0.1:${port}`
const skipApiBuild = process.env.AEROLINK_E2E_SKIP_BUILD === 'true'

// The seed helper and global setup talk to the API directly. Same origin as the client here, which is the
// point of the exercise.
process.env.AEROLINK_E2E_API_BASE = origin

export default defineConfig({
  testDir: './tests/production',
  globalSetup: './tests/global-setup.ts',
  outputDir: process.env.AEROLINK_E2E_OUTPUT_DIR ?? 'test-results-production',
  reporter: [['list'], ['html', { open: 'never', outputFolder: 'playwright-report-production' }]],
  fullyParallel: false,
  workers: 1,
  // The same fifteen seconds as the development journeys, and for the same reason: these assertions wait on
  // a server round-trip, and the five-second default is shorter than that work on a loaded runner.
  expect: { timeout: 15_000 },
  use: {
    baseURL: origin,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: [
    {
      command: `"${dotnet}" run --configuration Release ${skipApiBuild ? '--no-build ' : ''}--project ../src/AeroLink.Api --urls ${origin}`,
      env: {
        // Named explicitly rather than left to discovery, so a stale `dist` elsewhere on the machine can
        // never be the thing under test.
        Client__StaticFiles: join(clientDir, 'dist'),
        Database__Provider: 'Sqlite',
        DemoData__Enabled: 'false',
        Identity__SeedDemoAccounts: 'true',
        Identity__AllowDemoAccounts: 'true',
        // The demonstration and this gate both run over plain HTTP on the loopback interface. A deployment
        // terminates TLS at its proxy and leaves this at its default of true.
        Identity__CookieSecure: 'false',
        Identity__LoginRateLimitPerMinute: '500',
        ConnectionStrings__AeroLink: `Data Source=${database}`,
      },
      url: `${origin}/health/ready`,
      reuseExistingServer: false,
      timeout: 180_000,
    },
  ],
})
