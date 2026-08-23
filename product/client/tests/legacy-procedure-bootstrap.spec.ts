import { expect, test } from '@playwright/test'
import { login, openNavigationGroup, showcaseSeed } from './auth'

test('a Configuration Manager previews, confirms, and revisits the exact legacy procedure snapshot', async ({ page, request }) => {
  const showcase = await showcaseSeed(request)
  const hash = '7'.repeat(64)
  let completed = false
  let submitted: { expectedHash?: string; confirmLegacySnapshot?: boolean } | undefined

  await page.route(
    `**/api/baselines/${showcase.releasedBaselineId}/legacy-procedure-manifest-bootstrap`,
    async route => {
      if (route.request().method() === 'GET') {
        await route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({
            baselineId: showcase.releasedBaselineId,
            baselineDisplayNumber: 'SW-01.50.00',
            proceduresHash: hash,
            activeProcedureCount: 17,
            retiredProcedureCount: 2,
            draftRevisionCount: 3,
            selectionRule: 'Latest non-Draft controlled revision for each procedure in the same project; a latest Retired revision suppresses that procedure.',
            alreadyBootstrapped: completed,
            recordedAt: completed ? '2026-08-10T18:00:00Z' : null,
            recordedBy: completed ? 'admin' : null,
          }),
        })
        return
      }
      submitted = route.request().postDataJSON()
      completed = true
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          baselineId: showcase.releasedBaselineId,
          baselineDisplayNumber: 'SW-01.50.00',
          proceduresHash: hash,
          activeProcedureCount: 17,
          retiredProcedureCount: 2,
          draftRevisionCount: 3,
          selectionRule: 'Latest non-Draft controlled revision for each procedure in the same project; a latest Retired revision suppresses that procedure.',
          alreadyBootstrapped: true,
          recordedAt: '2026-08-10T18:00:00Z',
          recordedBy: 'admin',
        }),
      })
    },
  )

  await login(page, 'admin', { openProject: false })
  await page.getByRole('link', { name: 'Open FMS Product Development' }).click()
  await page.getByRole('button', { name: 'Open build 1.5' }).click()
  await openNavigationGroup(page, 'RELEASE')
  await page.getByRole('link', { name: 'Configuration Baselines / Legacy Verification Bootstrap' }).click()

  await expect(page).toHaveURL(/\/baselines$/)
  await expect(page.getByRole('heading', { name: 'Candidate Baselines' })).toBeVisible()
  const panel = page.getByRole('region', { name: 'Legacy verification artifact manifest bootstrap' })
  await expect(panel).toContainText('migration snapshot of the current legacy controlled inventory')
  await expect(panel).toContainText('17')
  await expect(panel).toContainText('2')
  await expect(panel).toContainText('3')
  await expect(panel).toContainText(hash)

  const establish = panel.getByRole('button', { name: 'Establish legacy verification artifact snapshot' })
  await expect(establish).toBeDisabled()
  await panel.getByRole('checkbox').check()
  await expect(establish).toBeEnabled()
  await establish.click()

  expect(submitted).toEqual({ expectedHash: hash, confirmLegacySnapshot: true })
  await expect(panel.getByText('Snapshot established')).toBeVisible()
  await expect(panel).toContainText('not reconstructed historical release evidence')

  const stableUrl = page.url()
  await page.reload()
  await expect(page).toHaveURL(stableUrl)
  await expect(page.getByRole('region', { name: 'Legacy verification artifact manifest bootstrap' }))
    .toContainText('Snapshot established')
})
