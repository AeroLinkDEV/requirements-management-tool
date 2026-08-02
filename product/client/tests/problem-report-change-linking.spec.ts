import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, login, selectProgram } from './auth'

test('a PR drives an SCR and can be added to a System TCR through the active center', async ({ page, request }) => {
  test.setTimeout(240_000)
  await apiLogin(request)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
  const workspaces = await (await page.request.get(`${apiBase}/api/workspaces`)).json()
  const fms = workspaces.find((item: { program: { name: string } }) => item.program.name === 'Flight Management System Live Program')
  const projectId = fms.projects[0].project.id
  const releaseId = fms.projects[0].releases.find((item: { isReleased: boolean }) => !item.isReleased).id

  const title = `PR-driven alert correction ${Date.now()}`
  const createdReport = await page.request.post(`${apiBase}/api/problem-reports`, {
    data: { projectId, releaseId, title, problem: 'The alert clears while the disagreement is still present.' },
  })
  expect(createdReport.ok(), await createdReport.text()).toBeTruthy()
  const report = await createdReport.json()

  await page.goto(new URL(`${root}/systems/change-requests/new`, page.url()).toString(), { waitUntil: 'load' })
  await expect(page.getByRole('heading', { name: 'Create System Change Request' })).toBeVisible({ timeout: 30_000 })
  const reportChoice = page.getByRole('checkbox', { name: new RegExp(report.displayNumber.replace('.', '\\.')) })
  await expect(reportChoice).toBeVisible()
  await reportChoice.check()
  await page.getByLabel('Title').fill('Correct persistent disagreement annunciation')
  await page.getByRole('button', { name: 'Save SCR Draft' }).click()
  await expect(page).toHaveURL(/\/systems\/change-requests\/[0-9a-f-]+$/i)
  await expect(page.getByRole('heading', { name: 'Driving Problem Reports' })).toBeVisible()
  await expect(page.getByText(report.displayNumber, { exact: true })).toBeVisible()
  const changeRequestId = new URL(page.url()).pathname.split('/').at(-1)!
  const linkedToChange = await page.request.get(`${apiBase}/api/problem-reports/linked/ChangeRequest/${changeRequestId}`)
  expect(linkedToChange.ok(), await linkedToChange.text()).toBeTruthy()
  expect((await linkedToChange.json()).map((item: { id: string }) => item.id)).toContain(report.id)

  await page.getByRole('button', { name: 'Check out & edit' }).click()
  const checkedOutReport = page.getByRole('checkbox', { name: new RegExp(report.displayNumber.replace('.', '\\.')) })
  await expect(checkedOutReport).toBeVisible()
  await expect(checkedOutReport).toBeChecked()
  await page.getByRole('button', { name: 'Discard checkout' }).click()
  await expect(page.getByRole('button', { name: 'Check out & edit' })).toBeVisible()

  const change = await (await page.request.get(`${apiBase}/api/scrs/${changeRequestId}`)).json()
  for (const controlledNumber of [change.displayNumber, report.displayNumber]) {
    await page.getByRole('button', { name: /Search & navigate/ }).click()
    const palette = page.getByRole('dialog', { name: 'Quick navigation' })
    const search = palette.getByLabel('Search AeroLink')
    await search.fill(controlledNumber)
    await expect(palette.getByRole('link').filter({ hasText: controlledNumber })).toBeVisible()
    await search.press('Escape')
  }

  await page.goto(new URL(`${root}/system-verification/coverage`, page.url()).toString(), { waitUntil: 'load' })
  await expect(page.getByRole('heading', { name: 'Testing Coverage' })).toBeVisible({ timeout: 30_000 })
  const linkButton = page.getByRole('button', { name: /^Link PRs/ }).first()
  await expect(linkButton).toBeVisible()
  await linkButton.click()
  const dialog = page.getByRole('dialog', { name: /Link PRs to SYSTCR-/ })
  await expect(dialog).toBeVisible()
  await dialog.getByRole('checkbox', { name: new RegExp(report.displayNumber.replace('.', '\\.')) }).check()
  await dialog.getByRole('button', { name: 'Save links' }).click()
  await expect(page.getByRole('status')).toContainText('PR links updated')
  await expect(page.getByRole('button', { name: 'Link PRs · 1' }).first()).toBeVisible()
  const requests = await (await page.request.get(`${apiBase}/api/releases/${releaseId}/test-change-reviews`)).json()
  expect(requests.some((item: { problemReports?: { id: string }[] }) =>
    item.problemReports?.some(linked => linked.id === report.id))).toBeTruthy()

  await page.goto(new URL(`${root}/problem-reports`, page.url()).toString(), { waitUntil: 'load' })
  await expect(page.getByRole('heading', { name: 'Problem Reports' })).toBeVisible()
  await page.getByRole('button', { name: new RegExp(report.displayNumber.replace('.', '\\.')) }).click()
  await expect(page.getByText('Proposed Corrective Action')).toBeVisible()
  await expect(page.getByText('Verification For Problem')).toBeVisible()
  await page.reload({ waitUntil: 'load' })
  await expect(page.getByRole('heading', { name: 'Problem Reports' })).toBeVisible()
  await expect(page.getByText('Proposed Corrective Action')).toBeVisible()
  await expect(page.getByText('Verification For Problem')).toBeVisible()
})
