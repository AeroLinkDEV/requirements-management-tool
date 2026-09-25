// Which Product runs belong to a merge-queue candidate the queue has discarded (#1147, #1152 A2).
//
// GitHub deletes a `gh-readonly-queue/main/...` ref when its entry leaves the queue or the queue recomposes
// around a failure. The run testing that ref is not cancelled: its concurrency group is the unique queue ref,
// so nothing supersedes it, and on 2026-09-25 such runs kept four Windows jobs busy for 15 to 46 minutes after
// their candidate was gone. Nothing reads their result: the queue has already dropped the candidate, and the
// binder refuses a run the queue no longer holds.
//
// The decision is deliberately narrow, because cancelling a live candidate would stall the queue:
// - only runs of the Product workflow, from a merge_group event, on exactly the deleted ref;
// - only runs that are not already complete;
// - only runs created before this decision's own trigger, so a candidate the queue creates after the
//   deletion is never touched;
// - and if the ref exists again (GitHub can recreate a candidate under the same name), only runs whose
//   commit is not the ref's current commit.

export const PRODUCT_WORKFLOW_PATH = '.github/workflows/ci.yml'
export const QUEUE_REF_PREFIX = 'gh-readonly-queue/main/'

const SHA = /^[0-9a-f]{40}$/

export function isQueueRef(ref) {
  return typeof ref === 'string' && ref.startsWith(QUEUE_REF_PREFIX) && ref.length > QUEUE_REF_PREFIX.length && !/[\s\r\n]/.test(ref)
}

/**
 * @param {object} input
 * @param {string} input.deletedRef the deleted branch name, without `refs/heads/`
 * @param {Array<object>} input.runs workflow runs from the GitHub API
 * @param {string|null} input.currentRefSha the ref's commit if it exists again, otherwise null
 * @param {string|number} input.triggeredAt when the deletion was observed; later runs are never cancelled
 * @returns {{ cancel: Array<object>, keep: Array<{ id: number, reason: string }> }}
 */
export function selectSupersededRuns({ deletedRef, runs = [], currentRefSha = null, triggeredAt }) {
  if (!isQueueRef(deletedRef)) throw new Error(`Refusing to act on a ref outside ${QUEUE_REF_PREFIX}: ${String(deletedRef).slice(0, 120)}`)
  if (currentRefSha !== null && !SHA.test(String(currentRefSha))) throw new Error('The current ref commit is malformed.')
  const cutoff = typeof triggeredAt === 'number' ? triggeredAt : Date.parse(triggeredAt)
  if (!Number.isFinite(cutoff)) throw new Error('No trigger time was supplied, so newer candidates could not be protected.')
  if (!Array.isArray(runs)) throw new Error('Runs must be an array.')

  const cancel = []
  const keep = []
  for (const run of runs) {
    if (!run || !Number.isInteger(run.id)) continue
    const reason =
      run.path !== PRODUCT_WORKFLOW_PATH ? 'not the Product workflow'
        : run.event !== 'merge_group' ? `event ${run.event} is not merge_group`
          : run.head_branch !== deletedRef ? 'a different ref'
            : run.status === 'completed' ? 'already complete'
              : !SHA.test(String(run.head_sha)) ? 'no usable commit'
                : !(Date.parse(run.created_at) <= cutoff) ? 'created after the deletion was observed'
                  : currentRefSha !== null && run.head_sha === currentRefSha ? 'the ref exists again at this commit'
                    : null
    if (reason === null) cancel.push(run)
    else keep.push({ id: run.id, reason })
  }
  return { cancel, keep }
}
