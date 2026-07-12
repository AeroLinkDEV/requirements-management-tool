import { expect, test } from '@playwright/test'
import { login } from './auth'

test('AeroLink starts against the real API and presents a valid entry state', async ({ page }) => {
  await login(page)
  await expect(page.getByText(/AeroLink/).first()).toBeVisible()
  await expect(page.getByRole('heading', { name: /Create your first program|Command Center/ })).toBeVisible()
})
