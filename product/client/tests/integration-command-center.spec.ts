import { expect, test } from '@playwright/test'
import { apiLogin, login, openNavigationGroup, showcaseSeed } from './auth'

/**
 * Every attempt of this journey owns its artifacts: Playwright retries re-run against the same
 * disposable SQLite database, so a partially successful attempt leaves its machine identities and
 * webhook destinations behind. Fixed artifact names made the retry fail on strict-locator ambiguity
 * left by the attempt it was retrying (#814) — an attempt stamp in every owned name plus locators
 * scoped to that attempt's rows keeps the retry honest: it replays the same product behavior against
 * its own state instead of tripping over the previous attempt's.
 *
 * One-time-secret semantics are untouched: the API key and webhook signing secret are still read only
 * from their single reveal dialogs and acknowledged exactly once.
 */

const attemptTag = () => `attempt ${test.info().retry}-${Date.now()}`

const openCenter = async (page: import('@playwright/test').Page) => {
  await login(page)
  await openNavigationGroup(page, 'ADMINISTRATION')
  await page.getByRole('link', { name: 'Integration Command Center' }).click()
  await expect(page.getByRole('heading', { name: 'Integration Command Center' })).toBeVisible()
  await expect(page.getByText('v1 operational')).toBeVisible()
  await expect(page.getByText('Scoped credentials')).toBeVisible()
}

const createIdentity = async (page: import('@playwright/test').Page, identityName: string) => {
  await page.getByRole('button', { name: '+ Machine identity' }).click()
  await page.getByLabel('Identity name').fill(identityName)
  await page.getByLabel('Publish integration events').check()
  await page.getByRole('button', { name: 'Create and reveal key' }).click()
  await expect(page.getByRole('heading', { name: 'Machine identity created' })).toBeVisible()
  await expect(page.locator('.secretValue code')).toContainText('alk_')
  await page.getByRole('button', { name: 'I have stored it securely' }).click()
  const identityRow = page.locator('.integrationTableRow').filter({ hasText: identityName })
  await expect(identityRow).toBeVisible()
  await expect(identityRow).toContainText('requirements:read')
  await expect(identityRow).toContainText('events:write')
}

const createWebhook = async (page: import('@playwright/test').Page, webhookName: string) => {
  await page.getByRole('button', { name: '+ Add destination' }).click()
  await page.getByLabel('Destination name').fill(webhookName)
  await page.getByLabel('HTTPS endpoint').fill('https://example.invalid/aerolink-events')
  await page.getByRole('button', { name: 'Add signed destination' }).click()
  await expect(page.getByRole('heading', { name: 'Webhook signing secret' })).toBeVisible()
  await expect(page.locator('.secretValue code')).toContainText('whsec_')
  await page.getByRole('button', { name: 'I have stored it securely' }).click()
  const webhookCard = page.locator('.webhookCards article').filter({ hasText: webhookName })
  await expect(webhookCard).toBeVisible()
  await expect(webhookCard.locator('code')).toHaveAttribute('title', 'https://example.invalid/aerolink-events')
  await expect(webhookCard).toContainText('Listening')
}

const deliverTestEvent = async (page: import('@playwright/test').Page, webhookName: string) => {
  await page.getByRole('button', { name: 'Send test event', exact: true }).click()
  // The delivery is proven for THIS attempt's destination, not merely for any row in the activity feed.
  const delivery = page.locator('.deliveryList > div')
    .filter({ hasText: webhookName })
    .filter({ hasText: 'aerolink.integration.test' })
  await expect(delivery).toBeVisible()
  await expect(delivery).toContainText(/attempt 0|attempt 1/)
}

test('the operator console shows runtime status without the product capability checklist', async ({ page }) => {
  // #734 F15: the Integration Command Center is an operator console, not a product marketing
  // surface. The static "API CONTRACT / Version 1 foundation / Ready" capability checklist is
  // removed while every piece of live operational information and operator action remains.
  await openCenter(page)

  await expect(page.getByText('API CONTRACT')).toHaveCount(0)
  await expect(page.getByText('Version 1 foundation')).toHaveCount(0)
  await expect(page.locator('.capabilityPanel')).toHaveCount(0)
  await expect(page.locator('.capabilityList')).toHaveCount(0)

  await expect(page.getByText('PUBLIC API')).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Machine identities' })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Webhook destinations' })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Recent delivery activity' })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Send test event', exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Create identity' })).toBeVisible()
})

test('integration command center governs machine access and signed event delivery', async ({ page }) => {
  const tag = attemptTag()
  const identityName = `Automated verification pipeline · ${tag}`
  const webhookName = `Supplier PLM gateway · ${tag}`

  await openCenter(page)
  await createIdentity(page, identityName)
  await createWebhook(page, webhookName)
  await deliverTestEvent(page, webhookName)
})

test('the journey reruns cleanly after a first attempt stopped once its machine identity existed', async ({ page, request }) => {
  // Attempt 0 left the database with its own identity before stopping; the retry below must neither
  // see it nor be ambiguous about which identity is its own.
  const showcase = await showcaseSeed(request)
  await apiLogin(request)
  const abandoned = `Automated verification pipeline · attempt 0-${Date.now()}`
  const created = await request.post(`${process.env.AEROLINK_E2E_API_BASE}/api/integrations/service-identities`, {
    data: { projectId: showcase.projectId, name: abandoned, scopes: ['requirements:read', 'events:write'] },
  })
  expect(created.ok(), await created.text()).toBeTruthy()

  const tag = attemptTag()
  const identityName = `Automated verification pipeline · ${tag}`
  const webhookName = `Supplier PLM gateway · ${tag}`

  await openCenter(page)
  await createIdentity(page, identityName)
  await createWebhook(page, webhookName)
  await deliverTestEvent(page, webhookName)
  // The abandoned attempt's identity remains present but never enters this attempt's locator path.
  await expect(page.getByText(abandoned)).toBeVisible()
  await expect(page.locator('.integrationTableRow').filter({ hasText: identityName })).toHaveCount(1)
})

test('the journey reruns cleanly after a first attempt stopped once its webhook destination existed', async ({ page, request }) => {
  // Attempt 0 got as far as a persisted webhook destination before stopping. The retry must still be
  // able to reveal ITS OWN signing secret (one-time) and deliver through ITS OWN destination.
  const showcase = await showcaseSeed(request)
  await apiLogin(request)
  const attempt0 = `attempt 0-${Date.now()}`
  const abandonedIdentity = `Automated verification pipeline · ${attempt0}`
  const abandonedWebhook = `Supplier PLM gateway · ${attempt0}`
  const identityResponse = await request.post(`${process.env.AEROLINK_E2E_API_BASE}/api/integrations/service-identities`, {
    data: { projectId: showcase.projectId, name: abandonedIdentity, scopes: ['requirements:read', 'events:write'] },
  })
  expect(identityResponse.ok(), await identityResponse.text()).toBeTruthy()
  const webhookResponse = await request.post(`${process.env.AEROLINK_E2E_API_BASE}/api/integrations/webhooks`, {
    data: { projectId: showcase.projectId, name: abandonedWebhook, endpointUrl: 'https://example.invalid/aerolink-events', eventTypes: ['aerolink.integration.test'] },
  })
  expect(webhookResponse.ok(), await webhookResponse.text()).toBeTruthy()

  const tag = attemptTag()
  const identityName = `Automated verification pipeline · ${tag}`
  const webhookName = `Supplier PLM gateway · ${tag}`

  await openCenter(page)
  await createIdentity(page, identityName)
  await createWebhook(page, webhookName)
  await deliverTestEvent(page, webhookName)
  await expect(page.locator('.webhookCards article').filter({ hasText: webhookName })).toHaveCount(1)
})
