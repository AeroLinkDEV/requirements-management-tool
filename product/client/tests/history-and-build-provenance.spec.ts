import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, firstSectionId, login, openNavigationGroup, selectProgram } from './auth'

const completeImpacts=JSON.stringify({trace:'Not Affected',verification:'Not Affected',documents:'Not Affected',baseline:'Not Affected',collaboration:'Not Affected'})

test('searches scoped change history while dormant build management stays unreachable', async ({ page, request }) => {
  await apiLogin(request)
  const suffix=Date.now().toString().slice(-7),programName=`History Program ${suffix}`
  const workspaceResponse = await request.post(`${apiBase}/api/workspaces`, { data: {
    programName, programCode: `HI${suffix}`, projectName: 'FMS Software', softwareProduct: 'Flight Management Software', initialRelease: '3.3', initialReleaseIsReleased: false,
  } }); expect(workspaceResponse.ok()).toBeTruthy(); const workspace = await workspaceResponse.json()
  const scrResponse = await request.post(`${apiBase}/api/scr-drafts`, { data: {
    projectId: workspace.project.id, targetReleaseId: workspace.release.id, type: 'Software', title: 'Introduce round robin routing', problem: 'Routing is unavailable', analysis: 'A new function is required', solution: 'Implement round robin routing',
    requirementChanges: [{ level: 'HighLevel', kind: 'Introduce', targetSectionId: await firstSectionId(request, workspace.project.id, 'HighLevel'), statement: 'The software shall provide round robin routing.', rationale: 'The new function is derived from the software architecture for this isolated lifecycle fixture.', verificationMethod: 'Test', impactDispositionJson:completeImpacts, isDerived:true }],
  } }); expect(scrResponse.ok()).toBeTruthy(); const scr = await scrResponse.json()
  await request.post(`${apiBase}/api/scrs/${scr.id}/submit`, { data: { approvers: [{ userId: 'admin', name: 'AeroLink Administrator' }] } })
  await request.post(`${apiBase}/api/scrs/${scr.id}/approve`, { data: { password: 'AeroLink!2026', meaning: 'Approved for test baseline assembly.' } })
  const baseline = await (await request.post(`${apiBase}/api/baselines`, { data: { baseNumber: 'SW-03.30', revision: 0, projectId: workspace.project.id, releaseId: workspace.release.id, name: 'FMS 3.3 exact manifest', actorId: 'cm' } })).json()
  await request.post(`${apiBase}/api/baselines/${baseline.id}/selections`, { data: { scrId: scr.id, actorId: 'cm' } })
  await request.post(`${apiBase}/api/baselines/${baseline.id}/freeze`, { data: { actorId: 'cm' } })
  await request.post(`${apiBase}/api/baselines/${baseline.id}/materialize-requirements`, { data: { actorId: 'cm' } })
  const historyResponse = await request.get(`${apiBase}/api/history/scrs?projectId=${workspace.project.id}&page=1&pageSize=50`)
  const historyBody = await historyResponse.text(); expect(historyResponse.status(), historyBody).toBe(200); expect(JSON.parse(historyBody).totalCount).toBe(1)

  await login(page, 'admin', { openProject: false })
  await selectProgram(page, programName)
  await openNavigationGroup(page,'SOFTWARE ENGINEERING')
  await page.getByRole('link', { name: 'Software Change Requests' }).click()
  await expect(page.getByRole('heading', { name: 'Software Change Requests' })).toBeVisible()
  await page.getByLabel('Search change requests').fill('round robin')
  await expect(page.getByText(scr.displayNumber)).toBeVisible()
  await page.getByLabel('Search change requests').fill('ZZZ-NO-MATCH')
  await expect(page.locator('.historyEmpty')).toContainText('No software change requests match “ZZZ-NO-MATCH” for Build 3.3.')
  await page.getByLabel('Lifecycle state filter').selectOption('Draft')
  await expect(page.locator('.historyEmpty')).toContainText('No software change requests match “ZZZ-NO-MATCH” within the draft filter for Build 3.3.')
  await page.getByRole('button', { name: 'Clear search' }).click()
  await expect(page.locator('.historyEmpty')).toContainText('No draft software change requests match Build 3.3.')
  await page.getByRole('button', { name: 'Clear lifecycle filter', exact: true }).click()
  await expect(page.getByText(scr.displayNumber)).toBeVisible()
  await expect(page.locator('.historyTabs')).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Record Software Build' })).toHaveCount(0)

  // The implementation is retained for possible future reuse, but no product route exposes it.
  const buildResponse = await request.post(`${apiBase}/api/builds`, { data: {
    projectId: workspace.project.id, releaseId: workspace.release.id, baselineId: baseline.id,
    buildNumber: 'FMS-3.3.0-rc1', description: 'Dormant build-management contract probe',
  } })
  expect(buildResponse.ok(), await buildResponse.text()).toBeTruthy()
  const build = await buildResponse.json()
  const detail = await request.get(`${apiBase}/api/builds/${build.id}`)
  expect(detail.ok(), await detail.text()).toBeTruthy()
  expect((await detail.json()).scrs.map((item:{id:string})=>item.id)).toContain(scr.id)

  const root = `/programs/${workspace.program.id}/projects/${workspace.project.id}/releases/${workspace.release.id}`
  for (const retired of ['release-planning', 'baselines']) {
    await page.goto(`${root}/${retired}`)
    await expect(page.getByRole('heading', { name: 'Page not found' })).toBeVisible()
  }
})

test('an unfiltered empty build still reports that no change requests are recorded', async ({ page, request }) => {
  await apiLogin(request)
  const suffix=Date.now().toString().slice(-7),programName=`Empty History ${suffix}`
  const workspaceResponse = await request.post(`${apiBase}/api/workspaces`, { data: {
    programName, programCode: `EH${suffix}`, projectName: 'Empty FMS Software', softwareProduct: 'Flight Management Software', initialRelease: '4.1', initialReleaseIsReleased: false,
  } })
  expect(workspaceResponse.ok()).toBeTruthy()

  await login(page, 'admin', { openProject: false })
  await selectProgram(page, programName)
  await openNavigationGroup(page,'SOFTWARE ENGINEERING')
  await page.getByRole('link', { name: 'Software Change Requests' }).click()

  await expect(page.locator('.historyEmpty')).toHaveText('No software change requests are recorded for Build 4.1.')
  await expect(page.getByRole('button', { name: 'Clear search' })).toHaveCount(0)
})
