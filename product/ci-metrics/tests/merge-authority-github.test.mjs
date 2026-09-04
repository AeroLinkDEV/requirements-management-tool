import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import {
  compareTrustedSurfaces,
  createGitHubRequest,
  fetchDefaultBranch,
  fetchLatestRunJobs,
  fetchWorkflowRun,
  publishMergeAuthorityCheck,
} from '../lib/merge-authority-github.mjs'

const repoRoot = fileURLToPath(new URL('../../../', import.meta.url))
const REPOSITORY = 'AeroLinkDEV/requirements-management-tool'
const sha = (character) => character.repeat(40)

test('workflow run metadata is mapped directly from the exact run endpoint', async () => {
  const calls = []
  const run = await fetchWorkflowRun({
    repository: REPOSITORY,
    runId: 42,
    request: async (path) => {
      calls.push(path)
      return {
        id: 42,
        run_attempt: 3,
        name: 'Product quality gate',
        path: '.github/workflows/ci.yml',
        event: 'merge_group',
        head_sha: sha('a'),
        head_branch: `gh-readonly-queue/main/pr-1-${sha('b')}`,
        status: 'completed',
        html_url: 'https://github.example/runs/42',
        repository: { full_name: REPOSITORY },
      }
    },
  })
  assert.deepEqual(calls, [`/repos/${REPOSITORY}/actions/runs/42`])
  assert.deepEqual(run, {
    repository: REPOSITORY,
    workflowName: 'Product quality gate',
    workflowPath: '.github/workflows/ci.yml',
    event: 'merge_group',
    headSha: sha('a'),
    headBranch: `gh-readonly-queue/main/pr-1-${sha('b')}`,
    runId: 42,
    runAttempt: 3,
    status: 'completed',
    detailsUrl: 'https://github.example/runs/42',
  })
})

test('job evidence uses explicit filter=latest on every page and maps attempt identity directly', async () => {
  const calls = []
  const jobs = await fetchLatestRunJobs({
    repository: REPOSITORY,
    runId: 42,
    request: async (path) => {
      calls.push(path)
      const page = Number(new URL(`https://api.example${path}`).searchParams.get('page'))
      const count = page === 1 ? 100 : 1
      return {
        total_count: 101,
        jobs: Array.from({ length: count }, (_, index) => ({
          run_id: 42,
          run_attempt: page,
          name: `job-${page}-${index}`,
          conclusion: 'success',
          ignored: 'not-authority',
        })),
      }
    },
  })
  assert.equal(jobs.length, 101)
  assert.deepEqual(jobs[0], { runId: 42, runAttempt: 1, name: 'job-1-0', conclusion: 'success' })
  assert.deepEqual(jobs[100], { runId: 42, runAttempt: 2, name: 'job-2-0', conclusion: 'success' })
  assert.equal(calls.length, 2)
  for (const call of calls) {
    assert.match(call, /\/actions\/runs\/42\/jobs\?filter=latest&per_page=100&page=[12]$/)
    assert.doesNotMatch(call, /filter=all|attempts/)
  }
})

test('malformed job responses fail closed instead of becoming an empty job list', async () => {
  await assert.rejects(
    fetchLatestRunJobs({ request: async () => ({ total_count: 0 }), repository: REPOSITORY, runId: 42 }),
    /did not contain a jobs array/,
  )
  await assert.rejects(
    fetchLatestRunJobs({ request: async () => ({ total_count: 2, jobs: [] }), repository: REPOSITORY, runId: 42 }),
    /returned 0 of 2 records/,
  )
})

function treeRequest({ changedPrefix = null, truncateTree = null } = {}) {
  const ids = {
    candidateRoot: sha('1'), baseRoot: sha('2'),
    candidateGithub: sha('3'), baseGithub: changedPrefix === '.github/' ? sha('4') : sha('3'),
    candidateProduct: sha('5'), baseProduct: sha('6'),
    candidatePlanner: sha('7'), basePlanner: changedPrefix === 'product/test-planner/' ? sha('8') : sha('7'),
    candidateMetrics: sha('9'), baseMetrics: changedPrefix === 'product/ci-metrics/' ? sha('a') : sha('9'),
  }
  const trees = new Map([
    [ids.candidateRoot, [{ path: '.github', type: 'tree', sha: ids.candidateGithub }, { path: 'product', type: 'tree', sha: ids.candidateProduct }]],
    [ids.baseRoot, [{ path: '.github', type: 'tree', sha: ids.baseGithub }, { path: 'product', type: 'tree', sha: ids.baseProduct }]],
    [ids.candidateProduct, [{ path: 'test-planner', type: 'tree', sha: ids.candidatePlanner }, { path: 'ci-metrics', type: 'tree', sha: ids.candidateMetrics }]],
    [ids.baseProduct, [{ path: 'test-planner', type: 'tree', sha: ids.basePlanner }, { path: 'ci-metrics', type: 'tree', sha: ids.baseMetrics }]],
  ])
  return async (path) => {
    if (path.endsWith(`/git/commits/${sha('b')}`)) return { tree: { sha: ids.candidateRoot } }
    if (path.endsWith(`/git/commits/${sha('c')}`)) return { tree: { sha: ids.baseRoot } }
    const treeSha = path.split('/').at(-1)
    if (trees.has(treeSha)) return { truncated: treeSha === truncateTree, tree: trees.get(treeSha) }
    return { truncated: false, tree: [] }
  }
}

test('complete trusted subtrees compare equal by Git tree identity', async () => {
  const changed = await compareTrustedSurfaces({
    request: treeRequest(), repository: REPOSITORY, candidateSha: sha('b'), baseSha: sha('c'),
  })
  assert.deepEqual(changed, [])
})

test('a differing trusted subtree is surfaced to the pure evaluator', async () => {
  for (const prefix of ['.github/', 'product/test-planner/', 'product/ci-metrics/']) {
    const changed = await compareTrustedSurfaces({
      request: treeRequest({ changedPrefix: prefix }), repository: REPOSITORY, candidateSha: sha('b'), baseSha: sha('c'),
    })
    assert.deepEqual(changed, [prefix])
  }
})

test('truncated or missing Git trees fail closed', async () => {
  await assert.rejects(
    compareTrustedSurfaces({
      request: treeRequest({ truncateTree: sha('1') }), repository: REPOSITORY, candidateSha: sha('b'), baseSha: sha('c'),
    }),
    /truncated/,
  )
  await assert.rejects(
    compareTrustedSurfaces({
      request: async (path) => path.includes('/git/commits/') ? { tree: { sha: sha('d') } } : { truncated: false, tree: [] },
      repository: REPOSITORY,
      candidateSha: sha('b'),
      baseSha: sha('c'),
    }),
    /missing or ambiguous/,
  )
})

test('default branch resolution binds both its name and current commit SHA', async () => {
  const calls = []
  const result = await fetchDefaultBranch({
    repository: REPOSITORY,
    request: async (path) => {
      calls.push(path)
      return path === `/repos/${REPOSITORY}` ? { default_branch: 'release/main' } : { object: { type: 'commit', sha: sha('d') } }
    },
  })
  assert.deepEqual(result, { name: 'release/main', sha: sha('d') })
  assert.deepEqual(calls, [`/repos/${REPOSITORY}`, `/repos/${REPOSITORY}/git/ref/heads/release/main`])
})

test('check publication binds the fixed context name to the candidate SHA and decision', async () => {
  const calls = []
  for (const decision of ['PASS', 'REFUSE', 'PENDING']) {
    await publishMergeAuthorityCheck({
      request: async (path, options) => { calls.push({ path, options }); return { id: calls.length } },
      repository: REPOSITORY,
      headSha: sha('e'),
      decision,
      reasons: decision === 'REFUSE' ? ['job-not-success: API'] : [],
      detailsUrl: 'https://github.example/runs/42',
    })
  }
  assert.equal(calls.length, 3)
  for (const call of calls) {
    assert.equal(call.path, `/repos/${REPOSITORY}/check-runs`)
    assert.equal(call.options.method, 'POST')
    assert.equal(call.options.body.name, 'Trusted merge-queue binding')
    assert.equal(call.options.body.head_sha, sha('e'))
  }
  assert.equal(calls[0].options.body.conclusion, 'success')
  assert.equal(calls[1].options.body.conclusion, 'failure')
  assert.match(calls[1].options.body.output.summary, /job-not-success: API/)
  assert.equal(calls[2].options.body.status, 'in_progress')
  assert.equal(Object.hasOwn(calls[2].options.body, 'conclusion'), false)
  assert.match(calls[2].options.body.output.summary, /earlier authority.*invalid/)
  await assert.rejects(
    publishMergeAuthorityCheck({
      request: async () => ({}), repository: REPOSITORY, headSha: sha('e'), decision: 'UNKNOWN', reasons: [],
    }),
    /Unsupported merge-authority decision/,
  )
})

test('request authentication is bearer-scoped and API errors never echo the token', async () => {
  const calls = []
  const request = createGitHubRequest({
    token: 'super-secret-token',
    apiUrl: 'https://api.example',
    fetchImpl: async (url, options) => {
      calls.push({ url, options })
      return { ok: false, status: 403, json: async () => ({}) }
    },
  })
  await assert.rejects(request('/forbidden'), (error) => {
    assert.match(error.message, /returned 403/)
    assert.doesNotMatch(error.message, /super-secret-token/)
    return true
  })
  assert.equal(calls[0].options.headers.Authorization, 'Bearer super-secret-token')
})

test('default-branch workflow keeps authority credentials behind the queue-only protected environment', () => {
  const workflow = readFileSync(join(repoRoot, '.github/workflows/merge-queue-binding.yml'), 'utf8')
  const requester = readFileSync(join(repoRoot, '.github/workflows/request-full-ci.yml'), 'utf8')
  const codeowners = readFileSync(join(repoRoot, '.github/CODEOWNERS'), 'utf8')
  const productWorkflow = readFileSync(join(repoRoot, '.github/workflows/ci.yml'), 'utf8')
  const readme = readFileSync(join(repoRoot, 'product/ci-metrics/README.md'), 'utf8')
  const verifier = readFileSync(join(repoRoot, 'product/ci-metrics/bin/verify-merge-authority.mjs'), 'utf8')
  assert.match(workflow, /workflow_run:\s+workflows:\s+- Product quality gate\s+types:\s+- in_progress\s+- completed/)
  assert.match(workflow, /permissions:\s+actions: read\s+contents: read/)
  assert.match(
    workflow,
    /group: merge-authority-\$\{\{ github\.event\.workflow_run\.id \}\}\s+cancel-in-progress: \$\{\{ github\.event\.action == 'in_progress' \}\}/,
  )
  assert.match(workflow, /if: github\.event\.workflow_run\.event == 'merge_group'/)
  assert.match(workflow, /environment:\s+name: merge-authority/)
  assert.match(workflow, /actions\/checkout@[0-9a-f]{40}/)
  assert.match(workflow, /actions\/setup-node@[0-9a-f]{40}/)
  assert.match(workflow, /actions\/create-github-app-token@[0-9a-f]{40}/)
  assert.match(workflow, /client-id: \$\{\{ vars\.MERGE_AUTHORITY_APP_CLIENT_ID \}\}/)
  assert.match(workflow, /private-key: \$\{\{ secrets\.MERGE_AUTHORITY_APP_PRIVATE_KEY \}\}/)
  assert.match(workflow, /permission-checks: write/)
  assert.match(workflow, /permission-contents: read/)
  assert.match(workflow, /MERGE_AUTHORITY_TOKEN: \$\{\{ steps\.authority-token\.outputs\.token \}\}/)
  assert.match(workflow, /run: node product\/ci-metrics\/bin\/verify-merge-authority\.mjs/)
  assert.match(verifier, /if \(action === 'in_progress'\)[\s\S]*decision: 'PENDING'[\s\S]*return/)
  assert.match(verifier, /if \(action !== 'completed'\)/)
  assert.ok(
    verifier.indexOf("if (action === 'in_progress')") < verifier.indexOf("token: requiredEnv('GITHUB_TOKEN')"),
    'the pending App check must replace prior authority before completed-run evidence collection starts',
  )
  const completedBoundary = verifier.indexOf("if (action !== 'completed')")
  const evidenceCollection = verifier.indexOf("token: requiredEnv('GITHUB_TOKEN')")
  const finalPublication = verifier.lastIndexOf('await publishMergeAuthorityCheck({')
  const finalRunFetch = verifier.lastIndexOf('const currentRun = await fetchWorkflowRun({')
  assert.ok(
    verifier.indexOf("decision: 'PENDING'", completedBoundary) < evidenceCollection,
    'a completed handler must clear earlier authority before it collects evidence',
  )
  assert.ok(
    evidenceCollection < finalRunFetch && finalRunFetch < finalPublication,
    'the completed handler must re-fetch live run state immediately before final App publication',
  )
  assert.match(verifier, /currentRun\.status !== 'completed' \|\| currentRun\.runAttempt !== trigger\.run_attempt/)
  assert.match(requester, /environment:\s+name: merge-authority/)
  assert.match(requester, /actions\/create-github-app-token@[0-9a-f]{40}/)
  assert.match(requester, /client-id: \$\{\{ vars\.MERGE_AUTHORITY_APP_CLIENT_ID \}\}/)
  assert.match(requester, /private-key: \$\{\{ secrets\.MERGE_AUTHORITY_APP_PRIVATE_KEY \}\}/)
  assert.match(requester, /permission-checks: write/)
  assert.match(requester, /permission-contents: read/)
  assert.match(requester, /"name": "Trusted merge-queue binding"/)
  assert.match(requester, /"head_sha": head_sha/)
  assert.match(requester, /"conclusion": "success"/)
  assert.match(requester, /aerolink-merge-authority:pull-request:\{head_sha\}/)
  assert.ok(
    requester.indexOf('Authenticate live ready PR, dispatch once, and bind exact Product success') <
      requester.indexOf('Mint the repository-scoped Merge Authority token'),
    'the App token must only be minted after trusted PR/Product validation succeeds',
  )
  assert.ok(
    requester.indexOf('Mint the repository-scoped Merge Authority token') <
      requester.indexOf('Publish trusted pull-request readiness'),
    'the App-authored readiness check must use the short-lived token minted after validation',
  )
  assert.match(verifier, /import \{ evaluateMergeGroupCandidate, TRUSTED_SURFACE_PREFIXES \} from '\.\.\/lib\/merge-authority\.mjs'/)
  assert.match(verifier, /fetchLatestRunJobs/)
  assert.match(verifier, /compareTrustedSurfaces/)
  for (const prefix of ['/.github/', '/product/test-planner/', '/product/ci-metrics/']) {
    assert.match(codeowners, new RegExp(`^${prefix.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\s+@seanmccarthyns$`, 'm'))
  }
  for (const testPath of [
    'product/ci-metrics/tests/merge-authority.test.mjs',
    'product/ci-metrics/tests/merge-authority-github.test.mjs',
  ]) {
    assert.match(productWorkflow, new RegExp(testPath.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')))
    assert.match(readme, new RegExp(testPath.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')))
  }
})
