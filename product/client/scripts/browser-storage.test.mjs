import test from 'node:test'
import assert from 'node:assert/strict'
import { randomUUID } from 'node:crypto'
import { existsSync, readFileSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { browserStoragePath, createBrowserStorage, removeBrowserStorage } from './browser-storage.mjs'

test('evidence is owned across restarts, independent of ambient storage, and removed with its database', () => {
  const runId = `storage-${randomUUID()}`
  const otherId = `storage-${randomUUID()}`
  const previous = process.env.Evidence__Root
  const other = createBrowserStorage(otherId)
  process.env.Evidence__Root = other.evidence
  try {
    const state = createBrowserStorage(runId)
    assert.notEqual(state.evidence, other.evidence)
    writeFileSync(join(state.evidence, 'generated.docx'), 'test document')
    writeFileSync(state.database, 'test database')
    assert.equal(createBrowserStorage(runId).evidence, state.evidence)
    assert.equal(readFileSync(join(state.evidence, 'generated.docx'), 'utf8'), 'test document')
    removeBrowserStorage(runId)
    assert.equal(existsSync(state.root), false)
    assert.equal(existsSync(other.root), true)
    removeBrowserStorage(runId)
    assert.ok(!browserStoragePath('../../persistent').includes('..'))
  } finally {
    removeBrowserStorage(runId); removeBrowserStorage(otherId)
    if (previous === undefined) delete process.env.Evidence__Root
    else process.env.Evidence__Root = previous
  }
})

test('cleanup refuses an altered owner marker', () => {
  const runId = randomUUID(); const state = createBrowserStorage(runId)
  try {
    writeFileSync(join(state.root, '.owner'), 'someone else')
    assert.throws(() => removeBrowserStorage(runId), /owner marker/)
    assert.equal(existsSync(state.root), true)
  } finally {
    writeFileSync(join(state.root, '.owner'), runId); removeBrowserStorage(runId)
  }
})
