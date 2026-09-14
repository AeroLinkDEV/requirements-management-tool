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
  // authority, while the empty project remains eligible for a structural correction.
  await expect(page.getByText(/empty project may still make a structural correction/i)).toBeVisible()
  await expect(page.getByRole('button', { name: 'Save effective correction' })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Attempt activation' })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Add level' })).toBeVisible()
  for (const row of await page.locator('.ladderRow').all()) {
    await expect(row.locator('select:not([aria-label])')).toHaveCount(1)
    await expect(row.locator('select:not([aria-label])')).toBeEnabled()
  }
  await expect(page.locator('.relationshipRow')).toHaveCount(2)

  // Empty Active correction keeps the effective state, records immutable history, and refreshes the
  // application's runtime ladder immediately. Switching the supported Low-Level verification profile to
  // Case-only is a structural correction that leaves the software ladder and its direct edges intact.
  await page.getByLabel('Low-Level software verification profile').selectOption('Case')
  await page.getByLabel('Reason').fill('Keep the empty project at the approved HLR boundary')
  await page.getByRole('button', { name: 'Save effective correction' }).click()
  await expect(page.getByRole('status')).toContainText('Empty ladder correction saved and effective immediately')
  await expect(page.locator('.ladderRow')).toHaveCount(3)
  await expect(page.getByRole('button', { name: /History/ })).toContainText('2 attributed edits')

  await page.getByRole('button', { name: /History/ }).click()
  await expect(page.getByRole('columnheader', { name: 'When' })).toBeVisible()
  await expect(page.getByText('Keep the empty project at the approved HLR boundary')).toBeVisible()
  await expect(page.getByText('Activated the new project ladder before first project content.')).toBeVisible()
  await page.locator('details').first().locator('summary').click()
  // Scope the canonical snapshot assertion to the edited revision; older history can contain the same
  // edge and Playwright's substring locator would otherwise match both expanded code blocks.
  await expect(page.getByRole('row').filter({ hasText: 'Keep the empty project at the approved HLR boundary' })
    .locator('code').filter({ hasText: 'System>HighLevel' })).toBeVisible()

  await page.getByRole('button', { name: /Requirement ladder/ }).click()

  // Once a controlled authoring package exists, the same projection becomes locked. This exercises the
  // content predicate rather than encoding Active as an unconditional UI lock.
  const content = await page.request.post(`${apiBase}/api/change-request-drafts`, { data: {
    projectId: workspace.project.id,
    targetReleaseId: workspace.release.id,
    type: 'System',
    title: `Lock ladder after content ${suffix}`,
    problem: 'The empty project now has a controlled requirement.',
    analysis: 'The ladder must remain stable after authoring begins.',
    solution: 'Keep the accepted effective ladder as the content authority.',
    requirementChanges: [{
      level: 'System', kind: 'Introduce',
      statement: 'The project shall retain its accepted system ladder.',
      rationale: 'The first authored package establishes ladder dependency.',
      verificationMethod: 'Inspection',
    }],
  } })
  expect(content.ok(), await content.text()).toBeTruthy()
  await page.reload()
  await expect(page.getByRole('button', { name: 'Save effective correction' })).toHaveCount(0)
  await expect(page.getByText(/locked because controlled content depends on it/i)).toBeVisible()
  for (const row of await page.locator('.ladderRow').all()) {
    await expect(row.locator('select:not([aria-label])')).toBeDisabled()
  }

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
