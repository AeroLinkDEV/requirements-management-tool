import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, login, showcaseSeed, writeRichField } from './auth'

/**
 * DEC-089: one Project-scoped Problem Report database, with build target as an explicit filter rather than
 * workspace ownership. Build-owned records keep their ordinary released-build policy; this journey proves
 * only the deliberate Problem Report exception.
 */
test('Problem Reports remain workable and explicitly target-filtered from every build', async ({ page, request }) => {
  test.setTimeout(240_000)
  await apiLogin(request)
  const showcase = await showcaseSeed(request)
  const stamp = Date.now()
  const title = `Project-scoped correction ${stamp}`
  const unassignedTitle = `Unassigned Project problem ${stamp}`
  const workaround = `Use the redundant channel until correction ${stamp} is released.`

  const targeted = await request.post(`${apiBase}/api/problem-reports`, {
    data: {
      category: 'CodeFunctional', projectId: showcase.projectId,
      releaseId: showcase.activeReleaseId,
      title,
      problem: 'Raised against Build 1.6 and corrected while the browser stands in Build 1.5.',
    },
  })
  expect(targeted.ok(), await targeted.text()).toBeTruthy()
  const targetedId = (await targeted.json()).id as string
  const unassigned = await request.post(`${apiBase}/api/problem-reports`, {
    data: {
      category: 'CodeFunctional', projectId: showcase.projectId,
      title: unassignedTitle,
      problem: 'No target build has been selected for this Project-scoped report.',
    },
  })
  expect(unassigned.ok(), await unassigned.text()).toBeTruthy()
  const unassignedId = (await unassigned.json()).id as string

  const openProblemReports = async () => {
    await page.getByRole('link', { name: 'Problem Reports' }).click()
    await expect(page.getByRole('heading', { name: 'Problem Report queue' })).toBeVisible({ timeout: 30_000 })
  }
  const targetBuild = () => page.getByLabel('Target build').first()
  const selectTargetBuild = async (value: string) => {
    const expected = value === 'unassigned'
      ? (url: URL) => url.searchParams.get('targetUnassigned') === 'true'
      : (url: URL) => url.searchParams.get('targetReleaseId') === value
    const [listResponse, dashboardResponse] = await Promise.all([
      page.waitForResponse(response => response.url().includes('/api/problem-reports?') && expected(new URL(response.url()))),
      page.waitForResponse(response => response.url().includes('/api/problem-reports/dashboard?') && expected(new URL(response.url()))),
      targetBuild().selectOption(value),
    ])
    expect(listResponse.ok(), await listResponse.text()).toBeTruthy()
    expect(dashboardResponse.ok(), await dashboardResponse.text()).toBeTruthy()
  }
  // The showcase Project is shared by every Problem Report journey, so another spec may legitimately
  // create a report between this spec's server responses and its DOM assertions. Those totals are
  // therefore nobody's to assert exactly — the frozen DOM snapshot can never converge with a list that
  // has since grown, and counting that as a product failure is the false red this journey exists to
  // avoid. The queue also pages at ten rows ordered by report number, so in a fully loaded sweep this
  // journey's freshly numbered records sit on a page no assertion can reach by default.
  // Both couplings are removed by narrowing the queue server-side to exactly this journey's records:
  // every title carries the unique stamp, so one search makes whatever the assertions need the whole list.
  const scopeQueueToOwnedRecords = async () => {
    await page.getByPlaceholder('Number, title, description, root cause').fill(String(stamp))
    await page.waitForResponse(response =>
      response.url().includes('/api/problem-reports?') &&
      new URL(response.url()).searchParams.get('search') === String(stamp))
  }
  // What the DEC-089 contract needs, and what only this journey owns, is that ITS record appears
  // exactly once under the target filter that owns it, from both workspace entry paths.
  const ensureOwnedRowsRendered = async () => {
    await expect(page.locator('.prList button').filter({ hasText: title })).toHaveCount(1)
  }

  await login(page)
  await openProblemReports()
  await scopeQueueToOwnedRecords()
  await selectTargetBuild(showcase.activeReleaseId)
  await ensureOwnedRowsRendered()
  await expect(page).toHaveURL(new RegExp(`targetBuild=${showcase.activeReleaseId}`))

  // Enter the released workspace. Its build-owned surfaces remain read-only, but the Project Problem Report
  // itself is governed by its own lifecycle, authority and lease. The same owned row must surface here:
  // entering through a different workspace must not change what a Project-scoped filter answers.
  await page.getByRole('button', { name: 'Back to Software Builds' }).click()
  await page.getByRole('button', { name: 'Open build 1.5' }).click()
  await openProblemReports()
  await expect(page.locator('.problemReportsPage').getByText('Released build · read-only')).toHaveCount(0)
  await scopeQueueToOwnedRecords()
  await selectTargetBuild(showcase.activeReleaseId)
  await ensureOwnedRowsRendered()

  await page.locator('.prList').getByText(title).click()
  await expect(page).toHaveURL(new RegExp(`${targetedId}.*targetBuild=${showcase.activeReleaseId}`))
  await page.getByRole('button', { name: 'Check out & edit' }).click()
  const editor = page.getByRole('dialog', { name: /^Edit PR-/ })
  await expect(editor).toBeVisible({ timeout: 30_000 })
  await writeRichField(editor, 'Workaround', workaround)
  await editor.getByRole('button', { name: 'Check in' }).click()
  await expect(editor).toHaveCount(0, { timeout: 30_000 })
  await expect(page.getByText(workaround)).toBeVisible({ timeout: 30_000 })

  // The same committed record opens from Build 1.6 without switching workspace to match the PR target.
  await page.getByRole('button', { name: 'Back to Software Builds' }).click()
  await page.getByRole('button', { name: 'Open build 1.6' }).click()
  await openProblemReports()
  await scopeQueueToOwnedRecords()
  await selectTargetBuild(showcase.activeReleaseId)
  await page.locator('.prList').getByText(title).click()
  await expect(page.getByText(workaround)).toBeVisible({ timeout: 30_000 })

  const releasedOption = await targetBuild().locator('option').filter({ hasText: 'released' }).getAttribute('value')
  expect(releasedOption).toBeTruthy()
  const releasedAnchorTitle = `Released target anchor ${stamp}`
  const releasedAnchor = await request.post(`${apiBase}/api/problem-reports`, {
    data: {
      category: 'CodeFunctional', projectId: showcase.projectId,
      releaseId: releasedOption,
      title: releasedAnchorTitle,
      problem: 'A released-build row exists so the queue has a deterministic fallback selection.',
    },
  })
  expect(releasedAnchor.ok(), await releasedAnchor.text()).toBeTruthy()
  const fallbackDetail = page.waitForResponse(response => {
    const url = new URL(response.url())
    return /\/api\/problem-reports\/[0-9a-f-]{36}$/i.test(url.pathname)
      && !url.pathname.endsWith(targetedId)
  })
  await selectTargetBuild(releasedOption!)
  const fallbackResponse = await fallbackDetail
  expect(fallbackResponse.ok(), await fallbackResponse.text()).toBeTruthy()
  await expect(page).not.toHaveURL(new RegExp(targetedId))
  await expect(page.locator('.prList').getByText(title)).toHaveCount(0)

  // Target filter state is addressable and follows browser history rather than silently following workspace.
  await page.goBack()
  await expect(targetBuild()).toHaveValue(showcase.activeReleaseId)
  await expect(page.locator('.prList').getByText(title)).toBeVisible({ timeout: 30_000 })
  await page.goForward()
  await expect(targetBuild()).toHaveValue(releasedOption!)
  await expect(page.locator('.prList').getByText(title)).toHaveCount(0)

  await selectTargetBuild('unassigned')
  await expect(page.locator('.prList').getByText(unassignedTitle)).toBeVisible({ timeout: 30_000 })
  await page.locator('.prList').getByText(unassignedTitle).click()
  await expect(page.getByRole('heading', { name: unassignedTitle })).toBeVisible({ timeout: 30_000 })
  await expect(page).toHaveURL(new RegExp(`${unassignedId}.*targetBuild=unassigned`))
  await expect(page.locator('.prIdentity').getByText('Not assigned', { exact: true })).toBeVisible()
  await page.reload({ waitUntil: 'load' })
  await expect(targetBuild()).toHaveValue('unassigned')
  await expect(page).toHaveURL(new RegExp(`${unassignedId}.*targetBuild=unassigned`))
  await expect(page.getByRole('heading', { name: unassignedTitle })).toBeVisible({ timeout: 30_000 })
  await expect(page.locator('.prIdentity').getByText('Not assigned', { exact: true })).toBeVisible()
})

test('an explicit target-build action refresh keeps its own history entry', async ({ page, request }) => {
  test.setTimeout(240_000)
  await apiLogin(request)
  const showcase = await showcaseSeed(request)
  const stamp = Date.now()
  const changedTitle = `Explicit target change ${stamp}`
  const activeAnchorTitle = `Active target anchor ${stamp}`

  const changed = await request.post(`${apiBase}/api/problem-reports`, {
    data: {
      category: 'CodeFunctional', projectId: showcase.projectId,
      releaseId: showcase.activeReleaseId,
      title: changedTitle,
      problem: 'This report will be explicitly moved to another target while the queue is filtered.',
    },
  })
  expect(changed.ok(), await changed.text()).toBeTruthy()
  const changedId = (await changed.json()).id as string
  const activeAnchor = await request.post(`${apiBase}/api/problem-reports`, {
    data: {
      category: 'CodeFunctional', projectId: showcase.projectId,
      releaseId: showcase.activeReleaseId,
      title: activeAnchorTitle,
      problem: 'A second active-target row keeps the explicit-action fallback deterministic.',
    },
  })
  expect(activeAnchor.ok(), await activeAnchor.text()).toBeTruthy()
  const activeAnchorId = (await activeAnchor.json()).id as string

  await login(page)
  await page.getByRole('link', { name: 'Problem Reports' }).click()
  await expect(page.getByRole('heading', { name: 'Problem Report queue' })).toBeVisible({ timeout: 30_000 })
  await page.getByPlaceholder('Number, title, description, root cause').fill(String(stamp))
  await page.waitForResponse(response =>
    response.url().includes('/api/problem-reports?') &&
    new URL(response.url()).searchParams.get('search') === String(stamp))

  const targetBuild = () => page.getByLabel('Target build').first()
  const [activeList, activeDashboard] = await Promise.all([
    page.waitForResponse(response => response.url().includes('/api/problem-reports?') &&
      new URL(response.url()).searchParams.get('targetReleaseId') === showcase.activeReleaseId),
    page.waitForResponse(response => response.url().includes('/api/problem-reports/dashboard?') &&
      new URL(response.url()).searchParams.get('targetReleaseId') === showcase.activeReleaseId),
    targetBuild().selectOption(showcase.activeReleaseId),
  ])
  expect(activeList.ok(), await activeList.text()).toBeTruthy()
  expect(activeDashboard.ok(), await activeDashboard.text()).toBeTruthy()
  await page.locator('.prList').getByText(changedTitle).click()
  await expect(page).toHaveURL(new RegExp(`${changedId}.*targetBuild=${showcase.activeReleaseId}`))

  const releasedOption = await targetBuild().locator('option').filter({ hasText: 'released' }).getAttribute('value')
  expect(releasedOption).toBeTruthy()
  const fallbackDetail = page.waitForResponse(response => {
    const url = new URL(response.url())
    return url.pathname.endsWith(`/api/problem-reports/${activeAnchorId}`)
  })
  const historyBefore = await page.evaluate(() => history.length)
  await page.locator('.prAdmin summary').click()
  await page.locator('.prAdmin select').selectOption(releasedOption!)
  const fallbackResponse = await fallbackDetail
  expect(fallbackResponse.ok(), await fallbackResponse.text()).toBeTruthy()
  await expect(page).toHaveURL(new RegExp(`${activeAnchorId}.*targetBuild=${showcase.activeReleaseId}`))
  // The contract is that the explicit action pushed rather than replaced its entry. Browser history can
  // also absorb an unrelated route commit, so the exact count is not part of the product contract; the
  // Back assertions below prove the restored URL and pane remain truthful.
  expect(await page.evaluate(() => history.length)).toBeGreaterThan(historyBefore)
  await page.goBack()
  await expect(page).toHaveURL(new RegExp(`${activeAnchorId}.*targetBuild=${showcase.activeReleaseId}`))
  await expect(page.getByRole('heading', { name: activeAnchorTitle })).toBeVisible({ timeout: 30_000 })
  await expect(page.getByRole('heading', { name: changedTitle })).toHaveCount(0)
})

test('Back restores the exact historical Problem Report snapshot after another report is shown', async ({ page, request }) => {
  test.setTimeout(240_000)
  await apiLogin(request)
  const showcase = await showcaseSeed(request)
  const stamp = Date.now()
  const historicalTitle = `Historical route report ${stamp}`
  const otherTitle = `Historical route other ${stamp}`

  const historical = await request.post(`${apiBase}/api/problem-reports`, {
    data: {
      category: 'CodeFunctional', projectId: showcase.projectId,
      releaseId: showcase.activeReleaseId,
      title: historicalTitle,
      problem: 'This report is opened later at an exact historical snapshot.',
    },
  })
  expect(historical.ok(), await historical.text()).toBeTruthy()
  const historicalId = (await historical.json()).id as string
  const historicalDetail = await request.get(`${apiBase}/api/problem-reports/${historicalId}`)
  expect(historicalDetail.ok(), await historicalDetail.text()).toBeTruthy()
  const historicalSnapshotId = ((await historicalDetail.json()).revisions as { id: string }[])[0]?.id
  expect(historicalSnapshotId, 'the created report has an immutable revision snapshot').toBeTruthy()

  const other = await request.post(`${apiBase}/api/problem-reports`, {
    data: {
      category: 'CodeFunctional', projectId: showcase.projectId,
      releaseId: showcase.activeReleaseId,
      title: otherTitle,
      problem: 'A different report is opened so Back must rehydrate the historical route.',
    },
  })
  expect(other.ok(), await other.text()).toBeTruthy()
  const otherId = (await other.json()).id as string

  await login(page)
  await page.getByRole('link', { name: 'Problem Reports' }).click()
  await expect(page.getByRole('heading', { name: 'Problem Report queue' })).toBeVisible({ timeout: 30_000 })
  await page.getByPlaceholder('Number, title, description, root cause').fill(String(stamp))
  await page.waitForResponse(response =>
    response.url().includes('/api/problem-reports?') &&
    new URL(response.url()).searchParams.get('search') === String(stamp))

  const snapshotUrl = new URL(page.url())
  snapshotUrl.pathname = `${snapshotUrl.pathname.replace(/\/$/, '')}/${historicalId}`
  snapshotUrl.searchParams.set('snapshotId', historicalSnapshotId!)
  await page.goto(snapshotUrl.toString(), { waitUntil: 'load' })
  await expect(page.getByText('HISTORICAL RECORD')).toBeVisible({ timeout: 30_000 })
  await expect(page.getByRole('heading', { name: historicalTitle })).toBeVisible()

  const otherUrl = new URL(snapshotUrl)
  otherUrl.pathname = `${snapshotUrl.pathname.replace(new RegExp(`/${historicalId}$`), '')}/${otherId}`
  otherUrl.searchParams.delete('snapshotId')
  await page.evaluate(url => {
    history.pushState({}, '', url)
    dispatchEvent(new PopStateEvent('popstate'))
  }, `${otherUrl.pathname}${otherUrl.search}`)
  await expect(page.getByRole('heading', { name: otherTitle })).toBeVisible()
  await page.goBack()
  await expect(page).toHaveURL(new RegExp(`${historicalId}.*snapshotId=${historicalSnapshotId}`))
  await expect(page.getByText('HISTORICAL RECORD')).toBeVisible({ timeout: 30_000 })
  await expect(page.getByRole('heading', { name: historicalTitle })).toBeVisible()
})

test('a failed route restoration cannot leave the previous Problem Report actionable', async ({ page, request }) => {
  test.setTimeout(240_000)
  await apiLogin(request)
  const showcase = await showcaseSeed(request)
  const stamp = Date.now()
  const firstTitle = `Restore failure first ${stamp}`
  const secondTitle = `Restore failure second ${stamp}`

  const first = await request.post(`${apiBase}/api/problem-reports`, {
    data: {
      category: 'CodeFunctional', projectId: showcase.projectId,
      releaseId: showcase.activeReleaseId,
      title: firstTitle,
      problem: 'This report is restored after another report is already displayed.',
    },
  })
  expect(first.ok(), await first.text()).toBeTruthy()
  const firstId = (await first.json()).id as string
  const second = await request.post(`${apiBase}/api/problem-reports`, {
    data: {
      category: 'CodeFunctional', projectId: showcase.projectId,
      releaseId: showcase.activeReleaseId,
      title: secondTitle,
      problem: 'This report remains displayed while the failed route restoration begins.',
    },
  })
  expect(second.ok(), await second.text()).toBeTruthy()

  await login(page)
  await page.getByRole('link', { name: 'Problem Reports' }).click()
  await expect(page.getByRole('heading', { name: 'Problem Report queue' })).toBeVisible({ timeout: 30_000 })
  await page.getByPlaceholder('Number, title, description, root cause').fill(String(stamp))
  await page.waitForResponse(response =>
    response.url().includes('/api/problem-reports?') &&
    new URL(response.url()).searchParams.get('search') === String(stamp))
  await page.locator('.prList').getByText(secondTitle).click()
  await expect(page.getByRole('heading', { name: secondTitle })).toBeVisible()

  const failFirst = (url: URL) => url.pathname === `/api/problem-reports/${firstId}`
  await page.route(failFirst, async route => { await route.abort('failed') })
  const firstUrl = new URL(page.url())
  firstUrl.pathname = `${firstUrl.pathname.replace(/\/[^/]+$/, '')}/${firstId}`
  await page.evaluate(url => {
    history.pushState({}, '', url)
    dispatchEvent(new PopStateEvent('popstate'))
  }, `${firstUrl.pathname}${firstUrl.search}`)

  await expect(page).toHaveURL(new RegExp(firstId))
  await expect(page.locator('.workspaceError')).toBeVisible({ timeout: 30_000 })
  await expect(page.getByRole('heading', { name: secondTitle })).toHaveCount(0)
  await expect(page.locator('.prFlow')).toHaveCount(0)

  await page.unroute(failFirst)
  await page.goBack()
  await expect(page.getByRole('heading', { name: secondTitle })).toBeVisible()
  await page.goForward()
  await expect(page).toHaveURL(new RegExp(firstId))
  await expect(page.getByRole('heading', { name: firstTitle })).toBeVisible({ timeout: 30_000 })
})

test('an implicit zero-row target clear replaces its history entry', async ({ page, request }) => {
  test.setTimeout(240_000)
  await apiLogin(request)
  const showcase = await showcaseSeed(request)
  const stamp = Date.now()
  const title = `Zero-row clear report ${stamp}`

  const created = await request.post(`${apiBase}/api/problem-reports`, {
    data: {
      category: 'CodeFunctional', projectId: showcase.projectId,
      releaseId: showcase.activeReleaseId,
      title,
      problem: 'This report is selected before the queue filters to an empty target.',
    },
  })
  expect(created.ok(), await created.text()).toBeTruthy()
  const reportId = (await created.json()).id as string

  await login(page)
  await page.getByRole('link', { name: 'Problem Reports' }).click()
  await expect(page.getByRole('heading', { name: 'Problem Report queue' })).toBeVisible({ timeout: 30_000 })
  await page.getByPlaceholder('Number, title, description, root cause').fill(String(stamp))
  await page.waitForResponse(response =>
    response.url().includes('/api/problem-reports?') &&
    new URL(response.url()).searchParams.get('search') === String(stamp))

  const targetBuild = () => page.locator('.prFilters').getByLabel('Target build')
  const activeOption = showcase.activeReleaseId
  const releasedOption = await targetBuild().locator('option').filter({ hasText: 'released' }).getAttribute('value')
  expect(releasedOption).toBeTruthy()
  const [activeList, activeDashboard] = await Promise.all([
    page.waitForResponse(response => response.url().includes('/api/problem-reports?') &&
      new URL(response.url()).searchParams.get('targetReleaseId') === activeOption),
    page.waitForResponse(response => response.url().includes('/api/problem-reports/dashboard?') &&
      new URL(response.url()).searchParams.get('targetReleaseId') === activeOption),
    targetBuild().selectOption(activeOption),
  ])
  expect(activeList.ok(), await activeList.text()).toBeTruthy()
  expect(activeDashboard.ok(), await activeDashboard.text()).toBeTruthy()
  await page.locator('.prList').getByText(title).click()
  await expect(page).toHaveURL(new RegExp(`${reportId}.*targetBuild=${activeOption}`))

  const emptyList = (url: URL) => url.pathname === '/api/problem-reports' &&
    url.searchParams.get('targetReleaseId') === releasedOption
  const emptyDashboard = (url: URL) => url.pathname === '/api/problem-reports/dashboard' &&
    url.searchParams.get('targetReleaseId') === releasedOption
  await page.route(emptyList, route => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify({ items: [], page: 1, pageSize: 10, totalCount: 0, totalPages: 0 }),
  }))
  await page.route(emptyDashboard, route => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify({ summary: { total: 0, active: 0, closureAwaitingApproval: 0, closed: 0, releaseBlockers: 0, waivedBlockers: 0 } }),
  }))

  await targetBuild().selectOption(releasedOption!)
  await expect(page).toHaveURL(new RegExp(`targetBuild=${releasedOption}`))
  await expect(page).not.toHaveURL(new RegExp(reportId))
  await page.goBack()
  await expect(targetBuild()).toHaveValue(activeOption)
  await expect(page.getByRole('heading', { name: title })).toBeVisible({ timeout: 30_000 })
  await page.goForward()
  await expect(targetBuild()).toHaveValue(releasedOption!)
  await expect(page.locator('.prList').getByText(title)).toHaveCount(0)
})
