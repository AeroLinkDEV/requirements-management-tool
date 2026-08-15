#!/usr/bin/env node
// Regenerates product/test-contracts/route-coverage.json from source.
//
// Run this deliberately, read the diff, and commit it. The manifest is a reviewed record of which mutating
// routes have hosted coverage; regenerating it silently as part of a build would make the guard agree with
// whatever just happened, which is the opposite of its purpose.

import { writeFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { buildRouteCoverage, summariseCoverage } from '../lib/routes.mjs'

const here = dirname(fileURLToPath(import.meta.url))
const repoRoot = join(here, '..', '..', '..')
const apiDirectory = join(repoRoot, 'product', 'src', 'AeroLink.Api')
const testsDirectory = join(repoRoot, 'product', 'tests', 'AeroLink.Api.Tests')
const manifestPath = join(here, '..', 'route-coverage.json')

const coverage = buildRouteCoverage(apiDirectory, testsDirectory)
const summary = summariseCoverage(coverage)

writeFileSync(
  manifestPath,
  `${JSON.stringify({ schemaVersion: 'aerolink-route-coverage/v1', routes: coverage }, null, 2)}\n`,
  'utf8',
)

console.log(`routes: ${summary.total}  covered: ${summary.covered}  uncovered: ${summary.uncovered.length}`)
for (const route of summary.uncovered) console.log(`  uncovered: ${route.method} ${route.path}  [${route.file}]`)
