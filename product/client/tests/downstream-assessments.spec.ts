import { expect, test } from '@playwright/test'
import { apiLogin, login, openNavigationGroup, showcaseSeed } from './auth'

test('software engineers receive build-scoped downstream assessments from approved System changes', async ({page,request}) => {
  const showcase=await showcaseSeed(request)
  await apiLogin(request)
  const apiResponse=await request.get(`${process.env.AEROLINK_E2E_API_BASE}/api/downstream-assessments?projectId=${showcase.projectId}&releaseId=${showcase.activeReleaseId}`)
  expect(apiResponse.ok(),await apiResponse.text()).toBeTruthy()
  expect((await apiResponse.json()).length).toBeGreaterThan(0)
  await login(page)
  await openNavigationGroup(page,'SOFTWARE ENGINEERING')
  await page.getByRole('link',{name:'Software Change Requests'}).click()

  await expect(page.getByRole('heading',{name:'Downstream change assessments'})).toBeVisible()
  const queue=page.locator('.downstreamQueue')
  await expect(queue.getByText('SCR-00031.00')).toBeVisible()
  await expect(queue.getByText('HLR assessment').first()).toBeVisible()
  await expect(queue.getByRole('button',{name:'Take it on'}).first()).toBeVisible()
  await expect(queue).toContainText('One Draft may answer several assessments')
  await expect(page.getByRole('heading',{name:'Software Change Requests'})).toBeVisible()
})
