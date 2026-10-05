import { expect, test } from '@playwright/test'
import type { Page } from '@playwright/test'
import { apiBase, apiLogin, login, openNavigationGroup, selectProgram } from './auth'

test('global administrator can inspect bounded SMTP delivery operations without rendering message content', async ({ page }) => {
  let notificationOperationReads = 0
  page.on('request', request => {
    if (request.method() === 'GET' && new URL(request.url()).pathname === '/api/operations/notifications') {
      notificationOperationReads++
    }
  })
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'ADMINISTRATION')
  await page.getByRole('link', { name: 'System Operations' }).click()
  await expect(page.getByRole('button', { name: 'Notifications' })).toBeVisible()
  expect(notificationOperationReads).toBe(0)
  await page.getByRole('button', { name: 'Notifications' }).click()

  await expect.poll(() => notificationOperationReads).toBeGreaterThan(0)
  await expect(page.getByText('INSTALLATION OPERATIONS / EMAIL OUTBOX')).toBeVisible()
  await expect(page.getByRole('heading', { name: /SMTP transport/ })).toBeVisible()
  await expect(page.getByText('Recent delivery state')).toBeVisible()
  await expect(page.getByText(/SMTP acceptance does not confirm inbox arrival/)).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Queue health and recovery' })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Send my transport test' })).toBeVisible()
  // Narrow actual-API pixel evidence excludes settings/credential fields, message bodies and addresses.
  await test.info().attach('disposable-notification-transport-summary', {
    body: await page.locator('.enterpriseHero').screenshot(), contentType: 'image/png',
  })
  await test.info().attach('disposable-notification-queue-health', {
    body: await page.locator('.enterpriseGrid > section').filter({ has: page.getByRole('heading', { name: 'Queue health and recovery' }) }).screenshot(),
    contentType: 'image/png',
  })
})

async function openNotifications(page: Page) {
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'ADMINISTRATION')
  await page.getByRole('link', { name: 'System Operations' }).click()
  await page.getByRole('button', { name: 'Notifications', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Save settings only' })).toBeEnabled()
}

// Primary browser owner for notice return: actual sign-in and password rotation retain the client URL,
// then render the bounded original context and navigate only after an explicit current-work action.
// Hosted API fixtures own source eligibility. Controlled context responses isolate URL/origin wiring:
// a domain/API test cannot see login replacing browser history or a client calling the wrong origin/path.
test('signed-out notification retains its original context through password rotation and deliberate navigation', async ({ page, request }) => {
  await apiLogin(request)
  const userName = `notice.return.${Date.now()}`
  const temporaryPassword = 'Notice-Temporary!2026'
  const permanentPassword = 'Notice-Rotated!2026'
  const created = await request.post(`${apiBase}/api/admin/users`, { data: {
    userName, displayName: 'Notification return fixture', email: `${userName}@example.test`, temporaryPassword,
  } })
  expect(created.ok(), await created.text()).toBeTruthy()
  const noticeId = '20000000-0000-0000-0000-000000000001'
  const currentWorkId = '20000000-0000-0000-0000-000000000002'
  const originalPath = `/notifications/${noticeId}`
  const currentPath = `/open/requirement/${currentWorkId}`
  const contextReads: string[] = []
  const currentReads: string[] = []
  await page.route(`**/api/notifications/${noticeId}/context`, async route => {
    contextReads.push(route.request().url())
    await route.fulfill({ json: {
      identifier: 'REQ-ORIGINAL-ROUND-2', eventType: 'RequirementAssignment', stage: 'Original assignment round 2',
      cycle: 2, revision: 3, sourceId: '20000000-0000-0000-0000-000000000003', sourceFamily: 'RequirementAssignment',
      snapshotHash: 'a'.repeat(64), originalObligationActive: false,
      explanation: 'The original assignment is no longer active. Current work belongs to a later round.',
      currentWorkPath: currentPath,
    } })
  })
  await page.route(`**/api/notifications/${noticeId}/current`, async route => {
    currentReads.push(route.request().url())
    await route.fulfill({ json: { path: currentPath } })
  })
  await page.goto(originalPath)
  const clientOrigin = new URL(page.url()).origin
  expect(clientOrigin, 'This journey qualifies separate client and API origin wiring').not.toBe(new URL(apiBase).origin)
  await expect(page.getByLabel('Username')).toBeVisible()
  expect(contextReads).toHaveLength(0)
  await page.getByLabel('Username').fill(userName)
  await page.getByLabel('Password').fill(temporaryPassword)
  await page.getByRole('button', { name: /Sign in securely/ }).click()
  await expect(page.getByRole('heading', { name: 'Replace temporary password' })).toBeVisible()
  expect(new URL(page.url()).pathname).toBe(originalPath)
  expect(contextReads).toHaveLength(0)
  await page.getByLabel('Temporary password').fill(temporaryPassword)
  await page.getByLabel('New password', { exact: true }).fill(permanentPassword)
  await page.getByLabel('Confirm new password').fill(permanentPassword)
  await page.getByRole('button', { name: /Change password securely/ }).click()
  await expect(page.getByLabel('Username')).toBeVisible()
  expect(new URL(page.url()).pathname).toBe(originalPath)
  await page.getByLabel('Username').fill(userName)
  await page.getByLabel('Password').fill(permanentPassword)
  await page.getByRole('button', { name: /Sign in securely/ }).click()
  await expect(page.getByRole('heading', { name: 'Original notification request' })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'REQ-ORIGINAL-ROUND-2' })).toBeVisible()
  await expect(page.getByText('Original assignment round 2', { exact: true })).toBeVisible()
  await expect(page.getByText('The original assignment is no longer active. Current work belongs to a later round.')).toBeVisible()
  expect(new URL(page.url()).pathname).toBe(originalPath)
  expect(contextReads.length).toBeGreaterThan(0)
  expect([...new Set(contextReads)]).toEqual([`${apiBase}/api/notifications/${noticeId}/context`])
  expect(currentReads).toHaveLength(0)
  await page.getByRole('button', { name: 'Open current work deliberately' }).click()
  await expect(page).toHaveURL(`${clientOrigin}${currentPath}`)
  expect(currentReads).toEqual([`${apiBase}/api/notifications/${noticeId}/current`])
})

// Primary browser owner: request/receipt wiring survives a lost response and reload, without storing a
// secret or turning settings save into activation. Hosted API tests own actual commit/idempotency policy;
// this controlled response fixture catches browser regressions those server tests cannot observe.
// No test-only production seam. It also checks that server pagination replaces the rendered page and that
// the administrator cannot declare quiescence, edit protected targets, or infer inbox delivery.
test('notification settings recover the original receipt after reload and keep explicit sends distinct', async ({ page }) => {
  const firstGeneration = '10000000-0000-0000-0000-000000000001'
  const secondGeneration = '10000000-0000-0000-0000-000000000002'
  const settings = { version: 7, mode: 'Capture', host: 'fixture.invalid', port: 25, sender: 'fixture@example.invalid', displayName: 'Fixture', baseUrl: 'https://fixture.invalid', userNameConfigured: true, credentialConfigured: true }
  type Intent = { operationKey: string; family: string; expectedVersion: number; credential?: string; userName?: string; clearCredential?: boolean; mode?: string; acknowledgeDuplicateRisk?: boolean; generationId?: string }
  type Receipt = { id: string; operationKey: string; family: string; createdAt: string; result: { state: string; version?: number } }
  const receipts = new Map<string, Receipt>()
  const submissions: Intent[] = []
  const receiptReads: string[] = []
  const pages: number[] = []
  const heldPages: number[] = []
  const generation = (id: string, contextIdentifier: string, state: string) => ({ id, deliveryId: id, notificationId: id, contextIdentifier, mode: 'Capture', state, version: 3, messageId: `<${id}@fixture.invalid>`, bodyHash: 'a'.repeat(64), intendedDestination: 'protected', effectiveDestination: 'protected', attempts: 1, createdAt: '2026-01-01T00:00:00Z', requiresDuplicateRiskAcknowledgement: state === 'AcceptanceUnknown' || id === secondGeneration })
  await page.route('**/api/operations/notifications**', async route => {
    const request = route.request()
    const url = new URL(request.url())
    if (request.method() === 'GET' && url.pathname === '/api/operations/notifications') {
      const requestedPage = Number(url.searchParams.get('page') ?? 1)
      const heldPage = Number(url.searchParams.get('heldPage') ?? 1)
      pages.push(requestedPage)
      heldPages.push(heldPage)
      await route.fulfill({ json: {
        generatedAt: '2026-01-01T00:00:00Z', smtp: { configured: true }, links: { configured: true, valid: true },
        totals: { pending: 1, sent: 0, failed: 0, suppressed: 0 }, deliveries: [],
        installation: { id: 'fixture-installation', label: 'Disposable browser fixture', database: 'fixture', version: 'test' }, settings,
        locks: { relay: true, sender: true, baseUrl: true, credentials: false, externalModes: true, diagnosticTarget: true },
        policy: { maximumMode: 'Capture', diagnosticTarget: 'protected', sendAuthority: 'current' },
        health: [{ state: 'AcceptanceUnknown', count: 1, oldestAt: '2026-01-01T00:00:00Z', action: 'Reconcile and confirm worker quiescence.' }, { state: 'RetryExhausted', count: 1, action: 'Readmit after correction.' }],
        generations: requestedPage === 1 ? [generation(firstGeneration, 'REQ-FIXTURE-A', 'AcceptanceUnknown')] : [generation(secondGeneration, 'REQ-FIXTURE-B', 'RetryExhausted')],
        page: requestedPage, total: 26, pageSize: 25,
        heldDeliveries: heldPage === 1 ? Array.from({ length: 25 }, (_, index) => ({ id: `held-${index + 1}`, notificationId: `held-notice-${index + 1}`, state: 'HeldAdmission', contextIdentifier: `HELD-FIXTURE-${String(index + 1).padStart(3, '0')}`, createdAt: '2026-01-01T00:00:00Z' }))
          : [{ id: 'held-26', notificationId: 'held-notice-26', state: 'HeldAdmission', contextIdentifier: 'HELD-FIXTURE-026', createdAt: '2026-01-01T00:00:00Z' }],
        heldPage, heldTotal: 26, heldPageSize: 25,
      } })
      return
    }
    if (request.method() === 'GET' && /^\/api\/operations\/notifications\/operations\/[^/]+$/.test(url.pathname)) {
      const key = url.pathname.split('/').at(-1)!
      receiptReads.push(key)
      const receipt = receipts.get(key)
      await route.fulfill(receipt ? { json: receipt } : { status: 404, json: { code: 'not-found' } })
      return
    }
    if (request.method() === 'GET' && url.pathname.endsWith('/attempts')) {
      await route.fulfill({ json: { attempts: [{ id: 'attempt-1', startedAt: '2026-01-01T00:00:00Z', outcome: 'AcceptanceUnknown', phase: 'Data', safeCode: 'smtp.reply-unknown', transportDisposed: false, quiescence: 'unproven' }] } })
      return
    }
    if (request.method() === 'POST') {
      const submitted = request.postDataJSON() as Intent
      submissions.push(submitted)
      const receipt = { id: `receipt-${submissions.length}`, operationKey: submitted.operationKey, family: submitted.family, createdAt: '2026-01-01T00:00:00Z', result: { state: submitted.family === 'Settings' ? 'SettingsSaved' : submitted.family === 'Activate' ? 'ActivatedFutureEvents' : submitted.family === 'Reissue' ? 'Reissued' : submitted.family === 'Suppress' ? 'Suppressed' : 'Queued', version: settings.version + 1 } }
      receipts.set(submitted.operationKey, receipt)
      if (submitted.family === 'Settings' && submissions.length === 1) {
        settings.version = 9 // A later settings edit must not alter recovery of revision 7's committed intent.
        await route.abort('failed')
      } else await route.fulfill({ json: receipt })
      return
    }
    await route.continue()
  })
  await openNotifications(page)
  await expect(page.getByLabel('SMTP host')).toHaveCount(0)
  await expect(page.getByLabel('Sender address')).toHaveCount(0)
  // Native option availability is its DOM property; Playwright's enabled matcher follows controls.
  await expect(page.getByRole('option', { name: 'Live', exact: true })).toHaveJSProperty('disabled', true)
  await expect(page.getByLabel('Delivery mode')).toHaveValue('Capture')
  const replay = page.getByRole('button', { name: 'Request replay after quiescence' })
  await expect(replay).toBeDisabled()
  await expect(page.getByRole('button', { name: 'Request new generation' })).toBeDisabled()
  await expect(page.getByRole('button', { name: 'Suppress generation' })).toBeDisabled()
  await page.getByRole('button', { name: 'View attempts' }).click()
  await expect(page.getByText(/quiescence unproven/)).toBeVisible()
  await expect(page.getByText('Quiescence is established by the server; it cannot be declared by this checkbox.')).toBeVisible()
  await page.getByRole('button', { name: 'Next page' }).click()
  await expect(page.getByText('Retry Exhausted · REQ-FIXTURE-B')).toBeVisible()
  // A later state can still have an unknown predecessor; obey the server flag rather than infer from state.
  await expect(page.getByRole('button', { name: 'Readmit generation' })).toBeDisabled()
  await expect(page.getByRole('button', { name: 'Request new generation' })).toBeDisabled()
  await expect(page.getByText(/REQ-FIXTURE-A/, { exact: false })).toHaveCount(0)
  expect(pages).toContain(2)
  // Independent bounded backlog paging: it replaces the held page while preserving the generation page.
  const backlog = page.getByRole('region', { name: 'Held notification deliveries' })
  await expect(backlog.getByRole('button', { name: 'Readmit selected held delivery' })).toHaveCount(25)
  await backlog.getByRole('button', { name: 'Next held page' }).click()
  await expect(backlog).toContainText('HELD-FIXTURE-026')
  await expect(backlog.getByRole('button', { name: 'Readmit selected held delivery' })).toHaveCount(1)
  await expect(backlog.getByText('HELD-FIXTURE-001', { exact: false })).toHaveCount(0)
  await expect(backlog.getByRole('button', { name: 'Next held page' })).toBeDisabled()
  expect(heldPages).toContain(2)
  await expect(page.getByText('Retry Exhausted · REQ-FIXTURE-B')).toBeVisible()

  const secret = 'fixture-credential-not-for-storage'
  await page.getByLabel('New SMTP credential').fill(secret)
  await page.getByRole('button', { name: 'Save settings only' }).click()
  await expect(page.getByRole('heading', { name: 'Recover the original operation' })).toBeVisible()
  expect(submissions).toHaveLength(1)
  expect(submissions[0]).toMatchObject({ family: 'Settings', expectedVersion: 7, credential: secret, clearCredential: false })
  expect(submissions[0].userName).toBeUndefined() // Hidden configured username is preserved.
  const key = submissions[0].operationKey
  const stored = await page.evaluate(() => Object.values(localStorage).join(' '))
  expect(stored).toContain(key)
  expect(stored).not.toContain(secret)
  await expect(page.getByRole('button', { name: 'Activate saved mode' })).toBeDisabled()
  await page.reload()
  await page.getByRole('button', { name: 'Notifications', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Recover the original operation' })).toBeVisible()
  await expect(page.getByLabel('Original SMTP credential for retry')).toHaveValue('')
  // The recovered receipt is obtained before a retry POST, even with no secret reentry and changed settings.
  await page.getByRole('button', { name: 'Retry original operation' }).click()
  await expect(page.getByRole('region', { name: 'Notification operation receipt' })).toContainText('Settings · Settings Saved')
  expect(receiptReads).toEqual([key])
  expect(submissions).toHaveLength(1)
  await expect(page.getByLabel('New SMTP credential')).toHaveValue('')
  expect(await page.evaluate(() => Object.values(localStorage).join(' '))).not.toContain(key)
  expect(await page.evaluate(() => Object.values(sessionStorage).join(' '))).not.toContain(secret)
  await page.getByRole('button', { name: 'Activate saved mode' }).click()
  await expect(page.getByRole('region', { name: 'Notification operation receipt' })).toContainText('Activate · Activated Future Events')
  expect(submissions[1]).toMatchObject({ family: 'Activate', expectedVersion: 9, mode: 'Capture' })
  expect(submissions[1].operationKey).not.toBe(key)
  await page.getByRole('button', { name: 'Send my transport test' }).click()
  await expect(page.getByRole('region', { name: 'Notification operation receipt' })).toContainText('TransportTest · Queued')
  expect(submissions[2].operationKey).not.toBe(submissions[1].operationKey)
  await page.getByLabel('I acknowledge that another send after unknown acceptance may produce a duplicate email.').check()
  await page.getByRole('button', { name: 'Request new generation' }).click()
  await expect(page.getByRole('region', { name: 'Notification operation receipt' })).toContainText('Reissue · Reissued')
  expect(submissions[3]).toMatchObject({ family: 'Reissue', generationId: firstGeneration, acknowledgeDuplicateRisk: true })
  // Each distinct disposition requires fresh deliberate acknowledgement, rather than carrying consent
  // forward from the preceding command whose receipt has already been recorded.
  await expect(page.getByLabel('I acknowledge that another send after unknown acceptance may produce a duplicate email.')).not.toBeChecked()
  await expect(page.getByRole('button', { name: 'Suppress generation' })).toBeDisabled()
  await page.getByLabel('I acknowledge that another send after unknown acceptance may produce a duplicate email.').check()
  await page.getByRole('button', { name: 'Suppress generation' }).click()
  await expect(page.getByRole('region', { name: 'Notification operation receipt' })).toContainText('Suppress · Suppressed')
  expect(submissions[4]).toMatchObject({ family: 'Suppress', generationId: firstGeneration, acknowledgeDuplicateRisk: true })
  expect(submissions[4].operationKey).not.toBe(submissions[3].operationKey)
})
