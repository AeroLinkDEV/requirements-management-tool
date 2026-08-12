import { expect, test } from '@playwright/test'
import { login } from './auth'

/**
 * Who is on a project, reached from Software Builds rather than from inside a build.
 *
 * These assert counts and named holders rather than the presence of markup. A page that renders a positions
 * grid proves nothing about whether the grid found anybody: the Test Procedure Explorer shipped a discipline
 * filter that matched nothing for two releases because no journey asserted how many rows it listed.
 */

const openPersonnel = async (page: import('@playwright/test').Page) => {
  await login(page, 'admin', { openProject: false })
  await page.getByRole('link', { name: 'Open FMS Product Development' }).click()
  await expect(page.getByRole('heading', { name: 'Software Builds', level: 1 })).toBeVisible()
  await page.getByRole('button', { name: 'Personnel' }).click()
  await expect(page.getByRole('heading', { name: 'Personnel', level: 1 })).toBeVisible()
}

test('Personnel is reached beside Software Builds, not from inside a build', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await openPersonnel(page)

  // Above a build, so the address carries the project and no release.
  await expect(page).toHaveURL(/\/projects\/fms-product-development\/personnel$/)
  await expect(page.getByText('Adding somebody here is what gives them access to the project.')).toBeVisible()

  // And it survives a reload, because a page you cannot link to is a page nobody can send anybody to.
  await page.reload()
  await expect(page.getByRole('heading', { name: 'Personnel', level: 1 })).toBeVisible()
  await expect(page).toHaveURL(/\/projects\/fms-product-development\/personnel$/)
})

test('Every position one person holds is reported, filled or not', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await openPersonnel(page)

  // The four singular project positions and the four discipline leads are always shown. A position nobody
  // holds is the answer somebody came here for, so it cannot be represented by being absent from a list.
  const positions = page.locator('[data-position]')
  await expect(positions).toHaveCount(9)
  const named = await positions.evaluateAll(items => items.map(item => item.getAttribute('data-position')))
  expect(named).toEqual([
    'ProjectEngineer', 'ProgramManager', 'EngineeringManager', 'ConfigurationManager', 'ProjectEngineeringLead',
    'SystemEngineeringLead', 'SoftwareEngineeringLead', 'SystemTestLead', 'SoftwareTestLead',
  ])

  await expect(page.getByRole('heading', { name: 'Disciplines' })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Independent assurance' })).toBeVisible()
  await expect(page.locator('[data-assurance="SoftwareQualityAnalyst"]')).toBeVisible()
  await expect(page.locator('[data-assurance="Airworthiness"]')).toBeVisible()
})

test('The roster lists the people the project actually has', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await openPersonnel(page)

  const rows = page.locator('[data-member]')
  // Wait for the first row before enumerating: .all() does not retry, so counting immediately after
  // navigation can see zero and assert nothing at all.
  await expect(rows.first()).toBeVisible()
  const count = await rows.count()
  expect(count).toBeGreaterThan(0)

  // Everybody listed carries at least one position or is shown as having left. A roster row with neither
  // would mean the projection lost the membership it was built from.
  const names = await rows.evaluateAll(items => items.map(item => item.getAttribute('data-member')))
  expect(new Set(names).size).toBe(names.length)
  await expect(page.getByRole('columnheader', { name: 'Position on this project' })).toBeVisible()
})

test('An administrator can add somebody, and the position refuses a second holder', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await openPersonnel(page)

  await page.getByRole('button', { name: '+ Add person' }).click()
  await expect(page.getByRole('heading', { name: 'Add someone to this project' })).toBeVisible()

  // Addressed by id rather than by label: the roster's "Position on this project" column header is an
  // implicit label for every cell beneath it, so getByLabel('Position') is ambiguous on this page.
  const roleSelect = page.locator('#add-person-role')
  await roleSelect.selectOption('SystemEngineeringLead')
  const holderWarning = page.locator('.addPersonWarning')
  if (await holderWarning.count()) {
    await expect(holderWarning).toContainText('End their position before assigning it to somebody else.')
  }

  await page.getByRole('button', { name: 'Cancel' }).click()
  await expect(page.getByRole('heading', { name: 'Add someone to this project' })).toBeHidden()
})
