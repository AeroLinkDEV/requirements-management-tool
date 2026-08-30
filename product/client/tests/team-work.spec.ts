import { expect, test } from '@playwright/test'
import { login } from './auth'

const fixture = {
  generatedAt: '2026-08-30T00:00:00Z',
  totals: { items: 6, returned: 6, unheld: 3 },
  people: [
    { userName: 'alice', displayName: 'API Alice', holds: 1, byLane: { work: 0, review: 1, sign: 0, approved: 0 } },
    { userName: 'bob', displayName: 'API Bob', holds: 1, byLane: { work: 0, review: 1, sign: 0, approved: 0 } },
    { userName: 'charlie', displayName: 'API Charlie', holds: 1, byLane: { work: 0, review: 0, sign: 1, approved: 0 } },
    { userName: 'dana', displayName: 'API Dana', holds: 1, byLane: { work: 0, review: 1, sign: 0, approved: 0 } },
  ],
  items: [
    {
      id: '00000000-0000-0000-0000-000000000001', family: 'system', category: 'system', prefix: 'SRCR', number: 'SRCR-00001.00',
      title: 'Draft system change', lane: 'work', nativeState: 'Draft', nativeOutcome: null, currentHolderIds: [], holderBasis: 'author',
      raisedById: null, raisedByKind: null, release: { id: '00000000-0000-0000-0000-0000000000a1', version: '1.6', isReleased: false }, deferred: false,
      allocation: null, deferredFromState: null, updatedAt: '2026-08-29T12:00:00Z', openUrl: '/open/change-request/00000000-0000-0000-0000-000000000001',
    },
    {
      id: '00000000-0000-0000-0000-000000000002', family: 'software', category: 'HLR', prefix: 'HLRCR', number: 'HLRCR-00002.00',
      title: 'Parallel review change', lane: 'review', nativeState: 'InReview', nativeOutcome: null, currentHolderIds: ['alice', 'bob'], holderBasis: 'activeReviewStage',
      raisedById: null, raisedByKind: null, release: { id: '00000000-0000-0000-0000-0000000000a1', version: '1.6', isReleased: false }, deferred: false,
      allocation: null, deferredFromState: null, updatedAt: '2026-08-29T11:00:00Z', openUrl: '/open/change-request/00000000-0000-0000-0000-000000000002',
    },
    {
      id: '00000000-0000-0000-0000-000000000005', family: 'interface', category: 'interface', prefix: 'ICDCR', number: 'ICDCR-00005.00',
      title: 'Interface change request', lane: 'review', nativeState: 'InReview', nativeOutcome: null, currentHolderIds: ['dana'], holderBasis: 'activeReviewStage',
      raisedById: 'historical-author', raisedByKind: 'author', release: { id: '00000000-0000-0000-0000-0000000000a1', version: '1.6', isReleased: false }, deferred: false,
      allocation: null, deferredFromState: null, updatedAt: '2026-08-29T10:30:00Z', openUrl: '/open/change-request/00000000-0000-0000-0000-000000000005',
    },
    {
      id: '00000000-0000-0000-0000-000000000006', family: 'problemReport', category: null, prefix: null, number: 'PR00006.00',
      title: 'Open problem report', lane: 'work', nativeState: 'Open', nativeOutcome: null, currentHolderIds: [], holderBasis: 'responsibleEngineer',
      raisedById: 'historical-reporter', raisedByKind: 'reportedBy', release: null, deferred: false,
      allocation: null, deferredFromState: null, updatedAt: '2026-08-29T10:15:00Z', openUrl: '/open/problem-report/00000000-0000-0000-0000-000000000006',
    },
    {
      id: '00000000-0000-0000-0000-000000000003', family: 'assessment', category: 'HLR assessment', prefix: null, number: null,
      title: 'Assessment of HLRCR-00002.00', lane: 'sign', nativeState: 'InReview', nativeOutcome: 'Pending', currentHolderIds: ['charlie'], holderBasis: 'selectedAssessmentApprover',
      raisedById: 'source-change-id', raisedByKind: 'changeRequest', release: { id: '00000000-0000-0000-0000-0000000000b1', version: '1.5', isReleased: true }, deferred: false,
      allocation: null, deferredFromState: null, updatedAt: '2026-08-29T10:00:00Z', openUrl: '/open/downstream-assessment/00000000-0000-0000-0000-000000000003',
    },
    {
      id: '00000000-0000-0000-0000-000000000004', family: 'verification', category: 'system', prefix: 'SYSTPCR', number: 'SYSTPCR-00004.00',
      title: 'Deferred test review', lane: 'approved', nativeState: 'Deferred', nativeOutcome: null, currentHolderIds: [], holderBasis: 'none',
      raisedById: null, raisedByKind: null, release: { id: '00000000-0000-0000-0000-0000000000a1', version: '1.6', isReleased: false }, deferred: true,
      allocation: { baselineId: '00000000-0000-0000-0000-0000000000c1', releaseId: '00000000-0000-0000-0000-0000000000b1', releaseVersion: '1.5', baselineNumber: 'BL-00001', baselineRevision: 0, isReleased: true },
      deferredFromState: 'Approved', updatedAt: '2026-08-29T09:00:00Z', openUrl: '/open/test-change-request/00000000-0000-0000-0000-000000000004',
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
  await expect(page.getByText('Project scope · every build')).toBeVisible()
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
  await expect(page.locator('main.teamWorkPage button')).toHaveCount(0)
  await expect(page.locator('main.teamWorkPage').getByText(/filter|drag|reassign|due date|age/i)).toHaveCount(0)
  if (process.env.AEROLINK_TEAM_WORK_SCREENSHOT)
    await page.screenshot({ path: process.env.AEROLINK_TEAM_WORK_SCREENSHOT, fullPage: true })
})

test('Team Work preserves a numbered automatic TCR whose title was not persisted', async ({ page }) => {
  const automaticTcr = {
    id: '00000000-0000-0000-0000-000000000007', family: 'verification', category: 'system', prefix: 'SYSTPCR', number: 'SYSTPCR-00007.00',
    title: '', lane: 'work', nativeState: 'Draft', nativeOutcome: 'Pending', currentHolderIds: [], holderBasis: 'assignedEngineer',
    raisedById: null, raisedByKind: 'problemReport', release: { id: '00000000-0000-0000-0000-0000000000a1', version: '1.6', isReleased: false }, deferred: false,
    allocation: null, deferredFromState: null, updatedAt: '2026-08-29T08:00:00Z', openUrl: '/open/test-change-request/00000000-0000-0000-0000-000000000007',
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
