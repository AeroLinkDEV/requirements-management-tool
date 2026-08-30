import { expect, test } from '@playwright/test'
import { apiBase, login } from './auth'

const fixture = {
  generatedAt: '2026-08-30T00:00:00Z',
  totals: { items: 6, returned: 6, unheld: 3 },
  people: [
    { userId: '00000000-0000-0000-0000-0000000000aa', userName: 'alice', displayName: 'API Alice', isCurrentProjectMember: true, accountState: 'active', baseRoles: ['SystemEngineer'], disciplineAffinities: ['system'], holds: 1, byLane: { work: 0, review: 1, sign: 0, approved: 0 } },
    { userId: '00000000-0000-0000-0000-0000000000ab', userName: 'bob', displayName: 'API Bob', isCurrentProjectMember: true, accountState: 'active', baseRoles: ['SoftwareEngineer'], disciplineAffinities: ['software'], holds: 1, byLane: { work: 0, review: 1, sign: 0, approved: 0 } },
    { userId: '00000000-0000-0000-0000-0000000000ac', userName: 'charlie', displayName: 'API Charlie', isCurrentProjectMember: true, accountState: 'active', baseRoles: [], disciplineAffinities: [], holds: 1, byLane: { work: 0, review: 0, sign: 1, approved: 0 } },
    { userId: '00000000-0000-0000-0000-0000000000ad', userName: 'dana', displayName: 'API Dana', isCurrentProjectMember: true, accountState: 'active', baseRoles: ['SystemTestEngineer'], disciplineAffinities: ['system'], holds: 1, byLane: { work: 0, review: 1, sign: 0, approved: 0 } },
  ],
  items: [
    {
      id: '00000000-0000-0000-0000-000000000001', family: 'system', category: 'system', prefix: 'SRCR', number: 'SRCR-00001.00',
      title: 'Draft system change', lane: 'work', nativeState: 'Draft', nativeOutcome: null, currentHolderIds: [], holderBasis: 'author',
      raisedById: null, raisedByKind: null, release: { id: '00000000-0000-0000-0000-0000000000a1', version: '1.6', isReleased: false }, deferred: false,
      allocation: null, deferredFromState: null, activeStageObligations: [], updatedAt: '2026-08-29T12:00:00Z', openUrl: '/open/change-request/00000000-0000-0000-0000-000000000001',
    },
    {
      id: '00000000-0000-0000-0000-000000000002', family: 'software', category: 'HLR', prefix: 'HLRCR', number: 'HLRCR-00002.00',
      title: 'Parallel review change', lane: 'review', nativeState: 'InReview', nativeOutcome: null, currentHolderIds: ['alice', 'bob'], holderBasis: 'activeReviewStage',
      raisedById: null, raisedByKind: null, release: { id: '00000000-0000-0000-0000-0000000000a1', version: '1.6', isReleased: false }, deferred: false,
      allocation: null, deferredFromState: null, activeStageObligations: [{ holderId: 'alice', stageKind: 'review' }, { holderId: 'bob', stageKind: 'review' }], updatedAt: '2026-08-29T11:00:00Z', openUrl: '/open/change-request/00000000-0000-0000-0000-000000000002',
    },
    {
      id: '00000000-0000-0000-0000-000000000005', family: 'interface', category: 'interface', prefix: 'ICDCR', number: 'ICDCR-00005.00',
      title: 'Interface change request', lane: 'review', nativeState: 'InReview', nativeOutcome: null, currentHolderIds: ['dana'], holderBasis: 'activeReviewStage',
      raisedById: 'historical-author', raisedByKind: 'author', release: { id: '00000000-0000-0000-0000-0000000000a1', version: '1.6', isReleased: false }, deferred: false,
      allocation: null, deferredFromState: null, activeStageObligations: [{ holderId: 'dana', stageKind: 'review' }], updatedAt: '2026-08-29T10:30:00Z', openUrl: '/open/change-request/00000000-0000-0000-0000-000000000005',
    },
    {
      id: '00000000-0000-0000-0000-000000000006', family: 'problemReport', category: null, prefix: null, number: 'PR00006.00',
      title: 'Open problem report', lane: 'work', nativeState: 'Open', nativeOutcome: null, currentHolderIds: [], holderBasis: 'responsibleEngineer',
      raisedById: 'historical-reporter', raisedByKind: 'reportedBy', release: null, deferred: false,
      allocation: null, deferredFromState: null, activeStageObligations: [], updatedAt: '2026-08-29T10:15:00Z', openUrl: '/open/problem-report/00000000-0000-0000-0000-000000000006',
    },
    {
      id: '00000000-0000-0000-0000-000000000003', family: 'assessment', category: 'HLR assessment', prefix: null, number: null,
      title: 'Assessment of HLRCR-00002.00', lane: 'sign', nativeState: 'InReview', nativeOutcome: 'Pending', currentHolderIds: ['charlie'], holderBasis: 'selectedAssessmentApprover',
      raisedById: 'source-change-id', raisedByKind: 'changeRequest', release: { id: '00000000-0000-0000-0000-0000000000b1', version: '1.5', isReleased: true }, deferred: false,
      allocation: null, deferredFromState: null, activeStageObligations: [], updatedAt: '2026-08-29T10:00:00Z', openUrl: '/open/downstream-assessment/00000000-0000-0000-0000-000000000003',
    },
    {
      id: '00000000-0000-0000-0000-000000000004', family: 'verification', category: 'system', prefix: 'SYSTPCR', number: 'SYSTPCR-00004.00',
      title: 'Deferred test review', lane: 'approved', nativeState: 'Deferred', nativeOutcome: null, currentHolderIds: [], holderBasis: 'none',
      raisedById: null, raisedByKind: null, release: { id: '00000000-0000-0000-0000-0000000000a1', version: '1.6', isReleased: false }, deferred: true,
      allocation: { baselineId: '00000000-0000-0000-0000-0000000000c1', releaseId: '00000000-0000-0000-0000-0000000000b1', releaseVersion: '1.5', baselineNumber: 'BL-00001', baselineRevision: 0, isReleased: true },
      deferredFromState: 'Approved', activeStageObligations: [], updatedAt: '2026-08-29T09:00:00Z', openUrl: '/open/test-change-request/00000000-0000-0000-0000-000000000004',
    },
  ],
}

async function openTeamWork(page: Parameters<typeof login>[0], body: unknown = fixture) {
  const calls: string[] = []
  await page.route('**/api/team-work*', async route => {
    calls.push(route.request().url())
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) })
  })
  await login(page)
  await page.getByRole('link', { name: 'Team Work', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Team Work', exact: true })).toBeVisible()
  return calls
}

test('Team Work is a project-wide four-lane board with API-owned card truth and canonical links', async ({ page }) => {
  const calls = await openTeamWork(page)
  const navHomeLinks = page.locator('.navHome > a')
  await expect(navHomeLinks).toHaveCount(3)
  await expect(navHomeLinks.nth(2)).toContainText('Team Work')
  await expect(navHomeLinks.nth(2)).toHaveClass(/active/)
  expect(calls.length).toBeGreaterThanOrEqual(1)
  expect(new Set(calls).size).toBe(1)
  for (const call of calls) {
    expect(call).toContain('projectId=')
    expect(call).not.toContain('releaseId=')
  }
  await expect(page.locator('main.teamWorkPage').getByText('Project scope · every build', { exact: true })).toBeVisible()
  const stats = page.locator('.teamWorkTotals')
  await expect(stats.getByText('Unique items').locator('..')).toContainText('6')
  await expect(stats.getByText('People holding work').locator('..')).toContainText('4')
  await expect(stats.getByText('No current holder').locator('..')).toContainText('3')

  const lanes = page.locator('[data-team-work-board="true"] [data-lane]')
  await expect(lanes).toHaveCount(4)
  await expect(page.getByRole('heading', { name: 'In work', exact: true })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'In review', exact: true })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Awaiting signature', exact: true })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Approved', exact: true })).toBeVisible()
  await expect(page.locator('[data-lane="work"]')).toContainText('Draft system change')
  await expect(page.locator('[data-lane="work"]')).toContainText('Open problem report')
  await expect(page.locator('[data-lane="review"]')).toContainText('Parallel review change')
  await expect(page.locator('[data-lane="review"]')).toContainText('Interface change request')
  await expect(page.locator('[data-lane="sign"]')).toContainText('Assessment of HLRCR-00002.00')
  await expect(page.locator('[data-lane="approved"]')).toContainText('Deferred test review')

  const parallelCard = page.getByRole('link', { name: /Parallel review change/ })
  await expect(parallelCard).toContainText('API Alice')
  await expect(parallelCard).toContainText('API Bob')
  await expect(parallelCard).toContainText('Active review obligation')
  await expect(parallelCard.locator('.teamWorkLanePill')).toContainText('In review')
  await expect(parallelCard.locator('.teamWorkLanePill')).toContainText('→')
  await expect(parallelCard).toHaveAttribute('href', '/open/change-request/00000000-0000-0000-0000-000000000002')
  const interfaceCard = page.getByRole('link', { name: /Interface change request/ })
  await expect(interfaceCard.locator('[data-family="interface"]')).toHaveText('ICDCR')
  await expect(interfaceCard).toContainText('ICDCR-00005.00')
  await expect(interfaceCard).toContainText('historical-author')
  await expect(interfaceCard).not.toContainText('Author action ·')
  await expect(interfaceCard).toHaveAttribute('href', '/open/change-request/00000000-0000-0000-0000-000000000005')
  const problemReportCard = page.getByRole('link', { name: /Open problem report/ })
  await expect(problemReportCard.locator('[data-family="problemReport"]')).toHaveText('Problem Report')
  await expect(problemReportCard).toContainText('PR00006.00')
  await expect(problemReportCard).toContainText('historical-reporter')
  await expect(problemReportCard).not.toContainText('Reported by ·')
  await expect(problemReportCard).toContainText('No current holder')
  await expect(problemReportCard).toContainText('Responsible engineer obligation')
  await expect(problemReportCard).toHaveAttribute('href', '/open/problem-report/00000000-0000-0000-0000-000000000006')
  const unheldCard = page.getByRole('link', { name: /Draft system change/ })
  await expect(unheldCard).toContainText('No current holder')
  await expect(unheldCard).toContainText('Author action')
  await expect(unheldCard).not.toContainText(/Reviewer|Approver|lead/i)
  await expect(page.getByRole('link', { name: /Assessment of HLRCR-00002.00/ })).toContainText('Build 1.5')
  await expect(page.getByRole('link', { name: /Assessment of HLRCR-00002.00/ })).toContainText('HLR assessment')
  await expect(page.getByRole('link', { name: /Assessment of HLRCR-00002.00/ })).not.toContainText('ASMT-')
  const deferredCard = page.getByRole('link', { name: /Deferred test review/ })
  await expect(deferredCard).toContainText('Deferred')
  await expect(deferredCard).toContainText('Allocation')
  await expect(deferredCard).toContainText('Build 1.5')
  await expect(page.locator('main.teamWorkPage')).toContainText('Last updated')
  await expect(page.getByRole('group', { name: 'Group by' })).toBeVisible()
  await expect(page.getByRole('group', { name: 'Build' })).toBeVisible()
  await expect(page.getByRole('group', { name: 'Record type' })).toBeVisible()
  await expect(page.locator('main.teamWorkPage').getByText(/\bdrag\b|\breassign\b|\bdue date\b|\bage in state\b/i)).toHaveCount(0)
  if (process.env.AEROLINK_TEAM_WORK_SCREENSHOT)
    await page.screenshot({ path: process.env.AEROLINK_TEAM_WORK_SCREENSHOT, fullPage: true })
})

test('Team Work preserves a numbered automatic TCR whose title was not persisted', async ({ page }) => {
  const automaticTcr = {
    id: '00000000-0000-0000-0000-000000000007', family: 'verification', category: 'system', prefix: 'SYSTPCR', number: 'SYSTPCR-00007.00',
    title: '', lane: 'work', nativeState: 'Draft', nativeOutcome: 'Pending', currentHolderIds: [], holderBasis: 'assignedEngineer',
    raisedById: null, raisedByKind: 'problemReport', release: { id: '00000000-0000-0000-0000-0000000000a1', version: '1.6', isReleased: false }, deferred: false,
    allocation: null, deferredFromState: null, activeStageObligations: [], updatedAt: '2026-08-29T08:00:00Z', openUrl: '/open/test-change-request/00000000-0000-0000-0000-000000000007',
  }
  await openTeamWork(page, {
    generatedAt: fixture.generatedAt,
    totals: { items: 1, returned: 1, unheld: 1 },
    people: [],
    items: [automaticTcr],
  })
  const card = page.getByRole('link', { name: /SYSTPCR-00007.00/ })
  await expect(card).toContainText('Title not recorded')
  await expect(card).toContainText('Assigned engineer obligation')
  await expect(card).toContainText('source problem report')
})

test('Team Work surfaces API failures locally and does not invent a card for an invalid projection', async ({ page }) => {
  const calls = await openTeamWork(page, { generatedAt: '2026-08-30T00:00:00Z', totals: { items: 1, returned: 1, unheld: 0 }, people: [], items: [{ ...fixture.items[0], lane: 'unknown', openUrl: '' }] })
  expect(calls.length).toBeGreaterThanOrEqual(1)
  await expect(page.getByRole('alert')).toContainText('Team Work could not be displayed')
  await expect(page.getByRole('alert')).toContainText('unknown family, lane, holder basis')
  await expect(page.getByRole('link', { name: /Draft system change/ })).toHaveCount(0)
})

test('Team Work fails closed for malformed people and unresolved current holders', async ({ page }) => {
  let body: unknown = {
    ...fixture,
    people: fixture.people.map((person, index) => index === 0 ? { ...person, displayName: '' } : person),
  }
  await page.route('**/api/team-work*', async route =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) }))
  await login(page)
  await page.getByRole('link', { name: 'Team Work', exact: true }).click()
  await expect(page.getByRole('alert')).toContainText('invalid identity')

  body = {
    ...fixture,
    items: fixture.items.map(item => item.title === 'Parallel review change'
      ? { ...item, currentHolderIds: ['not-a-person'] }
      : item),
  }
  await page.reload()
  await expect(page.getByRole('alert')).toContainText('unknown family, lane, holder basis')
})

test('Team Work rejects incomplete controlled identity and fabricated assessment identity', async ({ page }) => {
  let body: unknown = {
    ...fixture,
    items: fixture.items.map(item => item.title === 'Interface change request'
      ? { ...item, title: undefined }
      : item),
  }
  await page.route('**/api/team-work*', async route =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) }))
  await login(page)
  await page.getByRole('link', { name: 'Team Work', exact: true }).click()
  await expect(page.getByRole('alert')).toContainText('invalid identity')

  body = {
    ...fixture,
    items: fixture.items.map(item => item.title === 'Interface change request'
      ? { ...item, number: null }
      : item),
  }
  await page.reload()
  await expect(page.getByRole('alert')).toContainText('invalid identity')

  body = {
    ...fixture,
    items: fixture.items.map(item => item.family === 'assessment'
      ? { ...item, prefix: 'ASMT', number: 'ASMT-00001.00' }
      : item),
  }
  await page.reload()
  await expect(page.getByRole('alert')).toContainText('invalid identity')

  body = {
    ...fixture,
    items: fixture.items.map(item => item.family === 'assessment'
      ? { ...item, openUrl: `/open/change-request/${item.id}` }
      : item),
  }
  await page.reload()
  await expect(page.getByRole('alert')).toContainText('Team Work could not be displayed')
})

test('Team Work ignores a stale failed projection after changing projects', async ({ page }) => {
  const projectBId = '00000000-0000-0000-0000-0000000000d1'
  const releaseBId = '00000000-0000-0000-0000-0000000000e1'
  let programId = ''
  let projectAId = ''
  let staleFailureRelease: (() => void) | undefined
  let staleRequestSeen: (() => void) | undefined
  const staleFailure = new Promise<void>(resolve => { staleFailureRelease = resolve })
  const staleRequest = new Promise<void>(resolve => { staleRequestSeen = resolve })

  await page.route('**/api/workspaces', async route => {
    const upstream = await route.fetch()
    const workspaces = await upstream.json() as Array<{
      program: { id: string }
      projects: Array<{ project: Record<string, unknown>; releases: Array<Record<string, unknown>> }>
    }>
    const firstWorkspace = workspaces[0]
    const firstProject = firstWorkspace?.projects[0]
    if (!firstWorkspace || !firstProject) throw new Error('The seeded workspace did not contain a project.')
    programId = firstWorkspace.program.id
    projectAId = String(firstProject.project.id)
    const releases = firstProject.releases.length
      ? firstProject.releases.map((release, index) => ({
        ...release,
        id: index === 0 ? releaseBId : `00000000-0000-0000-0000-${String(index + 15).padStart(12, '0')}`,
      }))
      : [{ id: releaseBId, version: '1.6', isReleased: false }]
    const transitionProject = {
      ...firstProject,
      project: { ...firstProject.project, id: projectBId, name: 'Transition Project' },
      releases,
    }
    const next = workspaces.map((workspace, index) => index === 0
      ? { ...workspace, projects: [...workspace.projects, transitionProject] }
      : workspace)
    await route.fulfill({ response: upstream, body: JSON.stringify(next) })
  })
  await page.route('**/api/team-work*', async route => {
    const projectId = new URL(route.request().url()).searchParams.get('projectId')
    if (projectId === projectAId) {
      staleRequestSeen?.()
      await staleFailure
      try { await route.fulfill({ status: 503, body: 'stale failure' }) } catch { /* the browser may have aborted this route */ }
      return
    }
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ ...fixture, items: fixture.items.map((item, index) => index === 0 ? { ...item, title: 'Transition project item' } : item) }),
    })
  })

  await login(page)
  await page.getByRole('link', { name: 'Team Work', exact: true }).click()
  await staleRequest
  await page.evaluate(path => {
    history.pushState({}, '', path)
    dispatchEvent(new PopStateEvent('popstate'))
  }, `/programs/${programId}/projects/${projectBId}/releases/${releaseBId}/team-work`)
  await expect(page.getByRole('link', { name: /Transition project item/ })).toBeVisible()
  staleFailureRelease?.()
  await expect(page.getByRole('alert')).toHaveCount(0)
})

test('Team Work reports an authorized project with no active items without rendering fake lanes or cards', async ({ page }) => {
  await openTeamWork(page, { generatedAt: '2026-08-30T00:00:00Z', totals: { items: 0, returned: 0, unheld: 0 }, people: [], items: [] })
  await expect(page.getByText('No controlled work is recorded in this project yet.')).toBeVisible()
  await expect(page.locator('[data-team-work-board="true"]')).toHaveCount(0)
})

test('Team Work is available from the normal command palette after My Work', async ({ page }) => {
  await openTeamWork(page)
  await page.getByRole('button', { name: /Search & navigate/ }).click()
  const entries = page.getByRole('dialog', { name: 'Quick navigation' }).locator('.paletteGroup').filter({ hasText: 'SUGGESTED WORKSPACES' }).getByRole('link')
  await expect(entries.nth(0)).toHaveText(/Command Center/)
  await expect(entries.nth(1)).toHaveText(/My Work/)
  await expect(entries.nth(2)).toHaveText(/Team Work/)
})

test('Team Work rejects an unknown holder basis and reports a true fetch failure locally', async ({ page }) => {
  await openTeamWork(page, { generatedAt: '2026-08-30T00:00:00Z', totals: { items: 1, returned: 1, unheld: 0 }, people: [], items: [{ ...fixture.items[0], holderBasis: 'Reviewer' }] })
  await expect(page.getByRole('alert')).toContainText('unknown family, lane, holder basis')

  await page.unroute('**/api/team-work*')
  await page.route('**/api/team-work*', async route => route.fulfill({ status: 503, body: 'unavailable' }))
  await page.reload()
  await expect(page.getByRole('alert')).toContainText('Team Work could not be displayed')
  await expect(page.getByRole('alert')).toContainText('Team Work is unavailable.')
})

test('Team Work keeps a board-shaped loading skeleton while the project projection is pending', async ({ page }) => {
  let releaseResponse: (() => void) | undefined
  let requestStarted: (() => void) | undefined
  const responseHeld = new Promise<void>(resolve => { releaseResponse = resolve })
  const requestArrived = new Promise<void>(resolve => { requestStarted = resolve })
  await page.route('**/api/team-work*', async route => {
    requestStarted?.()
    await responseHeld
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(fixture) })
  })
  await login(page)
  await page.getByRole('link', { name: 'Team Work', exact: true }).click()
  await requestArrived
  await expect(page.locator('main.teamWorkPage[aria-busy="true"]')).toBeVisible()
  await expect(page.locator('.teamWorkBoardLoading .teamWorkLane')).toHaveCount(4)
  releaseResponse?.()
  await expect(page.getByRole('heading', { name: 'Team Work', exact: true })).toBeVisible()
})

test('Team Work derives builds and type counts without duplicating parallel items', async ({ page }) => {
  await openTeamWork(page)
  await expect(page.getByRole('button', { name: 'Build 1.6', exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Build 1.5', exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Deferred', exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: /Software \(1\)/ })).toBeVisible()

  await page.getByRole('button', { name: 'Current holder', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'No current holder', exact: true })).toBeVisible()
  const parallelCards = page.locator('.teamWorkHolderGroup').filter({ hasText: 'Parallel review change' }).locator('[data-team-work-card="true"]')
  await expect(parallelCards).toHaveCount(2)
  await page.getByRole('button', { name: 'Build 1.5', exact: true }).click()
  await expect(page.locator('[data-team-work-card="true"]')).toHaveCount(1)
  await expect(page.getByText('Assessment of HLRCR-00002.00')).toBeVisible()
  await expect(page.getByText('Deferred test review')).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Verification (0)', exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Deferred', exact: true }).click()
  await expect(page.getByText('Deferred test review')).toBeVisible()
  await expect(page.getByText('Assessment of HLRCR-00002.00')).toHaveCount(0)
})

test('Team Work searches people and opens a holder drawer with shared-item facts', async ({ page }) => {
  await openTeamWork(page)
  const search = page.getByRole('textbox', { name: 'Search' })
  await search.fill('API Alice')
  await expect(page.getByRole('button', { name: /API Alice/ })).toBeVisible()
  await expect(page.locator('[data-team-work-card="true"]')).toHaveCount(1)
  await search.fill('')
  await page.getByRole('button', { name: /API Alice/ }).click()
  const drawer = page.getByRole('dialog', { name: 'API Alice' })
  await expect(drawer).toBeVisible()
  await expect(drawer.getByText('Currently holds').locator('..')).toContainText('1')
  await expect(drawer.getByText('Shared with others').locator('..')).toContainText('1')
  await expect(drawer.getByRole('link', { name: /Parallel review change/ })).toHaveAttribute('href', '/open/change-request/00000000-0000-0000-0000-000000000002')
  if (process.env.AEROLINK_TEAM_WORK_DRAWER_SCREENSHOT)
    await page.screenshot({ path: process.env.AEROLINK_TEAM_WORK_DRAWER_SCREENSHOT, fullPage: true })
  await expect(page).toHaveURL(/holder=alice/)
  await drawer.getByRole('button', { name: 'Close current holder' }).click()
  await expect(page.getByRole('dialog')).toHaveCount(0)
  await expect(page).not.toHaveURL(/holder=/)
})

test('Team Work retains zero-hold members, scopes affinity storage, and exposes the exact zero-holder empty state', async ({ page }) => {
  const zero = { userId: '00000000-0000-0000-0000-0000000000ae', userName: 'erin', displayName: 'API Erin', isCurrentProjectMember: true, accountState: 'locked', baseRoles: ['SoftwareTestEngineer'], disciplineAffinities: ['software'], holds: 0, byLane: { work: 0, review: 0, sign: 0, approved: 0 } }
  const body = { ...fixture, totals: { ...fixture.totals, unheld: 3 }, people: [...fixture.people, zero] }
  await page.addInitScript(() => localStorage.setItem('aerolink-teamwork-affinity', '{malformed'))
  await openTeamWork(page, body)
  await expect(page.getByRole('button', { name: /API Erin/ })).toContainText('Account locked')
  await page.getByRole('button', { name: /API Erin/ }).click()
  const drawer = page.getByRole('dialog', { name: 'API Erin' })
  await expect(drawer.getByText('Nothing currently requires API Erin.', { exact: true })).toBeVisible()
  await expect(drawer).toBeVisible()
  const stored = await page.evaluate(() => JSON.parse(localStorage.getItem('aerolink-teamwork-affinity') || '{}'))
  expect(stored.version).toBe(1)
  expect(Object.keys(stored.viewers)).toHaveLength(1)
  expect(Object.keys(stored.viewers[Object.keys(stored.viewers)[0]])).toHaveLength(1)
})

test('Team Work has a distinct project-empty state while still showing its member strip', async ({ page }) => {
  const zero = { userId: '00000000-0000-0000-0000-0000000000af', userName: 'project.member', displayName: 'Project Member', isCurrentProjectMember: true, accountState: 'disabled', baseRoles: [], disciplineAffinities: [], holds: 0, byLane: { work: 0, review: 0, sign: 0, approved: 0 } }
  await openTeamWork(page, { generatedAt: fixture.generatedAt, totals: { items: 0, returned: 0, unheld: 0 }, people: [zero], items: [] })
  await expect(page.getByRole('heading', { name: 'People', exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: /Project Member/ })).toBeVisible()
  await expect(page.getByText('No controlled work is recorded in this project yet.')).toBeVisible()
})

test('Team Work validates active obligation uniqueness and invalid holder URL state', async ({ page }) => {
  const body = { ...fixture, items: fixture.items.map(item => item.title === 'Parallel review change' ? { ...item, activeStageObligations: [{ holderId: 'alice', stageKind: 'review' }, { holderId: 'alice', stageKind: 'review' }] } : item) }
  await openTeamWork(page, body)
  await expect(page.getByRole('alert')).toContainText('invalid identity, lifecycle, holder obligation')
  await page.unroute('**/api/team-work*')
  await page.route('**/api/team-work*', async route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(fixture) }))
  await page.evaluate(() => { const url = new URL(location.href); url.searchParams.set('holder', 'not-a-person'); history.replaceState({}, '', url); location.reload() })
  await expect(page.getByRole('heading', { name: 'Team Work', exact: true })).toBeVisible()
  await expect(page).not.toHaveURL(/holder=not-a-person/)
})

test('Team Work attributes mixed frozen Review and Approval obligations to the right holder', async ({ page }) => {
  const people = fixture.people.map(person => person.userName === 'alice' || person.userName === 'bob'
    ? { ...person, byLane: { work: 0, review: 0, sign: 1, approved: 0 } }
    : person)
  const items = fixture.items.map(item => item.title === 'Parallel review change'
    ? {
        ...item,
        lane: 'sign',
        holderBasis: 'activeReviewAndApprovalStages',
        activeStageObligations: [
          { holderId: 'alice', stageKind: 'review' },
          { holderId: 'bob', stageKind: 'approval' },
        ],
      }
    : item)
  await openTeamWork(page, { ...fixture, people, items })

  await page.getByRole('button', { name: /API Alice/ }).click()
  let drawer = page.getByRole('dialog', { name: 'API Alice' })
  await expect(drawer.getByText('Awaiting their signature').locator('..')).toContainText('0')
  await drawer.getByRole('button', { name: 'Close current holder' }).click()

  await page.getByRole('button', { name: /API Bob/ }).click()
  drawer = page.getByRole('dialog', { name: 'API Bob' })
  await expect(drawer.getByText('Awaiting their signature').locator('..')).toContainText('1')
  await expect(drawer.getByText('Also API Alice')).toBeVisible()
})

test('Team Work composes filters against unique items and clear preserves holder grouping', async ({ page }) => {
  await openTeamWork(page)
  await page.getByRole('button', { name: 'Current holder', exact: true }).click()
  await page.getByRole('button', { name: 'Build 1.5', exact: true }).click()
  await page.getByRole('button', { name: 'Software (0)', exact: true }).click()
  const empty = page.locator('.teamWorkFilteredEmpty')
  await expect(empty).toContainText('No Software on Build 1.5')
  await empty.getByRole('button', { name: 'Clear filters' }).click()
  await expect(page.getByRole('button', { name: 'Current holder', exact: true })).toHaveClass(/active/)
  await expect(page.locator('[data-team-work-card="true"]')).toHaveCount(7)
})

test('Team Work drawer traps focus, restores its trigger, preserves query state, and follows history', async ({ page }) => {
  await openTeamWork(page)
  await page.evaluate(() => history.replaceState({}, '', `${location.pathname}?mode=keep`))
  const alice = page.getByRole('button', { name: /API Alice/ })
  await alice.click()
  let drawer = page.getByRole('dialog', { name: 'API Alice' })
  const close = drawer.getByRole('button', { name: 'Close current holder' })
  await expect(close).toBeFocused()
  await page.keyboard.press('Shift+Tab')
  await expect(drawer.getByRole('link', { name: /Parallel review change/ })).toBeFocused()
  await page.keyboard.press('Escape')
  await expect(drawer).toHaveCount(0)
  await expect(alice).toBeFocused()
  await expect(page).toHaveURL(/mode=keep/)
  await expect(page).not.toHaveURL(/holder=/)

  await alice.click()
  await page.locator('.teamWorkDrawerBackdrop').click({ position: { x: 5, y: 5 } })
  await expect(page.getByRole('dialog')).toHaveCount(0)
  await alice.click()
  await page.getByRole('dialog').getByRole('button', { name: 'Close current holder' }).click()
  await page.goBack()
  await expect(page.getByRole('dialog', { name: 'API Alice' })).toBeVisible()
  await page.goBack()
  await expect(page.getByRole('dialog')).toHaveCount(0)
  await expect(page).toHaveURL(/mode=keep/)
})

test('Team Work scopes deterministic modern-discipline affinity by viewer, project, and user id', async ({ page }) => {
  let body: unknown = fixture
  await page.route('**/api/team-work*', async route =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) }))
  await login(page)
  const identityResponse = await page.request.get(`${apiBase}/api/auth/me`)
  expect(identityResponse.ok(), await identityResponse.text()).toBeTruthy()
  const me = await identityResponse.json() as { id: string; userName: string; displayName: string }
  const projectId = locationProjectId(page.url())
  const viewer = {
    userId: me.id,
    userName: me.userName,
    displayName: me.displayName,
    isCurrentProjectMember: true,
    accountState: 'active',
    baseRoles: ['SystemEngineer'],
    disciplineAffinities: ['system'],
    holds: 0,
    byLane: { work: 0, review: 0, sign: 0, approved: 0 },
  }
  body = { ...fixture, people: [viewer, ...fixture.people] }
  await page.evaluate(({ viewerId, project, aliceId, danaId }) => {
    localStorage.setItem('aerolink-teamwork-affinity', JSON.stringify({
      version: 1,
      viewers: {
        [viewerId]: { [project]: { [danaId]: 9, [aliceId]: 2 } },
        '00000000-0000-0000-0000-000000000099': { [project]: { [aliceId]: 999 } },
      },
    }))
  }, {
    viewerId: me.id,
    project: projectId,
    aliceId: fixture.people[0].userId,
    danaId: fixture.people[3].userId,
  })
  await page.getByRole('link', { name: 'Team Work', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Team Work', exact: true })).toBeVisible()
  const names = await page.locator('.teamWorkPeopleStrip .teamWorkPerson strong').allTextContents()
  expect(names.slice(0, 3)).toEqual([`${me.displayName} (you)`, 'API Dana', 'API Alice'])
  await page.getByRole('button', { name: /API Alice/ }).click()
  const stored = await page.evaluate(() => JSON.parse(localStorage.getItem('aerolink-teamwork-affinity') || '{}'))
  expect(stored.viewers[me.id][projectId][fixture.people[0].userId]).toBe(3)
})

test('Team Work rejects retired role vocabulary and fabricated stage provenance', async ({ page }) => {
  const retiredRole = {
    ...fixture,
    people: fixture.people.map((person, index) => index === 0
      ? { ...person, baseRoles: ['Reviewer'], disciplineAffinities: [] }
      : person),
  }
  await openTeamWork(page, retiredRole)
  await expect(page.getByRole('alert')).toContainText('invalid identity, account state, roles, affinity')

  await page.unroute('**/api/team-work*')
  const fabricatedAssessment = {
    ...fixture,
    items: fixture.items.map(item => item.family === 'assessment'
      ? { ...item, activeStageObligations: [{ holderId: 'charlie', stageKind: 'approval' }] }
      : item),
  }
  await page.route('**/api/team-work*', async route =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(fabricatedAssessment) }))
  await page.reload()
  await expect(page.getByRole('alert')).toContainText('invalid identity, lifecycle, holder obligation')
})

function locationProjectId(url: string) {
  const match = url.match(/\/projects\/([0-9a-f-]{36})\//i)
  if (!match) throw new Error(`Project id was missing from ${url}`)
  return match[1]
}
