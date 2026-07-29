import { expect, test } from '@playwright/test'
import { apiBase, login, openNavigationGroup, selectProgram } from './auth'

/**
 * Autosave protects typing, never the record. What is asserted here is that work survives a reload without
 * anything being committed, that a recovered draft is offered rather than applied, and that discarding it
 * really discards it.
 */
test('unfinished authoring survives a reload, is offered rather than applied, and can be discarded', async ({ page }) => {
  test.setTimeout(90_000)
  await login(page)

  const suffix = Date.now().toString().slice(-7)
  const programName = `Autosave ${suffix}`
  const created = await page.request.post(`${apiBase}/api/workspaces`, {
    data: {
      programName, programCode: `AS${suffix}`, projectName: 'Autosave Project',
      softwareProduct: 'Autosave Product', initialRelease: '1.0', initialReleaseIsReleased: false,
    },
  })
  expect(created.ok(), await created.text()).toBeTruthy()
  const workspace = await created.json()
  await selectProgram(page, programName)

  const openAuthoring = async () => {
    await openNavigationGroup(page, 'ENGINEERING')
    await page.getByRole('link', { name: /New System SCR/ }).first().click()
    await expect(page.locator('.pasField').first()).toBeVisible()
  }

  await openAuthoring()
  const title = `Oceanic sequencing ${suffix}`
  await page.getByLabel('Title', { exact: true }).fill(title)
  await page.getByLabel('Problem', { exact: true }).fill('Sequencing drifts on long oceanic legs.')

  // Saved a second after typing stops, and it says so — people trust what they can see.
  await expect(page.locator('.draftState.saved')).toBeVisible({ timeout: 10_000 })

  // Nothing was submitted. A draft must never become part of the record on its own.
  const before = await page.request.get(
    `${apiBase}/api/scrs?projectId=${workspace.project.id}&page=1&pageSize=50`)
  expect(JSON.stringify(await before.json())).not.toContain(title)

  await page.reload()
  await openAuthoring()

  // Offered, not applied. Silently repopulating a form is how somebody edits old text without noticing.
  const restore = page.locator('.draftRestore')
  await expect(restore).toBeVisible()
  await expect(page.getByLabel('Title', { exact: true })).toHaveValue('')

  await restore.getByRole('button', { name: 'Restore my draft' }).click()
  await expect(page.getByLabel('Title', { exact: true })).toHaveValue(title)
  await expect(restore).toBeHidden()

  // Discarding really discards: reopening offers nothing.
  await page.reload()
  await openAuthoring()
  await page.locator('.draftRestore').getByRole('button', { name: 'Discard it' }).click()
  await page.reload()
  await openAuthoring()
  await expect(page.locator('.draftRestore')).toBeHidden()
})
