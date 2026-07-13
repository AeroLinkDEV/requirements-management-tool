import { expect, test } from '@playwright/test'
import { apiLogin, login } from './auth'

test('searches full history and proves exact software build contents', async ({ page, request }) => {
  await apiLogin(request)
  const suffix=Date.now().toString().slice(-7),programName=`History Program ${suffix}`
  const workspaceResponse = await request.post('http://127.0.0.1:5080/api/workspaces', { data: {
    programName, programCode: `HI${suffix}`, projectName: 'FMS Software', softwareProduct: 'Flight Management Software', initialRelease: '3.3', initialReleaseIsReleased: false,
  } }); expect(workspaceResponse.ok()).toBeTruthy(); const workspace = await workspaceResponse.json()
  const scrResponse = await request.post('http://127.0.0.1:5080/api/scr-drafts', { data: {
    baseNumber: 'SCR-00000042', projectId: workspace.project.id, targetReleaseId: workspace.release.id, title: 'Introduce round robin routing', problem: 'Routing is unavailable', analysis: 'A new function is required', solution: 'Implement round robin routing', authorId: 'author',
    requirementChanges: [{ baseNumber: 'SWR-00002375', revision: 4, level: 'HighLevel', kind: 'Introduce', statement: 'The software shall provide round robin routing.', rationale: 'New function', verificationMethod: 'Test' }],
  } }); expect(scrResponse.ok()).toBeTruthy(); const scr = await scrResponse.json()
  await request.post(`http://127.0.0.1:5080/api/scrs/${scr.id}/submit`, { data: { approvers: [{ userId: 'admin', name: 'AeroLink Administrator' }] } })
  await request.post(`http://127.0.0.1:5080/api/scrs/${scr.id}/approve`, { data: { password: 'AeroLink!2026', meaning: 'Approved for test baseline assembly.' } })
  const baseline = await (await request.post('http://127.0.0.1:5080/api/baselines', { data: { baseNumber: 'SWBL-00000033', revision: 0, projectId: workspace.project.id, releaseId: workspace.release.id, name: 'FMS 3.3 exact manifest', actorId: 'cm' } })).json()
  await request.post(`http://127.0.0.1:5080/api/baselines/${baseline.id}/selections`, { data: { scrId: scr.id, actorId: 'cm' } })
  await request.post(`http://127.0.0.1:5080/api/baselines/${baseline.id}/freeze`, { data: { actorId: 'cm' } })
  await request.post(`http://127.0.0.1:5080/api/baselines/${baseline.id}/materialize-requirements`, { data: { actorId: 'cm' } })
  const historyResponse = await request.get(`http://127.0.0.1:5080/api/history/scrs?projectId=${workspace.project.id}&page=1&pageSize=50`)
  const historyBody = await historyResponse.text(); expect(historyResponse.status(), historyBody).toBe(200); expect(JSON.parse(historyBody).totalCount).toBe(1)

  await login(page)
  await page.locator('.program > select:not(.releaseSelector)').selectOption({ label: programName })
  await page.getByRole('button', { name: /Change Requests/ }).click()
  await expect(page.getByRole('heading', { name: 'Artifact & Build Explorer' })).toBeVisible()
  await page.getByLabel('Search history').fill('round robin')
  await expect(page.getByText('SCR-00000042.00')).toBeVisible()
  await page.getByRole('button', { name: /Requirement History/ }).click()
  await expect(page.getByText('SWR-00002375.04')).toBeVisible()
  await page.getByLabel('Search history').fill('')

  await page.getByRole('button', { name: 'Record Software Build' }).click()
  await page.getByPlaceholder('e.g. FMS-3.3.0-rc1').fill('FMS-3.3.0-rc1')
  await page.getByLabel('Frozen baseline').selectOption(baseline.id)
  await page.getByRole('button', { name: 'Record Build', exact: true }).click()
  await page.getByRole('button', { name: /Software Builds/ }).click()
  await page.getByRole('button', { name: /FMS-3.3.0-rc1/ }).click()
  await expect(page.getByText('SCR-00000042.00')).toBeVisible()
  await expect(page.getByText(/SWR-00002375.04.*Introduce/)).toBeVisible()
})
