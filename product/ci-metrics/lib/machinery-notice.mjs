// DEC-142 post-merge owner notice. Pure functions: the protected push-to-main workflow supplies every input.
import { isApprovalMachineryPath, MAINTENANCE_REPOSITORY, MAINTENANCE_RULESET_ID } from './maintenance-preflight.mjs'

export const MACHINERY_NOTICE_OWNER = 'seanmccarthyns'
export const MACHINERY_NOTICE_MARKER = '<!-- AEROLINK_APPROVAL_MACHINERY_NOTICE -->'
const sha = value => typeof value === 'string' && /^[0-9a-f]{40}$/.test(value)

/** The approval-machinery paths a pushed commit changed, sorted and de-duplicated. */
export function approvalMachineryChanges(files) {
  if (!Array.isArray(files)) throw new Error('The changed-file list is unavailable.')
  const paths = []
  for (const file of files) {
    // A rename changes both the old and the new path; either side can be machinery.
    for (const path of [file?.filename, file?.previous_filename]) {
      if (isApprovalMachineryPath(path)) paths.push(path)
    }
  }
  return [...new Set(paths)].sort()
}

/** Every reason the live `main` ruleset no longer has the shape DEC-142 relies on. Empty means verified. */
export function rulesetFindings(ruleset) {
  const findings = []
  if (!ruleset || typeof ruleset !== 'object' || ruleset.id !== MAINTENANCE_RULESET_ID) return ['ruleset-identity-unverified']
  if (ruleset.enforcement !== 'active') findings.push('ruleset-not-active')
  if (!Array.isArray(ruleset.bypass_actors)) findings.push('bypass-actors-unreadable')
  else if (ruleset.bypass_actors.length !== 0) findings.push(`bypass-actors-present: ${ruleset.bypass_actors.length}`)
  return findings
}

/** The PR a squash-merged commit came from: GitHub's association first, then the `(#N)` title suffix. */
export function mergedPullRequestNumber({ associated, message } = {}) {
  const merged = Array.isArray(associated) ? associated.filter(pr => pr?.merged_at && pr?.base?.ref === 'main') : []
  if (merged.length === 1 && Number.isSafeInteger(merged[0].number)) return merged[0].number
  const match = /\(#([1-9][0-9]*)\)\s*$/.exec(String(message ?? '').split('\n')[0])
  return match ? Number(match[1]) : null
}

export function machineryNoticeBody({ commitSha, beforeSha, prNumber, paths, findings, runUrl } = {}) {
  if (!sha(commitSha) || !sha(beforeSha) || !Array.isArray(paths) || paths.length === 0 || !Array.isArray(findings)) {
    throw new Error('Notice inputs are incomplete.')
  }
  // JSON-escaped paths keep candidate filenames out of Markdown structure, as the review summary does.
  const listed = JSON.stringify(paths, null, 2).replace(/[`<>&]/g, character =>
    `\\u${character.charCodeAt(0).toString(16).padStart(4, '0')}`)
  const ruleset = findings.length === 0
    ? `Ruleset ${MAINTENANCE_RULESET_ID} verified: active, no bypass actors.`
    : `**ALERT: ruleset ${MAINTENANCE_RULESET_ID} check failed:** ${findings.join('; ')}. Remove any bypass actor and check what merged.`
  return [
    MACHINERY_NOTICE_MARKER,
    `@${MACHINERY_NOTICE_OWNER} an approval-machinery change merged to \`main\` under DEC-142. No action is needed unless you want to reverse it.`,
    '',
    `- Pull request: ${prNumber ? `#${prNumber}` : 'not identified'}`,
    `- Commit: ${commitSha}`,
    `- [Exact change](https://github.com/${MAINTENANCE_REPOSITORY}/compare/${beforeSha}...${commitSha})`,
    `- ${ruleset}`,
    `- [Notice run](${runUrl})`,
    '',
    'Approval-machinery paths changed:',
    '```json', listed, '```',
  ].join('\n')
}
