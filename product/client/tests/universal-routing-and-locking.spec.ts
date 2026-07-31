import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, login, openNavigationGroup, selectProgram, showcaseSeed } from './auth'

test('deep links survive refresh, universal search resolves fragments, and checkout is read-only to another user',async({page,browser,request})=>{
 test.setTimeout(90_000)
 await apiLogin(request)
 const showcase=await showcaseSeed(request)
 await apiLogin(request,'software.author')
 const draftResponse=await request.post(`${apiBase}/api/scr-drafts`,{data:{projectId:showcase.projectId,targetReleaseId:showcase.activeReleaseId,type:'Software',title:`Universal routing lock ${Date.now()}`,problem:'Prove a deep-linked draft remains controlled.',analysis:'Exercise universal search and concurrent checkout visibility.',solution:'Create an isolated draft owned by this journey.',requirementChanges:[{level:'HighLevel',kind:'Introduce',statement:'The FMS software shall preserve isolated deep-link checkout state.',rationale:'Independent routing and locking verification.',verificationMethod:'Test'}]}})
 expect(draftResponse.ok(),await draftResponse.text()).toBeTruthy();const draft=await draftResponse.json()
 await login(page,'software.author')
 await selectProgram(page,'Flight Management System Live Program')
 await openNavigationGroup(page,'SYSTEMS ENGINEERING')
 await page.getByRole('link',{name:'System Requirements Explorer'}).click()
 await page.getByLabel('Search requirements').fill('0150')
 const requirement=page.getByText(/SYSR-000150\.\d{2}/).first();await expect(requirement).toBeVisible();await requirement.click()
 await expect(page).toHaveURL(/\/requirements\/[0-9a-f-]+\?discipline=system$/)
 const requirementUrl=page.url();await page.reload();await expect(page.getByText(/SYSR-000150\.\d{2}/).first()).toBeVisible();await expect(page).toHaveURL(requirementUrl)
 await page.keyboard.press('Control+K');await page.getByPlaceholder(/Search pages, change requests/).fill('0150');await expect(page.getByRole('dialog').getByRole('link',{name:/SYSR-000150\.\d{2}/}).first()).toBeVisible()
 await page.keyboard.press('Escape')

 await page.getByRole('link',{name:/Command Center/}).click()
 await page.getByRole('button',{name:/Search & navigate/}).click();await page.getByPlaceholder(/Search pages, change requests/).fill(draft.baseNumber)
 await page.getByRole('dialog').getByRole('link').filter({hasText:draft.displayNumber}).click()
 await page.getByRole('button',{name:'Check out & edit'}).click()
 await page.getByLabel('Title').fill(`Autosaved controlled checkout ${Date.now()}`)
 // No sleep before this: toContainText already retries until it matches or times out, so the 3.2 seconds were
 // spent whether autosave took 300ms or the full debounce. The generous timeout keeps the slow case covered.
 await expect(page.locator('.autosaveState')).toContainText('Saved', { timeout: 15_000 })
 const lockedUrl=page.url()

 const second=await browser.newContext();const reader=await second.newPage();await reader.goto(lockedUrl)
 await reader.getByLabel('Username').fill('admin');await reader.getByLabel('Password').fill('AeroLink!2026');await reader.getByRole('button',{name:/Sign in securely/}).click()
 // The holder is named, not identified by the account they signed in with: software.author is Daniel Reyes.
 // The point of the assertion is unchanged — a reader sees who holds the lock and cannot take it.
 await expect(reader.getByText('Read-only while checked out')).toBeVisible();await expect(reader.getByRole('button',{name:/Read only · Daniel Reyes/})).toBeDisabled()
 await second.close()
 // Discarding a checkout releases the lock on the server and the workspace reloads what it holds. The wait
 // is the suite's 30 seconds rather than the 5-second default, because this is a server round-trip and the
 // default has now failed three separate assertions in this suite on a loaded runner — each time reading as
 // "the control never came back" when it had simply not come back yet.
 await page.getByRole('button',{name:'Discard checkout'}).click()
 await expect(page.getByRole('button',{name:'Check out & edit'})).toBeVisible({timeout:30_000})
})
