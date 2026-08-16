import { test } from 'node:test'
import assert from 'node:assert/strict'
import {
  detectOverlaps,
  normalizeFileList,
  normalizePath,
  overlapsFor,
  pathsFromFile,
  renderComment,
  OVERLAP_STATUSES,
  PLANNER_LANES,
  plannerLanesForPaths,
  surfacesFor,
  SURFACES,
} from '../lib/overlap.mjs'
import {
  buildReport,
  createGithubApi,
  findMarkerComments,
  OVERLAP_LIMITS,
  REVIEWED_DISPOSITION_LABEL,
  runOverlapCheck,
  validateCommentList,
  validateFileList,
} from '../tools/check-overlap.mjs'

const pr = (number, files, extra = {}) => ({ number, title: `PR ${number}`, author: 'agent', branch: `b/${number}`, files, ...extra })
const sha = (number, salt = 'a') => `${salt.repeat(39)}${(Number(number) % 16).toString(16)}`
const rawPr = (number, { draft = false, headSha = sha(number, 'a'), baseSha = sha(number, 'b'), labels = [] } = {}) => ({
  number, title: `PR ${number}`, user: { login: 'agent', type: 'User' }, head: { ref: `b/${number}`, sha: headSha }, base: { ref: 'main', sha: baseSha }, draft, labels,
})
const botComment = (id, body) => ({ id, body, user: { login: 'github-actions[bot]', type: 'Bot' } })
const file = (filename, extra = {}) => ({ filename, status: 'modified', ...extra })

test('normalizes case, separators and duplicate paths', () => {
  assert.equal(normalizePath('.\\Product\\SRC\\A.cs'), 'product/src/a.cs')
  assert.deepEqual(normalizeFileList(['Product/SRC/A.cs', 'product\\src\\a.cs', './product/src/A.cs']), ['product/src/a.cs'])
})

test('preserves legal leading and trailing path spaces instead of conflating files', () => {
  assert.equal(normalizePath(' Product/SRC/A.cs '), ' product/src/a.cs ')
  assert.deepEqual(
    normalizeFileList(['product/src/A.cs', ' product/src/A.cs', 'product/src/A.cs ']),
    [' product/src/a.cs', 'product/src/a.cs', 'product/src/a.cs '],
  )
  assert.equal(detectOverlaps([pr(1, [' product/src/A.cs']), pr(2, ['product/src/A.cs'])]).length, 0)
})

test('rename evidence includes both old and new filenames', () => {
  assert.deepEqual(pathsFromFile({ filename: 'Product/New.cs', previous_filename: 'product/Old.cs', status: 'renamed' }), ['product/new.cs', 'product/old.cs'])
  const overlaps = detectOverlaps([
    pr(1, [{ filename: 'src/New.cs', previous_filename: 'src/Old.cs' }]),
    pr(2, ['SRC/OLD.CS']),
  ])
  assert.equal(overlaps[0].sharedFiles[0], 'src/old.cs')
})

test('two pull requests editing the same file are a high-severity overlap', () => {
  const overlaps = detectOverlaps([pr(1, ['product/client/src/App.tsx', 'README.md']), pr(2, ['product/client/src/App.tsx'])])
  assert.equal(overlaps.length, 1)
  assert.equal(overlaps[0].severity, 'high')
  assert.deepEqual(overlaps[0].sharedFiles, ['product/client/src/app.tsx'])
  assert.equal(overlaps[0].affectedLanes.some((lane) => lane.key === 'client'), true)
})

test('a shared migration surface with no shared file is reported', () => {
  const overlaps = detectOverlaps([pr(1, ['product/src/AeroLink.Infrastructure/Persistence/Migrations/0007_add_positions.cs']), pr(2, ['product/src/AeroLink.Infrastructure/Persistence/Migrations/0008_add_stage_kind.cs'])])
  assert.equal(overlaps.length, 1)
  assert.equal(overlaps[0].severity, 'medium')
  assert.equal(overlaps[0].sharedSurfaces.some((surface) => surface.key === 'migrations'), true)
  assert.equal(overlaps[0].affectedLanes.some((lane) => lane.key === 'postgresql'), true)
})

test('a surface-only client-shell overlap uses only the reviewed surface lanes', () => {
  const overlaps = detectOverlaps([
    pr(1, ['product/client/src/App.tsx']),
    pr(2, ['product/client/src/workspace/Workspace.tsx']),
  ])
  assert.equal(overlaps.length, 1)
  assert.equal(overlaps[0].severity, 'medium')
  assert.deepEqual(overlaps[0].affectedLanes.map((lane) => lane.key), ['client', 'browser'])
  assert.doesNotMatch(renderComment(1, overlaps), /documentation-only review/)
})

test('planner lane manifest is structured, bounded and follows the shared classifier', () => {
  assert.deepEqual(plannerLanesForPaths(['product/client/src/App.tsx']).map((lane) => lane.key), ['client', 'browser'])
  assert.deepEqual(plannerLanesForPaths(['product/ci-metrics/lib/rolling.mjs']).map((lane) => lane.key), ['full'])
  assert.deepEqual(plannerLanesForPaths(['README.md']).map((lane) => lane.key), ['documentation'])
  for (const lane of Object.values(PLANNER_LANES)) {
    assert.ok(lane.label.length > 0)
    assert.ok(lane.reason.length > 20)
    assert.ok(lane.jobs.length <= OVERLAP_LIMITS.maxJobsPerAffectedLane)
  }
  for (const surface of SURFACES) {
    assert.ok(Array.isArray(surface.laneKeys), surface.key)
    assert.ok(surface.laneKeys.length > 0, surface.key)
    for (const key of surface.laneKeys) assert.ok(PLANNER_LANES[key], `${surface.key}:${key}`)
  }
})

test('unrelated pull requests produce nothing', () => {
  const overlaps = detectOverlaps([pr(1, ['product/docs/OPERATIONS.md']), pr(2, ['product/client/src/components/Badge.tsx'])])
  assert.deepEqual(overlaps, [])
  assert.equal(renderComment(1, overlaps), null)
})

test('the real CI collision is detected without flagging unrelated metrics provenance', () => {
  const collision = detectOverlaps([
    pr(592, ['.github/workflows/ci.yml', 'product/ci-metrics/tests/ci-workflow-contract.test.mjs']),
    pr(597, ['.github/workflows/ci.yml', 'product/test-planner/lib/classify.mjs']),
  ])
  assert.equal(collision[0].severity, 'high')
  assert.ok(collision[0].sharedSurfaces.some((surface) => surface.key === 'ci-gate'))
  assert.deepEqual(detectOverlaps([pr(588, ['product/test-contracts/route-coverage.json']), pr(590, ['product/ci-metrics/lib/provenance.mjs'])]), [])
})

test('every requested hotspot has a reason and migration risk remains actionable', () => {
  for (const key of ['solution', 'project', 'lock', 'build', 'startup', 'identity', 'security', 'routing', 'api-contracts', 'test-harness']) {
    const surface = SURFACES.find((candidate) => candidate.key === key)
    assert.ok(surface, `${key} hotspot is present`)
    assert.ok(surface.why.length > 40)
  }
  const hits = surfacesFor(['product/src/AeroLink.Infrastructure/Persistence/Migrations/0007.cs', 'product/src/AeroLink.Infrastructure/Persistence/Migrations/0008.cs'])
  assert.ok(hits.has('migrations'))
  assert.ok(surfacesFor(['product/src/AeroLink.Api/ApiContracts.cs']).has('api-contracts'))
  assert.equal(surfacesFor(['product/src/AeroLink.Api/RequirementsEndpoints.cs']).has('api-contracts'), false)
})

test('ordinary files do not match a hotspot', () => {
  assert.equal(surfacesFor(['README.md', 'product/client/src/components/Badge.tsx', 'product/client/tests/journey.spec.ts', 'product/docs/OPERATIONS.md', 'product/tests/AeroLink.Domain.Tests/RuleTests.cs']).size, 0)
})

test('rendered warning carries analysis timestamp and current/peer SHA provenance', () => {
  const body = renderComment(1, detectOverlaps([
    pr(1, ['product/src/AeroLink.Domain/A.cs'], { headSha: 'current-sha' }),
    pr(2, ['product/src/AeroLink.Domain/A.cs'], { headSha: 'peer-sha' }),
  ]), { analysisTimestamp: '2026-08-16T00:00:00Z', currentSha: 'current-sha' })
  assert.match(body, /Analysis timestamp: 2026-08-16T00:00:00Z/)
  assert.match(body, /Current head SHA: current-sha/)
  assert.match(body, /Peer head SHA: peer-sha/)
  assert.match(body, /Status: Critical overlap/)
  assert.match(body, /Affected planner\/CI lanes:/)
})

test('surface-only overlap renders Coordinate while exact overlap renders Critical overlap', () => {
  const surfaceOnly = renderComment(1, detectOverlaps([
    pr(1, ['product/client/src/App.tsx']),
    pr(2, ['product/client/src/workspace/Workspace.tsx']),
  ]))
  assert.match(surfaceOnly, /Status: Coordinate/)
  const exact = renderComment(1, detectOverlaps([
    pr(1, ['product/client/src/App.tsx']),
    pr(2, ['product/client/src/App.tsx']),
  ]))
  assert.match(exact, /Status: Critical overlap/)
})

test('reviewed disposition is rendered without suppressing overlap', () => {
  const body = renderComment(1, detectOverlaps([
    pr(1, ['src/shared.cs']),
    pr(2, ['src/shared.cs']),
  ]), { reviewedDisposition: true })
  assert.match(body, /Status: Critical overlap/)
  assert.match(body, /overlap-reviewed.*does not suppress this warning/)
})

test('trusted overlap-reviewed label is carried into the report and current marker only', async () => {
  const current = rawPr(1, { labels: [{ name: REVIEWED_DISPOSITION_LABEL }] })
  const api = mockedApi({
    open: [current, rawPr(2)],
    files: { 1: [file('src/Shared.cs')], 2: [file('src/Shared.cs')] },
    comments: { 1: [botComment(10, '<!-- AEROLINK_PR_OVERLAP --> old')], 2: [botComment(20, '<!-- AEROLINK_PR_OVERLAP --> old')] },
  })
  const report = await runOverlapCheck({ repository: 'owner/repo', event: { action: 'synchronize', pull_request: current }, api })
  assert.equal(report.status, 'Critical overlap')
  assert.equal(report.currentPr.reviewedDisposition, true)
  assert.equal(report.peerHeads.find((record) => record.number === 1).reviewedDisposition, true)
  assert.equal(report.peerHeads.find((record) => record.number === 2).reviewedDisposition, false)
  assert.equal(report.overlaps[0].a.reviewedDisposition, true)
  assert.equal(report.overlaps[0].b.reviewedDisposition, false)
  const currentPatch = api.calls.find((call) => call.options.method === 'PATCH' && call.path.endsWith('/10'))
  const peerPatch = api.calls.find((call) => call.options.method === 'PATCH' && call.path.endsWith('/20'))
  assert.match(currentPatch.options.body.body, /Reviewed disposition: `overlap-reviewed`/)
  assert.doesNotMatch(peerPatch.options.body.body, /Reviewed disposition: `overlap-reviewed`/)
  assert.match(currentPatch.options.body.body, /Status: Critical overlap/)
})

test('full affected-lane jobs are identical in the comment and bounded artifact', () => {
  const overlaps = detectOverlaps([
    pr(1, ['.github/workflows/ci.yml']),
    pr(2, ['.github/workflows/ci.yml']),
  ])
  const expectedJobs = [...PLANNER_LANES.full.jobs]
  assert.equal(expectedJobs.length, 10)
  assert.ok(expectedJobs.length <= OVERLAP_LIMITS.maxJobsPerAffectedLane)
  assert.deepEqual(overlaps[0].affectedLanes[0].jobs, expectedJobs)
  const report = buildReport({ overlaps, analysisComplete: true })
  assert.deepEqual(report.overlaps[0].affectedLanes[0].jobs, expectedJobs)
  const comment = renderComment(1, overlaps)
  for (const job of expectedJobs) assert.match(comment, new RegExp(`\\\`${job}\\\``))
})

test('rendered warning code-escapes timestamp and current SHA metadata', () => {
  const body = renderComment(1, detectOverlaps([
    pr(1, ['product/src/AeroLink.Domain/A.cs'], { headSha: 'current-sha' }),
    pr(2, ['product/src/AeroLink.Domain/A.cs'], { headSha: 'peer-sha' }),
  ]), { analysisTimestamp: '2026-08-16T00:00:00Z\n`timestamp`', currentSha: 'current`sha\n[x](url)' })
  assert.ok(body.includes('Analysis timestamp: 2026-08-16T00:00:00Z \\x60timestamp\\x60'))
  assert.ok(body.includes('Current head SHA: current\\x60sha'))
  assert.ok(body.includes('\\[x\\]\\(url\\)'))
  assert.doesNotMatch(body, /00Z\n`timestamp`/)
})

test('rendering neutralizes PR-controlled Markdown, controls and code delimiters', () => {
  const maliciousPath = 'src/shared\n`bad`'
  const overlaps = detectOverlaps([
    pr(1, [maliciousPath]),
    pr(2, [maliciousPath], {
      title: 'evil\n## forged heading * [link] <tag> &',
      author: 'author\n[forged]',
      branch: 'feature/`tick`\nnext',
      headSha: 'peer\\sha`\n',
    }),
  ])
  const body = renderComment(1, overlaps, { analysisTimestamp: '2026-08-16T00:00:00Z', currentSha: 'current' })
  assert.ok(body)
  assert.doesNotMatch(body, /## forged heading/)
  assert.doesNotMatch(body, /\nnext/)
  assert.doesNotMatch(body, /`tick`/)
  assert.doesNotMatch(body, /`bad`/)
  assert.ok(body.includes('\\x60tick\\x60'))
  assert.ok(body.includes('\\[link\\]'))
})

test('malformed input cannot throw', () => {
  assert.deepEqual(detectOverlaps(null), [])
  assert.deepEqual(detectOverlaps([null, undefined, {}]), [])
  assert.deepEqual(detectOverlaps([pr(1, ['a']), { number: 2 }]), [])
  assert.deepEqual(surfacesFor(null).size, 0)
})

test('report schema preserves identity provenance and explicit completeness limits', () => {
  const report = buildReport({
    currentPr: { number: 7, title: 'Title', author: 'agent', branch: 'feature/7', headSha: sha(7), baseSha: sha(7, 'b') },
    records: [{ number: 8, title: 'Peer', author: 'peer', branch: 'feature/8', headSha: sha(8), baseSha: sha(8, 'b') }],
    errors: ['incomplete'],
    analysisComplete: false,
    eligibleCount: OVERLAP_LIMITS.maxEligiblePullRequests + 1,
  })
  assert.equal(report.status, 'Unknown')
  assert.equal(report.analysisComplete, false)
  assert.deepEqual(report.limits, OVERLAP_LIMITS)
  assert.equal(report.currentPr.baseSha, sha(7, 'b'))
  assert.equal(report.currentPr.reviewedDisposition, false)
  assert.equal(report.peerHeads[0].title, 'Peer')
  assert.equal(report.eligiblePullRequests, OVERLAP_LIMITS.maxEligiblePullRequests)
})

test('completed report status maps to Critical overlap, Coordinate, and Clear', () => {
  const overlap = (severity) => ({ severity, a: { number: 1 }, b: { number: 2 }, sharedFiles: [], sharedSurfaces: [], affectedLanes: [] })
  const critical = buildReport({ overlaps: [overlap('high')], analysisComplete: true })
  const coordinate = buildReport({ overlaps: [overlap('medium')], analysisComplete: true })
  const clear = buildReport({ overlaps: [], analysisComplete: true })
  assert.equal(critical.status, OVERLAP_STATUSES.critical)
  assert.equal(coordinate.status, OVERLAP_STATUSES.coordinate)
  assert.equal(clear.status, OVERLAP_STATUSES.clear)
})

test('the API client follows link and full-page pagination', async () => {
  const calls = []
  const api = createGithubApi({ baseUrl: 'https://api.example.test', token: 'test', fetchImpl: async (url) => {
    calls.push(url)
    const page = new URL(url).searchParams.get('page')
    const data = page === '1' ? [{ id: 1 }] : [{ id: 2 }]
    return { ok: true, status: 200, headers: { get: (name) => name.toLowerCase() === 'link' && page === '1' ? '<https://api.example.test/items?page=2>; rel="next"' : '' }, json: async () => data }
  } })
  assert.deepEqual(await api.paginate('/items'), [{ id: 1 }, { id: 2 }])
  assert.equal(calls.length, 2)
})

test('the API client rejects a cross-origin pagination link before bearer exfiltration', async () => {
  const calls = []
  const api = createGithubApi({ baseUrl: 'https://api.example.test', token: 'secret-token', fetchImpl: async (url, options) => {
    calls.push({ url, options })
    return {
      ok: true,
      status: 200,
      headers: { get: (name) => name.toLowerCase() === 'link' ? '<https://evil.example.test/steal?page=2>; rel="next"' : '' },
      json: async () => [{ id: 1 }],
    }
  } })
  await assert.rejects(() => api.paginate('/items'), /crossed the configured origin/)
  assert.equal(calls.length, 1)
  assert.equal(calls[0].url, 'https://api.example.test/items?per_page=100&page=1')
  assert.equal(calls.some(({ url }) => url.includes('evil.example.test')), false)
  assert.equal(calls[0].options.headers.Authorization, 'Bearer secret-token')
})

test('the API client fails closed when an item bound would be exceeded', async () => {
  const api = createGithubApi({ fetchImpl: async () => ({ ok: true, status: 200, headers: {}, json: async () => [{ id: 1 }, { id: 2 }] }) })
  await assert.rejects(() => api.paginate('/items', { maxItems: 1 }), /bounded limit of 1/)
})

function mockedApi({ open, files, comments, failFilesFor = null, failCommentsFor = null }) {
  const calls = []
  return {
    calls,
    async paginate(path) {
      const pull = path.match(/\/pulls\/(\d+)\/files/)
      const issue = path.match(/\/issues\/(\d+)\/comments/)
      if (path.includes('/pulls?')) return open
      if (pull) {
        if (Number(pull[1]) === failFilesFor) throw new Error('files unavailable')
        return files[Number(pull[1])] || []
      }
      if (issue) {
        if (Number(issue[1]) === failCommentsFor) throw new Error('comments unavailable')
        return comments[Number(issue[1])] || []
      }
      throw new Error(`unexpected pagination path ${path}`)
    },
    async request(path, options = {}) { calls.push({ path, options }); return { data: null, status: 200, headers: {} } },
  }
}

test('synchronize refreshes both current and peer comments and deduplicates markers', async () => {
  const open = [rawPr(1), rawPr(2)]
  const comments = {
    1: [botComment(10, '<!-- AEROLINK_PR_OVERLAP --> old'), botComment(11, '<!-- AEROLINK_PR_OVERLAP --> duplicate')],
    2: [botComment(20, '<!-- AEROLINK_PR_OVERLAP --> old')],
  }
  const api = mockedApi({ open, files: { 1: [file('src\\Shared.cs')], 2: [file('SRC/shared.cs')] }, comments })
  const report = await runOverlapCheck({ repository: 'owner/repo', event: { action: 'synchronize', pull_request: rawPr(1) }, api, analysisTimestamp: '2026-08-16T01:02:03Z' })
  assert.equal(report.status, 'Critical overlap')
  assert.equal(report.currentPr.headSha, sha(1))
  const patches = api.calls.filter((call) => call.options.method === 'PATCH')
  assert.equal(patches.length, 2)
  assert.ok(api.calls.some((call) => call.options.method === 'DELETE' && call.path.endsWith('/11')))
  assert.match(patches[0].options.body.body, new RegExp(`Peer head SHA: ${sha(2)}`))
  assert.match(patches[0].options.body.body, /Status: Critical overlap/)
  assert.match(patches[0].options.body.body, /Affected planner\/CI lanes:/)
})

test('closed lifecycle clears the closed PR and every peer stale claim', async () => {
  const open = [rawPr(2)]
  const comments = { 1: [botComment(10, '<!-- AEROLINK_PR_OVERLAP --> stale')], 2: [botComment(20, '<!-- AEROLINK_PR_OVERLAP --> stale')] }
  const api = mockedApi({ open, files: { 2: [file('src/Other.cs')] }, comments })
  const report = await runOverlapCheck({ repository: 'owner/repo', event: { action: 'closed', pull_request: rawPr(1) }, api, analysisTimestamp: '2026-08-16T01:02:03Z' })
  assert.equal(report.status, 'Clear')
  const patches = api.calls.filter((call) => call.options.method === 'PATCH')
  assert.equal(patches.length, 2)
  assert.match(patches[0].options.body.body, /Status: Clear/)
  assert.ok(patches.some((call) => call.options.body.body.includes(`Current head SHA: ${sha(1)}`)))
})

test('opened, ready and converted-to-draft lifecycle actions all refresh peers', async () => {
  for (const action of ['opened', 'ready_for_review', 'converted_to_draft']) {
    const current = rawPr(1, { draft: action === 'converted_to_draft' })
    const api = mockedApi({
      open: [rawPr(1), rawPr(2)],
      files: { 1: [file('src/Shared.cs')], 2: [file('src/Shared.cs')] },
      comments: { 1: [botComment(10, '<!-- AEROLINK_PR_OVERLAP --> old')], 2: [botComment(20, '<!-- AEROLINK_PR_OVERLAP --> old')] },
    })
    const report = await runOverlapCheck({ repository: 'owner/repo', event: { action, pull_request: current }, api, analysisTimestamp: '2026-08-16T01:02:03Z' })
    assert.notEqual(report.status, 'Unknown', action)
    assert.equal(api.calls.filter((call) => call.options.method === 'PATCH').length, 2, action)
  }
})

test('overlap-reviewed label add and remove refresh current and peer markers', async () => {
  for (const action of ['labeled', 'unlabeled']) {
    const reviewed = action === 'labeled'
    const eventLabels = reviewed ? [{ name: REVIEWED_DISPOSITION_LABEL }] : []
    const staleApiLabels = reviewed ? [] : [{ name: REVIEWED_DISPOSITION_LABEL }]
    const current = rawPr(1, { labels: eventLabels })
    const api = mockedApi({
      open: [rawPr(1, { labels: staleApiLabels }), rawPr(2)],
      files: { 1: [file('src/Shared.cs')], 2: [file('src/Shared.cs')] },
      comments: { 1: [botComment(10, '<!-- AEROLINK_PR_OVERLAP --> old')], 2: [botComment(20, '<!-- AEROLINK_PR_OVERLAP --> old')] },
    })
    const report = await runOverlapCheck({
      repository: 'owner/repo',
      event: { action, label: { name: REVIEWED_DISPOSITION_LABEL }, pull_request: current },
      api,
      analysisTimestamp: '2026-08-16T01:02:03Z',
    })
    assert.equal(report.status, 'Critical overlap', action)
    assert.equal(report.currentPr.reviewedDisposition, reviewed, action)
    assert.equal(report.peerHeads.find((record) => record.number === 1).reviewedDisposition, reviewed, action)
    assert.equal(report.overlaps[0].a.reviewedDisposition, reviewed, action)
    const currentPatch = api.calls.find((call) => call.options.method === 'PATCH' && call.path.endsWith('/10'))
    const peerPatch = api.calls.find((call) => call.options.method === 'PATCH' && call.path.endsWith('/20'))
    assert.ok(currentPatch, `${action}: current marker`)
    assert.ok(peerPatch, `${action}: peer marker`)
    assert.equal(currentPatch.options.body.body.includes('Reviewed disposition: `overlap-reviewed`'), reviewed, action)
    assert.doesNotMatch(peerPatch.options.body.body, /Reviewed disposition: `overlap-reviewed`/, action)
  }
})

test('API failure is explicit Unknown and never silently clean', async () => {
  const api = mockedApi({ open: [rawPr(1), rawPr(2)], files: {}, comments: { 1: [], 2: [] }, failFilesFor: 2 })
  const report = await runOverlapCheck({ repository: 'owner/repo', event: { action: 'opened', pull_request: rawPr(1) }, api, analysisTimestamp: '2026-08-16T01:02:03Z' })
  assert.equal(report.status, 'Unknown')
  assert.ok(report.errors.some((error) => error.includes('files unavailable')))
  const posts = api.calls.filter((call) => call.options.method === 'POST')
  assert.equal(posts.length, 2)
  assert.ok(posts.every((call) => call.options.body.body.includes('Status: Unknown')))
})

test('incomplete PR labels are Unknown rather than an unlabelled clean result', async () => {
  const incomplete = { ...rawPr(1), labels: undefined }
  const api = mockedApi({ open: [incomplete], files: {}, comments: { 1: [] } })
  const report = await runOverlapCheck({ repository: 'owner/repo', event: { action: 'opened', pull_request: rawPr(1) }, api })
  assert.equal(report.status, 'Unknown')
  assert.ok(report.errors.some((error) => error.includes('labels are missing or malformed')))
  const post = api.calls.find((call) => call.options.method === 'POST')
  assert.match(post.options.body.body, /Status: Unknown/)
})

test('malformed reviewed-disposition label metadata is Unknown', async () => {
  const malformed = rawPr(1, { labels: [{ name: 42 }] })
  const api = mockedApi({ open: [malformed], files: {}, comments: { 1: [] } })
  const report = await runOverlapCheck({ repository: 'owner/repo', event: { action: 'opened', pull_request: rawPr(1) }, api })
  assert.equal(report.status, 'Unknown')
  assert.ok(report.errors.some((error) => error.includes('label 1 is incomplete')))
})

test('a human marker-like comment is never patched or deleted', async () => {
  const human = { id: 10, body: '<!-- AEROLINK_PR_OVERLAP --> human-owned note', user: { login: 'alice', type: 'User' } }
  const api = mockedApi({ open: [rawPr(1)], files: { 1: [file('src/Only.cs')] }, comments: { 1: [human] } })
  const report = await runOverlapCheck({ repository: 'owner/repo', event: { action: 'opened', pull_request: rawPr(1) }, api })
  assert.equal(report.status, 'Clear')
  assert.equal(api.calls.length, 0)
  assert.deepEqual(findMarkerComments([human]), [])
})

test('a human marker-like comment remains untouched even when a trusted comment is needed', async () => {
  const human = { id: 10, body: '<!-- AEROLINK_PR_OVERLAP --> human-owned note', user: { login: 'alice', type: 'User' } }
  const api = mockedApi({
    open: [rawPr(1), rawPr(2)],
    files: { 1: [file('src/Shared.cs')], 2: [file('src/Shared.cs')] },
    comments: { 1: [human], 2: [] },
  })
  const report = await runOverlapCheck({ repository: 'owner/repo', event: { action: 'opened', pull_request: rawPr(1) }, api })
  assert.equal(report.status, 'Critical overlap')
  assert.equal(api.calls.filter((call) => call.options.method === 'PATCH' || call.options.method === 'DELETE').length, 0)
  assert.equal(api.calls.filter((call) => call.options.method === 'POST').length, 2)
})

test('incomplete PR identity produces Unknown rather than Clear', async () => {
  const malformed = { ...rawPr(1), base: { ref: 'main' } }
  const api = mockedApi({ open: [malformed], files: {}, comments: { 1: [] } })
  const report = await runOverlapCheck({ repository: 'owner/repo', event: { action: 'opened', pull_request: rawPr(1) }, api })
  assert.equal(report.status, 'Unknown')
  assert.equal(report.analysisComplete, false)
  assert.ok(report.errors.some((error) => error.includes('base SHA is invalid')))
})

test('malformed open peers still reconcile by bounded number without trusting metadata', async () => {
  const malformed = {
    ...rawPr(2),
    title: 'evil\n## forged peer title',
    head: { ...rawPr(2).head, sha: 'not-a-sha' },
  }
  const api = mockedApi({
    open: [rawPr(1), malformed],
    files: {},
    comments: {
      1: [],
      2: [botComment(20, '<!-- AEROLINK_PR_OVERLAP --> stale warning')],
    },
  })
  const report = await runOverlapCheck({ repository: 'owner/repo', event: { action: 'opened', pull_request: rawPr(1) }, api, analysisTimestamp: '2026-08-16T01:02:03Z' })
  assert.equal(report.status, 'Unknown')
  const peerPatch = api.calls.find((call) => call.options.method === 'PATCH' && call.path.endsWith('/20'))
  assert.ok(peerPatch)
  assert.match(peerPatch.options.body.body, /Status: Unknown/)
  assert.match(peerPatch.options.body.body, /Current head SHA: Unknown/)
  assert.doesNotMatch(peerPatch.options.body.body, /forged peer title|not-a-sha/)
})

test('duplicate PR identities in the open-list response produce Unknown', async () => {
  const api = mockedApi({ open: [rawPr(1), rawPr(1)], files: {}, comments: { 1: [] } })
  const report = await runOverlapCheck({ repository: 'owner/repo', event: { action: 'opened', pull_request: rawPr(1) }, api })
  assert.equal(report.status, 'Unknown')
  assert.ok(report.errors.some((error) => error.includes('duplicated')))
})

test('incomplete file identity or status produces Unknown rather than Clear', async () => {
  const api = mockedApi({ open: [rawPr(1)], files: { 1: [{ filename: 'src/Only.cs' }] }, comments: { 1: [] } })
  const report = await runOverlapCheck({ repository: 'owner/repo', event: { action: 'opened', pull_request: rawPr(1) }, api })
  assert.equal(report.status, 'Unknown')
  assert.ok(report.errors.some((error) => error.includes('status is invalid')))
  assert.throws(() => validateFileList([{ filename: 'src/New.cs', status: 'renamed' }]), /rename source is missing/)
})

test('path and comment payload bounds are explicit', () => {
  assert.throws(() => validateFileList([{ filename: 'x'.repeat(OVERLAP_LIMITS.maxPathLength + 1), status: 'modified' }]), /exceeds/)
  assert.throws(() => validateCommentList([{ id: 1, body: 'x'.repeat(OVERLAP_LIMITS.maxCommentBodyLength + 1), user: { login: 'alice', type: 'User' } }]), /exceeds/)
})

test('incomplete comment identity produces Unknown rather than Clear', async () => {
  const api = mockedApi({ open: [rawPr(1)], files: { 1: [file('src/Only.cs')] }, comments: { 1: [{ id: 1, body: 'ordinary' }] } })
  const report = await runOverlapCheck({ repository: 'owner/repo', event: { action: 'opened', pull_request: rawPr(1) }, api })
  assert.equal(report.status, 'Unknown')
  assert.equal(report.analysisComplete, false)
  assert.ok(report.errors.some((error) => error.includes('author is incomplete')))
})

test('eligible PR and analysis work bounds fail closed instead of truncating clean evidence', async () => {
  const open = Array.from({ length: OVERLAP_LIMITS.maxEligiblePullRequests + 1 }, (_, index) => rawPr(index + 1))
  const comments = Object.fromEntries(open.map((item) => [item.number, []]))
  const api = mockedApi({ open, files: {}, comments })
  const report = await runOverlapCheck({ repository: 'owner/repo', event: { action: 'opened', pull_request: rawPr(1) }, api })
  assert.equal(report.status, 'Unknown')
  assert.equal(report.analysisComplete, false)
  assert.ok(report.errors.some((error) => error.includes('Eligible pull-request count exceeded')))
  assert.equal(report.overlaps.length, 0)
})

test('marker lookup sees all already-paginated comments and keeps one', () => {
  const comments = [{ id: 1, body: 'ordinary', user: { login: 'human', type: 'User' } }, botComment(2, '<!-- AEROLINK_PR_OVERLAP --> first'), botComment(3, '<!-- AEROLINK_PR_OVERLAP --> second')]
  assert.deepEqual(findMarkerComments(comments).map((comment) => comment.id), [2, 3])
})
