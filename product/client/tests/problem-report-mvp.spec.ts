import { expect, test } from '@playwright/test'
import { login, selectProgram } from './auth'

test('an engineer creates a structured Draft PR and advances it through the SCCB workbench', async ({ page }) => {
  test.setTimeout(240_000)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
  await page.goto(new URL(`${root}/problem-reports`, page.url()).toString(), { waitUntil: 'load' })

  await page.getByRole('button', { name: '+ Record problem' }).click()
  const dialog = page.getByRole('dialog', { name: 'Record a problem' })
  const title = `Position-source alert clears early ${Date.now()}`
  await dialog.getByLabel('Title').fill(title)
  await dialog.getByRole('group', { name: 'Add content to Problem Description' }).getByRole('button', { name: 'Paragraph' }).click()
  await dialog.getByLabel('Problem Description paragraph 1').fill('The disagreement alert clears while the source mismatch is still present.')
  await dialog.getByText('Additional information and impact').click()
  await dialog.getByLabel('System / aircraft impact').fill('The flight crew can lose annunciation of a persistent navigation-source disagreement.')
  await dialog.getByLabel('System requirements').selectOption('Yes')
  await dialog.getByLabel('Code').selectOption('Yes')
  await dialog.getByLabel('Tests').selectOption('Yes')
  await dialog.getByRole('button', { name: 'Save Draft PR' }).click()

  await expect(page.getByRole('heading', { name: title })).toBeVisible()
  await expect(page.locator('.prState')).toHaveText('Draft')
  await expect(page.getByText('RAISED BY', { exact: true })).toBeVisible()
  await expect(page.getByText('ASSIGNED USER', { exact: true })).toBeVisible()
  await expect(page.getByText('TARGET BUILD', { exact: true })).toBeVisible()
  await expect(page.locator('.prImpactGrid').getByText('System requirements')).toBeVisible()
  await expect(page.locator('.prImpactGrid').getByText('Yes', { exact: true })).toHaveCount(3)

  await page.getByRole('button', { name: 'Ready for SCCB' }).click()
  await expect(page.locator('.prState')).toHaveText('Ready For SCCB')
  await page.reload({ waitUntil: 'load' })
  await expect(page.getByRole('heading', { name: title })).toBeVisible()
  await page.getByRole('button', { name: 'Open after SCCB review' }).click()
  await expect(page.locator('.prState')).toHaveText('Open')
  await page.getByRole('button', { name: 'Start implementing' }).click()
  await expect(page.locator('.prState')).toHaveText('Implementing')

  await page.getByRole('button', { name: /History/ }).click()
  await expect(page.getByRole('heading', { name: 'Immutable lifecycle history' })).toBeVisible()
  const history = page.locator('.prTimeline')
  await expect(history.getByText('Ready For SCCB')).toBeVisible()
  await expect(history.getByText('Opened By SCCB')).toBeVisible()
  await expect(history.getByText('Implementation Started')).toBeVisible()
})

/**
 * Correcting a Problem Report after it has left Draft.
 *
 * A Problem Report used to be the one controlled record edited through a form of its own, which posted the
 * whole record with an expected version and hoped nobody else was doing the same — and its edit policy still
 * named lifecycle states the MVP no longer produces, so in practice only a Draft could be checked out. It is
 * the record most likely to need correcting while the work it describes is still moving, so it now takes the
 * same exclusive server lease as everything else: check out, edit, check in, and the change lands in the
 * report's own History.
 */
test('an Open Problem Report is checked out, corrected, and the correction survives with its history', async ({ page }) => {
  test.setTimeout(240_000)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
  await page.goto(new URL(`${root}/problem-reports`, page.url()).toString(), { waitUntil: 'load' })

  await page.getByRole('button', { name: '+ Record problem' }).click()
  const dialog = page.getByRole('dialog', { name: 'Record a problem' })
  const stamp = Date.now()
  await dialog.getByLabel('Title').fill(`Autopilot disconnect tone lags ${stamp}`)
  await dialog.getByRole('group', { name: 'Add content to Problem Description' }).getByRole('button', { name: 'Paragraph' }).click()
  await dialog.getByLabel('Problem Description paragraph 1').fill('The tone follows the disconnect by about a second.')
  await dialog.getByRole('button', { name: 'Save Draft PR' }).click()
  await expect(page.locator('.prState')).toHaveText('Draft')

  await page.getByRole('button', { name: 'Ready for SCCB' }).click()
  await page.getByRole('button', { name: 'Open after SCCB review' }).click()
  await expect(page.locator('.prState')).toHaveText('Open')

  // The state that used to offer no controlled editing route at all.
  await page.getByRole('button', { name: 'Check out & edit' }).click()
  const editor = page.getByRole('dialog', { name: /^Edit PR-/ })
  await expect(editor).toBeVisible({ timeout: 30_000 })
  await expect(editor.getByText('CONTROLLED DRAFT / EXCLUSIVE LEASE')).toBeVisible()
  const workaround = `Use the redundant aural channel until build ${stamp} is released.`
  // Only these controlled values change. The evidence must bind the values themselves, not merely the
  // aggregate version increment caused by check-in.
  await editor.getByLabel('Type').selectOption('Code')
  await editor.getByLabel('Workaround').fill(workaround)
  await editor.getByRole('button', { name: 'Check in' }).click()
  await expect(editor).toHaveCount(0, { timeout: 30_000 })

  await expect(page.getByRole('heading', { name: `Autopilot disconnect tone lags ${stamp}` })).toBeVisible({ timeout: 30_000 })
  await expect(page.locator('.prIdentity').getByText('Code', { exact: true })).toBeVisible()
  // The lifecycle did not move because the record was corrected.
  await expect(page.locator('.prState')).toHaveText('Open')

  // A record, not a screen state: it survives a reload, and it is in the report's own History.
  await page.reload({ waitUntil: 'load' })
  await expect(page.getByRole('heading', { name: `Autopilot disconnect tone lags ${stamp}` })).toBeVisible({ timeout: 30_000 })
  await expect(page.getByText(workaround)).toBeVisible()
  await page.getByRole('button', { name: /History/ }).click()
  const checkIn = page.locator('.prTimeline article').filter({ hasText: 'Details Checked In' })
  await expect(checkIn).toContainText('Snapshot schema 2')
  await expect(checkIn).toContainText('Type Code')
  await expect(checkIn).toContainText(`Workaround ${workaround}`)
})
