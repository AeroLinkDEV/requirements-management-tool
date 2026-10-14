import { defineConfig, devices } from '@playwright/test'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import rendered from '../../playwright.rendered.config'

// The built-component run of the recording owner: the actual production plugins/bench/worker, emitted by
// fms-recording-built-vite.ts into a declared fresh directory and served by vite preview. A component fixture,
// not the shipped distribution. Paths are absolute because this file does not sit beside the config it extends.
const client = fileURLToPath(new URL('../../', import.meta.url))
const port = process.env.AEROLINK_FMS_RECORDING_PREVIEW_PORT ?? '5934'
const baseURL = `http://127.0.0.1:${port}`
if (!process.env.AEROLINK_FMS_RECORDING_DIST) throw new Error('The built component directory must be declared')

export default defineConfig({
  ...rendered,
  testDir: join(client, 'tests'),
  outputDir: join(client, 'test-results', 'fms-recording-built'),
  reporter: [['line']],
  testMatch: ['fms-kernel-journal-rendered.spec.ts'],
  grep: /the public recording file verifies in its declared environment without changing the live session/,
  workers: 1, retries: 0,
  projects: [{ name: 'recording-built', metadata: { recordingEnvironment: 'built-component' }, use: { ...devices['Desktop Chrome'] } }],
  use: { ...rendered.use, baseURL },
  webServer: {
    command: `node node_modules/vite/bin/vite.js preview --config tests/fixtures/fms-recording-built-vite.ts --host 127.0.0.1 --port ${port} --strictPort`,
    cwd: client,
    url: `${baseURL}/tests/fixtures/fms-cdu.html`, reuseExistingServer: false, timeout: 60_000,
  },
})
