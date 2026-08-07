import { expect, test } from '@playwright/test'
import { login, openNavigationGroup } from './auth'

/**
 * Browsing controlled procedures the way requirements are browsed.
 *
 * The requirements explorer answers what an artifact says, what it traces to, what happened to it, and what
 * anybody has said about it. Those are the same four questions asked of a procedure, so this page uses that
 * component's inspector rather than a second one that resembles it.
 */
test('a procedure opens onto the same four-tab inspector a requirement does', async ({ page }) => {
  test.setTimeout(120_000)
  await login(page, 'admin')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Test Procedure Explorer' }).click()

  await expect(page.getByRole('heading', { name: 'Test Procedure Explorer' })).toBeVisible({ timeout: 30_000 })

  const rows = page.locator('.procedureRow')
  await expect(rows.first()).toBeVisible({ timeout: 30_000 })
  const number = (await rows.first().locator('b').textContent())!.trim()
  expect(number).toMatch(/^SYSTP-\d{6}/)

  await rows.first().click()
  const inspector = page.getByRole('complementary', { name: new RegExp(`${number.replace('.', '\\.')} detail`) })
  await expect(inspector).toBeVisible()

  // The same four, in the same order, from the same stylesheet.
  for (const tab of ['Overview', 'Trace & impact', 'History']) {
    await expect(inspector.getByRole('button', { name: tab })).toBeVisible()
  }
  await expect(inspector.getByRole('button', { name: /^Discussion/ })).toBeVisible()

  await expect(inspector.getByText('Objective', { exact: true })).toBeVisible()

  // Trace runs the other way from a requirement's: a procedure shows what it exists to verify.
  await inspector.getByRole('button', { name: 'Trace & impact' }).click()
  await expect(inspector).toContainText('verifies')

  await inspector.getByRole('button', { name: 'History' }).click()
  await expect(inspector.locator('.revisionList li').first()).toBeVisible({ timeout: 30_000 })

  // Discussion is the requirement pane's own form and article markup, so what is asserted below is what would
  // hold on a requirement: an attributable comment that can then be dispositioned.
  await inspector.getByRole('button', { name: /^Discussion/ }).click()
  const comments = inspector.locator('.discussionPane article')
  const saidBefore = await comments.count()
  await inspector.locator('.discussionPane textarea').fill('Confirmed against the oceanic rig on the 6th.')
  await inspector.getByRole('button', { name: 'Add comment' }).click()
  await expect(comments).toHaveCount(saidBefore + 1, { timeout: 30_000 })
  await expect(comments.last()).toContainText('Confirmed against the oceanic rig on the 6th.')

  // It is a controlled record, not view state: it survives a reload.
  await page.reload()
  await expect(page.getByRole('heading', { name: 'Test Procedure Explorer' })).toBeVisible({ timeout: 30_000 })
  await page.locator('.procedureRow').filter({ hasText: number }).first().click()
  const reopened = page.getByRole('complementary', { name: new RegExp(`${number.replace('.', '\\.')} detail`) })
  await reopened.getByRole('button', { name: /^Discussion/ }).click()
  const reloaded = reopened.locator('.discussionPane article').last()
  await expect(reloaded).toContainText('Confirmed against the oceanic rig on the 6th.', { timeout: 30_000 })

  // Resolving goes through the artifact-comment route the requirements pane uses, not a procedure-only twin.
  page.once('dialog', dialog => void dialog.accept('Rig log attached.'))
  await reloaded.getByRole('button', { name: 'Resolve / disposition' }).click()
  await expect(reopened.locator('.discussionPane article').last()).toContainText('Rig log attached.')
})
