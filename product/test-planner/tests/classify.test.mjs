import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { classify, explain, localPlan, ciSelection, AREA_PATTERNS, BROAD_EVENTS } from '../lib/classify.mjs'

const repoRoot = fileURLToPath(new URL('../../../', import.meta.url))

const of = (paths, event = 'pull_request') => classify(paths, { event })

test('broad events classify every area without a diff', () => {
  // A merge-group event carries no base to diff against, and it is the last gate before main. The same
  // applies to push, schedule and dispatch, which have no pull-request base either.
  for (const event of BROAD_EVENTS) {
    const result = of(['README.md'], event)
    assert.equal(result.docsOnly, false, event)
    for (const area of ['backend', 'client', 'browser', 'postgresql']) {
      assert.equal(result[area], true, `${event} must select ${area}`)
    }
  }
})

test('documentation-only changes select nothing', () => {
  const result = of([
    'README.md',
    'product/docs/OPERATIONS.md',
    'docs/whatever.txt',
    'design/mockup.png',
    'showcase/demo.gif',
  ])
  assert.equal(result.docsOnly, true)
  assert.equal(result.backend, false)
  assert.equal(result.client, false)
  assert.equal(result.browser, false)
  assert.equal(result.postgresql, false)
  assert.deepEqual(localPlan(result).map((s) => s.label), ['Nothing'])
})

test('a workflow change selects every area, including backend and client', () => {
  // The regression this exists for: ci.yml keyed browser and postgresql but not backend and client, so
  // a change to how the backend tests run did not run the backend tests. It was only caught because an
  // unrelated merge exposed it.
  const result = of(['.github/workflows/ci.yml'])
  assert.equal(result.backend, true, 'a workflow change must run the backend suites')
  assert.equal(result.client, true, 'a workflow change must validate the client')
  assert.equal(result.browser, true)
  assert.equal(result.postgresql, true)
  assert.equal(result.unclassified, false, 'the workflow is recognised, not a fallback')
})

test('backend, client and browser select on their own paths', () => {
  const backend = of(['product/src/AeroLink.Domain/ChangeControl/SystemChangeRequest.cs'])
  assert.equal(backend.backend, true)
  assert.equal(backend.client, false)
  assert.equal(backend.browser, true, 'a domain change can alter what a journey sees')

  const client = of(['product/client/src/App.tsx'])
  assert.equal(client.client, true)
  assert.equal(client.browser, true)
  assert.equal(client.backend, false)

  // Project files anywhere under product/ are backend, not only those under src/ or tests/.
  assert.equal(of(['product/Directory.Build.props']).backend, true)
  assert.equal(of(['product/AeroLink.slnx']).backend, true)
})

test('postgresql keys on persistence as well as migrations, case-insensitively', () => {
  // A change to an EF query needs the real provider even when no schema moves: translation is not
  // portable, and the SQLite path every other gate runs on will accept an expression Npgsql cannot
  // produce.
  assert.equal(of(['product/src/AeroLink.Infrastructure/Persistence/Migrations/0001_init.cs']).postgresql, true)
  assert.equal(of(['product/src/AeroLink.Api/AuthEndpoints.cs']).postgresql, true)
  assert.equal(of(['product/tests/AeroLink.Api.Tests/DatabaseBootstrapTests.cs']).postgresql, true)
  assert.equal(of(['product/src/AeroLink.Infrastructure/PERSISTENCE/Thing.cs']).postgresql, true, 'matching is case-insensitive')
  assert.equal(of(['product/src/AeroLink.Domain/ChangeControl/Rules.cs']).postgresql, false)
})

test('an unrecognised product path runs full backend and client rather than nothing', () => {
  // The failure this prevents: a change that was neither documentation nor recognised product code
  // selected nothing, every step skipped on its condition, and the job reported success having executed
  // no test at all. A launcher script, a root config file, or a new top-level directory all landed here.
  const result = of(['START_AEROLINK_PRODUCTION.bat'])
  assert.equal(result.docsOnly, false)
  assert.equal(result.backend, true)
  assert.equal(result.client, true)
  assert.equal(result.unclassified, true)
  assert.match(result.reason, /Unclassified/)

  // And the case observed in practice: ci-metrics and test-contracts tooling match no area rule.
  const tooling = of(['product/ci-metrics/lib/rolling.mjs'])
  assert.equal(tooling.unclassified, true)
  assert.equal(tooling.backend, true)
})

test('a documentation file alongside product code does not make the change docs-only', () => {
  const result = of(['README.md', 'product/client/src/App.tsx'])
  assert.equal(result.docsOnly, false)
  assert.equal(result.client, true)
})

test('explain attributes each path to the areas it selected', () => {
  const rows = explain(['.github/workflows/ci.yml', 'README.md', 'product/client/src/App.tsx'])
  assert.deepEqual(rows[0].areas, ['backend', 'client', 'browser', 'postgresql'])
  assert.equal(rows[1].product, false)
  assert.deepEqual(rows[2].areas, ['client', 'browser'])
})

test('the CI selection matches the areas chosen', () => {
  const jobs = ciSelection(of(['product/client/src/App.tsx']))
  assert.ok(jobs.includes('client'))
  assert.ok(jobs.some((job) => job.startsWith('browser-pr')))
  assert.ok(!jobs.includes('backend-core'))
  assert.ok(jobs.includes('gate'), 'the gate always runs')

  const docs = ciSelection(of(['README.md']))
  assert.ok(!docs.includes('backend-core'))
  assert.ok(docs.includes('gate'))
})

test('the local plan never claims a PostgreSQL-sensitive change was proven locally', () => {
  const plan = localPlan(of(['product/src/AeroLink.Infrastructure/Persistence/Thing.cs']))
  const postgres = plan.find((step) => /PostgreSQL/.test(step.label))
  assert.ok(postgres, 'a persistence change must mention PostgreSQL')
  assert.equal(postgres.command, null, 'there is no local command that constitutes evidence here')
  assert.match(postgres.why, /not evidence/)
})

test('the workflow delegates to this module rather than carrying its own copy', () => {
  // The point of #568 is that one definition exists. A contract test is the only thing standing between
  // that and someone reintroducing an inline copy that drifts — which is the state this replaced.
  const workflow = readFileSync(join(repoRoot, '.github/workflows/ci.yml'), 'utf8')
  assert.match(workflow, /test-planner[/\\]bin[/\\]classify-ci\.mjs/, 'the changes job must call the shared classifier')

  const classifyJob = workflow.slice(workflow.indexOf('  changes:'), workflow.indexOf('  backend-api:'))
  assert.doesNotMatch(classifyJob, /grep -Eq '\^product/, 'the inline path patterns must not come back')
  assert.doesNotMatch(classifyJob, /backend=true/, 'the inline classification must not come back')
})

test('every area pattern is anchored so a lookalike path cannot match', () => {
  // `docs/.github/workflows/ci.yml` is not the workflow, and `vendor/product/src/x.cs` is not ours.
  for (const [area, pattern] of Object.entries(AREA_PATTERNS)) {
    assert.equal(pattern.test('docs/.github/workflows/ci.yml'), false, `${area} matched a nested lookalike`)
    assert.equal(pattern.test('vendor/product/src/Thing.cs'), false, `${area} matched a vendored lookalike`)
  }
})
