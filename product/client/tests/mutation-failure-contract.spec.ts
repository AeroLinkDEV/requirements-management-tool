import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, firstSectionId, login, selectProgram, showcaseSeed } from './auth'

const completeImpacts = JSON.stringify({
  trace: 'Not Affected',
  verification: 'Not Affected',
  documents: 'Not Affected',
  baseline: 'Not Affected',
  collaboration: 'Not Affected',
})

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

})

/**
 * A refused signature leaves the dialog, its input, and its control exactly as they were.
 *
 * This used to be shown on a procedure approval. There is no such thing now — a procedure is introduced or
 * changed by a test change request, and approving that package is what approves the work — so the contract is
 * held where a signature still happens: approving a change request. The dialog is the same component, and
 * what it must not do is close, clear the password, or leave its button dead after a refusal, because each of
 * those reads as "something happened" when nothing did.
 */
test('a rejected approval preserves signature input and records no approval evidence', async ({ page, request }) => {
  test.setTimeout(180_000)
  const suffix = Date.now().toString().slice(-7)
  const showcase = await showcaseSeed(request)

  await apiLogin(request, 'systems.author')
  const draftResponse = await request.post(`${apiBase}/api/change-request-drafts`, { data: {
    projectId: showcase.projectId,
    targetReleaseId: showcase.activeReleaseId,
    type: 'System',
    title: `Rejected approval ${suffix}`,
    problem: 'A refused signature must leave no trace and lose no input.',
    analysis: 'The dialog is the only place the password exists, so closing it would discard the work.',
    solution: 'Keep the dialog, its input and its control available after a refusal.',
    requirementChanges: [{
      level: 'System',
      kind: 'Introduce',
      targetSectionId: await firstSectionId(request, showcase.projectId),
      statement: 'The FMS shall preserve refused approval input.',
      rationale: 'A refusal is not a reason to retype a password.',
      verificationMethod: 'Inspection',
      impactDispositionJson: completeImpacts,
    }],
  } })
  expect(draftResponse.ok(), await draftResponse.text()).toBeTruthy()
  const draft = await draftResponse.json()
  const submitResponse = await request.post(`${apiBase}/api/change-requests/${draft.id}/submit`, { data: {
    expectedVersion: draft.version,
    mode: 'Sequential',
    approvers: [{ userId: 'systems.reviewer', name: 'Systems Engineer' }],
  } })
  expect(submitResponse.ok(), await submitResponse.text()).toBeTruthy()

  // Opened from My Work rather than by typing the address. A change request reached by a bare URL loads
  // without a selected Program behind it, so the page cannot resolve who the reader is to this change and
  // offers no approval — which is correct of it, and is what made an earlier version of this test wait three
  // minutes for a control that was never going to appear.
  await login(page, 'systems.reviewer')
  await page.getByRole('link', { name: 'My Work' }).click()
  const assigned = page.locator('.workQueue article').filter({ hasText: draft.displayNumber })
  await expect(assigned).toBeVisible({ timeout: 30_000 })
  await assigned.click()
  await expect(page).toHaveURL(new RegExp(`/systems/change-requests/${draft.id}$`))

  await page.getByRole('button', { name: 'Review & electronically approve' }).click()
  const dialog = page.locator('.signatureModal')
  await expect(dialog).toBeVisible()
  await dialog.getByLabel('Re-enter your password').fill('AeroLink!2026')
  await page.route(`**/api/change-requests/${draft.id}/approve`, route => route.fulfill({
    status: 403,
    contentType: 'application/json',
    body: JSON.stringify({ error: 'Approver authority is no longer active for this Program.' }),
  }))

  await dialog.getByRole('button', { name: 'Sign & approve' }).click()
  await expect(page.getByRole('alert')).toContainText('Approver authority is no longer active')
  await expect(dialog).toBeVisible()
  await expect(dialog.getByLabel('Re-enter your password')).toHaveValue('AeroLink!2026')
  await expect(dialog.getByRole('button', { name: 'Sign & approve' })).toBeEnabled()
  await page.unroute(`**/api/change-requests/${draft.id}/approve`)

  // Nothing was signed, so nothing is on the record.
  const signatures = await request.get(`${apiBase}/api/signatures?artifactId=${draft.id}`)
  expect(signatures.ok(), await signatures.text()).toBeTruthy()
  expect(await signatures.json()).toEqual([])
})