// A read-only operator command. It cannot publish App checks, approve deployments or change settings.
import { execFile } from 'node:child_process'
import { promisify } from 'node:util'
import { mkdirSync, writeFileSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { collectMaintenancePreflight } from '../lib/maintenance-preflight-github.mjs'

const [prArg, runArg, outputArg, ...extra] = process.argv.slice(2)
if (!/^\d+$/.test(prArg ?? '') || !/^\d+$/.test(runArg ?? '') || !outputArg || extra.length) {
  console.error('Usage: node product/ci-metrics/bin/prepare-authority-maintenance.mjs <pr-number> <product-run-id> <new-output.json>')
  process.exit(2)
}
const execute = promisify(execFile)
const api = async args => JSON.parse((await execute('gh', ['api', ...args], { encoding: 'utf8', maxBuffer: 32 * 1024 * 1024, windowsHide: true })).stdout)
try {
  const output = resolve(outputArg)
  if (/(?:^|[\\/])product[\\/]\.local(?:[\\/]|$)/i.test(output)) throw new Error('Output must stay outside product/.local.')
  const packet = await collectMaintenancePreflight({
    prNumber: Number(prArg), runId: Number(runArg),
    read: path => api([path, '--method', 'GET']),
    graphql: query => api(['graphql', '-f', `query=${query}`]),
  })
  mkdirSync(dirname(output), { recursive: true })
  writeFileSync(output, `${JSON.stringify(packet, null, 2)}\n`, { flag: 'wx' })
  console.log(`${packet.assessment.disposition}: ${packet.assessment.reasons.join('; ') || 'Owner review is still required.'}`)
  console.log(`Evidence digest: ${packet.digest}`)
  console.log(`Read-only packet: ${output}`)
  console.log('This packet cannot authorize an App check or a merge.')
  process.exitCode = packet.assessment.disposition === 'REFUSE' ? 1 : 0
} catch (error) {
  // Child-process errors may contain raw HTTP/CLI output. Do not print that output or inherited credentials.
  console.error(`Maintenance preparation failed closed: ${error?.cmd || error?.code !== undefined ? 'GitHub read or output write failed.' : String(error?.message ?? error).slice(0, 400)}`)
  process.exitCode = 2
}
