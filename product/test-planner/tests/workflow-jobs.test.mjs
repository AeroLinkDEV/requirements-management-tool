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

function jobBody(jobId) {
  const lines = workflow.split(/\r?\n/)
  const start = lines.findIndex((line) => line === `  ${jobId}:`)
  assert.ok(start >= 0, `workflow must contain ${jobId}`)
  const end = lines.findIndex((line, index) => index > start && /^  [a-zA-Z0-9_-]+:\s*$/.test(line))
  return lines.slice(start, end >= 0 ? end : lines.length).join('\n')
}

test('scoped backend jobs build their graphs while retained owners build the full solution', () => {
  const api = jobBody('backend-api')
  const infrastructure = jobBody('backend-core-infrastructure')
  const domain = jobBody('backend-core-domain')
  const postgresql = jobBody('postgresql-smoke')

  for (const [body, project] of [
    [api, 'AeroLink.Api.Tests'],
    [infrastructure, 'AeroLink.Infrastructure.Tests'],
  ]) {
    assert.match(body, new RegExp(`dotnet restore product/tests/${project}/${project}\\.csproj`))
    assert.match(body, new RegExp(`dotnet build product/tests/${project}/${project}\\.csproj --configuration Release --no-restore`))
    assert.match(body, /--no-build/)
    assert.doesNotMatch(body, /dotnet (restore|build) product\/AeroLink\.slnx/)
    assert.doesNotMatch(body, /--no-dependencies/)
  }
  assert.match(domain, /runs-on: windows-latest/)
  assert.match(domain, /dotnet restore product\/AeroLink\.slnx/)
  assert.match(domain, /dotnet build product\/AeroLink\.slnx --configuration Release --no-restore/)
  assert.match(domain, /AeroLink\.Domain\.Tests\.csproj.*--no-build/)
  assert.match(postgresql, /runs-on: ubuntu-latest/)
  assert.match(postgresql, /dotnet restore product\/AeroLink\.slnx/)
  assert.match(postgresql, /dotnet build product\/AeroLink\.slnx --configuration Release --no-restore/)
})

test('every supported backend mode selects the scoped jobs with their whole-solution owner', () => {
  const backendJobs = ['backend-api', 'backend-core-domain', 'backend-core-infrastructure']
  const events = ['pull_request', 'merge_group', 'push', 'schedule', 'workflow_dispatch']
  for (const event of events) {
    const inputModes = event === 'workflow_dispatch'
      ? [
          { pull_request_number: '951', full_diagnostics: false },
          { pull_request_number: '', full_diagnostics: true },
          { pull_request_number: '', full_diagnostics: false },
        ]
      : [{}]
    for (const inputs of inputModes) {
      for (let flags = 0; flags < 32; flags += 1) {
        const classification = Object.fromEntries(
          ['docsOnly', 'backend', 'client', 'browser', 'postgresql']
            .map((key, bit) => [key, Boolean(flags & (1 << bit))]),
        )
        for (const postMergeSkip of [false, true]) {
          const selected = ids(selectJobs(workflow, classification, { event, inputs, postMergeSkip }).selected)
          const expected = classification.backend && !classification.docsOnly && !postMergeSkip
          for (const job of backendJobs) {
            assert.equal(selected.has(job), expected, `${event}/${JSON.stringify(inputs)}/${flags}/${postMergeSkip}: ${job}`)
          }
          if (expected) {
            assert.ok(selected.has('backend-core-domain'), `${event}/${JSON.stringify(inputs)}/${flags}: backend owner missing`)
          }
        }
      }
    }
  }
})

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
