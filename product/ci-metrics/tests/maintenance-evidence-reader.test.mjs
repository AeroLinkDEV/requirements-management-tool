import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { MAINTENANCE_REPOSITORY, MAINTENANCE_RULESET_ID, evaluateMaintenancePreflight } from '../lib/maintenance-preflight.mjs'
import { createMaintenanceRulesetReader, MAINTENANCE_RULESET_PATH, routeMaintenanceRead } from '../lib/maintenance-evidence-reader.mjs'

const appId = 4876850
const installationId = 160142588
const appSlug = 'aerolink-maintenance-evidence'
const ruleset = { id: MAINTENANCE_RULESET_ID, enforcement: 'active', target: 'branch', bypass_actors: [], rules: [] }
const responses = {
  '/installation/repositories?per_page=100': { total_count: 1, repositories: [{ full_name: MAINTENANCE_REPOSITORY }] },
  [MAINTENANCE_RULESET_PATH]: ruleset,
}

function fetchFixture({ mutate = () => {}, responseOverride } = {}) {
  const calls = []
  const fetchImpl = async (url, options) => {
    calls.push({ url, options })
    const path = url.replace('https://api.github.com', '')
    const body = responseOverride?.(path) ?? responses[path]
    if (body === undefined) throw new Error('unexpected endpoint')
    const response = { ok: true, status: 200, redirected: false, json: async () => structuredClone(body) }
    mutate(response, path)
    return response
  }
  return { calls, fetchImpl }
}

test('privileged reader verifies the exact selected repository and then performs only the ruleset GET', async () => {
  const fixture = fetchFixture()
  const reader = createMaintenanceRulesetReader({ token: 'evidence-token', expectedAppId: appId,
    expectedInstallationId: installationId, expectedAppSlug: appSlug, actionAppSlug: appSlug,
    actionInstallationId: installationId, fetchImpl: fixture.fetchImpl })
  const result = await reader()
  assert.deepEqual(result.ruleset, ruleset)
  assert.deepEqual(result.identity, { appId, installationId, repository: MAINTENANCE_REPOSITORY,
    appSlug, repositorySelection: 'selected', identitySource: 'pinned-action-and-owner-jwt-audit',
    permissions: { administration: 'write', metadata: 'read' } })
  assert.deepEqual(fixture.calls.map(call => call.url.replace('https://api.github.com', '')), [
    '/installation/repositories?per_page=100', MAINTENANCE_RULESET_PATH,
  ])
  for (const call of fixture.calls) {
    assert.equal(call.options.method, 'GET')
    assert.equal(call.options.redirect, 'error')
    assert.equal(call.options.body, undefined)
    assert.equal(call.options.headers.Authorization, 'Bearer evidence-token')
  }
})

for (const [name, mutate] of [
  ['missing App ID', () => createMaintenanceRulesetReader({ token: 't', expectedAppId: 0, expectedInstallationId: installationId })],
  ['missing installation ID', () => createMaintenanceRulesetReader({ token: 't', expectedAppId: appId, expectedInstallationId: 0 })],
  ['missing App slug', () => createMaintenanceRulesetReader({ token: 't', expectedAppId: appId, expectedInstallationId: installationId, expectedAppSlug: '' })],
  ['missing token', () => createMaintenanceRulesetReader({ expectedAppId: appId, expectedInstallationId: installationId,
    expectedAppSlug: appSlug, actionAppSlug: appSlug, actionInstallationId: installationId })],
]) {
  test('refuses ' + name + ' before transport', () => assert.throws(mutate, /not configured|missing/))
}

for (const [name, options] of [
  ['action App slug mismatch', { actionAppSlug: 'other-app', actionInstallationId: installationId }],
  ['action installation mismatch', { actionAppSlug: appSlug, actionInstallationId: installationId + 1 }],
]) {
  test('refuses ' + name + ' before transport', () => assert.throws(() => createMaintenanceRulesetReader({
    token: 't', expectedAppId: appId, expectedInstallationId: installationId, expectedAppSlug: appSlug, ...options,
  }), /action identity/))
}

for (const [name, responseOverride, expected] of [
  ['multiple repositories', path => path === '/installation/repositories?per_page=100' ? { total_count: 2, repositories: [{ full_name: MAINTENANCE_REPOSITORY }, { full_name: 'other/repo' }] } : responses[path], /exactly the target/],
  ['wrong repository', path => path === '/installation/repositories?per_page=100' ? { total_count: 1, repositories: [{ full_name: 'other/repo' }] } : responses[path], /exactly the target/],
  ['wrong ruleset identity', path => path === MAINTENANCE_RULESET_PATH ? { ...responses[path], id: MAINTENANCE_RULESET_ID + 1 } : responses[path], /ruleset identity/],
]) {
  test('refuses ' + name + ' without using a broader endpoint', async () => {
    const fixture = fetchFixture({ responseOverride })
    const reader = createMaintenanceRulesetReader({ token: 'evidence-token', expectedAppId: appId,
      expectedInstallationId: installationId, expectedAppSlug: appSlug, actionAppSlug: appSlug,
      actionInstallationId: installationId, fetchImpl: fixture.fetchImpl })
    await assert.rejects(reader, expected)
    assert.ok(fixture.calls.every(call => ['/installation/repositories?per_page=100', MAINTENANCE_RULESET_PATH].includes(call.url.replace('https://api.github.com', ''))))
  })
}

test('reader pins api.github.com and does not accept an arbitrary HTTPS origin', () => {
  assert.throws(() => createMaintenanceRulesetReader({ token: 't', expectedAppId: appId,
    expectedInstallationId: installationId, expectedAppSlug: appSlug, actionAppSlug: appSlug,
    actionInstallationId: installationId, apiUrl: 'https://attacker.example' }), /origin is invalid/)
})

test('redirect or transport failure is refused without exposing response text', async () => {
  const fixture = fetchFixture({ mutate: response => { response.redirected = true } })
  const reader = createMaintenanceRulesetReader({ token: 'evidence-token', expectedAppId: appId,
    expectedInstallationId: installationId, expectedAppSlug: appSlug, actionAppSlug: appSlug,
    actionInstallationId: installationId, fetchImpl: fixture.fetchImpl })
  await assert.rejects(reader, /failed closed/)
})

test('malformed privileged JSON is refused through the sanitized boundary', async () => {
  const fixture = fetchFixture({ mutate: response => { response.json = async () => { throw new Error('secret response text') } } })
  const reader = createMaintenanceRulesetReader({ token: 'evidence-token', expectedAppId: appId,
    expectedInstallationId: installationId, expectedAppSlug: appSlug, actionAppSlug: appSlug,
    actionInstallationId: installationId, fetchImpl: fixture.fetchImpl })
  await assert.rejects(reader, error => error.message === 'Maintenance evidence GET /installation/repositories?per_page=100 failed closed.')
})

test('router sends only the exact ruleset read to the privileged reader and rejects attempted writes/bodies', async () => {
  const ordinary = []
  const privileged = []
  const router = routeMaintenanceRead({
    read: async (path, options) => { ordinary.push({ path, options }); return { source: 'ordinary' } },
    rulesetReader: async () => { privileged.push('ruleset'); return { ruleset } },
  })
  assert.deepEqual(await router(MAINTENANCE_RULESET_PATH), ruleset)
  assert.deepEqual(await router('/repos/' + MAINTENANCE_REPOSITORY + '/pulls/946'), { source: 'ordinary' })
  assert.equal(privileged.length, 1)
  assert.equal(ordinary.length, 1)
  await assert.rejects(router(MAINTENANCE_RULESET_PATH, { method: 'PUT' }), /only the exact ruleset GET/)
  await assert.rejects(router(MAINTENANCE_RULESET_PATH, { method: 'GET', body: {} }), /only the exact ruleset GET/)
  assert.equal(privileged.length, 1)
})

test('ruleset evaluator refuses omitted and nonempty bypass actors', () => {
  const base = { repository: MAINTENANCE_REPOSITORY, main: { name: 'main', sha: 'a'.repeat(40) },
    pr: { number: 1, state: 'open', draft: false, base: { ref: 'main' }, head: { sha: 'b'.repeat(40), repo: { full_name: MAINTENANCE_REPOSITORY } } },
    queue: { prNumber: 1, prHeadSha: 'b'.repeat(40), position: 1, state: 'AWAITING_CHECKS', headSha: 'c'.repeat(40), baseSha: 'a'.repeat(40) },
    run: { headSha: 'c'.repeat(40), runId: 1, runAttempt: 1 }, jobs: [], changes: [{ path: '.github/workflows/ci.yml' }],
    ruleset: { id: MAINTENANCE_RULESET_ID, enforcement: 'active', target: 'branch', conditions: { ref_name: { include: ['~DEFAULT_BRANCH'], exclude: [] } },
      rules: [{ type: 'pull_request' }, { type: 'deletion' }, { type: 'non_fast_forward' }, { type: 'required_status_checks', parameters: { required_status_checks: [] } },
        { type: 'merge_queue', parameters: { grouping_strategy: 'ALLGREEN', max_entries_to_merge: 1, merge_method: 'SQUASH' } }] },
  }
  assert.ok(evaluateMaintenancePreflight({ ...base, ruleset: { ...base.ruleset, bypass_actors: [{ actor_id: 1 }] } }).reasons.includes('required-publishers-or-protection-changed'))
  assert.ok(evaluateMaintenancePreflight({ ...base, ruleset: { ...base.ruleset } }).reasons.includes('required-publishers-or-protection-changed'))
})

test('workflow mints the privileged token only from the protected candidate output and keeps review tokenless', () => {
  const workflow = readFileSync(new URL('../../../.github/workflows/merge-queue-binding.yml', import.meta.url), 'utf8')
  assert.match(workflow, /id: detect-maintenance[\s\S]*?run: node product\/ci-metrics\/bin\/detect-maintenance-candidate\.mjs/)
  assert.match(workflow, /id: maintenance-evidence-token[\s\S]*?if: steps\.detect-maintenance\.outputs\.maintenance-needed == 'true'/)
  const evidenceBlocks = workflow.split('      - name: Mint the repository-scoped maintenance evidence token').slice(1)
  assert.equal(evidenceBlocks.length, 2)
  for (const block of evidenceBlocks) {
    assert.match(block, /permission-administration: write/)
    assert.match(block, /permission-metadata: read/)
    assert.match(block, /owner: AeroLinkDEV/)
    assert.match(block, /repositories: requirements-management-tool/)
    assert.doesNotMatch(block.split('      - name: Mint the repository-scoped Merge Authority token')[0], /permission-(checks|contents):/)
  }
  const review = workflow.split('  review-maintenance:\n')[1].split('  publish-maintenance:\n')[0]
  assert.match(review, /permissions: \{\}/)
  assert.doesNotMatch(review, /secrets\.|token|MAINTENANCE_EVIDENCE/)
  assert.match(workflow, /MAINTENANCE_EVIDENCE_ACTION_INSTALLATION_ID: \$\{\{ steps\.maintenance-evidence-token\.outputs\.installation-id \}\}/)
  assert.match(workflow, /MAINTENANCE_EVIDENCE_ACTION_APP_SLUG: \$\{\{ steps\.maintenance-evidence-token\.outputs\.app-slug \}\}/)
  assert.doesNotMatch(workflow, /\/installation['"]?\s*\}/)
})
