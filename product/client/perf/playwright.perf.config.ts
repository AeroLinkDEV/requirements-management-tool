import { defineConfig, devices } from '@playwright/test'
import { existsSync, readFileSync } from 'node:fs'
import { homedir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { createBrowserStorage } from '../scripts/browser-storage.mjs'
import { assertBrowserScoringAvailable } from './browser-accounting.ts'

/**
 * The FMS performance harness (#1510 I0a). Outside tests/, so neither the Full nor the production config discovers it.
 * One invocation is one run; the orchestrator (run-perf.ts) spawns each run in ABBA order and never uses --repeat-each.
 *
 * AEROLINK_PERF_MODE=headless runs headless.perf.ts in this process tree with no server.
 * AEROLINK_PERF_MODE=browser refuses before storage, authentication or API creation while accounting is unqualified.
 * AEROLINK_PERF_MODE=seed remains available for independently scoped disposable fixture diagnostics, serving a built
 * arm through Client__StaticFiles with the terrain relay off. It does not admit or qualify a browser scored window.
 */
const clientDir = fileURLToPath(new URL('..', import.meta.url))
const mode = process.env.AEROLINK_PERF_MODE ?? 'headless'
if (!['headless', 'browser', 'seed'].includes(mode)) throw new Error(`unknown AEROLINK_PERF_MODE ${mode}`)
// Refuse before constructing storage/global setup/webServer when this config is invoked without run-perf.ts.
if (mode === 'browser') assertBrowserScoringAvailable(JSON.parse(readFileSync(new URL('./protocol.json', import.meta.url), 'utf8')))
const outputDir = process.env.AEROLINK_PERF_OUTPUT_DIR ?? join(clientDir, 'test-results', 'perf')
const browser = mode !== 'headless'

const windowsDotnet = process.env.USERPROFILE ? join(process.env.USERPROFILE, '.dotnet', 'dotnet.exe') : undefined
const posixDotnet = join(homedir(), '.dotnet', 'dotnet')
const dotnet = process.env.AEROLINK_DOTNET ?? [windowsDotnet, posixDotnet].find(candidate => candidate && existsSync(candidate)) ?? 'dotnet'
const port = process.env.AEROLINK_PERF_PORT ?? '5091'
const origin = `http://127.0.0.1:${port}`

function browserServer() {
  const dist = process.env.AEROLINK_PERF_DIST
  if (!dist || !existsSync(join(dist, 'index.html'))) throw new Error('AEROLINK_PERF_DIST must name an arm\'s built dist')
  const runId = process.env.AEROLINK_E2E_RUN_ID ?? `perf-${Date.now()}`
  process.env.AEROLINK_E2E_RUN_ID = runId
  process.env.AEROLINK_E2E_API_BASE = origin
  const storage = createBrowserStorage(runId)
  return {
    command: 'node scripts/run-api-with-log.mjs',
    cwd: clientDir,
    env: {
      // --no-build: the API is built once from the harness checkout (dotnet build -c Release) before a session, and the
      // orchestrator records the harness commit, source tree and the built assembly's SHA-256. --no-build runs whatever
      // was last built there: the record identifies that binary but cannot by itself prove which tree it came from.
      AEROLINK_E2E_API_ARGV: JSON.stringify([dotnet, 'run', '--configuration', 'Release', '--no-build', '--project', '../src/AeroLink.Api', '--urls', origin]),
      AEROLINK_E2E_API_LOG: join(outputDir, 'api.log'),
      AEROLINK_E2E_API_LOG_LABEL: 'perf-api',
      'Logging__LogLevel__Microsoft.AspNetCore': 'Warning',
      'Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command': 'Warning',
      Client__StaticFiles: dist,
      Database__Provider: 'Sqlite',
      Database__SqliteSynchronous: 'Off',
      Evidence__Root: storage.evidence,
      DemoData__Enabled: 'false',
      Identity__SeedDemoAccounts: 'true',
      Identity__AllowDemoAccounts: 'true',
      Identity__CookieSecure: 'false',
      Identity__LoginRateLimitPerMinute: '500',
      FmsBench__TerrainRelay: 'false',
      ConnectionStrings__AeroLink: `Data Source=${storage.database}`,
    },
    url: `${origin}/health/ready`,
    reuseExistingServer: false,
    timeout: 180_000,
  }
}

export default defineConfig({
  testDir: '.',
  testMatch: `${mode}.perf.ts`,
  outputDir,
  workers: 1,
  retries: 0,
  fullyParallel: false,
  reporter: [['list']],
  ...(browser ? {
    // A browser run takes the seed the orchestrator recorded (AEROLINK_SHOWCASE_SEED); the seed run makes it.
    ...(mode === 'browser' ? { globalSetup: '../tests/global-setup.ts' } : {}),
    use: { ...devices['Desktop Chrome'], baseURL: origin, trace: 'off', screenshot: 'off', video: 'off' },
    webServer: [browserServer()],
  } : {}),
})
