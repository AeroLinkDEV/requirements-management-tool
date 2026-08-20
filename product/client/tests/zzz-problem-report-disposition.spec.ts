import { expect, test } from '@playwright/test'
import type { APIRequestContext } from '@playwright/test'
import { apiBase, apiLogin, login, showcaseSeed } from './auth'

type ReportRef = { id: string; version: number; title: string; state?: string }

async function transition(request: APIRequestContext, report: ReportRef, targetState: string, rationale?: string) {
  const response = await request.post(`${apiBase}/api/problem-reports/${report.id}/transition`, {
    data: { expectedVersion: report.version, targetState, rationale },
  })
  expect(response.ok(), await response.text()).toBeTruthy()
  const body = await response.json() as ReportRef
  report.version = body.version
  report.state = body.state
  return body
}

async function createReport(request: APIRequestContext, projectId: string, releaseId: string,
  title: string): Promise<ReportRef> {
  const created = await request.post(`${apiBase}/api/problem-reports`, { data: {
    projectId,
    releaseId,
    title,
    problem: 'A controlled engineering conclusion is required for this observed condition.',
  } })
  expect(created.ok(), await created.text()).toBeTruthy()
  return await created.json() as ReportRef
}

async function createOpenReport(request: APIRequestContext, projectId: string, releaseId: string,
  title: string): Promise<ReportRef> {
  const report = await createReport(request, projectId, releaseId, title)
  await transition(request, report, 'ReadyForSccb')
  // Administrators have ordinary Project access but are deliberately not SCCB opening authority.
  await apiLogin(request, 'systems.lead')
  await transition(request, report, 'Open')
  await apiLogin(request, 'admin')
  return report
}

test('Problem Report lifecycle controls expose canonical states, rationale gates, and immutable history', async ({ page, request }) => {
  test.setTimeout(180_000)
  await apiLogin(request)
  const showcase = await showcaseSeed(request)
  const stamp = Date.now()
  const source = await createOpenReport(request, showcase.projectId, showcase.activeReleaseId,
    `Canonical lifecycle ${stamp}`)

  await login(page)
  await page.getByRole('link', { name: 'Problem Reports' }).click()
  await page.getByLabel('Search').fill(source.title)
  // Search is debounced and refreshes the queue asynchronously. Select the unique queue row only after
  // the filtered result has settled; otherwise a late refresh can restore the previously selected report.
  const sourceRow = page.locator('.prList > button').filter({ hasText: source.title })
  await expect(sourceRow).toHaveCount(1)
  await expect(sourceRow).toBeVisible()
  await sourceRow.click()
  await expect(page.locator('.prDetail h2')).toHaveText(source.title)
  await expect(page.locator('.prState')).toHaveText('Open')

  // Open -> Draft is a backward transition and must be explained before the server accepts it.
  await page.getByRole('button', { name: 'Move backward…' }).click()
  const backward = page.getByRole('dialog', { name: 'Backward Problem Report transition' })
  const backwardAction = backward.getByRole('button', { name: /Return to Draft/ })
  await expect(backwardAction).toBeDisabled()
  const backwardRationale = `The original triage needs to be reopened ${stamp}`
  await backward.getByLabel('Rationale').fill(backwardRationale)
  await backwardAction.click()
  await expect(page.locator('.prState')).toHaveText('Draft')

  await page.getByRole('button', { name: 'Reject…' }).click()
  const rejection = page.getByRole('dialog', { name: 'Reject Problem Report' })
  const rejectAction = rejection.getByRole('button', { name: /Reject Problem Report/ })
  await expect(rejectAction).toBeDisabled()
  const rejectionRationale = `The condition is not accepted for this controlled baseline ${stamp}`
  await rejection.getByLabel('Rationale').fill(rejectionRationale)
  await rejectAction.click()
  await expect(page.locator('.prState')).toHaveText('Rejected')
  await expect(page.getByLabel('Controlled disposition')).toContainText(rejectionRationale)

  await page.getByRole('button', { name: /History/ }).click()
  const history = page.locator('.prTimeline')
  const backwardHistory = history.locator('article').filter({ hasText: 'Open → Draft' })
  await expect(backwardHistory).toBeVisible()
  await expect(backwardHistory.locator('p').filter({ hasText: `Rationale: ${backwardRationale}` })).toHaveCount(1)
  const rejectionHistory = history.locator('article').filter({ hasText: 'Draft → Rejected' })
  await expect(rejectionHistory).toBeVisible()
  await expect(rejectionHistory.locator('p').filter({ hasText: `Rationale: ${rejectionRationale}` })).toHaveCount(1)
})

test('Problem Report status filters expose exactly the eight canonical lifecycle states', async ({ page, request }) => {
  test.setTimeout(180_000)
  await apiLogin(request)
  const showcase = await showcaseSeed(request)
  const stamp = Date.now()
  const draft = await createReport(request, showcase.projectId, showcase.activeReleaseId, `Draft state ${stamp}`)
  const open = await createOpenReport(request, showcase.projectId, showcase.activeReleaseId, `Open state ${stamp}`)
  const rejected = await createOpenReport(request, showcase.projectId, showcase.activeReleaseId, `Rejected state ${stamp}`)
  await transition(request, rejected, 'Rejected', `Reject the unsupported condition ${stamp}`)

  await login(page)
  await page.getByRole('link', { name: 'Problem Reports' }).click()
  const status = page.getByLabel('Status')
  const canonicalLabels = [
    'Draft', 'Ready for SCCB', 'Open', 'Implementing', 'Verifying',
    'Waiting for SQA to Close', 'Closed', 'Rejected',
  ]
  await expect(status.locator('option')).toHaveText(['All', ...canonicalLabels])
  await status.selectOption('Rejected')
  await page.getByRole('button', { name: 'Apply filters' }).click()
  await expect(page.locator('.prList').getByText(rejected.title)).toBeVisible()
  await expect(page.locator('.prList').getByText(draft.title)).toHaveCount(0)
  await expect(page.locator('.prList').getByText(open.title)).toHaveCount(0)
})
