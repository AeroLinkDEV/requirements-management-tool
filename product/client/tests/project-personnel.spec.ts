import { expect, test } from '@playwright/test'
import { login } from './auth'

/**
 * The #816 Personnel page: Project Leadership first, roster as membership, identity details per person.
 *
 * These assert named holders, stable card order, and backup state on every one of the eight positions -
 * the presence of markup proves nothing about whether the page found anybody. The retired
 * ProjectEngineeringLead is deliberately absent, and every card exposes its backup state because a
 * position's cover is part of the answer, not optional furniture.
 *
 * Seed-name pairings used throughout (from IdentitySeeder): systems.author performs "Systems Requirements
 * Author"; software.author performs "Software Requirements Author"; test.author performs "Verification
 * Author"; test.engineer is "Ethan Brooks".
 */

const LEADERSHIP_POSITIONS = [
  'ProjectEngineer', 'ProgramManager', 'EngineeringManager', 'ConfigurationManager',
  'SystemEngineeringLead', 'SoftwareEngineeringLead', 'SystemTestLead', 'SoftwareTestLead',
]

const openPersonnel = async (page: import('@playwright/test').Page, userName = 'admin') => {
  await login(page, userName, { openProject: false })
  await page.getByRole('link', { name: 'Open FMS Product Development' }).click()
  await expect(page.getByRole('heading', { name: 'Software Builds', level: 1 })).toBeVisible()
  await page.getByRole('button', { name: 'Personnel' }).click()
  await expect(page.getByRole('heading', { name: 'Personnel', level: 1 })).toBeVisible()
}

const openPersonDetails = async (page: import('@playwright/test').Page, member: string, displayName: string) => {
  await page.locator(`[data-member="${member}"]`).getByRole('button', { name: displayName, exact: true }).click()
  await expect(page.getByRole('heading', { name: displayName, level: 2 })).toBeVisible()
}

async function ensureBaseRole(page, member, displayName, roleLabel) {
  const row = page.locator('[data-member="' + member + '"]')
  if (await row.locator('.roleChip').filter({ hasText: roleLabel }).count()) return
  await openPersonDetails(page, member, displayName)
  await page.getByLabel('Add a base role').selectOption({ label: roleLabel })
  await page.getByRole('button', { name: 'Close' }).click()
}

test('Project Leadership reports exactly the eight positions with backup state on every card', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await login(page, 'admin', { openProject: false })
  await page.getByRole('link', { name: 'Open FMS Product Development' }).click()
  await page.getByRole('button', { name: 'Personnel' }).click()
  await expect(page.getByRole('heading', { name: 'Project Leadership', level: 2 })).toBeVisible()

  // Stable order, all eight, and the retired ProjectEngineeringLead is not among them.
  const positions = page.locator('[data-position]')
  await expect(positions).toHaveCount(8)
  const named = await positions.evaluateAll(items => items.map(item => item.getAttribute('data-position')))
  expect(named).toEqual(LEADERSHIP_POSITIONS)

  // A fresh project has no elevations yet, so every card is honestly vacant rather than borrowing a
  // backup or a base role to fill itself.
  await expect(page.getByText('Nobody assigned')).toHaveCount(8)

  // Backup state is rendered for every card - a missing backup is a valid state, shown, not omitted.
  const backupStates = page.locator('.positionCard').getByText(/No backup assigned|Backup /)
  await expect(backupStates).toHaveCount(8)
  await expect(page.getByText('No backup assigned')).toHaveCount(8)
  await expect(page.locator('[data-assurance="SoftwareQualityAnalyst"]')).toBeVisible()
  await expect(page.locator('[data-assurance="Airworthiness"]')).toBeVisible()
})

test('a leader can be assigned only from people holding the required base role, then replaced', async ({ page }) => {
  test.setTimeout(120_000)
  await page.setViewportSize({ width: 1440, height: 900 })
  await login(page, 'admin', { openProject: false })
  await page.getByRole('link', { name: 'Open FMS Product Development' }).click()
  await page.getByRole('button', { name: 'Personnel' }).click()
  await expect(page.getByRole('heading', { name: 'Project Leadership', level: 2 })).toBeVisible()

  // The Project Engineer position requires the Project Engineer base role; nobody on the fresh roster
  // holds it, so the picker lists the members as ineligible with the reason attached.
  const position = 'ProjectEngineer'
  const engineerCard = page.locator('[data-position="ProjectEngineer"]')
  await engineerCard.getByRole('button', { name: 'Assign leader' }).click()
  await expect(page.getByRole('dialog')).toBeVisible()
  const ineligible = page.locator('.directoryResult.ineligible')
  await expect(ineligible.first()).toBeVisible()
  await expect(ineligible.first()).toContainText('Requires the Project Engineer role')

  // Grant the base role through the roster's person details, then the picker admits them.
  await page.getByRole('button', { name: 'Cancel' }).click()
  await openPersonDetails(page, 'software.author', 'Software Requirements Author')
  await page.getByLabel('Add a base role').selectOption({ label: 'Project Engineer' })
  await expect(page.locator('[data-member="software.author"] .roleChip').filter({ hasText: 'Project Engineer' })).toHaveCount(1)
  await page.getByRole('button', { name: 'Close' }).click()

  await engineerCard.getByRole('button', { name: 'Assign leader' }).click()
  await page.locator('.directoryResult', { hasText: 'Software Requirements Author' }).click()
  await page.getByRole('button', { name: 'Confirm' }).click()
  await expect(engineerCard).toContainText('Software Requirements Author')
  await expect(engineerCard).toContainText('Elevated authority')

  // A second eligible holder exists after the role is granted to them as well; replacement moves the
  // authority and the roster's leadership chip with it.
  await openPersonDetails(page, 'test.author', 'Verification Author')
  await page.getByLabel('Add a base role').selectOption({ label: 'Project Engineer' })
  await expect(page.locator('[data-member="test.author"] .roleChip').filter({ hasText: 'Project Engineer' })).toHaveCount(1)
  await page.getByRole('button', { name: 'Close' }).click()
  await engineerCard.getByRole('button', { name: 'Replace leader' }).click()
  await page.locator('.directoryResult', { hasText: 'Verification Author' }).click()
  await page.getByRole('button', { name: 'Confirm' }).click()
  await expect(engineerCard).toContainText('Verification Author')
  await expect(page.locator('[data-member="test.author"] .roleChip.lead')).toHaveCount(1)
})

test('a standing backup is named, carries the position until removed, and the roster records it', async ({ page }) => {
  test.setTimeout(120_000)
  await page.setViewportSize({ width: 1440, height: 900 })
  await login(page, 'admin', { openProject: false })
  await page.getByRole('link', { name: 'Open FMS Product Development' }).click()
  await page.getByRole('button', { name: 'Personnel' }).click()

  const position = 'ProjectEngineer'
  const engineerCard = page.locator('[data-position="ProjectEngineer"]')
  // Eligibility first: the Project Engineer position requires that base role. Tests of this file share
  // one worker database, so the grant runs only when the roster does not already show the role.
  const peAlreadyHeld = await page.locator('[data-member="software.author"] .roleChip')
    .filter({ hasText: 'Project Engineer' }).count()
  if (peAlreadyHeld === 0) {
    await openPersonDetails(page, 'software.author', 'Software Requirements Author')
    await page.getByLabel('Add a base role').selectOption(position)
    await page.getByRole('button', { name: 'Close' }).click()
    await expect(page.locator('[data-member="software.author"] .roleChip')
      .filter({ hasText: 'Project Engineer' })).toHaveCount(1)
  }
  await engineerCard.getByRole('button', { name: 'Assign leader' }).click()
  await page.locator('.directoryResult', { hasText: 'Software Requirements Author' }).click()
  await page.getByRole('button', { name: 'Confirm' }).click()
  await expect(engineerCard).toContainText('Software Requirements Author')
  await expect(page.locator('[data-member="software.author"] .roleChip.lead')).toHaveCount(1)

await ensureBaseRole(page, 'test.author', 'Verification Author', 'Project Engineer')
  await engineerCard.getByRole('button', { name: 'Assign backup' }).click()
  await page.locator('.directoryResult', { hasText: 'Verification Author' }).click()
  await page.getByRole('button', { name: 'Confirm' }).click()
  await expect(engineerCard.getByText('Backup Verification Author')).toBeVisible()
  await expect(page.locator('[data-member="test.author"]').getByText('Backup · Project Engineer')).toBeVisible()

  await engineerCard.getByRole('button', { name: 'Remove backup' }).click()
  await expect(engineerCard.getByText('No backup assigned')).toBeVisible()
  await expect(page.locator('[data-member="test.author"]').getByText('Backup · Project Engineer')).toHaveCount(0)
})

test('a person is added from the directory with several base roles in one confirm', async ({ page }) => {
  test.setTimeout(120_000)
  await page.setViewportSize({ width: 1440, height: 900 })
  await login(page, 'admin', { openProject: false })
  await page.getByRole('link', { name: 'Open FMS Product Development' }).click()
  await page.getByRole('button', { name: 'Personnel' }).click()

  await page.getByRole('button', { name: '+ Add Person to Project' }).click()
  await page.getByLabel('Search the directory').fill('avery')
  // Every active account of this seeded program is already a member, so the directory is genuinely empty
  // and the admin path is the one the owner designed: create the local person, then choose the roles.
  await page.getByRole('button', { name: 'Create local person/account' }).click()
  await page.getByLabel('Display name').fill('Avery Qualification')
  await page.getByLabel('Username').fill('avery.qualification')
  await page.getByLabel('Email').fill('avery.qualification@example.test')
  await page.getByLabel('Temporary password').fill('AeroLink!2026')
  await page.getByRole('button', { name: 'Create account' }).click()

  await page.getByRole('checkbox', { name: 'System Engineer' }).check()
  await page.getByRole('checkbox', { name: 'Airworthiness' }).check()
  await page.getByRole('button', { name: 'Add to project' }).click()

  const row = page.locator('[data-member="avery.qualification"]')
  await expect(row).toBeVisible()
  await expect(row).toContainText('System Engineer')
  await expect(row).toContainText('Airworthiness')
  await expect(row).toContainText('avery.qualification@example.test')
})

test('an administrator edits the current identity, and the change is scoped to now', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await login(page, 'admin', { openProject: false })
  await page.getByRole('link', { name: 'Open FMS Product Development' }).click()
  await page.getByRole('button', { name: 'Personnel' }).click()

  await openPersonDetails(page, 'test.engineer', 'Ethan Brooks')
  await page.getByLabel('Email').fill('ethan.brooks-reyes@example.test')
  await page.getByLabel('Display name').fill('Ethan Brooks-Reyes')
  await page.getByRole('button', { name: 'Save identity' }).click()
  await expect(page.locator('[data-member="test.engineer"]')).toContainText('Ethan Brooks-Reyes')
  await expect(page.locator('[data-member="test.engineer"]')).toContainText('ethan.brooks-reyes@example.test')

  // The username - the stable login identity - is not editable through this surface.
  await expect(page.getByLabel('Username')).toHaveCount(0)
})

test('a non-admin roster manager cannot create accounts or edit global identity', async ({ page }) => {
  test.setTimeout(120_000)
  await page.setViewportSize({ width: 1440, height: 900 })
  await login(page, 'program.manager', { openProject: false })
  await page.getByRole('link', { name: 'Open FMS Product Development' }).click()
  await page.getByRole('button', { name: 'Personnel' }).click()
  await expect(page.getByRole('heading', { name: 'Project Leadership', level: 2 })).toBeVisible()

  await page.getByRole('button', { name: '+ Add Person to Project' }).click()
  await page.getByLabel('Search the directory').fill('nobody-matches-this')
  await expect(page.getByText('An AeroLink administrator must create the account before the person can be added.')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Create local person/account' })).toHaveCount(0)
  await page.getByRole('button', { name: 'Cancel' }).click()

  await openPersonDetails(page, 'quality.analyst', 'Marcus Hale')
  await expect(page.getByText('Current identity (global administrator)')).toHaveCount(0)
})
