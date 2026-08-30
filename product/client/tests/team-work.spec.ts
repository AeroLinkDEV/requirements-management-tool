import { expect, test } from '@playwright/test'
import { login } from './auth'

const fixture = {
  generatedAt: '2026-08-30T00:00:00Z',
  totals: { items: 4, returned: 4, unheld: 2 },
  people: [
    { userName: 'alice', displayName: 'API Alice', holds: 1, byLane: { work: 0, review: 1, sign: 0, approved: 0 } },
    { userName: 'bob', displayName: 'API Bob', holds: 1, byLane: { work: 0, review: 1, sign: 0, approved: 0 } },
    { userName: 'charlie', displayName: 'API Charlie', holds: 1, byLane: { work: 0, review: 0, sign: 1, approved: 0 } },
  ],
  items: [
    {
      id: '00000000-0000-0000-0000-000000000001', family: 'system', category: 'system', prefix: 'SRCR', number: 'SRCR-00001.00',
      title: 'Draft system change', lane: 'work', nativeState: 'Draft', nativeOutcome: null, currentHolderIds: [], holderBasis: 'none',
      raisedById: 'legacy-author', raisedByKind: 'author', release: { id: 'release-a', version: '1.6', isReleased: false }, deferred: false,
      allocation: null, deferredFromState: null, updatedAt: '2026-08-29T12:00:00Z', openUrl: '/open/change-request/00000000-0000-0000-0000-000000000001',
    },
    {
      id: '00000000-0000-0000-0000-000000000002', family: 'software', category: 'HLR', prefix: 'HLRCR', number: 'HLRCR-00002.00',
      title: 'Parallel review change', lane: 'review', nativeState: 'InReview', nativeOutcome: null, currentHolderIds: ['alice', 'bob'], holderBasis: 'activeReviewStage',
      raisedById: null, raisedByKind: null, release: { id: 'release-a', version: '1.6', isReleased: false }, deferred: false,
      allocation: null, deferredFromState: null, updatedAt: '2026-08-29T11:00:00Z', openUrl: '/open/change-request/00000000-0000-0000-0000-000000000002',
    },
    {
      id: '00000000-0000-0000-0000-000000000003', family: 'assessment', category: 'HLR assessment', prefix: null, number: null,
      title: 'Assessment of HLRCR-00002.00', lane: 'sign', nativeState: 'InReview', nativeOutcome: 'Pending', currentHolderIds: ['charlie'], holderBasis: 'selectedAssessmentApprover',
      raisedById: 'source-change-id', raisedByKind: 'changeRequest', release: { id: 'release-b', version: '1.5', isReleased: true }, deferred: false,
      allocation: null, deferredFromState: null, updatedAt: '2026-08-29T10:00:00Z', openUrl: '/open/downstream-assessment/00000000-0000-0000-0000-000000000003',
    },
    {
      id: '00000000-0000-0000-0000-000000000004', family: 'verification', category: 'system', prefix: 'SYSTPCR', number: 'SYSTPCR-00004.00',
      title: 'Deferred test review', lane: 'approved', nativeState: 'Deferred', nativeOutcome: null, currentHolderIds: [], holderBasis: 'none',
      raisedById: null, raisedByKind: null, release: { id: 'release-a', version: '1.6', isReleased: false }, deferred: true,
      allocation: { baselineId: 'baseline-a', releaseId: 'release-b', releaseVersion: '1.5', baselineNumber: 'BL-00001', baselineRevision: 0, isReleased: true },
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
  await expect(stats.getByText('Unique items').locator('..')).toContainText('4')
  await expect(stats.getByText('People holding work').locator('..')).toContainText('3')
  await expect(stats.getByText('No current holder').locator('..')).toContainText('2')

  const lanes = page.locator('[data-team-work-board="true"] [data-lane]')
  await expect(lanes).toHaveCount(4)
  await expect(page.getByRole('heading', { name: 'In work', exact: true })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'In review', exact: true })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Awaiting signature', exact: true })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Approved', exact: true })).toBeVisible()
  await expect(page.locator('[data-lane="work"]')).toContainText('Draft system change')
  await expect(page.locator('[data-lane="review"]')).toContainText('Parallel review change')
  await expect(page.locator('[data-lane="sign"]')).toContainText('Assessment of HLRCR-00002.00')
  await expect(page.locator('[data-lane="approved"]')).toContainText('Deferred test review')

  const parallelCard = page.getByRole('link', { name: /Parallel review change/ })
  await expect(parallelCard).toContainText('API Alice')
  await expect(parallelCard).toContainText('API Bob')
  await expect(parallelCard).toContainText('Active review obligation')
  await expect(parallelCard.locator('.teamWorkLanePill')).toContainText('In review')
  await expect(parallelCard.locator('.teamWorkLanePill')).toContainText('→')
  await expect(parallelCard).toHaveAttribute('href', '/open/change-request/00000000-0000-0000-0000-000000000002')
  const unheldCard = page.getByRole('link', { name: /Draft system change/ })
  await expect(unheldCard).toContainText('No current holder')
  await expect(unheldCard).not.toContainText('legacy-author')
  await expect(unheldCard).not.toContainText(/Reviewer|Approver|lead/i)
  await expect(page.getByRole('link', { name: /Assessment of HLRCR-00002.00/ })).toContainText('Build 1.5')
  await expect(page.getByRole('link', { name: /Assessment of HLRCR-00002.00/ })).toContainText('HLR assessment')
  await expect(page.getByRole('link', { name: /Assessment of HLRCR-00002.00/ })).not.toContainText('ASMT-')
  await expect(page.getByRole('link', { name: /Deferred test review/ })).toContainText('Deferred')
  await expect(page.getByRole('link', { name: /Deferred test review/ })).toContainText('Allocation')
  await expect(page.getByRole('link', { name: /Deferred test review/ })).toContainText('Build 1.5')
  await expect(page.locator('main.teamWorkPage')).toContainText('Last updated')
  await expect(page.locator('main.teamWorkPage button')).toHaveCount(0)
  await expect(page.locator('main.teamWorkPage').getByText(/filter|drag|reassign|due date|age/i)).toHaveCount(0)
  if (process.env.AEROLINK_TEAM_WORK_SCREENSHOT)
    await page.screenshot({ path: process.env.AEROLINK_TEAM_WORK_SCREENSHOT, fullPage: true })
})

test('Team Work surfaces API failures locally and does not invent a card for an invalid projection', async ({ page }) => {
  const calls = await openTeamWork(page, { generatedAt: '2026-08-30T00:00:00Z', totals: { items: 1, returned: 1, unheld: 0 }, people: [], items: [{ ...fixture.items[0], lane: 'unknown', openUrl: '' }] })
  expect(calls.length).toBeGreaterThanOrEqual(1)
  await expect(page.getByRole('alert')).toContainText('Team Work could not be displayed')
  await expect(page.getByRole('alert')).toContainText('unknown family, lane, holder basis')
  await expect(page.getByRole('link', { name: /Draft system change/ })).toHaveCount(0)
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
