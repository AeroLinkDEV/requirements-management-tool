import { expect, test } from '@playwright/test'
import type { Page } from '@playwright/test'
import { login } from './auth'

async function dispatchShortcut(page: Page) {
  return page.evaluate(() => {
    let defaultPrevented = false
    const observer = (event: KeyboardEvent) => { defaultPrevented = event.defaultPrevented }
    window.addEventListener('keydown', observer)
    const event = new KeyboardEvent('keydown', { bubbles: true, cancelable: true, ctrlKey: true, key: 'k' })
    window.dispatchEvent(event)
    window.removeEventListener('keydown', observer)
    return defaultPrevented
  })
}

test('Ctrl/Cmd+K is inert above a build and opens quick navigation inside one', async ({ page }) => {
  await login(page, 'admin', { openProject: false })

  expect(await dispatchShortcut(page)).toBeFalsy()
  await expect(page.getByRole('dialog', { name: 'Quick navigation' })).toHaveCount(0)

  await page.getByRole('link', { name: 'Open FMS Product Development' }).click()
  await expect(page.getByRole('heading', { name: 'Software Builds' })).toBeVisible()
  await page.getByRole('button', { name: 'Open build 1.6' }).click()
  await expect(page.getByRole('heading', { name: 'Command Center' })).toBeVisible()

  expect(await dispatchShortcut(page)).toBeTruthy()
  const palette = page.getByRole('dialog', { name: 'Quick navigation' })
  await expect(palette).toBeVisible()
  await page.keyboard.press('Escape')
  await expect(palette).toHaveCount(0)
})
