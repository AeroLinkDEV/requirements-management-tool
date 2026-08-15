#!/usr/bin/env node
// Regenerates the *observation* — product/test-contracts/route-coverage.json.
//
// It deliberately does not touch grandfathered-uncovered.json. That file is the policy baseline: the set of
// routes allowed to have no hosted boundary evidence, frozen when this contract was introduced.
//
// The distinction is the whole safeguard. Round-1 review of #588 found that a generator which rewrites both
// makes the guard self-resetting: a migration could remove the last hosted proof of a route, run this script
// exactly as the failure message instructed, and get a green required gate. The observation may change freely
// because it describes what is; the policy may only change by a reviewed edit because it describes what is
// allowed.

import { writeFileSync, readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { buildRouteCoverage, coveredButStillGrandfathered, summariseCoverage, uncoveredOutsideBaseline } from '../lib/routes.mjs'

const here = dirname(fileURLToPath(import.meta.url))
const repoRoot = join(here, '..', '..', '..')
const apiDirectory = join(repoRoot, 'product', 'src', 'AeroLink.Api')
const testsDirectory = join(repoRoot, 'product', 'tests', 'AeroLink.Api.Tests')
const manifestPath = join(here, '..', 'route-coverage.json')
const baselinePath = join(here, '..', 'grandfathered-uncovered.json')

const coverage = buildRouteCoverage(apiDirectory, testsDirectory)
const summary = summariseCoverage(coverage)

writeFileSync(
  manifestPath,
  `${JSON.stringify({ schemaVersion: 'aerolink-route-coverage/v2', routes: coverage }, null, 2)}\n`,
  'utf8',
)

const baseline = new Set(JSON.parse(readFileSync(baselinePath, 'utf8')).uncovered)
const outsideBaseline = uncoveredOutsideBaseline(coverage, baseline)
const earnedOut = coveredButStillGrandfathered(coverage, baseline)

console.log(`routes: ${summary.total}  covered: ${summary.covered}  uncovered: ${summary.uncovered.length}`)
console.log(`grandfathered: ${baseline.size}  outside the baseline: ${outsideBaseline.length}  earned out: ${earnedOut.length}`)

if (outsideBaseline.length > 0) {
  console.log('\nThese have no hosted boundary evidence and are not grandfathered. The guard will fail until')
  console.log('each is covered by a hosted test using its own HTTP method, or deliberately added to')
  console.log('grandfathered-uncovered.json as a reviewed decision:')
  for (const key of outsideBaseline) console.log(`  ${key}`)
  process.exitCode = 1
}

// The baseline may only shrink. An exception left standing after its route gains coverage still permits the
// next loss of that route's last hosted proof, which turns a shrinking record into a permanent exemption.
if (earnedOut.length > 0) {
  console.log('\nThese now have hosted boundary coverage and must surrender their exception. Remove them from')
  console.log('grandfathered-uncovered.json:')
  for (const key of earnedOut) console.log(`  ${key}`)
  process.exitCode = 1
}
