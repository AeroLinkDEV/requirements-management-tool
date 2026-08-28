import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, login, showcaseSeed } from './auth'

/**
 * The #816 Personnel page: Project Leadership first, roster as membership, identity details per person.
 *
 * Each leadership-flow test creates its own accounts and data via the API before the browser opens, so
 * no test depends on or is contaminated by another test's mutations. The browser verifies the UI
 * rendering and the specific interaction; the authority enforcement is proven by the API tests.
 */

const LEADERSHIP_POSITIONS = [
  'ProjectEngineer', 'ProgramManager', 'EngineeringManager', 'ConfigurationManager',
  'SystemEngineeringLead', 'SoftwareEngineeringLead', 'SystemTestLead', 'SoftwareTestLead',
]

const openPersonDetails = async (page: import('@playwright/test').Page, member: string, displayName: string) => {
  await page.locator(`[data-member="${member}"]`).getByRole('button', { name: displayName, exact: true }).click()
  await expect(page.getByRole('heading', { name: displayName, level: 2 })).toBeVisible()
}

test('Project Leadership reports exactly the eight positions with backup state on every card', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await login(page, 'admin', { openProject: false })
  await page.getByRole('link', { name: 'Open FMS Product Development' }).click()
  await page.getByRole('button', { name: 'Personnel' }).click()
  await expect(page.getByRole('heading', { name: 'Project Leadership', level: 2 })).toBeVisible()

  const positions = page.locator('[data-position]')
  await expect(positions).toHaveCount(8)
  const named = await positions.evaluateAll(items => items.map(item => item.getAttribute('data-position')))
  expect(named).toEqual(LEADERSHIP_POSITIONS)
  await expect(page.getByText('Nobody assigned')).toHaveCount(8)
  const backupStates = page.locator('.positionCard').getByText(/No backup assigned|Backup /)
  await expect(backupStates).toHaveCount(8)
  await expect(page.getByText('No backup assigned')).toHaveCount(8)
  await expect(page.locator('[data-assurance="SoftwareQualityAnalyst"]')).toBeVisible()
  await expect(page.locator('[data-assurance="Airworthiness"]')).toBeVisible()
})

test('a leader can be assigned from an eligible member and replaced with another eligible member', async ({ page, request }) => {
  test.setTimeout(180_000)
  const showcase = await showcaseSeed(request)
  await apiLogin(request)
  const suffix = Date.now().toString(36)

  // Create two Project Engineer base-role members via the API.
  for (const [key, name] of [['pe1', 'PE First'], ['pe2', 'PE Second']] as const) {
    const created = await request.post(`${apiBase}/api/admin/users`, {
      data: { userName: `s3.pe.${key}.${suffix}`, displayName: name, email: `s3.pe.${key}.${suffix}@example.test`, temporaryPassword: 'AeroLink!2026' },
    })
    expect(created.ok(), await created.text()).toBeTruthy()
    const userId = (await created.json()).id as string
    const granted = await request.post(`${apiBase}/api/projects/${showcase.projectId}/personnel`, {
      data: { userId, roles: ['ProjectEngineer'] },
    })
    expect(granted.ok(), await granted.text()).toBeTruthy()
  }

  await page.setViewportSize({ width: 1440, height: 900 })
  await login(page, 'admin', { openProject: false })
  await page.getByRole('link', { name: 'Open FMS Product Development' }).click()
  await page.getByRole('button', { name: 'Personnel' }).click()
  await expect(page.getByRole('heading', { name: 'Project Leadership', level: 2 })).toBeVisible()

  const engineerCard = page.locator('[data-position="ProjectEngineer"]')
  await engineerCard.getByRole('button', { name: 'Assign leader' }).click()
  await expect(page.getByRole('dialog')).toBeVisible()
  await page.locator('.directoryResult', { hasText: 'PE First' }).click()
  await page.getByRole('button', { name: 'Confirm' }).click()
  await expect(engineerCard).toContainText('PE First')
  await expect(engineerCard).toContainText('Elevated authority')

  await engineerCard.getByRole('button', { name: 'Replace leader' }).click()
  await page.locator('.directoryResult', { hasText: 'PE Second' }).click()
  await page.getByRole('button', { name: 'Confirm' }).click()
  await expect(engineerCard).toContainText('PE Second')
  await expect(engineerCard).toContainText('Elevated authority')
})

test('a standing backup is named and removed, and the roster records it', async ({ page, request }) => {
  test.setTimeout(180_000)
  const showcase = await showcaseSeed(request)
  await apiLogin(request)
  const suffix = Date.now().toString(36)

  for (const [key, name] of [['bk1', 'BK Primary'], ['bk2', 'BK Deputy']] as const) {
    const created = await request.post(`${apiBase}/api/admin/users`, {
      data: { userName: `s3.bk.${key}.${suffix}`, displayName: name, email: `s3.bk.${key}.${suffix}@example.test`, temporaryPassword: 'AeroLink!2026' },
    })
    expect(created.ok(), await created.text()).toBeTruthy()
    const userId = (await created.json()).id as string
    await request.post(`${apiBase}/api/projects/${showcase.projectId}/personnel`, {
      data: { userId, roles: ['SystemEngineer'] },
    })
  }

  await page.setViewportSize({ width: 1440, height: 900 })
  await login(page, 'admin', { openProject: false })
  await page.getByRole('link', { name: 'Open FMS Product Development' }).click()
  await page.getByRole('button', { name: 'Personnel' }).click()
  await expect(page.getByRole('heading', { name: 'Project Leadership', level: 2 })).toBeVisible()

  const leadCard = page.locator('[data-position="SystemEngineeringLead"]')
  await leadCard.getByRole('button', { name: 'Assign leader' }).click()
  await page.locator('.directoryResult', { hasText: 'BK Primary' }).click()
  await page.getByRole('button', { name: 'Confirm' }).click()
  await expect(leadCard).toContainText('BK Primary')

  await leadCard.getByRole('button', { name: 'Assign backup' }).click()
  await page.locator('.directoryResult', { hasText: 'BK Deputy' }).click()
  await page.getByRole('button', { name: 'Confirm' }).click()
  await expect(leadCard.getByText('Backup BK Deputy')).toBeVisible()

  await leadCard.getByRole('button', { name: 'Remove backup' }).click()
  await expect(leadCard.getByText('No backup assigned')).toBeVisible()
})

test('a person is added from the directory with several base roles in one confirm', async ({ page }) => {
  test.setTimeout(120_000)
  await page.setViewportSize({ width: 1440, height: 900 })
  await login(page, 'admin', { openProject: false })
  await page.getByRole('link', { name: 'Open FMS Product Development' }).click()
  await page.getByRole('button', { name: 'Personnel' }).click()

  await page.getByRole('button', { name: '+ Add Person to Project' }).click()
  await page.getByLabel('Search the directory').fill('avery')
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
  test.setTimeout(120_000)
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
