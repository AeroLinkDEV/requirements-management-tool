import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { approvalMachineryChanges, machineryNoticeBody, MACHINERY_NOTICE_MARKER, MACHINERY_NOTICE_OWNER,
  mergedPullRequestNumber, rulesetFindings } from '../lib/machinery-notice.mjs'
import { MAINTENANCE_RULESET_ID } from '../lib/maintenance-preflight.mjs'

const sha = character => character.repeat(40)
const repoFile = path => readFileSync(fileURLToPath(new URL(`../../../${path}`, import.meta.url)), 'utf8')

test('only approval-machinery paths trigger the owner notice, including either side of a rename', () => {
  assert.deepEqual(approvalMachineryChanges([
    { filename: 'product/ci-metrics/lib/merge-authority.mjs' },
    { filename: '.github/workflows/ci.yml' },
    { filename: 'product/test-planner/lib/classify.mjs' },
    { filename: 'product/client/src/App.tsx' },
    { filename: 'product/ci-metrics/lib/renamed.mjs', previous_filename: 'product/ci-metrics/lib/maintenance-runtime.mjs' },
  ]), ['product/ci-metrics/lib/maintenance-runtime.mjs', 'product/ci-metrics/lib/merge-authority.mjs'])
  assert.deepEqual(approvalMachineryChanges([{ filename: '.github/workflows/ci.yml' }]), [])
  assert.throws(() => approvalMachineryChanges(undefined))
})

test('the notice machinery is itself approval machinery', () => {
  for (const path of ['.github/workflows/approval-machinery-notice.yml', 'product/ci-metrics/lib/machinery-notice.mjs',
    'product/ci-metrics/bin/notify-machinery-merge.mjs']) {
    assert.deepEqual(approvalMachineryChanges([{ filename: path }]), [path], path)
  }
})

test('the ruleset check alerts on any bypass actor or an unverifiable ruleset', () => {
  const ruleset = { id: MAINTENANCE_RULESET_ID, enforcement: 'active', bypass_actors: [] }
  assert.deepEqual(rulesetFindings(ruleset), [])
  assert.deepEqual(rulesetFindings({ ...ruleset, bypass_actors: [{ actor_id: 5 }] }), ['bypass-actors-present: 1'])
  assert.deepEqual(rulesetFindings({ ...ruleset, bypass_actors: undefined }), ['bypass-actors-unreadable'])
  assert.deepEqual(rulesetFindings({ ...ruleset, enforcement: 'disabled' }), ['ruleset-not-active'])
  assert.deepEqual(rulesetFindings({ ...ruleset, id: 1 }), ['ruleset-identity-unverified'])
  assert.deepEqual(rulesetFindings(null), ['ruleset-identity-unverified'])
})

test('the merged PR comes from GitHub association first, then the squash title suffix', () => {
  const merged = { number: 1145, merged_at: '2026-09-25T00:00:00Z', base: { ref: 'main' } }
  assert.equal(mergedPullRequestNumber({ associated: [merged], message: 'Title (#9)' }), 1145)
  assert.equal(mergedPullRequestNumber({ associated: [], message: 'Install DEC-142 (#1145)\n\nbody (#3)' }), 1145)
  assert.equal(mergedPullRequestNumber({ associated: [{ ...merged, merged_at: null }], message: 'No number' }), null)
})

test('the notice names the owner, the exact change and any ruleset alert, with inert paths', () => {
  const input = { commitSha: sha('c'), beforeSha: sha('b'), prNumber: 1145, runUrl: 'https://example.invalid/run',
    paths: ['product/ci-metrics/lib/`evil`<b>.mjs'], findings: [] }
  const body = machineryNoticeBody(input)
  assert.ok(body.startsWith(MACHINERY_NOTICE_MARKER))
  assert.match(body, new RegExp(`@${MACHINERY_NOTICE_OWNER}`))
  assert.match(body, /#1145/)
  assert.match(body, new RegExp(`compare/${sha('b')}\\.\\.\\.${sha('c')}`))
  assert.match(body, /no bypass actors/)
  assert.doesNotMatch(body, /`evil`|<b>/)
  assert.match(machineryNoticeBody({ ...input, findings: ['bypass-actors-present: 1'] }), /ALERT/)
  assert.throws(() => machineryNoticeBody({ ...input, paths: [] }))
  assert.throws(() => machineryNoticeBody({ ...input, commitSha: 'abc' }))
})

test('the workflow runs from protected main after the merge and mints the evidence token only for machinery', () => {
  const workflow = repoFile('.github/workflows/approval-machinery-notice.yml')
  assert.match(workflow, /on:\n  push:\n    branches: \[main\]/)
  assert.doesNotMatch(workflow, /pull_request|workflow_dispatch|merge_group/)
  assert.match(workflow, /if: needs\.detect\.outputs\.machinery-changed == 'true'/)
  const detectJob = workflow.slice(workflow.indexOf('  detect:'), workflow.indexOf('  notify:'))
  assert.doesNotMatch(detectJob, /MAINTENANCE_EVIDENCE|environment:/, 'detection never sees the privileged key')
  const notifyJob = workflow.slice(workflow.indexOf('  notify:'))
  assert.match(notifyJob, /environment:\n      name: merge-authority/)
  for (const uses of workflow.match(/uses: \S+/g)) assert.match(uses, /@[0-9a-f]{40}$/, uses)
})
