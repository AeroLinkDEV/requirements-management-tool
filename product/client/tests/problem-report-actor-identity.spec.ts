import { expect, test } from '@playwright/test'
import { chooseCategory, login, selectProgram, writeRichField } from './auth'

/**
 * A Problem Report's audit trail names the person who acted, for an account the demo registry has never
 * heard of.
 *
 * `people-not-accounts.spec.ts` already guards this, but only for the fifteen seeded accounts it lists by
 * name — which is exactly why #776 survived it. `PeopleRegistry.ts` resolves those fifteen client-side, so
 * every demonstration read correctly while every account a real deployment creates rendered its login
 * handle.
 *
 * `admin` is deliberately the actor here: it is a real account with a real display name and it is **not** in
 * `PeopleRegistry.ts`, so before the fix this history entry read "admin". It is the cheapest honest probe of
 * the path that was broken.
 *
 * This proves the browser wiring. The identity model underneath it — capture on the immutable event, live
 * resolution for current assignment, no backfill, and a directory rename that must not rewrite a past event
 * — is proven against non-seeded accounts in `ProblemReportHistoricalIdentityApiTests`.
 */
test('a Problem Report history names an actor the demo registry does not contain', async ({ page }) => {
  test.setTimeout(240_000)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
  await page.goto(new URL(`${root}/problem-reports`, page.url()).toString(), { waitUntil: 'load' })

  await page.getByRole('button', { name: '+ Record problem' }).click()
  const dialog = page.getByRole('dialog', { name: 'Record a problem' })
  const title = `Navigation source disagreement annunciation ${Date.now()}`
  await dialog.getByLabel('Title').fill(title)
  await dialog.getByRole('group', { name: 'Add content to Problem Description' })
    .getByRole('button', { name: 'Paragraph' }).click()
  await dialog.getByRole('textbox', { name: 'Problem Description paragraph 1' })
    .fill('The disagreement alert clears while the source mismatch is still present.')
  await writeRichField(dialog, 'System / aircraft impact',
    'The flight crew can lose annunciation of a persistent navigation-source disagreement.')
  await chooseCategory(dialog, 'Code Issue — Functional Impact')
  await dialog.getByRole('button', { name: 'Save Draft PR' }).click()
  await expect(page.getByRole('heading', { name: title })).toBeVisible()

  // One controlled transition, so the history has an event this account actually performed.
  await page.locator('.prFlow').getByRole('button', { name: 'Ready for SCCB →', exact: true }).click()
  await expect(page.locator('.prState')).toHaveText('Ready for SCCB')

  await page.getByRole('button', { name: /History/ }).click()
  await expect(page.getByRole('heading', { name: 'Immutable lifecycle history' })).toBeVisible()
  const entry = page.locator('.prTimeline article').filter({ hasText: 'Draft → Ready for SCCB' }).first()
  await expect(entry).toBeVisible()

  // The person, not the credentials they signed in with.
  await expect(entry.getByText('AeroLink Administrator')).toBeVisible()
  await expect(entry).not.toContainText('admin')
  // And the account stays reachable for anyone reconciling this event against the identity provider. The
  // handle is never traded away for the name; both travel together.
  await expect(entry.locator('.personName').first()).toHaveAttribute('title', 'admin')
})
