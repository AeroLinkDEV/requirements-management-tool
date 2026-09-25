// #1156: a merge-queue candidate that carries a protected diff is judged only once it heads the queue.
// Behind another entry it would be refused (a maintenance review is prepared only for position 1, and an
// ordinary candidate composed on a pending maintenance entry carries that entry's protected diff), so the
// binding job waits here instead. Every outcome other than "wait" hands control back to the unchanged verifier.

export const QUEUE_BRANCH_PATTERN = /^gh-readonly-queue\/main\/pr-([1-9][0-9]*)-[0-9a-f]{40}$/
const sha = value => typeof value === 'string' && /^[0-9a-f]{40}$/.test(value)

/**
 * 'wait'       the candidate is current, not first, and differs from main in a protected path;
 * 'superseded' the queue now holds a different candidate for this PR: this run's candidate is discarded;
 * 'evaluate'   anything else, including a PR no longer queued and every malformed input, so the verifier
 *              decides as it always has.
 */
export function queueWaitDecision({ entry, candidateSha, protectedDiff } = {}) {
  if (!sha(candidateSha) || !Array.isArray(protectedDiff)) return 'evaluate'
  if (!entry || typeof entry !== 'object') return 'evaluate'
  const current = entry.headCommit?.oid
  if (sha(current) && current !== candidateSha) return 'superseded'
  if (current !== candidateSha) return 'evaluate'
  if (!Number.isSafeInteger(entry.position) || entry.position <= 1) return 'evaluate'
  return protectedDiff.length > 0 ? 'wait' : 'evaluate'
}

/** Poll until the candidate may be judged. The budget bounds the wait; exhausting it falls back to 'evaluate'. */
export async function awaitQueueHead({ readEntry, readProtectedDiff, candidateSha, budgetMs, intervalMs,
  now = () => Date.now(), sleep = ms => new Promise(resolve => setTimeout(resolve, ms)), log = () => {} } = {}) {
  const deadline = now() + budgetMs
  let protectedDiff
  try {
    protectedDiff = await readProtectedDiff()
  } catch {
    return { outcome: 'evaluate', reason: 'protected-diff-unreadable' }
  }
  for (;;) {
    let entry
    try {
      entry = await readEntry()
    } catch {
      return { outcome: 'evaluate', reason: 'queue-unreadable' }
    }
    const decision = queueWaitDecision({ entry, candidateSha, protectedDiff })
    if (decision !== 'wait') return { outcome: decision, reason: `position ${entry?.position ?? 'none'}` }
    if (now() + intervalMs > deadline) return { outcome: 'evaluate', reason: 'wait-budget-exhausted' }
    log(`waiting at position ${entry.position} for the entries ahead to merge`)
    await sleep(intervalMs)
  }
}
