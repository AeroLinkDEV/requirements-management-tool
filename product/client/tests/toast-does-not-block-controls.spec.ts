import { expect, test } from '@playwright/test'
import { login, openNavigationGroup } from './auth'

/**
 * A confirmation must not take the click that follows it.
 *
 * The toast is fixed at the bottom right on z-index 1300, which is exactly where the authoring forms put Save
 * and Check in. It was intercepting pointer events, so for as long as it was on screen a click on Save went
 * into the announcement instead — silently, because nothing about a message saying "saved" suggests it is also
 * a shield. It surfaced as a journey that timed out on an enabled, visible, unobstructed-looking button.
 *
 * The rule is asserted at the point it can be broken: the element's own pointer-events. Reaching through it to
 * a control underneath would depend on whichever page happened to have a button in that corner.
 */
test('the confirmation toast never intercepts a click meant for the page', async ({ page }) => {
  await login(page, 'admin')
  // A workspace rather than the Command Center, because the context bar that raises this toast belongs to
  // the workspace shell.
  await openNavigationGroup(page, 'REQUIREMENTS')
  await page.getByRole('link', { name: 'System Requirements Explorer' }).click()
  await expect(page.getByRole('button', { name: 'Copy link to this page' })).toBeVisible({ timeout: 30_000 })

  await page.getByRole('button', { name: 'Copy link to this page' }).click()

  const toast = page.locator('.experienceToast')
  await expect(toast).toBeVisible({ timeout: 30_000 })
  await expect(toast).toHaveCSS('pointer-events', 'none')

  // Nothing inside it is interactive, which is why refusing the pointer outright is the right rule rather
  // than re-enabling it for some child.
  await expect(toast.locator('button, a[href], input, select, textarea')).toHaveCount(0)
})
