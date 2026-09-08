import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { MAINTENANCE_REPOSITORY, MAINTENANCE_KERNEL_PATHS } from '../lib/maintenance-preflight.mjs'
import { MAINTENANCE_REQUEST_LABEL, shouldMintMaintenanceEvidence } from '../lib/maintenance-candidate.mjs'

const sha = character => character.repeat(40)

function fixture(overrides = {}) {
  const base = {
    eventAction: 'completed',
    trigger: { event: 'merge_group', status: 'completed', id: 42, run_attempt: 2, head_sha: sha('c') },
    run: { repository: MAINTENANCE_REPOSITORY, status: 'completed', event: 'merge_group', runId: 42, runAttempt: 2, headSha: sha('c') },
    pr: {
      number: 946, state: 'open', draft: false,
      base: { ref: 'main', repo: { full_name: MAINTENANCE_REPOSITORY } },
      head: { sha: sha('b'), repo: { full_name: MAINTENANCE_REPOSITORY } },
      labels: [{ name: MAINTENANCE_REQUEST_LABEL }],
    },
    queue: { prNumber: 946, prHeadSha: sha('b'), position: 1, state: 'MERGEABLE', headSha: sha('c'), baseSha: sha('a') },
    main: { name: 'main', sha: sha('a') },
    ordinary: { decision: 'REFUSE', reasons: ['trusted-surface-modified: .github/'] },
    changedPaths: ['.github/workflows/ci.yml'],
  }
  return { ...base, ...overrides }
}

test('mint eligibility requires the complete protected candidate and current sole queue entry', () => {
  assert.equal(shouldMintMaintenanceEvidence(fixture()), true)
  for (const [name, overrides] of [
    ['stale queue entry', { queue: { ...fixture().queue, headSha: sha('d') } }],
    ['missing queue entry', { queue: null }],
    ['fork head', { pr: { ...fixture().pr, head: { sha: sha('b'), repo: { full_name: 'someone/fork' } } } }],
    ['changed PR head', { pr: { ...fixture().pr, head: { sha: sha('d'), repo: { full_name: MAINTENANCE_REPOSITORY } } } }],
    ['non-main base', { pr: { ...fixture().pr, base: { ref: 'release', repo: { full_name: MAINTENANCE_REPOSITORY } } } }],
    ['queue not first', { queue: { ...fixture().queue, position: 2 } }],
    ['ordinary refusal has non-protected reason', { ordinary: { decision: 'REFUSE', reasons: ['job-failed'] } }],
    ['run is not completed', { run: { ...fixture().run, status: 'in_progress' } }],
  ]) {
    assert.equal(shouldMintMaintenanceEvidence(fixture(overrides)), false, name)
  }
})

test('kernel changes never mint the privileged token even when the broad surface changed', () => {
  for (const path of [
    ...MAINTENANCE_KERNEL_PATHS,
    '.github/workflows/merge-queue-binding.yml',
    'product/ci-metrics/lib/maintenance-approval.mjs',
  ]) {
    assert.equal(shouldMintMaintenanceEvidence(fixture({ changedPaths: [path] })), false, path)
  }
})

test('candidate requires an explicit opt-in label and protected change', () => {
  assert.equal(shouldMintMaintenanceEvidence(fixture({ pr: { ...fixture().pr, labels: [] } })), false)
  assert.equal(shouldMintMaintenanceEvidence(fixture({ changedPaths: [] })), false)
  assert.equal(shouldMintMaintenanceEvidence(fixture({ changedPaths: ['src/ordinary.cs'] })), false)
})

test('the executable detector uses exact protected paths and current GraphQL queue linkage', () => {
  const source = readFileSync(fileURLToPath(new URL('../bin/detect-maintenance-candidate.mjs', import.meta.url)), 'utf8')
  assert.match(source, /compareTrustedSurfacePaths/)
  assert.match(source, /mergeQueueEntry/)
  assert.match(source, /shouldMintMaintenanceEvidence/)
  assert.doesNotMatch(source, /compareTrustedSurfaces\(/)
})
