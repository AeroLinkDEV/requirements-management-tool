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

// Where the API's own console output is kept (#939).
//
// Playwright forwards a webServer's stderr by default but discards its stdout unless asked, and ASP.NET
// Core's console logger writes every level — requests, warnings, exceptions — to stdout. So a shard could
// fail on requests that recorded no response and still leave a complete job log containing nothing at all
// from the server. `scripts/run-api-with-log.mjs` keeps a transcript instead.
//
// Deliberately a sibling of the report and results directories rather than a child of either: Playwright
// clears its output directory when a run starts, and the server is launched before that happens.
const apiLogDir = process.env.AEROLINK_E2E_API_LOG_DIR ?? 'api-logs'
const apiLogPath = join(apiLogDir, `api-${runId}.log`)

export default defineConfig({
  testDir: './tests',
  // The production journeys have their own config, because they need the API to serve the built client rather
  // than Vite to serve modules. Running them here would test dev and assert about a build.
  testIgnore: 'production/**',
  globalSetup: './tests/global-setup.ts',
  outputDir,
  // One worker, deliberately: every journey shares one API and one database, so two running at once can see
  // each other's seeded state. Parallelism here needs per-test isolation first, which is tracked separately —
  // CI gets its parallelism from sharding, where each shard is a separate process with its own API, database
  // and ports.
  fullyParallel: false,
  workers: 1,
  // One retry on CI, none locally.
  //
  // This suite produces roughly one load-induced flake per full run — a different test each time, each one
  // passing in isolation. With no retries and fail-fast shards, a single flake cost a complete re-run: about
  // twenty-five minutes to re-learn that nothing was wrong. A retry costs seconds and only when something has
  // already failed.
  //
  // Playwright reports a test that passes on retry as `flaky`, not as passing, so this hides nothing: the
  // count still appears in the run summary and the trace from the failed attempt is still uploaded. What it
  // stops is a flake ending the whole run.
  retries: process.env.CI ? 1 : 0,
  // Fifteen seconds, not Playwright's five.
  //
  // Almost every assertion here waits on a server round-trip — a signed determination, a released lock, a
  // refetched list — and on a loaded two-core runner five seconds is shorter than the work. Four separate
  // assertions have failed that way, each reading as "the control never came back" when it simply had not
  // come back yet, and each fix was to write the timeout out longhand at one more call site.
  //
  // Fifteen is well under the thirty this suite already writes explicitly where a wait is known to be long,
  // and far enough above the work that a failure means something. A genuine hang still fails, fifteen
  // seconds later, which is the trade: slower to report the real thing, and it reports the real thing.
  expect: { timeout: 15_000 },
  reporter: [['list'], ['./tests/slow-test-reporter.ts'], ['html', { open: 'never', outputFolder: reportDir }]],
  use: {
    baseURL: `http://127.0.0.1:${e2eClientPort}`,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: [
    {
      // The real command travels in the environment as a structured argument vector, so no shell has to
      // interpret a Windows dotnet path containing spaces — and, more importantly, so the wrapper's child
      // is the server itself rather than an intermediate shell it cannot see past.
      command: 'node scripts/run-api-with-log.mjs',
      env: {
        AEROLINK_E2E_API_ARGV: JSON.stringify([
          dotnet, 'run', '--configuration', 'Release',
          ...(skipApiBuild ? ['--no-build'] : []),
          '--project', '../src/AeroLink.Api',
          '--urls', `http://127.0.0.1:${e2eApiPort}`,
        ]),
        AEROLINK_E2E_API_LOG: apiLogPath,
        AEROLINK_E2E_API_LOG_LABEL: 'browser-api',
        // What the transcript is actually for, chosen by measurement rather than taste.
        //
        // A first capture of one spec file produced 127,942 lines and 25.5 MB, of which 97% were
        // `EntityFrameworkCore.Database.Command` at Information — mostly the showcase seed's DDL — and
        // *none* were request events, because the shipped `appsettings.json` pins `Microsoft.AspNetCore`
        // to Warning. That transcript could not have answered the question it exists to answer: a request
        // that records no response needs "request starting" and "request finished", and those are exactly
        // the lines that were missing.
        //
        // So the harness raises request logging and lowers the SQL flood. This is test-harness
        // configuration, in the same block that already chooses the provider and the identity settings; no
        // shipped configuration, assertion, timeout, retry or gate changes.
        //
        // EF at Warning retains what EF emits at Warning or above. It is not a promise that a slow but
        // successful command will be recorded — EF logs those at Information, and they are gone. Accepting
        // that is the trade for a transcript small enough to read; a slow-query detector is a different
        // change with its own justification, and is not being smuggled in here.
        'Logging__LogLevel__Microsoft.AspNetCore': 'Information',
        'Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command': 'Warning',
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
