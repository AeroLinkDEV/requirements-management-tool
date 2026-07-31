import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, login, openNavigationGroup, selectProgram, showcaseSeed } from './auth'

test('upload, job, identity, and conflict failures stay visible without false success or stuck controls', async ({ page, request }) => {
  test.setTimeout(180_000)
  await apiLogin(request)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')

  const administration = page.locator('.navGroup').filter({ has: page.locator('summary').filter({ hasText: 'ADMINISTRATION' }) })
  await administration.locator('summary').click()
  await administration.getByRole('link', { name: 'People & Authority' }).click()
  await expect(page.getByRole('heading', { name: 'People & Authority' })).toBeVisible()
  await page.getByLabel('Search people and authority').fill('systems.author')
  const account = page.locator('.userTable article').filter({ hasText: 'systems.author' })
  await expect(account).toBeVisible()
  await page.route('**/api/admin/users/*/state', route => route.fulfill({
    status: 403,
    contentType: 'application/json',
    body: JSON.stringify({ error: 'Administrator authority was withdrawn before this operation.' }),
  }))
  await account.getByRole('button', { name: 'Disable' }).click()
  await expect(page.getByRole('alert')).toContainText('Administrator authority was withdrawn')
  await expect(account.getByRole('button', { name: 'Disable' })).toBeEnabled()
  await page.unroute('**/api/admin/users/*/state')

  await administration.getByRole('link', { name: 'System Operations' }).click()
  await page.getByRole('button', { name: 'Job engine' }).click()
  await page.route('**/api/enterprise-hardening/jobs', route => route.fulfill({
    status: 503,
    contentType: 'text/plain',
    body: 'temporarily unavailable',
  }))
  const createJob = page.getByRole('button', { name: 'Generate controlled export' })
  await createJob.click()
  await expect(page.getByRole('alert')).toContainText('No success was recorded')
  await expect(createJob).toBeEnabled()
  await expect(page.getByRole('status')).toHaveCount(0)
  await page.unroute('**/api/enterprise-hardening/jobs')

  await page.getByRole('button', { name: 'Content vault' }).click()
  await page.getByLabel('Document label').fill('Failure-preservation attachment')
  await page.getByLabel('Select file').setInputFiles({
    name: 'failure-preservation.txt',
    mimeType: 'text/plain',
    buffer: Buffer.from('controlled upload that must not be cleared on transport failure'),
  })
  await page.route('**/api/enterprise-hardening/attachments', route => route.abort('connectionfailed'))
  const upload = page.getByRole('button', { name: 'Upload controlled version' })
  await upload.click()
  await expect(page.getByRole('alert')).toContainText('Your input has been preserved')
  await expect(page.getByLabel('Document label')).toHaveValue('Failure-preservation attachment')
  await expect(upload).toBeEnabled()
  await page.unroute('**/api/enterprise-hardening/attachments')

  await page.getByRole('button', { name: 'Concurrency' }).click()
  await page.getByRole('button', { name: 'Open editing session' }).click()
  await expect(page.getByText(/Session active/)).toBeVisible()
  await page.route('**/api/enterprise-hardening/edit-sessions/*', route => route.fulfill({
    status: 409,
    contentType: 'application/json',
    body: JSON.stringify({
      id: '0f8fad5b-d9cb-469f-a165-70867728950e',
      baseJson: JSON.stringify({ statement: 'Common controlled base' }),
      localJson: JSON.stringify({ statement: 'Retained local draft' }),
      remoteJson: JSON.stringify({ statement: 'Competing remote draft' }),
    }),
  }))
  await page.getByRole('button', { name: 'Save draft checkpoint' }).click()
  await expect(page.getByText('MERGE REQUIRED')).toBeVisible()
  await expect(page.getByText('Retained local draft')).toBeVisible()
  await expect(page.getByRole('status')).toContainText('No content was overwritten')
})

test('a rejected approval preserves signature input and records no approval evidence', async ({ page, request, playwright }) => {
  test.setTimeout(180_000)
  const showcase = await showcaseSeed(request)
  await apiLogin(request)
  const requirementsResponse = await request.get(`${apiBase}/api/requirements?projectId=${showcase.projectId}&baselineId=${showcase.releasedBaselineId}&scope=System&page=1&pageSize=1`)
  expect(requirementsResponse.ok(), await requirementsResponse.text()).toBeTruthy()
  const requirements = await requirementsResponse.json()

  const engineer = await playwright.request.newContext()
  const engineerLogin = await engineer.post(`${apiBase}/api/auth/login`, { data: { userName: 'test.engineer', password: 'AeroLink!2026' } })
  expect(engineerLogin.ok(), await engineerLogin.text()).toBeTruthy()
  const title = `Rejected approval ${Date.now()}`
  const created = await engineer.post(`${apiBase}/api/test-procedures`, { data: {
    projectId: showcase.projectId,
    baseNumber: 'SERVER-ALLOCATED',
    title,
    objective: 'Prove rejected approval remains visibly uncommitted.',
    preconditions: 'A Draft procedure exists.',
    steps: 'Attempt approval after authority is withdrawn.',
    expectedResult: 'The Draft and signature input remain available with no signature evidence.',
    requirementRevisionIds: [requirements.items[0].revisionId],
    level: 'System',
  } })
  expect(created.ok(), await created.text()).toBeTruthy()
  const procedure = await created.json()
  await engineer.dispose()

  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Testing Coverage' }).click()
  // The list is paged, so a procedure created through the API is found rather than scrolled to.
  await page.getByLabel('Find a procedure').fill(title)
  const row = page.locator('.procedureLibrary .coverageRow').filter({ hasText: title })
  await expect(row).toBeVisible()
  await row.getByRole('button', { name: 'Review & approve' }).click()
  const dialog = page.locator('.signatureModal')
  await dialog.getByLabel('Re-enter your password').fill('AeroLink!2026')
  await page.route(`**/api/test-procedures/${procedure.revisionId}/approve`, route => route.fulfill({
    status: 403,
    contentType: 'application/json',
    body: JSON.stringify({ error: 'Approver authority is no longer active for this Program.' }),
  }))
  await dialog.getByRole('button', { name: 'Sign & approve' }).click()
  await expect(page.getByRole('alert')).toContainText('Approver authority is no longer active')
  await expect(dialog).toBeVisible()
  await expect(dialog.getByLabel('Re-enter your password')).toHaveValue('AeroLink!2026')
  await expect(dialog.getByRole('button', { name: 'Sign & approve' })).toBeEnabled()

  const signatures = await request.get(`${apiBase}/api/signatures?artifactId=${procedure.revisionId}`)
  expect(signatures.ok(), await signatures.text()).toBeTruthy()
  expect(await signatures.json()).toEqual([])
})
