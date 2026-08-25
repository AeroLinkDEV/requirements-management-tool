import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { selectJobs } from '../lib/workflow-jobs.mjs'

const repoRoot = fileURLToPath(new URL('../../../', import.meta.url))
const workflow = readFileSync(join(repoRoot, '.github/workflows/ci.yml'), 'utf8')
const fullClassification = {
  docsOnly: false,
  backend: true,
  client: true,
  browser: true,
  postgresql: true,
}

function ids(rows) {
  return new Set(rows.map((row) => row.id))
}

test('changed-area planning defaults provenance to the conservative full-test posture', () => {
  const plan = selectJobs(workflow, fullClassification, { event: 'pull_request' })
  const selected = ids(plan.selected)
  for (const job of ['backend-api', 'backend-core-domain', 'backend-core-infrastructure', 'client', 'script-contracts', 'postgresql-smoke']) {
    assert.ok(selected.has(job), `${job} remains selected when no trusted provenance decision is supplied`)
  }
})

test('an explicit provenanced main-push model skips exactly the redundant product retest jobs', () => {
  const ordinary = selectJobs(workflow, fullClassification, { event: 'push', postMergeSkip: false })
  const provenanced = selectJobs(workflow, fullClassification, { event: 'push', postMergeSkip: true })
  const ordinarySelected = ids(ordinary.selected)
  const provenancedSelected = ids(provenanced.selected)
  const provenancedSkipped = ids(provenanced.skipped)

  for (const job of ['backend-api', 'backend-core-domain', 'backend-core-infrastructure', 'client', 'script-contracts', 'postgresql-smoke']) {
    assert.ok(ordinarySelected.has(job), `${job} normally runs on main`)
    assert.ok(provenancedSkipped.has(job), `${job} skips only after an explicit trusted decision`)
    assert.ok(!provenancedSelected.has(job), `${job} is not simultaneously selected`)
  }
  assert.ok(provenancedSelected.has('warm-chromium-cache'), 'cache warming remains selected')
})


test('workflow-dispatch readiness inputs preserve PR browser selection while diagnostics stay separate', () => {
  const ready = selectJobs(workflow, fullClassification, {
    event: 'workflow_dispatch',
    inputs: { pull_request_number: '652', full_diagnostics: false },
  })
  const readySelected = ids(ready.selected)
  const readySkipped = ids(ready.skipped)
  assert.ok(readySelected.has('browser-pr'), 'ready Full dispatch runs the ordinary PR browser shards')
  assert.ok(readySkipped.has('browser-full'), 'ready Full dispatch does not run the diagnostic browser matrix')

  const diagnostic = selectJobs(workflow, fullClassification, {
    event: 'workflow_dispatch',
    inputs: { pull_request_number: '', full_diagnostics: true },
  })
  const diagnosticSelected = ids(diagnostic.selected)
  const diagnosticSkipped = ids(diagnostic.skipped)
  assert.ok(diagnosticSelected.has('browser-full'), 'manual diagnostics retain the full browser matrix')
  assert.ok(diagnosticSkipped.has('browser-pr'), 'manual diagnostics do not impersonate a pull request')
})
