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
  expect(await page.evaluate(() => history.length)).toBe(historyBefore + 1)
  await page.goBack()
  await expect(page).toHaveURL(new RegExp(`${activeAnchorId}.*targetBuild=${showcase.activeReleaseId}`))
  await expect(page.getByRole('heading', { name: activeAnchorTitle })).toBeVisible({ timeout: 30_000 })
  await expect(page.getByRole('heading', { name: changedTitle })).toHaveCount(0)
})
