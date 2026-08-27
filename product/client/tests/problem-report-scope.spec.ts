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
  await selectTargetBuild(releasedOption!)
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
  await expect(page.locator('.prIdentity').getByText('Not assigned', { exact: true })).toBeVisible()
  await expect(page).toHaveURL(/targetBuild=unassigned/)
  await page.reload({ waitUntil: 'load' })
  await expect(targetBuild()).toHaveValue('unassigned')
  await expect(page.getByRole('heading', { name: unassignedTitle })).toBeVisible({ timeout: 30_000 })
  await expect(page.locator('.prIdentity').getByText('Not assigned', { exact: true })).toBeVisible()
})
