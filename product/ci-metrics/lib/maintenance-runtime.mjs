import { execFileSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import { MAINTENANCE_REPOSITORY as repository } from './maintenance-preflight.mjs'

/** The workflow checks out its protected triggering SHA explicitly; never check out candidate code here. */
export function trustedMaintenanceContext(event, env = process.env) {
  const trigger = event?.workflow_run
  const match = /^gh-readonly-queue\/main\/pr-([1-9][0-9]*)-[0-9a-f]{40}$/.exec(trigger?.head_branch ?? '')
  if (env.GITHUB_REPOSITORY !== repository || env.GITHUB_REF !== 'refs/heads/main' ||
      env.GITHUB_WORKFLOW_REF !== `${repository}/.github/workflows/merge-queue-binding.yml@refs/heads/main` ||
      event?.action !== 'completed' || trigger?.event !== 'merge_group' || trigger?.status !== 'completed' ||
      !match || !/^[0-9a-f]{40}$/.test(trigger?.head_sha ?? '') ||
      !Number.isSafeInteger(trigger?.id) || trigger.id < 1 || !Number.isSafeInteger(trigger?.run_attempt) || trigger.run_attempt < 1 ||
      !/^[1-9][0-9]*$/.test(env.GITHUB_RUN_ID ?? '') || env.GITHUB_RUN_ATTEMPT !== '1') {
    throw new Error('Maintenance must execute in a first-attempt protected-main binding workflow for a completed queue run.')
  }
  const root = fileURLToPath(new URL('../../../', import.meta.url))
  const git = args => execFileSync('git', ['-C', root, ...args], { encoding: 'utf8', windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] }).trim()
  const preparer = { commitSha: git(['rev-parse', 'HEAD']), treeSha: git(['rev-parse', 'HEAD^{tree}']) }
  if (git(['status', '--porcelain']) || preparer.commitSha !== env.GITHUB_SHA) {
    throw new Error('Maintenance checkout must be clean and equal the protected workflow SHA.')
  }
  return { preparer, prNumber: Number(match[1]), runId: trigger.id,
    bindingRunId: Number(env.GITHUB_RUN_ID), bindingRunAttempt: Number(env.GITHUB_RUN_ATTEMPT),
    expectedProduct: { headSha: trigger.head_sha, runAttempt: trigger.run_attempt } }
}
