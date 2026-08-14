// Runs one GitHub browser shard as two isolated Playwright processes (#564).
//
// Usage (cwd product/client): node scripts/run-dual-lanes.mjs <lane-plan.json>
// Each lane gets its own run ID, API/client ports, SQLite database, output and report directories, and
// JSON report. Both children are waited for; one failure never cancels the other. Coverage verification
// runs after both lanes finish, and a merged journey-durations-<shard>.json is written for the existing
// metrics/duration consumers.

import { readFileSync, writeFileSync, existsSync } from 'node:fs'
import { spawn, spawnSync } from 'node:child_process'
import { verifyLaneCoverage, mergeLaneReports, laneEnvironment } from './lane-plan-lib.mjs'

const [planPath] = process.argv.slice(2)
if (!planPath || !existsSync(planPath)) {
  console.error('usage: run-dual-lanes.mjs <lane-plan.json>')
  process.exit(2)
}
const plan = JSON.parse(readFileSync(planPath, 'utf8'))
const runId = process.env.GITHUB_RUN_ID ?? `local-${Date.now()}`
const laneTimeoutMs = Number(process.env.DUAL_LANE_TIMEOUT_MS ?? 28 * 60 * 1000)

function killTree(pid) {
  try {
    spawnSync('taskkill', ['/PID', String(pid), '/T', '/F'], { stdio: 'ignore', windowsHide: true })
  } catch {
    try { process.kill(pid, 'SIGKILL') } catch { /* already gone */ }
  }
}

function runLane(lane) {
  return new Promise((resolve) => {
    const env = { ...process.env, ...laneEnvironment({ runId, shard: plan.shard, lane: lane.name }) }
    const child = spawn('npx.cmd', ['playwright', 'test', ...lane.files, '--reporter=list,json'], {
      cwd: process.cwd(),
      env,
      shell: false,
      stdio: ['ignore', 'pipe', 'pipe'],
      windowsHide: true,
    })
    child.stdout.on('data', (chunk) => process.stdout.write(`[lane-${lane.name}] ${chunk}`))
    child.stderr.on('data', (chunk) => process.stderr.write(`[lane-${lane.name}] ${chunk}`))
    const timer = setTimeout(() => {
      console.error(`[lanes] Lane ${lane.name} exceeded ${Math.round(laneTimeoutMs / 60000)} minutes; killing its process tree.`)
      killTree(child.pid)
    }, laneTimeoutMs)
    child.on('exit', (code, signal) => {
      clearTimeout(timer)
      resolve({ lane, code, signal, pid: child.pid })
    })
  })
}

async function main() {
  const results = await Promise.all(plan.lanes.map(runLane))
  const lanes = []
  const failures = []
  for (const result of results) {
    const reportPath = `durations-lane-${result.lane.name}.json`
    if (!existsSync(reportPath)) {
      failures.push(`Lane ${result.lane.name} produced no JSON report (exit ${result.code}).`)
      continue
    }
    const report = JSON.parse(readFileSync(reportPath, 'utf8'))
    const files = []
    const walk = (suites) => {
      for (const suite of suites ?? []) {
        for (const spec of suite.specs ?? []) files.push(spec.file)
        walk(suite.suites)
      }
    }
    walk(report.suites)
    lanes.push({ name: result.lane.name, stats: report.stats, suites: report.suites ?? [], files, executed: report.stats.expected + report.stats.unexpected + report.stats.flaky + report.stats.skipped, exitCode: result.code })
    if (result.code !== 0) failures.push(`Lane ${result.lane.name} exited ${result.code}.`)
  }

  const verification = verifyLaneCoverage({ plan, lanes })
  if (!verification.ok) failures.push(`Coverage verification failed: ${verification.errors.join('; ')}`)

  if (lanes.length === plan.lanes.length) {
    const merged = mergeLaneReports(lanes, { shard: plan.shard })
    writeFileSync(`journey-durations-${plan.shard}.json`, `${JSON.stringify(merged, null, 2)}\n`, 'utf8')
  }
  writeFileSync('dual-lanes-summary.json', `${JSON.stringify({ planId: plan.planId, shard: plan.shard, failures, verification, lanes: lanes.map((lane) => ({ name: lane.name, executed: lane.executed, exitCode: lane.exitCode })) }, null, 2)}\n`, 'utf8')

  for (const result of results) if (result.pid) killTree(result.pid)
  if (failures.length > 0) {
    console.error(`[lanes] ${failures.join('; ')}`)
    process.exit(1)
  }
  console.log(`[lanes] Shard ${plan.shard} complete: ${verification.combined}/${plan.expected} tests, both lanes isolated.`)
}

process.on('SIGINT', () => process.exit(130))
process.on('SIGTERM', () => process.exit(143))

main().catch((error) => {
  console.error(`[lanes] Orchestrator failed: ${error.message}`)
  process.exit(1)
})
