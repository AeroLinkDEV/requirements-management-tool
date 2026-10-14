import { defineConfig, devices } from '@playwright/test'
import rendered from './playwright.rendered.config'

const port = process.env.AEROLINK_FMS_RECORDING_PREVIEW_PORT ?? '5934'
const baseURL = `http://127.0.0.1:${port}`
if (!process.env.AEROLINK_FMS_RECORDING_DIST) throw new Error('The built component directory must be declared')

export default defineConfig({
  ...rendered,
  testMatch: ['fms-kernel-journal-rendered.spec.ts'],
  grep: /the public recording file verifies in its declared environment without changing the live session/,
  workers: 1, retries: 0,
  projects: [{ name: 'recording-built', metadata: { recordingEnvironment: 'built-component' }, use: { ...devices['Desktop Chrome'] } }],
  use: { ...rendered.use, baseURL },
  webServer: {
    command: `node node_modules/vite/bin/vite.js preview --config tests/fixtures/fms-recording-built.config.ts --host 127.0.0.1 --port ${port} --strictPort`,
    url: `${baseURL}/tests/fixtures/fms-cdu.html`, reuseExistingServer: false, timeout: 60_000,
  },
})
