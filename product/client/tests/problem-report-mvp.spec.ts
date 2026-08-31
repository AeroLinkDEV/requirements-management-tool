import { expect, test } from '@playwright/test'
import { chooseCategory, login, selectProgram, writeRichField } from './auth'

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
  await dialog.getByRole('textbox', { name: 'Problem Description paragraph 1' }).fill('The disagreement alert clears while the source mismatch is still present.')
  await writeRichField(dialog, 'System / aircraft impact', 'The flight crew can lose annunciation of a persistent navigation-source disagreement.')
  await dialog.getByLabel('System requirements').selectOption('Yes')
  // Exact: the emphasis toolbar has an "Inline code" button, and a substring match claims both.
  await dialog.getByLabel('Code', { exact: true }).selectOption('Yes')
  await dialog.getByLabel('Tests').selectOption('Yes')
  // A Draft may be saved unclassified, but it cannot reach the SCCB that way.
  await chooseCategory(dialog, 'Code Issue — Functional Impact')
  await dialog.getByRole('button', { name: 'Save Draft PR' }).click()

  await expect(page.getByRole('heading', { name: title })).toBeVisible()
  await expect(page.locator('.prState')).toHaveText('Draft')
  await expect(page.getByText('RAISED BY', { exact: true })).toBeVisible()
  await expect(page.getByText('ASSIGNED USER', { exact: true })).toBeVisible()
  await expect(page.getByText('TARGET BUILD', { exact: true })).toBeVisible()
  const impact = page.getByRole('region', { name: 'Impact and linked evidence' })
  await expect(impact.locator('.impactRow').filter({ hasText: 'System requirements' })).toBeVisible()
  // The three areas answered Yes, each beside the area it answers rather than in a separate grid.
  await expect(impact.locator('.impactPill.yes')).toHaveCount(3)

  await page.locator('.prFlow').getByRole('button', { name: 'Ready for SCCB →', exact: true }).click()
  await expect(page.locator('.prState')).toHaveText('Ready for SCCB')
  // SCCB opening is restricted to the explicit opening-authority roles; an administrator's Project access
  // is not a substitute for that authority.
  await login(page, 'systems.lead', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await page.goto(new URL(`${root}/problem-reports`, page.url()).toString(), { waitUntil: 'load' })
  await page.getByLabel('Search').fill(title)
  await page.locator('.prList').getByText(title).click()
  await expect(page.getByRole('heading', { name: title })).toBeVisible()
  // The pane has to show this record at Ready for SCCB before the click, so a detail response for another
  // record arriving late cannot turn the Open click into a different record's lifecycle action. (#793)
  await expect(page.locator('.prState')).toHaveText('Ready for SCCB')
  await page.locator('.prFlow').getByRole('button', { name: 'Open →', exact: true }).click()
  await expect(page.locator('.prState')).toHaveText('Open')
  await page.getByRole('button', { name: 'Start implementing' }).click()
  await expect(page.locator('.prState')).toHaveText('Implementing')

  await page.getByRole('button', { name: /History/ }).click()
  await expect(page.getByRole('heading', { name: 'Immutable lifecycle history' })).toBeVisible()
  const history = page.locator('.prTimeline')
  await expect(history.locator('article').filter({ hasText: 'Draft → Ready for SCCB' })).toHaveCount(1)
  await expect(history.locator('article').filter({ hasText: 'Ready for SCCB → Open' })).toHaveCount(1)
  await expect(history.locator('article').filter({ hasText: 'Open → Implementing' })).toHaveCount(1)
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
  await dialog.getByRole('textbox', { name: 'Problem Description paragraph 1' }).fill('The tone follows the disconnect by about a second.')
  // A Draft may be unclassified, but it cannot reach the SCCB that way.
  await chooseCategory(dialog, 'Code Issue — Functional Impact')
  await dialog.getByRole('button', { name: 'Save Draft PR' }).click()
  await expect(page.locator('.prState')).toHaveText('Draft')

  // The transition must settle on the server before the identity switch: login() navigates the page away,
  // and an in-flight transition request caught mid-navigation is aborted, leaving the record a Draft for
  // whoever opens it next. (#793)
  await page.locator('.prFlow').getByRole('button', { name: 'Ready for SCCB →', exact: true }).click()
  await expect(page.locator('.prState')).toHaveText('Ready for SCCB')
  await login(page, 'systems.lead', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await page.goto(new URL(`${root}/problem-reports`, page.url()).toString(), { waitUntil: 'load' })
  await page.getByLabel('Search').fill(`Autopilot disconnect tone lags ${stamp}`)
  await page.locator('.prList').getByText(`Autopilot disconnect tone lags ${stamp}`).click()
  // As above: pin the pane to this record at Ready for SCCB before the Open click. (#793)
  await expect(page.getByRole('heading', { name: `Autopilot disconnect tone lags ${stamp}` })).toBeVisible()
  await expect(page.locator('.prState')).toHaveText('Ready for SCCB')
  await page.locator('.prFlow').getByRole('button', { name: 'Open →', exact: true }).click()
  await expect(page.locator('.prState')).toHaveText('Open')

  // SCCB authority opened the report; its assigned owner performs the controlled edit.
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await page.goto(new URL(`${root}/problem-reports`, page.url()).toString(), { waitUntil: 'load' })
  await page.getByLabel('Search').fill(`Autopilot disconnect tone lags ${stamp}`)
  await page.locator('.prList').getByText(`Autopilot disconnect tone lags ${stamp}`).click()
  await expect(page.locator('.prState')).toHaveText('Open')

  // The state that used to offer no controlled editing route at all.
  await page.getByRole('button', { name: 'Check out & edit' }).click()
  const editor = page.getByRole('dialog', { name: /^Edit PR-/ })
  await expect(editor).toBeVisible({ timeout: 30_000 })
  await expect(editor.getByText('CONTROLLED DRAFT / EXCLUSIVE LEASE')).toBeVisible()
  const workaround = `Use the redundant aural channel until build ${stamp} is released.`
  // Only these controlled values change. The evidence must bind the values themselves, not merely the
  // aggregate version increment caused by check-in.
  await chooseCategory(editor, 'Code Issue — Non-Functional Impact')
  await writeRichField(editor, 'Workaround', workaround)
  await editor.getByRole('button', { name: 'Check in' }).click()
  await expect(editor).toHaveCount(0, { timeout: 30_000 })

  await expect(page.getByRole('heading', { name: `Autopilot disconnect tone lags ${stamp}` })).toBeVisible({ timeout: 30_000 })
  // The record shows the category chosen at check-in: its code, its name, and — because a person chose
  // it — no "derived" marker.
  await expect(page.locator('.prIdentityCategory')).toContainText('32')
  await expect(page.locator('.prIdentityCategory')).toContainText('Code Issue — Non-Functional Impact')
  await expect(page.locator('.prIdentityCategory .catDerived')).toHaveCount(0)
  // The lifecycle did not move because the record was corrected.
  await expect(page.locator('.prState')).toHaveText('Open')

  // A record, not a screen state: it survives a reload, and it is in the report's own History.
  await page.reload({ waitUntil: 'load' })
  await expect(page.getByRole('heading', { name: `Autopilot disconnect tone lags ${stamp}` })).toBeVisible({ timeout: 30_000 })
  await expect(page.getByText(workaround)).toBeVisible()
  await page.getByRole('button', { name: /History/ }).click()
  const checkIn = page.locator('.prTimeline article').filter({ hasText: 'Details Checked In' })
  // Schema 3 retired the four-kind Type for the category vocabulary; schema 4 added the authored
  // companion to each narrative field; schema 5 added typed inline-image blocks; schema 6 commits the
  // exact active supporting-file manifest. Each step makes the snapshot incomparable field for field with
  // the one before it, which is what the version is for.
  await expect(checkIn).toContainText('Snapshot schema 6')
  await expect(checkIn).toContainText('Category CodeNonFunctional')
  await expect(checkIn).toContainText(`Workaround ${workaround}`)
})
