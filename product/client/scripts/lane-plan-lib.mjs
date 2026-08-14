// Pure planning and verification logic for dual-lane browser execution (#564).
//
// The GitHub shard is computed exactly like plan-journey-shard.mjs (duration-weighted heaviest-first);
// then the shard's files are split into two local lanes the same way. Duration data is an optimisation,
// never a coverage input: unknown files use the median and the union of the lanes is always the shard.

import { readFileSync, existsSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

export function discoverSpecs(listedText) {
  const counts = new Map()
  for (const line of String(listedText).split('\n')) {
    const match = line.match(/›\s+([A-Za-z0-9._-]+\.spec\.ts):/)
    if (match) counts.set(match[1], (counts.get(match[1]) ?? 0) + 1)
  }
  return counts
}

export function loadDurations() {
  const path = join(dirname(fileURLToPath(import.meta.url)), '..', 'journey-durations.json')
  if (!existsSync(path)) return {}
  try {
    return JSON.parse(readFileSync(path, 'utf8'))
  } catch {
    return {}
  }
}

export function weightFiles(counts, durations) {
  const known = [...counts.keys()].map((file) => durations[file]).filter((d) => typeof d === 'number' && d > 0).sort((a, b) => a - b)
  const median = known.length ? known[Math.floor(known.length / 2)] : 1
  return [...counts.keys()]
    .map((file) => ({ file, tests: counts.get(file), weight: durations[file] ?? median }))
    .sort((a, b) => b.weight - a.weight || a.file.localeCompare(b.file))
}

export function packIntoLanes(entries, laneCount) {
  if (!Number.isInteger(laneCount) || laneCount < 1) throw new Error('laneCount must be a positive integer.')
  const load = Array.from({ length: laneCount }, () => 0)
  const lanes = Array.from({ length: laneCount }, () => [])
  for (const entry of entries) {
    let lightest = 0
    for (let i = 1; i < laneCount; i += 1) if (load[i] < load[lightest]) lightest = i
    load[lightest] += entry.weight
    lanes[lightest].push(entry)
  }
  return lanes.map((entries, index) => ({
    name: String.fromCharCode(97 + index),
    files: entries.map((entry) => entry.file),
    expected: entries.reduce((sum, entry) => sum + entry.tests, 0),
    estimatedMs: Math.round(load[index]),
  }))
}

export function planShard(counts, durations, shard, shardTotal) {
  const files = weightFiles(counts, durations)
  const load = Array.from({ length: shardTotal }, () => 0)
  const mine = []
  let expected = 0
  for (const entry of files) {
    let lightest = 0
    for (let i = 1; i < shardTotal; i += 1) if (load[i] < load[lightest]) lightest = i
    load[lightest] += entry.weight
    if (lightest === shard - 1) {
      mine.push(entry)
      expected += entry.tests
    }
  }
  return { mine, expected, shard, shardTotal }
}

export function verifyLaneCoverage({ plan, lanes }) {
  const errors = []
  if (!Array.isArray(plan?.lanes) || plan.lanes.length < 2) errors.push('Plan has no two lanes.')
  if (!Array.isArray(lanes) || lanes.length !== (plan?.lanes?.length ?? 0)) errors.push('Lane result count does not match the plan.')
  const plannedFiles = new Set((plan?.lanes ?? []).flatMap((lane) => lane.files))
  const seen = new Set()
  for (const [index, lane] of (lanes ?? []).entries()) {
    const expectedFiles = new Set(plan?.lanes?.[index]?.files ?? [])
    if (expectedFiles.size === 0) errors.push(`Lane ${index} planned no files; refusing to run the whole suite.`)
    const executedFiles = new Set((lane?.files ?? []).map((file) => file.split(/[\\/]/).at(-1)))
    for (const file of executedFiles) {
      if (seen.has(file)) errors.push(`Spec ${file} ran in more than one lane.`)
      seen.add(file)
      if (!expectedFiles.has(file)) errors.push(`Spec ${file} was not planned for lane ${index}.`)
    }
    if (lane?.executed !== plan?.lanes?.[index]?.expected) {
      errors.push(`Lane ${index} executed ${lane?.executed} but planned ${plan?.lanes?.[index]?.expected}.`)
    }
  }
  for (const file of plannedFiles) {
    if (!seen.has(file)) errors.push(`Planned spec ${file} did not run in any lane.`)
  }
  const combined = (lanes ?? []).reduce((sum, lane) => sum + (lane?.executed ?? 0), 0)
  if (combined !== (plan?.expected ?? -1)) errors.push(`Combined executed ${combined} does not equal the shard plan ${plan?.expected}.`)
  return { ok: errors.length === 0, errors, combined }
}

export function mergeLaneReports(lanes, { shard }) {
  const stats = { expected: 0, unexpected: 0, flaky: 0, skipped: 0 }
  const suites = []
  for (const lane of lanes) {
    stats.expected += lane.stats.expected
    stats.unexpected += lane.stats.unexpected
    stats.flaky += lane.stats.flaky
    stats.skipped += lane.stats.skipped
    for (const suite of lane.suites ?? []) suites.push(suite)
  }
  return { stats, suites, shard }
}

export function laneEnvironment({ runId, shard, lane, baseApiPort = 6080, baseClientPort = 7080 }) {
  const laneIndex = lane === 'a' ? 0 : 1
  const offset = (shard - 1) * 20
  return {
    AEROLINK_E2E_RUN_ID: `${runId}-${shard}-${lane}`,
    AEROLINK_E2E_API_PORT: String(baseApiPort + offset + laneIndex),
    AEROLINK_E2E_CLIENT_PORT: String(baseClientPort + offset + laneIndex),
    AEROLINK_E2E_OUTPUT_DIR: `test-results-lane-${lane}`,
    AEROLINK_E2E_REPORT_DIR: `playwright-report-lane-${lane}`,
    PLAYWRIGHT_JSON_OUTPUT_NAME: `durations-lane-${lane}.json`,
    AEROLINK_E2E_SKIP_BUILD: 'true',
    CI: 'true',
  }
}
