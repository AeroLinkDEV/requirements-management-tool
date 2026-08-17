import { test } from 'node:test'
import assert from 'node:assert/strict'
import { detectOverlaps, normalizeFileList } from '../lib/overlap.mjs'
import { validateFileList } from '../tools/check-overlap.mjs'

const pr = (number, files) => ({
  number,
  title: `PR ${number}`,
  author: 'agent',
  branch: `b/${number}`,
  files,
})

test('GitHub removed file status is valid and participates in overlap analysis', () => {
  const removed = validateFileList([{ filename: 'Product/Deleted.cs', status: 'removed' }])
  assert.deepEqual(normalizeFileList(removed), ['product/deleted.cs'])

  const overlaps = detectOverlaps([
    pr(1, removed),
    pr(2, [{ filename: 'product/deleted.cs', status: 'modified' }]),
  ])

  assert.equal(overlaps.length, 1)
  assert.deepEqual(overlaps[0].sharedFiles, ['product/deleted.cs'])
})

test('unknown statuses remain fail-closed and rename evidence is still required', () => {
  assert.throws(() => validateFileList([{ filename: 'src/Unknown.cs', status: 'mystery' }]), /status is invalid/)
  assert.throws(() => validateFileList([{ filename: 'src/New.cs', status: 'renamed' }]), /rename source is missing/)
})
