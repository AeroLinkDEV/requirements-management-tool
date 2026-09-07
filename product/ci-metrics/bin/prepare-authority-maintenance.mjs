// A read-only operator command. It cannot publish App checks, approve deployments or change settings.
import { execFile } from 'node:child_process'
import { promisify } from 'node:util'
import { mkdirSync, writeFileSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { collectMaintenancePreflight } from '../lib/maintenance-preflight-github.mjs'

const [prArg, runArg, outputArg, ...extra] = process.argv.slice(2)
if (!/^\d+$/.test(prArg ?? '') || !/^\d+$/.test(runArg ?? '') || !outputArg || extra.length) {
  console.error('Usage: node product/ci-metrics/bin/prepare-authority-maintenance.mjs <pr-number> <product-run-id> <new-output.json>')
  process.exit(2)
}
const execute = promisify(execFile)
const api = async args => JSON.parse((await execute('gh', ['api', ...args], { encoding: 'utf8', maxBuffer: 32 * 1024 * 1024, windowsHide: true })).stdout)
const root = fileURLToPath(new URL('../../../', import.meta.url))
const git = async args => (await execute('git', ['-C', root, ...args], { encoding: 'utf8', windowsHide: true })).stdout.trim()
async function preparerIdentity() {
  if (await git(['status', '--porcelain'])) throw new Error('The reviewed preparer checkout must be clean.')
  return { commitSha: await git(['rev-parse', 'HEAD']), treeSha: await git(['rev-parse', 'HEAD^{tree}']) }
}
try {
  const output = resolve(outputArg)
  if (/(?:^|[\\/])product[\\/]\.local(?:[\\/]|$)/i.test(output)) throw new Error('Output must stay outside product/.local.')
  const preparer = await preparerIdentity()
  const packet = await collectMaintenancePreflight({
    preparer,
    prNumber: Number(prArg), runId: Number(runArg),
    read: path => api([path, '--method', 'GET']),
    graphql: query => api(['graphql', '-f', `query=${query}`]),
  })
  if (JSON.stringify(await preparerIdentity()) !== JSON.stringify(preparer)) throw new Error('The preparer changed during evidence collection.')
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
