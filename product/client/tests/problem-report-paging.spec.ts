import { expect, test, type APIRequestContext } from '@playwright/test'
import { apiBase, apiLogin, login } from './auth'

async function createReport(request: APIRequestContext, projectId: string, releaseId: string, title: string) {
  const response = await request.post(`${apiBase}/api/problem-reports`, { data: {
    category: 'CodeFunctional', projectId, releaseId, title, problem: `${title} belongs to the Project-scale paging qualification set.`,
  } })
  expect(response.ok(), await response.text()).toBeTruthy()
  return await response.json() as { id: string; displayNumber: string }
}

test('the Project queue and build picker reach every Problem Report beyond their first pages', async ({ page, request }) => {
  test.setTimeout(300_000)
  await apiLogin(request)
  const suffix = Date.now().toString().slice(-7)
  const workspaceResponse = await request.post(`${apiBase}/api/workspaces`, { data: {
    programName: `PR paging ${suffix}`,
    programCode: `PRP${suffix}`,
    projectName: 'Project-scale Problem Reports',
    softwareProduct: 'Paging qualification product',
    initialRelease: '1.0',
    initialReleaseIsReleased: false,
  } })
  expect(workspaceResponse.ok(), await workspaceResponse.text()).toBeTruthy()
  const workspace = await workspaceResponse.json()
  const projectId = workspace.project.id as string
  const releaseId = workspace.release.id as string
  const titles: string[] = []
  for (let index = 1; index <= 125; index += 1) {
    const title = `Scale browser report ${suffix}-${String(index).padStart(3, '0')}`
    titles.push(title)
    await createReport(request, projectId, releaseId, title)
  }

  const listRequests: URL[] = []
  page.on('request', requestEvent => {
    const url = new URL(requestEvent.url())
    if (url.pathname === '/api/problem-reports') listRequests.push(url)
  })
  await login(page, 'admin', { openProject: false })
  const root = `/programs/${workspace.program.id}/projects/${projectId}/releases/${releaseId}`
  await page.goto(`${root}/problem-reports`)

  const pager = page.getByRole('navigation', { name: 'Problem Report queue pages' })
  await expect(pager).toContainText('Page 1 of 13', { timeout: 30_000 })
  await expect(page.locator('.prListHead')).toContainText('1–10 of 125 matching records')
  await expect(page.locator('.prList').getByText(titles[0], { exact: true })).toBeVisible()
  await expect(page.getByRole('heading', { name: titles[0], exact: true })).toBeVisible()

  await pager.getByRole('button', { name: 'Next' }).click()
  await expect(pager).toContainText('Page 2 of 13')
  await expect(page.locator('.prList').getByText(titles[10], { exact: true })).toBeVisible()
  // Paging the queue does not discard a directly opened/deep-linked controlled record.
  await expect(page.getByRole('heading', { name: titles[0], exact: true })).toBeVisible()

  for (let expectedPage = 3; expectedPage <= 13; expectedPage += 1) {
    await pager.getByRole('button', { name: 'Next' }).click()
    await expect(pager).toContainText(`Page ${expectedPage} of 13`)
  }
  await expect(page.locator('.prListHead')).toContainText('121–125 of 125 matching records')
  await expect(page.locator('.prList').getByText(titles[124], { exact: true })).toBeVisible()
  await expect(pager.getByRole('button', { name: 'Next' })).toBeDisabled()

  const filters = page.locator('.prFilters')
  const search = filters.getByLabel('Search')
  await search.fill(titles[124])
  await expect(pager).toContainText('Page 1 of 1')
  await expect(page.locator('.prListHead')).toContainText('1–1 of 1 matching records')
  await search.fill('')
  await expect(pager).toContainText('Page 1 of 13')

  await filters.getByLabel('Status').selectOption('Draft')
  await filters.getByLabel('Target build').selectOption(releaseId)
  await filters.getByLabel('Category', { exact: true }).selectOption('CodeFunctional')
  await filters.getByLabel('Severity').selectOption('Major')
  await filters.getByLabel('Priority').selectOption('Normal')
  await filters.getByLabel('Assigned user').fill('admin')
  await filters.getByRole('button', { name: 'Apply filters' }).click()
  await expect(pager).toContainText('Page 1 of 13')
  await pager.getByRole('button', { name: 'Next' }).click()
  await expect(pager).toContainText('Page 2 of 13')
  await expect.poll(() => listRequests.some(url => url.searchParams.get('page') === '2'
    && url.searchParams.get('pageSize') === '10'
    && url.searchParams.get('targetReleaseId') === releaseId
    && url.searchParams.get('state') === 'Draft'
    && url.searchParams.get('category') === 'CodeFunctional'
    && url.searchParams.get('severity') === 'Major'
    && url.searchParams.get('priority') === 'Normal'
    && url.searchParams.get('owner') === 'admin')).toBeTruthy()

  await page.goBack()
  await expect(pager).toContainText('Page 1 of 13')
  await expect(page).not.toHaveURL(new RegExp(`targetBuild=${releaseId}`))
  await page.goForward()
  await expect(pager).toContainText('Page 1 of 13')
  await expect(page).toHaveURL(new RegExp(`targetBuild=${releaseId}`))

  await page.goto(`${root}/systems/change-requests/new`)
  await expect(page.getByRole('heading', { name: 'Create System Change Request' })).toBeVisible({ timeout: 30_000 })
  const picker = page.locator('.problemReportPicker')
  await expect(picker.getByText('Showing 50 of 125 matching PRs')).toBeVisible()
  await picker.getByRole('button', { name: 'Load more problem reports' }).click()
  await expect(picker.getByText('Showing 100 of 125 matching PRs')).toBeVisible()
  const beyondFirstFifty = picker.getByRole('checkbox', { name: new RegExp(titles[50]) })
  await expect(beyondFirstFifty).toBeVisible()
  await beyondFirstFifty.check()
  await expect(beyondFirstFifty).toBeChecked()

  const pickerSearch = picker.getByRole('searchbox', { name: 'Find controlled PR' })
  await pickerSearch.fill(titles[124])
  await expect(picker.getByText(titles[124], { exact: true })).toBeVisible()
  // The selected item is pinned independently of the replacement candidate page.
  await expect(picker.getByRole('checkbox', { name: new RegExp(titles[50]) })).toBeChecked()
  await expect.poll(() => listRequests.some(url => url.searchParams.get('pageSize') === '50'
    && url.searchParams.get('search') === titles[124]
    && url.searchParams.get('targetReleaseId') === releaseId)).toBeTruthy()
})
