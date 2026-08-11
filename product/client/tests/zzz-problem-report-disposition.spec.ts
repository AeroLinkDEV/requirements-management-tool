import { expect, test } from '@playwright/test'
import type { APIRequestContext } from '@playwright/test'
import { apiBase, apiLogin, login, showcaseSeed } from './auth'

type ReportRef = { id: string; version: number; title: string }

async function createOpenReport(request: APIRequestContext, projectId: string, releaseId: string,
  title: string): Promise<ReportRef> {
  const created = await request.post(`${apiBase}/api/problem-reports`, { data: {
    projectId,
    releaseId,
    title,
    problem: 'A controlled engineering conclusion is required for this observed condition.',
  } })
  expect(created.ok(), await created.text()).toBeTruthy()
  const report = await created.json() as ReportRef
  const ready = await request.post(`${apiBase}/api/problem-reports/${report.id}/ready-for-sccb`, {
    data: { expectedVersion: report.version },
  })
  expect(ready.ok(), await ready.text()).toBeTruthy()
  const readyBody = await ready.json() as ReportRef
  const opened = await request.post(`${apiBase}/api/problem-reports/${report.id}/sccb/open`, {
    data: { expectedVersion: readyBody.version },
  })
  expect(opened.ok(), await opened.text()).toBeTruthy()
  return { ...report, version: (await opened.json() as ReportRef).version }
}

async function apiDisposition(request: APIRequestContext, report: ReportRef, disposition: string,
  rationale: string, duplicateOfId?: string) {
  const response = await request.post(`${apiBase}/api/problem-reports/${report.id}/disposition`, { data: {
    expectedVersion: report.version,
    disposition,
    rationale,
    duplicateOfId,
  } })
  expect(response.ok(), await response.text()).toBeTruthy()
  report.version = (await response.json() as ReportRef).version
}

test('responsible engineer can disposition, reopen, defer, resume and record a canonical Duplicate in the browser', async ({ page, request }) => {
  test.setTimeout(180_000)
  await apiLogin(request)
  const showcase = await showcaseSeed(request)
  const stamp = Date.now()
  const source = await createOpenReport(request, showcase.projectId, showcase.activeReleaseId,
    `Disposition lifecycle ${stamp}`)
  const canonical = await createOpenReport(request, showcase.projectId, showcase.activeReleaseId,
    `Canonical anomaly ${stamp}`)

  await login(page)
  await page.getByRole('link', { name: 'Problem Reports' }).click()
  await page.getByLabel('Search').fill(source.title)
  await page.locator('.prList').getByText(source.title).click()

  await page.getByRole('button', { name: 'Disposition…' }).click()
  const dispositionDialog = page.getByRole('dialog', { name: 'Disposition Problem Report' })
  await expect(dispositionDialog.getByRole('option', { name: 'Fixed' })).toHaveCount(0)
  await dispositionDialog.getByLabel('Disposition').selectOption('CannotReproduce')
  await expect(dispositionDialog.getByRole('button', { name: /Record controlled disposition/ })).toBeDisabled()
  const cannotRationale = `Environmental condition could not be reproduced ${stamp}`
  await dispositionDialog.getByLabel('Rationale').fill(cannotRationale)
  await dispositionDialog.getByRole('button', { name: /Record controlled disposition/ }).click()

  await expect(page.getByLabel('Controlled disposition').getByRole('heading', { name: 'Cannot Reproduce' })).toBeVisible()
  await expect(page.getByText(cannotRationale)).toBeVisible()
  await expect(page.getByRole('button', { name: 'Check out & edit' })).toHaveCount(0)
  const exactUrl = page.url()
  await page.reload()
  await expect(page).toHaveURL(exactUrl)
  await expect(page.getByText(cannotRationale)).toBeVisible()
  await page.getByRole('button', { name: /History/ }).click()
  await expect(page.locator('.prTimeline').getByText(cannotRationale)).toBeVisible()

  await page.getByRole('button', { name: 'Record', exact: true }).click()
  await page.getByRole('button', { name: 'Reopen…' }).click()
  const reopen = page.getByRole('dialog', { name: 'Reopen Problem Report' })
  await expect(reopen.getByRole('button', { name: /Reopen as active work/ })).toBeDisabled()
  const reopenRationale = `New observations require active investigation ${stamp}`
  await reopen.getByLabel('Rationale').fill(reopenRationale)
  await reopen.getByRole('button', { name: /Reopen as active work/ }).click()
  await expect(page.locator('.prState')).toHaveText('Open')

  await page.getByRole('button', { name: 'Disposition…' }).click()
  await dispositionDialog.getByLabel('Disposition').selectOption('Deferred')
  const deferredRationale = `Supplier qualification build is pending ${stamp}`
  await dispositionDialog.getByLabel('Rationale').fill(deferredRationale)
  await dispositionDialog.getByRole('button', { name: /Record controlled disposition/ }).click()
  await expect(page.locator('.prState')).toHaveText('Deferred')
  await expect(page.getByText(deferredRationale)).toBeVisible()
  await page.getByRole('button', { name: /Resume Problem Report/ }).click()
  await expect(page.locator('.prState')).toHaveText('Open')
  await page.getByRole('button', { name: /History/ }).click()
  await expect(page.locator('.prTimeline').getByText(deferredRationale)).toBeVisible()
  await expect(page.locator('.prTimeline').getByText(reopenRationale)).toBeVisible()

  await page.getByRole('button', { name: 'Record', exact: true }).click()
  await page.getByRole('button', { name: 'Disposition…' }).click()
  await dispositionDialog.getByLabel('Disposition').selectOption('Duplicate')
  await expect(dispositionDialog.getByRole('option', { name: new RegExp(source.title) })).toHaveCount(0)
  await dispositionDialog.getByLabel('Search same-Project reports').fill(canonical.title)
  const candidate = dispositionDialog.getByRole('option', { name: new RegExp(canonical.title) })
  await expect(candidate).toContainText('Open')
  await candidate.click()
  const duplicateRationale = `Represented by the canonical anomaly ${stamp}`
  await dispositionDialog.getByLabel('Rationale').fill(duplicateRationale)
  await dispositionDialog.getByRole('button', { name: /Record controlled disposition/ }).click()
  await expect(page.locator('.prState')).toHaveText('Duplicate')
  await expect(page.getByLabel('Controlled disposition')).toContainText(duplicateRationale)
  await expect(page.getByLabel('Controlled disposition')).toContainText('Canonical target PR-')
  await expect(page.getByLabel('Controlled disposition')).toContainText(`${canonical.title} · Open`)
  await expect(page.getByRole('button', { name: 'Check out & edit' })).toHaveCount(0)
})

test('terminal disposition filters expose every non-fix conclusion', async ({ page, request }) => {
  test.setTimeout(180_000)
  await apiLogin(request)
  const showcase = await showcaseSeed(request)
  const stamp = Date.now()
  const states = ['NoFaultFound', 'AcceptedRisk', 'Rejected'] as const
  const reports: { state: typeof states[number]; report: ReportRef }[] = []
  for (const state of states) {
    const report = await createOpenReport(request, showcase.projectId, showcase.activeReleaseId,
      `${state} filter proof ${stamp}`)
    await apiDisposition(request, report, state, `${state} controlled rationale ${stamp}`)
    reports.push({ state, report })
  }

  await login(page)
  await page.getByRole('link', { name: 'Problem Reports' }).click()
  for (const item of reports) {
    await page.getByLabel('Status').selectOption(item.state)
    await page.getByRole('button', { name: 'Apply filters' }).click()
    await expect(page.locator('.prList').getByText(item.report.title)).toBeVisible()
  }
  for (const option of ['Deferred', 'Duplicate', 'CannotReproduce', 'NoFaultFound', 'AcceptedRisk', 'Rejected', 'Closed']) {
    await expect(page.getByLabel('Status').getByRole('option', { name: option.replace(/([a-z])([A-Z])/g, '$1 $2') })).toHaveCount(1)
  }
})
