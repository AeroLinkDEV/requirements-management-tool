import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, login, openNavigationGroup } from './auth'

test('deep links survive refresh, universal search resolves fragments, and checkout is read-only to another user',async({page,browser,request})=>{
 test.setTimeout(90_000)
 await apiLogin(request)
 const seed=await request.post(`${apiBase}/api/showcase/seed`,{timeout:60_000});expect(seed.ok(),await seed.text()).toBeTruthy()
 await login(page)
 await page.locator('.program > select:not(.releaseSelector)').selectOption({label:'Flight Management System Live Program'})
 await openNavigationGroup(page,'SYSTEMS ENGINEERING')
 await page.getByRole('link',{name:'System Requirements Explorer'}).click()
 await page.getByLabel('Search requirements').fill('0150')
 const requirement=page.getByText(/SYSR-000150\.\d{2}/).first();await expect(requirement).toBeVisible();await requirement.click()
 await expect(page).toHaveURL(/\/requirements\/[0-9a-f-]+\?discipline=system$/)
 const requirementUrl=page.url();await page.reload();await expect(page.getByText(/SYSR-000150\.\d{2}/).first()).toBeVisible();await expect(page).toHaveURL(requirementUrl)
 await page.keyboard.press('Control+K');await page.getByPlaceholder(/Search pages, SCRs/).fill('0150');await expect(page.getByRole('dialog').getByRole('link',{name:/SYSR-000150\.\d{2}/}).first()).toBeVisible()
 await page.keyboard.press('Escape')

 await page.getByRole('link',{name:/Command Center/}).click()
 await page.getByRole('button',{name:/Search & navigate/}).click();await page.getByPlaceholder(/Search pages, SCRs/).fill('SWCR-00000078')
 await page.getByRole('dialog').getByRole('link',{name:/SWCR-00000078\.00/}).click()
 await page.getByRole('button',{name:'Check out & edit'}).click()
 await page.getByLabel('Title').fill(`Autosaved controlled checkout ${Date.now()}`)
 await page.waitForTimeout(3200);await expect(page.locator('.autosaveState')).toContainText('Saved')
 const lockedUrl=page.url()

 const second=await browser.newContext();const reader=await second.newPage();await reader.goto(lockedUrl)
 await reader.getByLabel('Username').fill('software.author');await reader.getByLabel('Password').fill('AeroLink!2026');await reader.getByRole('button',{name:/Sign in securely/}).click()
 await expect(reader.getByText('Read-only while checked out')).toBeVisible();await expect(reader.getByRole('button',{name:/Read only · admin/})).toBeDisabled()
 await second.close()
 await page.getByRole('button',{name:'Discard checkout'}).click();await expect(page.getByRole('button',{name:'Check out & edit'})).toBeVisible()
})
