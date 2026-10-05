import { defineConfig } from '@playwright/test'
import tiers from './fast-client-tests.json' with { type: 'json' }
import durations from './fast-logic-durations.json' with { type: 'json' }
import { fastShard, packedFiles } from './fast-shard'

// Pure behavior checks do not start Chromium, Vite, an API, or a database.
// Tests, not files, are the unit of distribution within a run: two FMS scenario files held half the suite's time, so
// file-level workers finished minutes apart and the hosted Fast lane hit its ten-minute cap (#1456). Fast runs the
// tier as shards of whole files packed by their recorded hosted durations (fast-logic-durations.json, fast-shard.ts).
const shard = fastShard()
export default defineConfig({
  testDir: './tests',
  testMatch: shard ? packedFiles(tiers.logic, durations, shard) : tiers.logic,
  workers: 3,
  fullyParallel: true,
  retries: 0,
  outputDir: 'test-results/fast/logic',
  reporter: [['list'], ['json', { outputFile: 'test-results/fast/logic.json' }]],
  projects: [{ name: 'logic' }],
})
