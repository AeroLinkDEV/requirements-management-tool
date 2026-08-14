// Writes the dual-lane plan for one GitHub browser shard.
//
// Usage: node scripts/plan-journey-lanes.mjs <listed.txt> <shard> <shardTotal> <output.json>
// listed.txt is `npx playwright test --list`. The plan splits the shard's files into two duration-balanced
// lanes and fails on an empty lane or empty discovery.

import { readFileSync, writeFileSync } from 'node:fs'
import { createHash } from 'node:crypto'
import { discoverSpecs, loadDurations, planShard, packIntoLanes } from './lane-plan-lib.mjs'

const [listedPath, shardArg, totalArg, outputPath] = process.argv.slice(2)
const shard = Number(shardArg)
const shardTotal = Number(totalArg)
if (!listedPath || !Number.isInteger(shard) || !Number.isInteger(shardTotal) || shard < 1 || shardTotal < 1 || shard > shardTotal || !outputPath) {
  console.error('usage: plan-journey-lanes.mjs <listed.txt> <shard> <shardTotal> <output.json>')
  process.exit(2)
}

const counts = discoverSpecs(readFileSync(listedPath, 'utf8'))
if (counts.size === 0) {
  console.error('No spec files found in the discovery output; refusing to plan an empty suite.')
  process.exit(1)
}

const { mine, expected } = planShard(counts, loadDurations(), shard, shardTotal)
const lanes = packIntoLanes(mine, 2)
if (lanes.some((lane) => lane.files.length === 0 || lane.expected === 0)) {
  console.error('A lane planned no files; refusing to run the whole suite in the other lane.')
  process.exit(1)
}

const plan = {
  schemaVersion: 'aerolink-dual-lane-plan/v1',
  shard,
  shardTotal,
  expected,
  lanes,
  planId: createHash('sha256').update([...lanes.flatMap((lane) => lane.files)].sort().join('\n')).digest('hex').slice(0, 16),
}
writeFileSync(outputPath, `${JSON.stringify(plan, null, 2)}\n`, 'utf8')
console.log(`[lanes] Shard ${shard}/${shardTotal}: ${expected} tests across lanes ${lanes.map((lane) => `${lane.name}=${lane.expected} tests/~${Math.round(lane.estimatedMs / 1000)}s`).join(', ')} (plan ${plan.planId}).`)
