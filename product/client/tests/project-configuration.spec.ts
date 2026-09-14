import { expect, test } from '@playwright/test'
import { apiBase, login } from './auth'

test('Project configuration exposes the effective ladder, history, and nested approvals for a new Project', async ({ page }) => {
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
  // Project names are display values and may collide. The route contract is the stable server Project id.
  await page.goto(`/projects/${workspace.project.id}/configuration`)
  await expect(page.getByRole('heading', { name: 'Project configuration', level: 1 })).toBeVisible()
  await expect(page.locator('.ladderRow')).toHaveCount(3)

  // New projects establish the accepted ladder before their first content. It is already the effective
  // authority, so this surface is a truthful read-only projection until a separate controlled revision exists.
  await expect(page.getByText(/active and immutable/i)).toBeVisible()
  await expect(page.getByRole('button', { name: 'Attempt activation' })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Add level' })).toHaveCount(0)
  for (const row of await page.locator('.ladderRow').all()) {
    await expect(row.locator('select:not([aria-label])')).toHaveCount(1)
    await expect(row.locator('select:not([aria-label])')).toBeDisabled()
  }
  await expect(page.locator('.relationshipRow')).toHaveCount(2)

  await page.getByRole('button', { name: /History/ }).click()
  await expect(page.getByRole('columnheader', { name: 'When' })).toBeVisible()
  await expect(page.getByText('Activated the new project ladder before first project content.')).toBeVisible()
  await page.locator('details').first().locator('summary').click()
  await expect(page.getByText('System>HighLevel')).toBeVisible()

  await page.getByRole('button', { name: /Requirement ladder/ }).click()

  // The runtime consumer must receive the activated direct graph, not reconstruct the legacy HLR rung or
  // continue using the pre-activation draft. One target queue means one assessment read even in StrictMode.
  let lowLevelAssessmentRequests = 0
  page.on('request', request => {
    if (request.url().includes('/api/downstream-assessments') && request.url().includes('targetLevel=LowLevel')) {
      lowLevelAssessmentRequests++
    }
  })
  await page.goto(`/programs/${workspace.program.id}/projects/${workspace.project.id}/releases/${workspace.release.id}/software/change-requests?level=LLR`)
  const downstreamQueue = page.locator('.downstreamQueue')
  await expect(downstreamQueue).toHaveCount(1)
  await expect(downstreamQueue).toContainText('LLR engineering conclusion')
  await expect(downstreamQueue).toHaveAttribute('data-queue-state', /empty|rows/)
  expect(lowLevelAssessmentRequests).toBe(1)

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
  await expect(page.getByRole('option', { name: 'Software HLR' })).toHaveCount(1)
  await expect(page.getByRole('option', { name: 'Software LLR' })).toHaveCount(1)
  const verification = nav.locator('.navGroup').filter({ has: page.locator('summary').filter({ hasText: 'VERIFICATION' }) })
  await verification.locator('summary').click()
  await verification.getByRole('button', { name: 'Software' }).click()
  await expect(verification.getByRole('link', { name: 'Software LLR Test Results' })).toBeVisible()
  await expect(verification.getByRole('link', { name: 'Software HLR Test Results' })).toBeVisible()
  await expect(verification.getByRole('link', { name: 'Test Case/Procedure Explorer' })).toBeVisible()

  await page.goto(`/programs/${workspace.program.id}/projects/${workspace.project.id}/releases/${workspace.release.id}/software-verification/hlr/procedures`)
  await expect(page.getByRole('heading', { name: 'Software Test Case/Procedure Explorer' })).toBeVisible()

  // The old deep link remains readable, while the same approval surface is nested in Project configuration.
  await page.goto(`/projects/${workspace.project.id}/approval-configuration`)
  await expect(page.getByRole('heading', { name: 'Approval configuration', level: 1 })).toBeVisible()
  await page.goto(`/projects/${workspace.project.id}/configuration/approvals`)
  await expect(page.getByRole('heading', { name: 'Approval configuration', level: 1 })).toBeVisible()
})
