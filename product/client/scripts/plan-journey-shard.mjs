// Decides which journey spec files this shard runs.
//
// Playwright's own `--shard` balances test *count*, which it does well — 58/57/54/56 across four shards. But
// the shards took 360s, 328s, 233s and 662s, because what varies is duration per test. One file,
// zzz-post-414-picker-integrity.spec.ts, is 220 of the suite's 1182 seconds on its own. No count-based split
// can see that.
//
// So the files are packed by recorded duration instead: heaviest first into whichever shard is currently
// lightest. Every shard runs this identical computation over the same inputs, so the union of the shards is
// every discovered file and the intersection is empty by construction — there is no list to maintain and no
// way for a new spec to land outside every shard.
//
// Usage: node scripts/plan-journey-shard.mjs <listed.txt> <shard> <shardTotal>
//   listed.txt is the output of `npx playwright test --list`.
//   Prints the expected test count on the first line, then one spec file per line.

import { readFileSync, existsSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const [listedPath, shardArg, totalArg] = process.argv.slice(2)
const shard = Number(shardArg)
const total = Number(totalArg)

if (!listedPath || !Number.isInteger(shard) || !Number.isInteger(total) || shard < 1 || total < 1 || shard > total) {
  console.error('usage: plan-journey-shard.mjs <listed.txt> <shard> <shardTotal>')
  process.exit(2)
}

// Count tests per spec file from the discovery output. Lines look like:
//   [chromium] › some-name.spec.ts:12:1 › the test title
const listed = readFileSync(listedPath, 'utf8')
const counts = new Map()
for (const line of listed.split('\n')) {
  const match = line.match(/›\s+([A-Za-z0-9._-]+\.spec\.ts):/)
  if (match) counts.set(match[1], (counts.get(match[1]) ?? 0) + 1)
}

if (counts.size === 0) {
  console.error('No spec files found in the discovery output. Playwright listed nothing, so a shard plan would silently run no tests.')
  process.exit(1)
}

// Recorded durations are an optimisation, not a correctness input. A missing file, a new spec, or a stale
// entry must never drop a test — an unknown file is simply weighted at the median, which degenerates to a
// count-based split when nothing is known at all.
const durationsPath = join(dirname(fileURLToPath(import.meta.url)), '..', 'journey-durations.json')
const durations = existsSync(durationsPath) ? JSON.parse(readFileSync(durationsPath, 'utf8')) : {}

const known = [...counts.keys()].map((f) => durations[f]).filter((d) => typeof d === 'number' && d > 0).sort((a, b) => a - b)
const median = known.length ? known[Math.floor(known.length / 2)] : 1

const files = [...counts.keys()]
  .map((file) => ({ file, tests: counts.get(file), weight: durations[file] ?? median }))
  // Heaviest first, ties broken by name so every runner computes the identical assignment.
  .sort((a, b) => b.weight - a.weight || a.file.localeCompare(b.file))

const load = Array.from({ length: total }, () => 0)
const mine = []
let expected = 0

for (const entry of files) {
  let lightest = 0
  for (let i = 1; i < total; i++) if (load[i] < load[lightest]) lightest = i
  load[lightest] += entry.weight
  if (lightest === shard - 1) {
    mine.push(entry.file)
    expected += entry.tests
  }
}


// Playwright matches a positional argument as a regular expression against the spec path, not as a file
// name, so a bare basename also matches every longer name ending with it: handing a shard
// review-comments.spec.ts silently drags in document-review-comments.spec.ts, which another shard was
// given. That runs one test twice across the suite and leaves this shard one over the count it planned.
// So each name is anchored between something that cannot be part of a file name -- a path separator of
// either kind, or the start of the path -- and the end of the path, which names exactly one file.
const anchored = (file) => '(^|[^A-Za-z0-9._-])' + file.split('.').join('[.]') + '$'

console.log(expected)
for (const file of mine) console.log(anchored(file))
