// Restricts a browser-journey discovery listing to the FMS Test Bench journeys, for an FMS-only change.
//
// Usage: node filter-fms-journeys.mjs <listed.txt>
//   listed.txt is the output of `npx playwright test --list`. Prints the same listing keeping only the test
//   lines whose spec file is an FMS journey (classify.mjs isFmsJourneySpec): the bench's own specs and the
//   specs that observe it. The shard planner then packs these exactly as it packs the full listing.
//
// Fails, rather than printing nothing, when no FMS journey is listed: an empty plan would run every spec on
// every shard (Playwright with no file arguments runs the whole suite), or none, and neither is the FMS set.

import { readFileSync } from 'node:fs'
import { isFmsJourneySpec } from '../lib/classify.mjs'

const [listedPath] = process.argv.slice(2)
if (!listedPath) {
  console.error('usage: filter-fms-journeys.mjs <listed.txt>')
  process.exit(2)
}

const kept = []
const files = new Set()
for (const line of readFileSync(listedPath, 'utf8').split('\n')) {
  const match = line.match(/›\s+([A-Za-z0-9._-]+\.spec\.ts):/)
  if (match && isFmsJourneySpec(match[1])) {
    kept.push(line)
    files.add(match[1])
  }
}
if (kept.length === 0) {
  console.error('::error::The discovery listing holds no FMS journey, so an FMS-only change would validate no journey at all.')
  process.exit(1)
}
console.error(`FMS-only change: ${kept.length} tests across ${files.size} spec files (${[...files].sort().join(', ')}).`)
process.stdout.write(`${kept.join('\n')}\n`)
