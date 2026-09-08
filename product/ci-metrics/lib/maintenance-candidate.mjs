import { TRUSTED_SURFACE_PREFIXES } from './merge-authority.mjs'
import { MAINTENANCE_KERNEL_PATHS, MAINTENANCE_REPOSITORY } from './maintenance-preflight.mjs'

export const MAINTENANCE_REQUEST_LABEL = 'authority-maintenance-requested'
const kernelPath = path => MAINTENANCE_KERNEL_PATHS.includes(path) ||
  /^product\/ci-metrics\/(?:lib|bin)\/.*(?:maintenance|merge-authority)/.test(path) ||
  /^\.github\/workflows\/.*maintenance/.test(path)

export function shouldMintMaintenanceEvidence({ eventAction, trigger, run, pr, queue, main, ordinary, changedPaths } = {}) {
  const labels = Array.isArray(pr?.labels)
    ? pr.labels.map(label => typeof label === 'string' ? label : label?.name)
    : []
  const onlyProtectedRefusal = ordinary?.decision === 'REFUSE' &&
    Array.isArray(ordinary.reasons) && ordinary.reasons.length > 0 &&
    ordinary.reasons.every(reason => reason.startsWith('trusted-surface-modified:'))
  return eventAction === 'completed' && trigger?.event === 'merge_group' &&
    trigger?.status === 'completed' && run?.status === 'completed' &&
    String(run?.runId) === String(trigger?.id) && run?.event === 'merge_group' &&
    run?.runAttempt === trigger?.run_attempt && run?.headSha === trigger?.head_sha &&
    run?.repository === MAINTENANCE_REPOSITORY &&
    pr?.state === 'open' && pr?.draft === false && pr?.base?.ref === 'main' &&
    pr?.base?.repo?.full_name === MAINTENANCE_REPOSITORY &&
    pr?.head?.repo?.full_name === MAINTENANCE_REPOSITORY &&
    pr?.head?.sha === queue?.prHeadSha && labels.includes(MAINTENANCE_REQUEST_LABEL) &&
    queue?.prNumber === pr?.number && queue?.position === 1 &&
    ['AWAITING_CHECKS', 'MERGEABLE'].includes(queue?.state) &&
    queue?.headSha === run?.headSha && queue?.baseSha === main?.sha &&
    Array.isArray(changedPaths) &&
    changedPaths.some(path => typeof path === 'string' && TRUSTED_SURFACE_PREFIXES.some(prefix => path.startsWith(prefix))) &&
    !changedPaths.some(path => typeof path === 'string' && kernelPath(path)) && onlyProtectedRefusal
}
