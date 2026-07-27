import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, login, openNavigationGroup } from './auth'

// A released build is closed, and the product used to offer to author into it anyway.
//
// Pressing New Change Request while the released build was selected opened the editor, took the whole change
// case, and produced a change request allocated to a build that had already shipped — a record that could never
// reach a baseline, be incorporated, or be revised. It is also the likely reason a saved draft could not be
// found afterwards: it was allocated to the released build while the list being searched was filtered to the
// in-work one.
//
// This drives it through the browser, because the fix has to be reachable and not merely enforced: the server
// refusal is asserted separately in ClosedReleaseAuthoringTests.
test('a released build explains where to raise a change instead of taking one', async ({ page, request }) => {
  await apiLogin(request)
  const suffix = Date.now().toString().slice(-7)
  const programName = `Closed Release Program ${suffix}`
  const created = await request.post(`${apiBase}/api/workspaces`, { data: {
    programName, programCode: `CR${suffix}`, projectName: 'FMS Software',
    softwareProduct: 'Flight Management Software', initialRelease: '1.5', initialReleaseIsReleased: true,
  } })
  expect(created.ok(), await created.text()).toBeTruthy()
  const workspace = await created.json()

  // A successor to switch to, planned the way the product plans one.
  const successor = await request.post(`${apiBase}/api/releases`, { data: {
    projectId: workspace.project.id, version: '1.6', predecessorReleaseId: workspace.release.id,
  } })
  expect(successor.ok(), await successor.text()).toBeTruthy()

  await login(page)
  await page.locator('.program > select:not(.releaseSelector)').selectOption({ label: programName })
  // The product opens on the in-work build, which is right, so the released one is chosen deliberately.
  await page.getByLabel('Active release').selectOption({ label: '1.5 · Released' })
  await openNavigationGroup(page, 'ENGINEERING')
  // Reads "New Change Request" and is labelled for the discipline it acts on, which is the accessible name.
  await page.getByRole('link', { name: 'New System SCR' }).click()

  // Told, not silently refused, and told which build to use.
  await expect(page.getByRole('heading', { name: '1.5 has been released' })).toBeVisible()
  await expect(page.getByText(/could never reach a baseline/)).toBeVisible()

  // And the way out is one press, landing in the editor rather than back at the start.
  await page.getByRole('button', { name: /Switch to 1\.6 and continue/ }).click()
  await expect(page.getByLabel(/Title/).first()).toBeVisible()
  await expect(page.getByRole('heading', { name: '1.5 has been released' })).toBeHidden()
})
