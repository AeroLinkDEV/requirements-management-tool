import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, login } from './auth'

test('managed Word documents remain one Project-wide register across build navigation', async ({ page }) => {
  test.setTimeout(240_000)
  await login(page, 'software.author')

  await page.getByRole('link', { name: 'Documentation Center' }).click()
  await expect(page).toHaveURL(/\/programs\/[0-9a-f-]+\/projects\/[0-9a-f-]+\/documentation-center$/)
  await expect(page.getByRole('heading', { name: 'Documentation Center' })).toBeVisible()
  await expect(page.getByText('7 matching records')).toBeVisible()
  await expect(page.locator('.mdMetrics').getByText('4', { exact: true })).toBeVisible()

  await page.getByRole('button', { name: /SDP SDP-000001/ }).click()
  await expect(page).toHaveURL(/documentation-center\/[0-9a-f-]+$/)
  await page.reload({ waitUntil: 'load' })
  await expect(page.getByRole('heading', { name: 'FMS Software Development Plan' })).toBeVisible()
  await expect(page.locator('.mdIdentity').getByText(/Draft SDP-000001\.01/)).toBeVisible()

  await expect(page.getByText('Add GitLab merge-request traceability and desktop connector responsibilities.')).toBeVisible()
  await page.getByRole('button', { name: 'Edit formal scope' }).click()
  const summaryEditor = page.locator('.mdInlineForm')
  await summaryEditor.getByLabel('Formal revision scope').fill('Add GitLab traceability and preserve immutable check-in evidence.')
  await summaryEditor.getByLabel('Reason for correction').fill('Clarify the controlled formal scope before review.')
  await summaryEditor.getByRole('button', { name: 'Record formal scope correction' }).click()
  await expect(page.getByText(/formal revision scope for SDP-000001\.01 was revised/i)).toBeVisible()
  await page.reload({ waitUntil: 'load' })
  await expect(page.getByText('Add GitLab traceability and preserve immutable check-in evidence.')).toBeVisible()
  await page.getByRole('button', { name: 'Versions' }).click()
  await expect(page.getByText('Most recent checked-in draft.')).toBeVisible()

  await page.getByRole('button', { name: 'Review & release' }).click()
  await expect(page.getByRole('heading', { name: 'Electronic signatures for SDP-000001.01' })).toBeVisible()
  await expect(page.getByText('No signatures are recorded for this exact revision.')).toBeVisible()

  await page.getByRole('button', { name: /Back to Software Builds/ }).click()
  await page.getByRole('button', { name: 'Open build 1.5' }).click()
  await page.goto(page.url().replace(/command-center$/, 'documentation-center'))
  await expect(page).toHaveURL(/\/programs\/[0-9a-f-]+\/projects\/[0-9a-f-]+\/documentation-center$/)
  await expect(page.getByText('7 matching records')).toBeVisible()
  await expect(page.getByRole('button', { name: '+ New document' })).toBeVisible()
  await expect(page.locator('.mdList').getByText(/\.01 · (Draft|In Review|Returned)/)).toHaveCount(4)
})

test('Documentation Center back navigation retains a non-showcase Project across refresh', async ({ page, request }) => {
  await apiLogin(request)
  const suffix = Date.now().toString().slice(-7)
  const workspaceResponse = await request.post(`${apiBase}/api/workspaces`, { data: {
    programName: `Document navigation ${suffix}`,
    programCode: `DN${suffix}`,
    projectName: `Review Back Project ${suffix}`,
    softwareProduct: 'Document navigation product',
    initialRelease: '1.0',
    initialReleaseIsReleased: false,
  } })
  expect(workspaceResponse.ok(), await workspaceResponse.text()).toBeTruthy()
  const workspace = await workspaceResponse.json()
  const created = await request.post(`${apiBase}/api/managed-documents`, { data: {
    projectId: workspace.project.id,
    acronym: 'SQAP',
    documentType: 'Software Quality Assurance Plan',
    title: `Navigation SQAP ${suffix}`,
    changeSummary: 'Prove Project-specific back navigation.',
  } })
  expect(created.ok(), await created.text()).toBeTruthy()

  await login(page, 'admin', { openProject: false })
  await page.goto(`/programs/${workspace.program.id}/projects/${workspace.project.id}/documentation-center`)
  await page.getByRole('button', { name: new RegExp(`SQAP-${suffix}|Navigation SQAP ${suffix}`) }).click()
  await page.getByRole('button', { name: /Back to Software Builds/ }).click()

  await expect(page).toHaveURL(new RegExp(`/projects/review-back-project-${suffix}/builds$`))
  await page.reload({ waitUntil: 'load' })
  await expect(page).toHaveURL(new RegExp(`/projects/review-back-project-${suffix}/builds$`))
  await page.getByRole('button', { name: 'Imported baselines' }).click()
  await expect(page).toHaveURL(new RegExp(`/projects/review-back-project-${suffix}/imported-baselines$`))
})
