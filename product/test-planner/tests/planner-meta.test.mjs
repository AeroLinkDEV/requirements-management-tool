import { test } from 'node:test'
import assert from 'node:assert/strict'
import { copyFileSync, mkdirSync, mkdtempSync, rmSync, appendFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { PLANNER_FILES, PLANNER_VERSION, plannerHash } from '../lib/planner-meta.mjs'

const repoRoot = fileURLToPath(new URL('../../../', import.meta.url))

test('planner hash is stable and covers its own metadata contract', () => {
  assert.match(PLANNER_VERSION, /^aerolink-test-planner\/v\d+$/)
  assert.ok(PLANNER_FILES.includes('product/test-planner/lib/planner-meta.mjs'))

  const first = plannerHash(repoRoot)
  assert.equal(first, plannerHash(repoRoot))
  assert.match(first, /^[0-9a-f]{64}$/)

  const temporaryRoot = mkdtempSync(join(tmpdir(), 'aerolink-planner-hash-'))
  try {
    for (const relativePath of PLANNER_FILES) {
      const destination = join(temporaryRoot, relativePath)
      mkdirSync(join(destination, '..'), { recursive: true })
      copyFileSync(join(repoRoot, relativePath), destination)
    }
    const beforeMetadataChange = plannerHash(temporaryRoot)
    appendFileSync(join(temporaryRoot, 'product/test-planner/lib/planner-meta.mjs'), '\n// deterministic hash fixture change\n')
    assert.notEqual(plannerHash(temporaryRoot), beforeMetadataChange, 'metadata changes must change the planner hash')
  } finally {
    rmSync(temporaryRoot, { recursive: true, force: true })
  }
})
