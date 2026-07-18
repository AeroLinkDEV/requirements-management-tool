import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, login, openNavigationGroup } from './auth'

test('integration command center governs machine access and signed event delivery', async ({ page, request }) => {
  await apiLogin(request)
  const seeded = await request.post(`${apiBase}/api/showcase/seed`, { timeout: 60_000 })
  expect(seeded.ok(), await seeded.text()).toBeTruthy()
  await login(page)

  await openNavigationGroup(page, 'ADMINISTRATION')
  await page.getByRole('link', { name: 'Integration Command Center' }).click()
  await expect(page.getByRole('heading', { name: 'Integration Command Center' })).toBeVisible()
  await expect(page.getByText('v1 operational')).toBeVisible()
  await expect(page.getByText('Scoped service identities')).toBeVisible()

  await page.getByRole('button', { name: '+ Machine identity' }).click()
  await page.getByLabel('Identity name').fill('Automated verification pipeline')
  await page.getByLabel('Publish integration events').check()
  await page.getByRole('button', { name: 'Create and reveal key' }).click()
  await expect(page.getByRole('heading', { name: 'Machine identity created' })).toBeVisible()
  await expect(page.locator('.secretValue code')).toContainText('alk_')
  await page.getByRole('button', { name: 'I have stored it securely' }).click()
  await expect(page.getByText('Automated verification pipeline')).toBeVisible()
  await expect(page.getByText('requirements:read')).toBeVisible()

  await page.getByRole('button', { name: '+ Add destination' }).click()
  await page.getByLabel('Destination name').fill('Supplier PLM gateway')
  await page.getByLabel('HTTPS endpoint').fill('https://example.invalid/aerolink-events')
  await page.getByRole('button', { name: 'Add signed destination' }).click()
  await expect(page.getByRole('heading', { name: 'Webhook signing secret' })).toBeVisible()
  await expect(page.locator('.secretValue code')).toContainText('whsec_')
  await page.getByRole('button', { name: 'I have stored it securely' }).click()
  await expect(page.getByText('Supplier PLM gateway')).toBeVisible()

  await page.getByRole('button', { name: 'Send test event', exact: true }).click()
  await expect(page.getByText('aerolink.integration.test')).toBeVisible()
  await expect(page.getByText(/attempt 0|attempt 1/)).toBeVisible()
})
