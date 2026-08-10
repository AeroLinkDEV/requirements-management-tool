import { expect, test } from '@playwright/test'
import {
  isControlledTestChangeRequest,
  reviewsVisibleInCurrentRelease,
  successorReferenceFor,
  supersededHistoryFor,
  type SupersessionRecord,
} from '../src/testChangeReviewPresentation'

const review = (
  id: string,
  displayNumber: string,
  state: string,
  supersededByTestChangeRequestId?: string,
): SupersessionRecord => ({ id, displayNumber, state, supersededByTestChangeRequestId })

test('a cross-release superseded review remains visible when its successor is not loaded', () => {
  const predecessor = review('old', 'SYSTCR-000101.00', 'Superseded', 'successor-in-another-release')
  const currentRelease = [predecessor]

  expect(reviewsVisibleInCurrentRelease(currentRelease)).toEqual([predecessor])
  expect(successorReferenceFor(predecessor, currentRelease)).toEqual({
    id: 'successor-in-another-release',
  })
})

test('a same-release predecessor moves under the exact successor rather than remaining a peer row', () => {
  const predecessor = review('old', 'SYSTCR-000102.00', 'Superseded', 'new')
  const successor = review('new', 'SYSTCR-000102.01', 'Open')
  const currentRelease = [predecessor, successor]

  expect(reviewsVisibleInCurrentRelease(currentRelease)).toEqual([successor])
  expect(successorReferenceFor(predecessor, currentRelease)).toEqual({
    id: successor.id,
    displayNumber: successor.displayNumber,
  })
  expect(supersededHistoryFor(successor, currentRelease)).toEqual([predecessor])
})

test('folding several automatic assessments preserves every sibling and earlier ancestor', () => {
  const current = review('current', 'SYSTCR-000200.02', 'Open')
  const siblingA = review('a', 'SYSTCR-000200.00', 'Superseded', current.id)
  const siblingB = review('b', 'SYSTCR-000201.00', 'Superseded', current.id)
  const ancestor = review('ancestor', 'SYSTCR-000199.00', 'Superseded', siblingB.id)
  const records = [current, siblingB, ancestor, siblingA]

  expect(reviewsVisibleInCurrentRelease(records)).toEqual([current])
  expect(supersededHistoryFor(current, records)).toEqual([siblingA, siblingB, ancestor])
})

test('folded automatic predecessors remain assessments while numbered predecessors remain TCRs', () => {
  const automatic = review('automatic', 'SRCR-000199.00', 'Superseded', 'current')
  const controlled = review('controlled', 'SYSTCR-000200.00', 'Superseded', 'current')

  expect(isControlledTestChangeRequest(automatic, 'SYSTCR-')).toBe(false)
  expect(isControlledTestChangeRequest(controlled, 'SYSTCR-')).toBe(true)
})
