export type SupersessionRecord = {
  id: string
  displayNumber: string
  state: string
  supersededByTestChangeRequestId?: string
}

/** A folded automatic predecessor has no controlled TCR number and remains an assessment record. */
export function isControlledTestChangeRequest(
  review: Pick<SupersessionRecord, 'displayNumber'>,
  controlledPrefix: string,
): boolean {
  return review.displayNumber.startsWith(controlledPrefix)
}

/**
 * A Superseded review is hidden as a peer active-work row only when its exact successor is already present in
 * the loaded release. Retargeting can put the successor in another release; hiding the predecessor there would
 * remove the old release's only historical record from the browser.
 */
export function reviewsVisibleInCurrentRelease<T extends SupersessionRecord>(reviews: readonly T[]): T[] {
  const loadedIds = new Set(reviews.map(review => review.id))
  return reviews.filter(review =>
    review.state !== 'Superseded'
    || !review.supersededByTestChangeRequestId
    || !loadedIds.has(review.supersededByTestChangeRequestId))
}

export type SuccessorReference = { id: string; displayNumber?: string }

/**
 * The exact superseding review remains navigable even when it belongs to another release and is not
 * present in the release-scoped list. The optional display number is presentation metadata; the ID is
 * the authoritative route.
 */
export function successorReferenceFor<T extends SupersessionRecord>(
  review: T,
  reviews: readonly T[],
): SuccessorReference | undefined {
  const id = review.supersededByTestChangeRequestId
  if (!id) return undefined
  return { id, displayNumber: reviews.find(candidate => candidate.id === id)?.displayNumber }
}

/**
 * Every exact predecessor that ultimately points to the supplied current review.
 *
 * Supersession is not necessarily a single line: manually raising a TCR can fold several pending automatic
 * assessments into one surviving package. Breadth-first traversal keeps every sibling and every earlier
 * ancestor, while deterministic ordering and cycle protection make corrupt history readable rather than
 * hanging the workspace.
 */
export function supersededHistoryFor<T extends SupersessionRecord>(
  current: T,
  reviews: readonly T[],
): T[] {
  const result: T[] = []
  const successorIds = [current.id]
  const seen = new Set<string>()

  while (successorIds.length > 0) {
    const successorId = successorIds.shift()!
    const direct = reviews
      .filter(review => review.state === 'Superseded'
        && review.supersededByTestChangeRequestId === successorId)
      .sort((left, right) => left.displayNumber.localeCompare(right.displayNumber)
        || left.id.localeCompare(right.id))

    for (const predecessor of direct) {
      if (seen.has(predecessor.id)) continue
      seen.add(predecessor.id)
      result.push(predecessor)
      successorIds.push(predecessor.id)
    }
  }

  return result
}
