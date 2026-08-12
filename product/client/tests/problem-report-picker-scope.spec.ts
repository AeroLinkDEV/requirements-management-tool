import { expect, test, type APIRequestContext } from '@playwright/test'
import { apiBase, apiLogin, login } from './auth'

async function createReport(request: APIRequestContext, projectId: string, releaseId: string, title: string) {
  const response = await request.post(`${apiBase}/api/problem-reports`, { data: {
    projectId, releaseId, title, problem: `${title} must be corrected under controlled configuration.`,
  } })
  expect(response.ok(), await response.text()).toBeTruthy()
  return await response.json() as { id: string; displayNumber: string; version: number }
}

test('build-specific PR pickers preserve the explicit target while the Project database stays Project-wide', async ({ page, request }) => {
  test.setTimeout(300_000)
  await apiLogin(request)
  const suffix = Date.now().toString().slice(-7)
  const workspaceResponse = await request.post(`${apiBase}/api/workspaces`, { data: {
    programName: `PR picker scope ${suffix}`,
    programCode: `PPS${suffix}`,
    projectName: 'Picker scope project',
    softwareProduct: 'Picker scope product',
    initialRelease: '1.5',
    initialReleaseIsReleased: true,
  } })
  expect(workspaceResponse.ok(), await workspaceResponse.text()).toBeTruthy()
  const workspace = await workspaceResponse.json()
  const projectId = workspace.project.id as string
  const releasedId = workspace.release.id as string
  const successorResponse = await request.post(`${apiBase}/api/releases`, { data: {
    projectId, version: '1.6', predecessorReleaseId: releasedId,
  } })
  expect(successorResponse.ok(), await successorResponse.text()).toBeTruthy()
  const activeId = (await successorResponse.json()).id as string

  const activeTitle = `Active-build selectable problem ${suffix}`
  const otherTitle = `Other-build forbidden problem ${suffix}`
  const activeReport = await createReport(request, projectId, activeId, activeTitle)
  const otherReport = await createReport(request, projectId, releasedId, otherTitle)
  // Prove the bounded picker can page without dropping its build filter.
  for (let index = 0; index < 50; index += 1) {
    await createReport(request, projectId, activeId, `Paged active-build problem ${suffix}-${String(index).padStart(2, '0')}`)
  }

  const pickerRequests: URL[] = []
  page.on('request', requestEvent => {
    const url = new URL(requestEvent.url())
    if (url.pathname === '/api/problem-reports') pickerRequests.push(url)
  })
  await login(page, 'admin', { openProject: false })
  const root = `/programs/${workspace.program.id}/projects/${projectId}/releases/${activeId}`

  await page.goto(`${root}/systems/change-requests/new`)
  await expect(page.getByRole('heading', { name: 'Create System Change Request' })).toBeVisible({ timeout: 30_000 })
  const changePicker = page.locator('.problemReportPicker')
  await expect(changePicker.getByText(activeTitle)).toBeVisible()
  await expect(changePicker.getByText(otherTitle)).toHaveCount(0)
  await changePicker.getByRole('button', { name: 'Load more problem reports' }).click()
  await expect.poll(() => pickerRequests.some(url => url.searchParams.get('page') === '2'
    && url.searchParams.get('targetReleaseId') === activeId)).toBeTruthy()
  const changeSearch = changePicker.getByRole('searchbox', { name: 'Find controlled PR' })
  await changeSearch.fill(otherTitle)
  await expect(changePicker.getByText(otherTitle)).toHaveCount(0)
  await expect.poll(() => pickerRequests.some(url => url.searchParams.get('search') === otherTitle
    && url.searchParams.get('targetReleaseId') === activeId)).toBeTruthy()
  await changeSearch.fill(activeTitle)
  await expect(changePicker.getByText(activeTitle)).toBeVisible()

  await page.goto(`${root}/system-verification/coverage`)
  await expect(page.getByRole('heading', { name: 'Change Requests' })).toBeVisible({ timeout: 30_000 })
  await page.getByRole('button', { name: '+ New System Test Change Request' }).click()
  // The authoring page, not a dialog — raising a package is the same act as raising a change request.
  await expect(page.getByRole('heading', { name: 'Create System Test Change Request', level: 1 }))
    .toBeVisible({ timeout: 30_000 })
  const tcrPicker = page.locator('[data-tcr-editor] .problemReportPicker')
  await tcrPicker.getByRole('searchbox', { name: 'Find controlled PR' }).fill(otherTitle)
  await expect(tcrPicker.getByText(otherTitle)).toHaveCount(0)
  await expect.poll(() => pickerRequests.some(url => url.searchParams.get('search') === otherTitle
    && url.searchParams.get('targetReleaseId') === activeId)).toBeTruthy()

  // Browser filtering is not the authority boundary: a forged direct call is refused and leaves the PR alone.
  const forged = await request.post(`${apiBase}/api/change-requests`, { data: {
    projectId, targetReleaseId: activeId, type: 'System', title: 'Forged cross-build PR selection',
    problem: 'P', analysis: 'A', solution: 'S', problemReportIds: [otherReport.id],
  } })
  expect(forged.status()).toBe(400)
  expect(await forged.text()).toContain('target build')
  const otherDetail = await (await request.get(`${apiBase}/api/problem-reports/${otherReport.id}`)).json()
  expect(otherDetail.state).toBe('Draft')
  expect(otherDetail.links.some((link: { relationship: string }) => link.relationship === 'ProposedCorrectiveAction')).toBeFalsy()

  // Retargeting an already-linked Draft makes that relationship explicitly stale. It remains visible so the
  // author can remove it, but it cannot become a valid new choice through detail hydration.
  const linkedResponse = await request.post(`${apiBase}/api/change-requests`, { data: {
    projectId, targetReleaseId: activeId, type: 'System', title: 'Retargeted relationship proof',
    problem: 'P', analysis: 'A', solution: 'S', problemReportIds: [activeReport.id],
  } })
  expect(linkedResponse.ok(), await linkedResponse.text()).toBeTruthy()
  const linked = await linkedResponse.json()
  const currentReport = await (await request.get(`${apiBase}/api/problem-reports/${activeReport.id}`)).json()
  const retargeted = await request.post(`${apiBase}/api/problem-reports/${activeReport.id}/target-build`, { data: {
    expectedVersion: currentReport.version, targetReleaseId: releasedId,
  } })
  expect(retargeted.ok(), await retargeted.text()).toBeTruthy()
  await page.goto(`${root}/systems/change-requests/${linked.id}`)
  await page.getByRole('button', { name: 'Check out & edit' }).click()
  const staleChoice = page.getByRole('checkbox', { name: new RegExp(activeReport.displayNumber.replace('.', '\\.')) })
  await expect(staleChoice).toBeChecked()
  await expect(page.getByText('Stale relationship: this PR is no longer targeted to this build. Remove it before saving.')).toBeVisible()
  await staleChoice.click()
  await expect(staleChoice).toHaveCount(0)

  // DEC-089 remains intact: the general Problem Report center makes no implicit active-build request.
  pickerRequests.length = 0
  await page.goto(`${root}/problem-reports`)
  await expect(page.getByRole('heading', { name: 'Problem Report queue' })).toBeVisible({ timeout: 30_000 })
  await expect.poll(() => pickerRequests.some(url => url.searchParams.get('projectId') === projectId
    && !url.searchParams.has('targetReleaseId'))).toBeTruthy()
})
