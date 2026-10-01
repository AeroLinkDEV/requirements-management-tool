import { defineConfig, devices } from '@playwright/test'
import tiers from './fast-client-tests.json' with { type: 'json' }

const port = process.env.AEROLINK_FAST_CLIENT_PORT ?? '5188'
const baseURL = `http://127.0.0.1:${port}`

// The advisory Fast lane runs the rendered tier in two parallel parts (#1313): the software-WebGL out-the-window spec
// alone ("3d"), which takes about as long as all the others together, and the rest ("standard"). Unset, every
// rendered spec runs, as it does locally.
export const RENDERED_3D = ['fms-out-the-window-rendered.spec.ts']
const part = process.env.AEROLINK_FAST_RENDERED_PART || undefined
if (part !== undefined && part !== 'standard' && part !== '3d') throw new Error(`Unknown AEROLINK_FAST_RENDERED_PART: ${part}`)
const testMatch = part === '3d' ? tiers.rendered.filter((file) => RENDERED_3D.includes(file))
  : part === 'standard' ? tiers.rendered.filter((file) => !RENDERED_3D.includes(file)) : tiers.rendered

// These fixtures provide their own raw response data and exercise real components.
// Integrated pages and persistence continue to be proved by the complete Full suite.
export default defineConfig({
  testDir: './tests',
  testMatch,
  // The 3d part's eleven tests are independent and each spends most of its time in software WebGL, so two workers
  // share them (#1232): on one worker the part reached the job's 10-minute cap on 6 of 10 advisory runs.
  workers: part === '3d' ? 2 : 1,
  fullyParallel: part === '3d',
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
