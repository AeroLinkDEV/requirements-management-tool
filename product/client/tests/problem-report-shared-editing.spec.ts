import { expect, test } from '@playwright/test'
import { chooseCategory, login, selectProgram, writeRichField } from './auth'

/**
 * A Problem Report is the record a Project works on together, and the person who can correct it is rarely
 * the person it happens to be assigned to.
 *
 * It did not read that way. The detail page offered "Check out & edit" only to the responsible engineer,
 * and the server agreed twice over — the checkout named that engineer as the record's governing author,
 * and the check-in engine demanded an engineering role on top. So a report sitting in Verifying showed
 * nothing at all to the person who found the mistake in it, whose only remaining move was to raise a
 * second report contradicting the first.
 *
 * Priya Raman holds Airworthiness and nothing else: a member of the Program with no engineering authority
 * anywhere, and not this report's owner. Every gate that used to exist would have refused her.
 */
test('a Project member who does not own a Verifying Problem Report can still correct it', async ({ page }) => {
  test.setTimeout(240_000)
  const stamp = Date.now()
  const title = `Shared correction in Verifying ${stamp}`
  const rootCause = `Queued behind the annunciator ${stamp}`

  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
  const problemReports = new URL(`${root}/problem-reports`, page.url()).toString()
  await page.goto(problemReports, { waitUntil: 'load' })

  await page.getByRole('button', { name: '+ Record problem' }).click()
  const raise = page.getByRole('dialog', { name: 'Record a problem' })
  await raise.getByLabel('Title').fill(title)
  await raise.getByRole('group', { name: 'Add content to Problem Description' })
    .getByRole('button', { name: 'Paragraph' }).click()
  await raise.getByRole('textbox', { name: 'Problem Description paragraph 1' })
    .fill('The disconnect tone follows the disconnect by about a second.')
  await chooseCategory(raise, 'Code Issue — Functional Impact')
  await raise.getByRole('button', { name: 'Save Draft PR' }).click()
  await expect(page.locator('.prState')).toHaveText('Draft')
  await page.locator('.prFlow').getByRole('button', { name: 'Ready for SCCB →', exact: true }).click()
  // Asserted here so a refused transition is reported where it happened, rather than as a button that
  // never appears three steps later.
  await expect(page.locator('.prState')).toHaveText('Ready for SCCB', { timeout: 30_000 })

  // Opening is SCCB authority, which the owner does not hold, so it is a different person again.
  const open = async (userName: string) => {
    await login(page, userName, { openProject: false })
    await selectProgram(page, 'Flight Management System Live Program')
    await page.goto(problemReports, { waitUntil: 'load' })
    await page.getByLabel('Search').fill(title)
    await page.locator('.prList').getByText(title).click()
    await expect(page.getByRole('heading', { name: title })).toBeVisible({ timeout: 30_000 })
  }
  await open('systems.lead')
  await page.locator('.prFlow').getByRole('button', { name: 'Open →', exact: true }).click()
  await expect(page.locator('.prState')).toHaveText('Open')
  await page.locator('.prFlow').getByRole('button', { name: 'Start implementing →', exact: true }).click()
  await expect(page.locator('.prState')).toHaveText('Implementing')
  await page.locator('.prFlow').getByRole('button', { name: 'Move to Verifying →', exact: true }).click()
  await expect(page.locator('.prState')).toHaveText('Verifying')

  // The reported case, exactly: a report in Verifying, opened by somebody who does not own it.
  await open('airworthiness.lead')
  await expect(page.locator('.prState')).toHaveText('Verifying')
  await expect(page.locator('.prIdentity')).not.toContainText('Priya Raman')

  await page.getByRole('button', { name: 'Check out & edit' }).click()
  const editor = page.getByRole('dialog', { name: /^Edit PR-/ })
  await expect(editor).toBeVisible({ timeout: 30_000 })
  await writeRichField(editor, 'Root cause', rootCause)
  await editor.getByRole('button', { name: 'Check in' }).click()
  await expect(page.getByText(rootCause)).toBeVisible({ timeout: 30_000 })

  // Correcting the report does not take it: the assignment is somebody else's decision, and History
  // credits the correction to whoever actually made it.
  await expect(page.locator('.prIdentity')).not.toContainText('Priya Raman')
  await page.getByRole('button', { name: /^History/ }).click()
  await expect(page.locator('.prTimeline').getByText('Details Checked In').first()).toBeVisible({ timeout: 30_000 })
  // The timeline names the account rather than the display name the identity panel resolves. That
  // difference is not this change's to make, so this asserts what the surface actually says.
  await expect(page.locator('.prTimeline article').filter({ hasText: 'Details Checked In' }).first())
    .toContainText('airworthiness.lead')
})
