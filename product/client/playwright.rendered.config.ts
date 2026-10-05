import { defineConfig, devices } from '@playwright/test'
import tiers from './fast-client-tests.json' with { type: 'json' }
import { fastShard } from './fast-shard'

const port = process.env.AEROLINK_FAST_CLIENT_PORT ?? '5188'
const baseURL = `http://127.0.0.1:${port}`

// The advisory Fast lane runs the rendered tier in parallel parts (#1313): the software-WebGL out-the-window specs
// ("3d"), which take about as long as all the others together; the CDU spec ("cdu"), 246 of the other 462 seconds on
// the hosted runners (2026-10-05); and the rest ("standard"). The view's tests are in three files so the Full browser
// shards can spread them (#1298, #1232). Each part runs one worker per job, and Fast shards every part by test across
// jobs (#1232). Playwright shards by test count in file order, so a long file kept with short ones would leave one
// shard with most of the time; a part of its own spreads it. Unset, every rendered spec runs, as it does locally.
export const RENDERED_3D = ['fms-out-the-window-rendered.spec.ts', 'fms-out-the-window-imagery-rendered.spec.ts', 'fms-out-the-window-models-rendered.spec.ts']
export const RENDERED_CDU = ['fms-cdu-rendered.spec.ts']
const part = process.env.AEROLINK_FAST_RENDERED_PART || undefined
if (part !== undefined && part !== 'standard' && part !== 'cdu' && part !== '3d') throw new Error(`Unknown AEROLINK_FAST_RENDERED_PART: ${part}`)
const testMatch = part === '3d' ? tiers.rendered.filter((file) => RENDERED_3D.includes(file))
  : part === 'cdu' ? tiers.rendered.filter((file) => RENDERED_CDU.includes(file))
  : part === 'standard' ? tiers.rendered.filter((file) => !RENDERED_3D.includes(file) && !RENDERED_CDU.includes(file)) : tiers.rendered

// These fixtures provide their own raw response data and exercise real components.
// Integrated pages and persistence continue to be proved by the complete Full suite.
export default defineConfig({
  testDir: './tests',
  testMatch,
  workers: 1,
  fullyParallel: true,
  shard: fastShard(),
  retries: 0,
  expect: { timeout: 15_000 },
  outputDir: 'test-results/fast/rendered',
  reporter: [
    ['list'],
    ['json', { outputFile: 'test-results/fast/rendered.json' }],
    ['html', { open: 'never', outputFolder: 'playwright-report/fast' }],
    // #986: the host socket state at a failed attempt, logged because the job log is always retained.
    ['./scripts/socket-snapshot-reporter.mjs'],
  ],
  use: {
    baseURL,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    // Motion evidence is captured on request (AEROLINK_1022_VIDEO=1) so a reviewer can watch the automatic
    // click-time framing and its interruption without paying the recording cost on every run.
    video: process.env.AEROLINK_1022_VIDEO ? 'on' : 'off',
  },
  projects: [{ name: 'rendered', use: { ...devices['Desktop Chrome'] } }],
  webServer: {
    command: `npm run dev -- --host 127.0.0.1 --port ${port} --strictPort`,
    url: baseURL,
    reuseExistingServer: false,
    timeout: 60_000,
  },
})
