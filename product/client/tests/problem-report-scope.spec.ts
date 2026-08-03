import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, login, showcaseSeed } from './auth'

/**
 * One Problem Report database, read the same from any build.
 *
 * A report names a target build and may be closed during a particular one, but the database of what is open
 * and in work is a Project-level record set. Scoping the queue to the active workspace showed ten reports in
 * Build 1.6 and none in Build 1.5 — not a different view of one database, but what reads as a different
 * database. See DEC-089; this deliberately reverses the build-scoping half of #298.
 *
 * The report is raised against the in-work build through the API so the journey owns a record whose target
 * build is unambiguous, and is then looked for from the build that is *not* its target.
 */
test('the Problem Report queue is identical in the active and the released build', async ({ page, request }) => {
  test.setTimeout(120_000)
  await apiLogin(request)
  const showcase = await showcaseSeed(request)
  const title = `Project-scoped queue check ${Date.now()}`
  const raised = await request.post(`${apiBase}/api/problem-reports`, {
    data: {
      projectId: showcase.projectId,
      releaseId: showcase.activeReleaseId,
      title,
      problem: 'Raised against the in-work build to prove the queue is not filtered by workspace.',
    },
  })
  expect(raised.ok(), await raised.text()).toBeTruthy()

  await login(page)
  await page.getByRole('link', { name: 'Problem Reports' }).click()
  await expect(page.getByRole('heading', { name: 'Problem Report queue' })).toBeVisible({ timeout: 30_000 })

  // The heading paints before the queue has loaded, so the record is waited for and only then counted.
  // Counting first reads zero on a slower runner and fails as "the build lists nothing" when the answer had
  // simply not arrived yet — which is exactly what this journey did on its first CI run.
  await expect(page.locator('.prList').getByText(title)).toBeVisible({ timeout: 30_000 })
  const inWorkCount = await page.locator('.prList button').count()
  expect(inWorkCount, 'the in-work build should list the report it raised').toBeGreaterThan(0)

  // The released build is a different workspace, not a different database.
  await page.getByRole('button', { name: 'Back to Software Builds' }).click()
  await page.getByRole('button', { name: 'Open build 1.5' }).click()
  await page.getByRole('link', { name: 'Problem Reports' }).click()
  await expect(page.getByRole('heading', { name: 'Problem Report queue' })).toBeVisible({ timeout: 30_000 })

  await expect(page.locator('.prList').getByText(title)).toBeVisible({ timeout: 30_000 })
  expect(await page.locator('.prList button').count(), 'the released build must show the same database').toBe(inWorkCount)

  // And the record itself opens from the build that is not its target, rather than being refused as a
  // cross-build resource.
  await page.locator('.prList').getByText(title).click()
  await expect(page.getByText(title).first()).toBeVisible({ timeout: 30_000 })
})
