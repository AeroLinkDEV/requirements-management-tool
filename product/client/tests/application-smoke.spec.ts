import { expect, test } from '@playwright/test'
import { login } from './auth'

test('Password visibility is a compact control that does not cover the password field', async ({ page }) => {
  await page.goto('/')
  const input = page.getByLabel('Password')
  const toggle = page.getByRole('button', { name: 'Reveal typed characters' })

  await expect(input).toBeVisible()
  await expect(toggle).toBeVisible()
  const [inputBox, toggleBox] = await Promise.all([input.boundingBox(), toggle.boundingBox()])
  expect(inputBox).not.toBeNull()
  expect(toggleBox).not.toBeNull()
  expect(toggleBox!.width).toBeLessThanOrEqual(48)
  expect(toggleBox!.height).toBeLessThanOrEqual(inputBox!.height)
  expect(toggleBox!.x).toBeGreaterThan(inputBox!.x + inputBox!.width - 60)
  await expect(toggle).toHaveCSS('background-color', 'rgba(0, 0, 0, 0)')

  await input.fill('AeroLink!2026')
  await toggle.click()
  await expect(input).toHaveAttribute('type', 'text')
  await expect(input).toHaveValue('AeroLink!2026')
  await page.getByRole('button', { name: 'Conceal typed characters' }).click()
  await expect(input).toHaveAttribute('type', 'password')
})

test('AeroLink starts against the real API and presents a valid entry state', async ({ page }) => {
  const seedless = process.env.AEROLINK_E2E_SKIP_SHOWCASE_SEED === 'true'
  await login(page,'admin',{openProject:!seedless})
  await expect(page.getByText(/AeroLink/).first()).toBeVisible()
  await expect(page.getByRole('heading', { name: seedless ? 'Create your first program' : 'Command Center' })).toBeVisible()
})

test('Sign in recovers cleanly when the local API is temporarily unavailable', async ({ page }) => {
  await page.route('**/api/auth/login', route => route.abort('connectionrefused'))
  await page.goto('/')
  await page.getByLabel('Username').fill('admin')
  await page.getByLabel('Password').fill('AeroLink!2026')
  await page.getByRole('button', { name: /Sign in securely/ }).click()

  await expect(page.getByText(/could not reach its local API/i)).toBeVisible()
  await expect(page.getByRole('button', { name: /Sign in securely/ })).toBeEnabled()

  await page.unroute('**/api/auth/login')
  await page.getByRole('button', { name: /Sign in securely/ }).click()
  await expect(page.getByRole('heading', { name: /Create your first program|Projects/ })).toBeVisible()
})
