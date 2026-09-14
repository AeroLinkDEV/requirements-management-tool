import { defineConfig, devices } from '@playwright/test'
export default defineConfig({
  testDir: './tests', testMatch: ['digital-thread-1046-reproduction.spec.ts', 'digital-thread-1046-geometry.spec.ts'], workers: 1, retries: 0,
  outputDir: 'test-results/1046', reporter: [['list'], ['json', { outputFile: 'test-results/1046-result.json' }]],
  use: { baseURL: 'http://127.0.0.1:5196', viewport: { width: 1920, height: 1000 }, trace: 'on', video: { mode: 'on', size: { width: 1920, height: 1000 } }, screenshot: 'on' },
  projects: [{ name: '1046', use: { ...devices['Desktop Chrome'], viewport: { width: 1920, height: 1000 } } }],
  webServer: { command: 'npm run dev -- --host 127.0.0.1 --port 5196 --strictPort --config vite.1046.config.ts', url: 'http://127.0.0.1:5196', reuseExistingServer: false },
})
