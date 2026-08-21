import { expect, test } from '@playwright/test'
import { apiBase, login } from './auth'

test('Project configuration authors a disposable graph, records history, refuses activation, and nests approvals', async ({ page }) => {
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
  const workspace = await created.json() as { project: { name: string } }
  const slug = workspace.project.name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '')

  await page.goto(`/projects/${slug}/configuration`)
  await expect(page.getByRole('heading', { name: 'Project configuration', level: 1 })).toBeVisible()
  await expect(page.locator('.ladderRow')).toHaveCount(3)

  // Remove the middle level and use the server-provided selected-step relationship editor for System -> LowLevel.
  await page.locator('.ladderRow').nth(1).getByRole('button', { name: 'Remove' }).click()
  await page.getByRole('button', { name: 'Add relationship' }).click()
  await page.getByPlaceholder('Why is this ladder changing?').fill('Use the direct System to Low-Level pilot graph')
  await page.getByRole('button', { name: 'Save draft' }).click()
  await expect(page.getByText('Draft configuration saved with immutable history evidence.')).toBeVisible()

  await page.getByRole('button', { name: /History/ }).click()
  await expect(page.getByRole('columnheader', { name: 'When' })).toBeVisible()
  await expect(page.getByText('Use the direct System to Low-Level pilot graph')).toBeVisible()
  await page.locator('details').first().locator('summary').click()
  await expect(page.getByText('System>LowLevel')).toBeVisible()

  await page.getByRole('button', { name: /Requirement ladder/ }).click()
  await page.getByPlaceholder('Why is this ladder changing?').fill('Attempt the named readiness gate')
  await page.getByRole('button', { name: 'Attempt activation' }).click()
  const activationAlert = page.getByRole('alert')
  await expect(activationAlert).toContainText('approval.workflow-subject')
  await expect(activationAlert).toContainText('release.reconciliation')
  await expect(activationAlert).not.toContainText('change-request.authoring')

  // The old deep link remains readable, while the same approval surface is nested in Project configuration.
  await page.goto(`/projects/${slug}/approval-configuration`)
  await expect(page.getByRole('heading', { name: 'Approval configuration', level: 1 })).toBeVisible()
  await page.goto(`/projects/${slug}/configuration/approvals`)
  await expect(page.getByRole('heading', { name: 'Approval configuration', level: 1 })).toBeVisible()
})
