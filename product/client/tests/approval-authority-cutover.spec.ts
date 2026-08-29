import { expect, test } from '@playwright/test'
import { apiBase, login } from './auth'

/**
 * The Slice 4 cutover journey, on an Approval Configuration surface of its own.
 *
 * A stage records two independent facts: the required PROJECT AUTHORITY (a base project role, or the one
 * accountable Project Leadership position) and what the signature MEANS (Review or Approval). Generic
 * Reviewer/Approver are no longer authority choices, nothing arrives pre-selected, and Save stays disabled
 * until every row names an authority. The same four names legitimately appear in both groups — the base
 * role is the job, the leadership entry is the position — so each option names its group.
 */

test('the approval editor demands an explicit modern authority and persists it truthfully', async ({ page }) => {
  test.setTimeout(120_000)
  await login(page, 'admin', { openProject: false })

  const suffix = Date.now().toString().slice(-7)
  const created = await page.request.post(`${apiBase}/api/workspaces`, {
    data: {
      programName: `Authority Cutover ${suffix}`, programCode: `AC${suffix}`, projectName: 'Authority Project',
      softwareProduct: 'Authority Product', initialRelease: '1.0', initialReleaseIsReleased: false,
    },
  })
  expect(created.ok(), await created.text()).toBeTruthy()
  const workspace = await created.json()
  const slug = workspace.project.name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '')
  await page.goto(`/projects/${slug}/approval-configuration`)
  await expect(page.getByRole('heading', { name: 'Approval configuration', level: 1 })).toBeVisible()

  await page.locator('[data-artifact="System"]').click()
  await page.getByRole('button', { name: 'Configure this artifact' }).click()

  // The wording names an authority, not a Program role, and the choices arrive in the two decided groups.
  // (A closed <select> reports its optgroups as not visible, so the groups are asserted in the DOM.)
  const authority = page.getByLabel('Required project authority 1')
  await expect(authority).toHaveCount(1)
  await expect(authority.locator('optgroup[label="Base project roles"] option')).toHaveCount(10)
  await expect(authority.locator('optgroup[label="Project Leadership"] option')).toHaveCount(8)

  const optionLabels = await authority.locator('option').evaluateAll(options => options.map(option => option.textContent?.trim()))
  expect(optionLabels).toContain('System Engineer')
  expect(optionLabels).toContain('System Engineering Lead — leadership position')
  // Reviewer, Approver and the retired Project Engineering Lead are not modern authorities.
  expect(optionLabels).not.toContain('Reviewer')
  expect(optionLabels).not.toContain('Approver')
  expect(optionLabels).not.toContain('Project Engineering Lead')
  // The duplicated names are distinguishable: the base role plain, the leadership entry labelled.
  expect(optionLabels.filter(label => label === 'Project Engineer')).toHaveLength(1)
  expect(optionLabels).toContain('Project Engineer — leadership position')

  // Nothing is pre-selected: a new row has no authority until somebody chooses one, and Save refuses.
  const save = page.getByRole('button', { name: 'Save and activate' })
  await expect(save).toBeDisabled()
  await page.getByLabel('Stage name 1').fill('Technical review')
  await expect(save).toBeDisabled()
  await authority.selectOption({ label: 'System Engineer' })
  await expect(save).toBeEnabled()
  await save.click()
  await expect(page.locator('.approvalConfigSuccess')).toBeVisible({ timeout: 30_000 })

  // Persisted truthfully: an explicit base-role authority and an independent Review signature.
  await expect(page.getByRole('columnheader', { name: 'Required project authority' })).toBeVisible()
  const row = page.locator('[data-stage="0"]')
  await expect(row).toContainText('Technical review')
  await expect(row).toContainText('Review')
  await expect(row).toContainText('System Engineer')
  await expect(page.locator('.legacyAuthority')).toHaveCount(0)

  // Revising the configuration produces the next explicit version: this time a leadership position whose
  // signature means Approval.
  await page.getByRole('button', { name: 'Edit configuration' }).click()
  await expect(page.getByLabel('Required project authority 1')).toHaveValue('BaseRole:SystemEngineer')
  await page.getByLabel('Required project authority 1').selectOption({ label: 'System Engineering Lead — leadership position' })
  await page.getByLabel('Signature 1').selectOption('Approval')
  await expect(save).toBeEnabled()
  await save.click()
  await expect(page.locator('.approvalConfigSuccess')).toBeVisible({ timeout: 30_000 })

  await page.reload()
  await expect(page.getByRole('columnheader', { name: 'Required project authority' })).toBeVisible()
  const revised = page.locator('[data-stage="0"]')
  await expect(revised).toContainText('Technical review')
  await expect(revised).toContainText('Approval')
  await expect(revised).toContainText('Project Leadership · System Engineering Lead')
})
