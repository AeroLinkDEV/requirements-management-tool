// DEC-142 post-merge owner notice. Runs from protected main on every push to main.
//   --detect: ordinary token only; decides whether the pushed commit changed the approval machinery.
//   notify:   posts the owner notice and checks the live ruleset through the pinned evidence App reader.
import { readFileSync, appendFileSync } from 'node:fs'
import { createGitHubRequest } from '../lib/merge-authority-github.mjs'
import { createMaintenanceRulesetReader } from '../lib/maintenance-evidence-reader.mjs'
import { MAINTENANCE_REPOSITORY as repository } from '../lib/maintenance-preflight.mjs'
import { approvalMachineryChanges, machineryNoticeBody, mergedPullRequestNumber,
  rulesetFindings } from '../lib/machinery-notice.mjs'

const sha = value => typeof value === 'string' && /^[0-9a-f]{40}$/.test(value)
// The compare endpoint lists at most 300 files; a longer list cannot prove the machinery was untouched.
const COMPARE_FILE_LIMIT = 300
const TRUNCATED = '(changed-file list truncated: inspect the exact change)'

function pushIdentity() {
  const event = JSON.parse(readFileSync(process.env.GITHUB_EVENT_PATH, 'utf8'))
  if (event?.ref !== 'refs/heads/main' || !sha(event?.before) || !sha(event?.after) || /^0+$/.test(event.before)) {
    throw new Error('Not a push to an existing main.')
  }
  return { beforeSha: event.before, commitSha: event.after, message: event.head_commit?.message ?? '' }
}

async function changedMachinery(request, { beforeSha, commitSha }) {
  const compare = await request(`/repos/${repository}/compare/${beforeSha}...${commitSha}`)
  const files = Array.isArray(compare?.files) ? compare.files : null
  if (!files) throw new Error('The compare response has no file list.')
  const paths = approvalMachineryChanges(files)
  return files.length >= COMPARE_FILE_LIMIT ? [...paths, TRUNCATED] : paths
}

async function detect() {
  const request = createGitHubRequest({ token: process.env.GITHUB_TOKEN })
  const paths = await changedMachinery(request, pushIdentity())
  appendFileSync(process.env.GITHUB_OUTPUT, `machinery-changed=${paths.length > 0}\n`)
  console.log(`[machinery-notice] approval-machinery paths changed: ${paths.length}`)
}

async function notify() {
  const push = pushIdentity()
  const request = createGitHubRequest({ token: process.env.GITHUB_TOKEN })
  const paths = await changedMachinery(request, push)
  if (paths.length === 0) return
  let findings
  try {
    const readRuleset = createMaintenanceRulesetReader({
      token: process.env.MAINTENANCE_EVIDENCE_TOKEN,
      expectedAppId: Number(process.env.MAINTENANCE_EVIDENCE_APP_ID),
      expectedInstallationId: Number(process.env.MAINTENANCE_EVIDENCE_INSTALLATION_ID),
      expectedAppSlug: process.env.MAINTENANCE_EVIDENCE_APP_SLUG,
      actionAppSlug: process.env.MAINTENANCE_EVIDENCE_ACTION_APP_SLUG,
      actionInstallationId: Number(process.env.MAINTENANCE_EVIDENCE_ACTION_INSTALLATION_ID),
      apiUrl: process.env.GITHUB_API_URL || 'https://api.github.com',
    })
    findings = rulesetFindings((await readRuleset()).ruleset)
  } catch {
    // An unreadable ruleset is itself an alert; the notice is still sent.
    findings = ['ruleset-unreadable']
  }
  const associated = await request(`/repos/${repository}/commits/${push.commitSha}/pulls`)
  const prNumber = mergedPullRequestNumber({ associated, message: push.message })
  const runUrl = `https://github.com/${repository}/actions/runs/${process.env.GITHUB_RUN_ID}`
  const body = machineryNoticeBody({ ...push, prNumber, paths, findings, runUrl })
  if (prNumber) {
    await request(`/repos/${repository}/issues/${prNumber}/comments`, { method: 'POST', body: { body } })
  } else {
    await request(`/repos/${repository}/issues`, { method: 'POST',
      body: { title: `Approval-machinery change merged: ${push.commitSha.slice(0, 8)}`, body } })
  }
  console.log(`[machinery-notice] owner notified; ruleset findings: ${findings.join('; ') || 'none'}`)
  if (findings.length) process.exitCode = 1
}

const run = process.argv.includes('--detect') ? detect : notify
run().catch(() => {
  // Do not print API payloads, tokens, or candidate-controlled text.
  console.error('[machinery-notice] Failed closed: the owner notice could not be completed.')
  process.exitCode = 1
})
