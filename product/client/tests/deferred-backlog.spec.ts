import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, login, openNavigationGroup, selectProgram, showcaseSeed } from './auth'

/**
 * The shelf, and taking something off it.
 *
 * A build's register is the work it is taking and the work it raised. Work deferred by an earlier build is
 * deliberately not in it — that would make the plan for this build read as though it already contained work
 * nobody has committed to. It waits in its own tab until somebody brings it in, and that act is what moves it.
 *
 * This proves the whole path through the interface rather than the endpoint: the deferred item is absent from
 * the register, present in the tab, and after bringing it in it is in the register as a Draft while the build
 * that raised it still shows it.
 */
test('deferred work waits in its own tab until a build takes it, and the build that raised it keeps it', async ({ page, request }) => {
  test.setTimeout(240_000)
  const showcase = await showcaseSeed(request)
  await apiLogin(request)

  // A change request raised in the current build, then shelved. Deferring is what puts it on the shelf; the
  // journey is about what the interface does with it afterwards.
  const created = await request.post(`${apiBase}/api/change-requests`, {
    data: {
      projectId: showcase.projectId,
      targetReleaseId: showcase.activeReleaseId,
      title: 'DEFERRED-BACKLOG oceanic sequencing',
      problem: 'Reload latency is asserted rather than derived.',
      analysis: 'The budget was never apportioned.',
      solution: 'Apportion it and state the derived figure.',
      changes: [{
        baseNumber: 'SYSR-000005',
        revision: 1,
        level: 'System',
        kind: 'Modify',
        statement: 'The system shall make the active flight plan available within 1.5 seconds.',
        rationale: 'Derived from the reload budget.',
        verificationMethod: 'Test',
      }],
    },
  })
  expect(created.ok(), `creating the change request should succeed: ${await created.text()}`).toBeTruthy()
  const scr = await created.json()

  const deferred = await request.post(`${apiBase}/api/change-requests/${scr.id}/defer`, {
    data: { reason: 'Not shipping in this build.' },
  })
  expect(deferred.ok(), `deferring should succeed: ${await deferred.text()}`).toBeTruthy()

  // The register is reached by navigating, not by a route this test can guess.
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'SYSTEMS ENGINEERING')
  await page.getByRole('link', { name: 'Change Requests' }).click()

  await expect(page.locator('.historyTable')).toBeVisible({ timeout: 30_000 })

  // It is on the shelf, so the build's own register does not offer it as this build's work.
  const tabs = page.getByRole('navigation', { name: 'Register view' })
  await expect(tabs).toBeVisible({ timeout: 30_000 })

  await tabs.getByRole('button', { name: /^Deferred/ }).click()
  const backlog = page.locator('.deferredBacklog')
  await expect(backlog).toBeVisible({ timeout: 30_000 })

  const row = backlog.locator('.deferredRow', { hasText: 'DEFERRED-BACKLOG oceanic sequencing' })
  await expect(row).toBeVisible()
  // The row says which build shelved it, because that is what somebody deciding whether to take it needs.
  await expect(row).toContainText(/shelved from Build/)

  const target = row.locator('a.deferredOpen')
  await expect(target).toHaveAttribute('href', /systems\/change-requests\/[0-9a-f-]{36}$/)
  const expectedUrl = new URL(await target.getAttribute('href')!, page.url()).toString()
  const [opened] = await Promise.all([
    page.context().waitForEvent('page', { timeout: 30_000 }),
    target.click({ button: 'middle' }),
  ])
  await expect(opened).toHaveURL(expectedUrl)
  await opened.close()

  // Bringing it in is the explicit act that moves it, and the button names the build it is moving into.
  const bringIn = row.getByRole('button', { name: /^Bring into Build/ })
  await expect(bringIn).toBeVisible()
  await bringIn.click()

  // The view returns to the build's register, where the work now is, as a Draft.
  await expect(page.locator('.historyTable')).toBeVisible({ timeout: 30_000 })
  const landed = page.locator('[data-register-row]', { hasText: 'DEFERRED-BACKLOG oceanic sequencing' })
  await expect(landed).toBeVisible({ timeout: 30_000 })
  await expect(landed.locator('[data-state]')).toHaveAttribute('data-state', 'Draft')

  // And it is off the shelf, because it is somebody's work now rather than nobody's.
  await tabs.getByRole('button', { name: /^Deferred/ }).click()
  // Nothing is left on the shelf, so the tab says so rather than rendering an empty list.
  await expect(page.getByText('Nothing is deferred')).toBeVisible({ timeout: 30_000 })
  await expect(page.locator('.deferredRow', { hasText: 'DEFERRED-BACKLOG oceanic sequencing' })).toHaveCount(0)
})
