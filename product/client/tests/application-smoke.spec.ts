import { expect, test } from '@playwright/test'
import { login } from './auth'

test('AeroLink starts against the real API and presents a valid entry state', async ({ page }) => {
  await login(page)
  await expect(page.getByText(/AeroLink/).first()).toBeVisible()
  await expect(page.getByRole('heading', { name: /Create your first program|Command Center/ })).toBeVisible()
})

test('Sign in recovers cleanly when the local API is temporarily unavailable', async ({ page }) => {
  await page.route('**/api/auth/login', route => route.abort('connectionrefused'))
  await page.goto('/')
  await page.getByRole('button', { name: /Sign in securely/ }).click()

  await expect(page.getByText(/could not reach its local API/i)).toBeVisible()
  await expect(page.getByRole('button', { name: /Sign in securely/ })).toBeEnabled()

  await page.unroute('**/api/auth/login')
  await page.getByRole('button', { name: /Sign in securely/ }).click()
  await expect(page.getByRole('heading', { name: /Create your first program|Command Center/ })).toBeVisible()
})
