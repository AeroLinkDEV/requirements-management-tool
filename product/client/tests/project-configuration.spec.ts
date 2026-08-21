import { expect, test } from '@playwright/test'
import { apiBase, login } from './auth'

test('Project configuration authors and activates a disposable graph, records history, and nests approvals', async ({ page }) => {
  await login(page, 'admin', { openProject: false })
  const suffix = Date.now().toString(36)
  const created = await page.request.post(`${apiBase}/api/workspaces`, { data: {
    programName: `Ladder UI ${suffix}`,
    programCode: `LU${suffix}`,
    projectName: `Ladder UI Project ${suffix}`,
    softwareProduct: 'Ladder UI Software',
    initialRelease: '1.0',
    initialReleaseIsReleased: false,
  } })
  expect(created.ok(), await created.text()).toBeTruthy()
  const workspace = await created.json() as { program: { id: string }; project: { id: string; name: string }; release: { id: string } }
  const slug = workspace.project.name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '')

  await page.goto(`/projects/${slug}/configuration`)
  await expect(page.getByRole('heading', { name: 'Project configuration', level: 1 })).toBeVisible()
  await expect(page.locator('.ladderRow')).toHaveCount(3)

  // Re-add LowLevel from the catalogue. Its JsonStringEnumConverter response contains all four capability
  // flags as names, so this proves the client masks the catalogue projection before rendering checkboxes.
  await page.locator('.ladderRow').nth(2).getByRole('button', { name: 'Remove' }).click()
  await page.getByRole('button', { name: 'Add level' }).click()
  const addedLowLevel = page.locator('.ladderRow').nth(2)
  await expect(addedLowLevel.locator('select')).toHaveValue('LowLevel')
  await expect(addedLowLevel.locator('input[type="checkbox"]')).toHaveCount(4)
  for (const checkbox of await addedLowLevel.locator('input[type="checkbox"]').all()) await expect(checkbox).toBeChecked()

  // Remove the middle level and use the server-provided selected-step relationship editor for System -> LowLevel.
  await page.locator('.ladderRow').nth(1).getByRole('button', { name: 'Remove' }).click()
  await page.getByRole('button', { name: 'Add relationship' }).click()
  await page.getByPlaceholder('Why is this ladder changing?').fill('Use the direct System to Low-Level pilot graph')
  await page.getByRole('button', { name: 'Save draft' }).click()
  await expect(page.getByText('Draft configuration saved with immutable history evidence.')).toBeVisible()
  const savedLowLevel = page.locator('.ladderRow').nth(1)
  await expect(savedLowLevel.locator('select')).toHaveValue('LowLevel')
  for (const checkbox of await savedLowLevel.locator('input[type="checkbox"]').all()) await expect(checkbox).toBeChecked()

  await page.getByRole('button', { name: /History/ }).click()
  await expect(page.getByRole('columnheader', { name: 'When' })).toBeVisible()
  await expect(page.getByText('Use the direct System to Low-Level pilot graph')).toBeVisible()
  await page.locator('details').first().locator('summary').click()
  await expect(page.getByText('System>LowLevel')).toBeVisible()

  await page.getByRole('button', { name: /Requirement ladder/ }).click()
  await page.getByPlaceholder('Why is this ladder changing?').fill('Attempt the named readiness gate')
  await page.getByRole('button', { name: 'Attempt activation' }).click()
  await expect(page.getByRole('status')).toContainText('Ladder activated. Runtime surfaces now use the stored effective ladder.')
  await expect(page.locator('.ladderRow')).toHaveCount(2)
  const activatedLowLevel = page.locator('.ladderRow').nth(1)
  await expect(activatedLowLevel.locator('select')).toHaveValue('LowLevel')
  for (const checkbox of await activatedLowLevel.locator('input[type="checkbox"]').all()) await expect(checkbox).toBeChecked()
  await expect(page.getByText(/active and immutable/i)).toBeVisible()
  await expect(page.getByRole('button', { name: 'Attempt activation' })).toHaveCount(0)

  await page.goto(`/programs/${workspace.program.id}/projects/${workspace.project.id}/releases/${workspace.release.id}/command-center`)
  const nav = page.getByRole('navigation', { name: 'Primary navigation' })
  await expect(nav).toBeVisible()
  const requirements = nav.locator('.navGroup').filter({ has: page.locator('summary').filter({ hasText: 'REQUIREMENTS' }) })
  await requirements.locator('summary').click()
  await expect(requirements.getByRole('button', { name: 'System' })).toBeVisible()
  await expect(requirements.getByRole('button', { name: 'Software' })).toBeVisible()
  await requirements.getByRole('button', { name: 'Software' }).click()
  await expect(requirements.getByRole('link', { name: 'Software Requirements Explorer' })).toBeVisible()
  await expect(requirements.getByRole('link', { name: 'Generated Software Requirements Documents' })).toBeVisible()
  await requirements.getByRole('link', { name: 'Software Requirements Explorer' }).click()
  await expect(page.getByRole('combobox', { name: 'Level filter' })).toBeVisible()
  await expect(page.getByRole('option', { name: 'Software HLR' })).toHaveCount(0)
  await expect(page.getByRole('option', { name: 'Software LLR' })).toHaveCount(1)
  const verification = nav.locator('.navGroup').filter({ has: page.locator('summary').filter({ hasText: 'VERIFICATION' }) })
  await verification.locator('summary').click()
  await verification.getByRole('button', { name: 'Software' }).click()
  await expect(verification.getByRole('link', { name: 'Software LLR Test Results' })).toBeVisible()
  await expect(verification.getByRole('link', { name: 'Software HLR Test Results' })).toHaveCount(0)
  await expect(verification.getByRole('link', { name: 'Software Test Procedure Explorer' })).toBeVisible()

  let removedLevelProcedureRequests = 0
  page.on('request', request => {
    if (request.url().includes('/api/test-procedures')) removedLevelProcedureRequests++
  })
  await page.goto(`/programs/${workspace.program.id}/projects/${workspace.project.id}/releases/${workspace.release.id}/software-verification/hlr/procedures`)
  await expect(page.getByRole('heading', { name: 'Workspace unavailable' })).toBeVisible()
  expect(removedLevelProcedureRequests).toBe(0)

  // The old deep link remains readable, while the same approval surface is nested in Project configuration.
  await page.goto(`/projects/${slug}/approval-configuration`)
  await expect(page.getByRole('heading', { name: 'Approval configuration', level: 1 })).toBeVisible()
  await page.goto(`/projects/${slug}/configuration/approvals`)
  await expect(page.getByRole('heading', { name: 'Approval configuration', level: 1 })).toBeVisible()
})
