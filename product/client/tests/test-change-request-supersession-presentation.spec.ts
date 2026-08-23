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
  const predecessor = review('old', 'SYSTPCR-000101.00', 'Superseded', 'successor-in-another-release')
  const currentRelease = [predecessor]

  expect(reviewsVisibleInCurrentRelease(currentRelease)).toEqual([predecessor])
  expect(successorReferenceFor(predecessor, currentRelease)).toEqual({
    id: 'successor-in-another-release',
  })
})

test('a same-release predecessor moves under the exact successor rather than remaining a peer row', () => {
  const predecessor = review('old', 'SYSTPCR-000102.00', 'Superseded', 'new')
  const successor = review('new', 'SYSTPCR-000102.01', 'Draft')
  const currentRelease = [predecessor, successor]

  expect(reviewsVisibleInCurrentRelease(currentRelease)).toEqual([successor])
  expect(successorReferenceFor(predecessor, currentRelease)).toEqual({
    id: successor.id,
    displayNumber: successor.displayNumber,
  })
  expect(supersededHistoryFor(successor, currentRelease)).toEqual([predecessor])
})

test('folding several automatic assessments preserves every sibling and earlier ancestor', () => {
  const current = review('current', 'SYSTPCR-000200.02', 'Draft')
  const siblingA = review('a', 'SYSTPCR-000200.00', 'Superseded', current.id)
  const siblingB = review('b', 'SYSTPCR-000201.00', 'Superseded', current.id)
  const ancestor = review('ancestor', 'SYSTPCR-000199.00', 'Superseded', siblingB.id)
  const records = [current, siblingB, ancestor, siblingA]

  expect(reviewsVisibleInCurrentRelease(records)).toEqual([current])
  expect(supersededHistoryFor(current, records)).toEqual([siblingA, siblingB, ancestor])
})

test('folded automatic predecessors remain assessments while numbered predecessors remain TCRs', () => {
  const automatic = review('automatic', 'SRCR-000199.00', 'Superseded', 'current')
  const controlled = review('controlled', 'SYSTPCR-000200.00', 'Superseded', 'current')

  expect(isControlledTestChangeRequest(automatic, 'SYSTPCR-')).toBe(false)
  expect(isControlledTestChangeRequest(controlled, 'SYSTPCR-')).toBe(true)
})
