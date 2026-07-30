import { expect, test } from '@playwright/test'
import { apiLogin, login, openNavigationGroup, selectProgram } from './auth'

/**
 * The execution-time field was seeded by truncating an ISO UTC string, which a `datetime-local` control then
 * reads as local wall time. A run recorded at 23:20 in Toronto prefilled as 03:20 the following day — wrong
 * by the offset, and on the wrong calendar date.
 *
 * The timezone is pinned deliberately. CI runs in UTC, where a zero offset makes the defect invisible, so a
 * test that did not fix a zone would have passed against the broken code for exactly the reason the defect
 * survived in the first place.
 */
test.use({ timezoneId: 'America/Toronto' })

test('the execution time field is prefilled with local wall time, not the UTC clock', async ({ page, request }) => {
  test.setTimeout(180_000)
  await apiLogin(request)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'VERIFICATION')
  await page.getByRole('link', { name: 'System Verification' }).click()
  await expect(page.getByRole('heading', { name: 'Verification & Evidence' })).toBeVisible({ timeout: 30_000 })

  await page.getByRole('button', { name: /Test procedures/ }).click()
  await page.locator('.procedureRow').first().getByRole('button', { name: 'Record result' }).click()

  const field = page.getByLabel('Execution time')
  await expect(field).toBeVisible()
  const prefilled = await field.inputValue()

  // Both computed in the page, so they use the pinned zone rather than whatever the runner is set to.
  const clocks = await page.evaluate(() => {
    const now = new Date(), pad = (value: number) => String(value).padStart(2, '0')
    return {
      local: `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}T${pad(now.getHours())}:${pad(now.getMinutes())}`,
      utc: now.toISOString().slice(0, 16),
      offsetMinutes: now.getTimezoneOffset(),
    }
  })

  // Guard the guard: if the offset were zero the two clocks would agree and this would prove nothing.
  expect(clocks.offsetMinutes, 'the pinned timezone must have a non-zero offset for this test to mean anything').not.toBe(0)
  expect(prefilled.slice(0, 13), `prefilled ${prefilled}, local ${clocks.local}, UTC ${clocks.utc}`).toBe(clocks.local.slice(0, 13))
  expect(prefilled.slice(0, 13)).not.toBe(clocks.utc.slice(0, 13))

  // The zone is stated, so a reader knows which clock the value is in.
  await expect(page.getByText(/Local time, .+\. Stored as an exact instant\./)).toBeVisible()
})
