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
  await expect(page.getByText('RESPONSIBLE OWNER', { exact: true })).toBeVisible()
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
