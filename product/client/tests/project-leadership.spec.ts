import { expect, test } from '@playwright/test'
import { apiLogin, apiBase, login, showcaseSeed } from './auth'

/**
 * The #816 Personnel page: Project Leadership first, roster as membership, identity details per person.
 *
 * Each test sets up its own data via the API (accounts, memberships, leadership assignments) and then
 * opens the browser to verify the UI renders and behaves correctly. This makes the tests fully
 * independent of execution order and shared worker database state.
 */

const LEADERSHIP_POSITIONS = [
  'ProjectEngineer', 'ProgramManager', 'EngineeringManager', 'ConfigurationManager',
  'SystemEngineeringLead', 'SoftwareEngineeringLead', 'SystemTestLead', 'SoftwareTestLead',
]

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

test('a leader can be assigned from an eligible member and then replaced', async ({ page, request }) => {
  test.setTimeout(180_000)
  const showcase = await showcaseSeed(request)
  await apiLogin(request)

  // Create two members with the Project Engineer base role via the API.
  var suffix = Date.now().toString(36)
  const pe1 = await request.post(`${apiBase}/api/admin/users`, {
    data: { userName: `s3.pe.first.${suffix}`, displayName: 'PE First', email: `pe1.${suffix}@example.test`, temporaryPassword: 'AeroLink!2026' },
  })
  expect(pe1.ok(), await pe1.text()).toBeTruthy()
  const pe1Id = (await pe1.json()).id as string
  const pe2 = await request.post(`${apiBase}/api/admin/users`, {
    data: { userName: `s3.pe.second.${suffix}`, displayName: 'PE Second', email: `pe2.${suffix}@example.test`, temporaryPassword: 'AeroLink!2026' },
  })
  expect(pe2.ok(), await pe2.text()).toBeTruthy()
  const pe2Id = (await pe2.json()).id as string

  await request.post(`${apiBase}/api/projects/${showcase.projectId}/personnel`, {
    data: { userId: pe1Id, roles: ['ProjectEngineer'] },
  })
  await request.post(`${apiBase}/api/projects/${showcase.projectId}/personnel`, {
    data: { userId: pe2Id, roles: ['ProjectEngineer'] },
  })

  await page.setViewportSize({ width: 1440, height: 900 })
  await login(page, 'admin', { openProject: false })
  await page.getByRole('link', { name: 'Open FMS Product Development' }).click()
  await page.getByRole('button', { name: 'Personnel' }).click()
  await expect(page.getByRole('heading', { name: 'Project Leadership', level: 2 })).toBeVisible()

  const engineerCard = page.locator('[data-position="ProjectEngineer"]')

  // Assign the first PE holder as the Project Engineer leader.
  await engineerCard.getByRole('button', { name: 'Assign leader' }).click()
  await expect(page.getByRole('dialog')).toBeVisible()
  await page.locator('.directoryResult', { hasText: 'PE First' }).click()
  await page.getByRole('button', { name: 'Confirm' }).click()
  await expect(engineerCard).toContainText('PE First')
  await expect(engineerCard).toContainText('Elevated authority')

  // The ineligible picker entry explains the base-role requirement.
  await engineerCard.getByRole('button', { name: 'Replace leader' }).click()
  await expect(page.getByRole('dialog')).toBeVisible()
  await expect(page.locator('.directoryResult.ineligible').first()).toBeVisible()

  // Replace with the second PE holder.
  await page.locator('.directoryResult', { hasText: 'PE Second' }).click()
  await page.getByRole('button', { name: 'Confirm' }).click()
  await expect(engineerCard).toContainText('PE Second')
  await expect(engineerCard).toContainText('Elevated authority')
})

test('a standing backup carries the same authority until removed', async ({ page, request }) => {
  test.setTimeout(180_000)
  const showcase = await showcaseSeed(request)
  await apiLogin(request)

  var suffix = Date.now().toString(36)
  const primary = await request.post(`${apiBase}/api/admin/users`, {
    data: { userName: `s3.bk.primary.${suffix}`, displayName: 'Backup Primary', email: `bp1.${suffix}@example.test`, temporaryPassword: 'AeroLink!2026' },
  })
  const primaryId = (await primary.json()).id as string
  const backup = await request.post(`${apiBase}/api/admin/users`, {
    data: { userName: `s3.bk.backup.${suffix}`, displayName: 'Backup Deputy', email: `bd1.${suffix}@example.test`, temporaryPassword: 'AeroLink!2026' },
  })
  const backupId = (await backup.json()).id as string

  await request.post(`${apiBase}/api/projects/${showcase.projectId}/personnel`, {
    data: { userId: primaryId, roles: ['SystemEngineer'] },
  })
  await request.post(`${apiBase}/api/projects/${showcase.projectId}/personnel`, {
    data: { userId: backupId, roles: ['SystemEngineer'] },
  })

  await page.setViewportSize({ width: 1440, height: 900 })
  await login(page, 'admin', { openProject: false })
  await page.getByRole('link', { name: 'Open FMS Product Development' }).click()
  await page.getByRole('button', { name: 'Personnel' }).click()
  await expect(page.getByRole('heading', { name: 'Project Leadership', level: 2 })).toBeVisible()

  // Assign the System Engineering Lead position to the primary holder.
  const leadCard = page.locator('[data-position="SystemEngineeringLead"]')
  await leadCard.getByRole('button', { name: 'Assign leader' }).click()
  await page.locator('.directoryResult', { hasText: 'Backup Primary' }).click()
  await page.getByRole('button', { name: 'Confirm' }).click()
  await expect(leadCard).toContainText('Backup Primary')

  // Name the standing backup.
  await leadCard.getByRole('button', { name: 'Assign backup' }).click()
  await page.locator('.directoryResult', { hasText: 'Backup Deputy' }).click()
  await page.getByRole('button', { name: 'Confirm' }).click()
  await expect(leadCard.getByText('Backup Deputy')).toBeVisible()

  // Remove the backup.
  await leadCard.getByRole('button', { name: 'Remove backup' }).click()
  await expect(leadCard.getByText('No backup assigned')).toBeVisible()
})
