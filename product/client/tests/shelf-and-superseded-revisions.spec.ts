import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, login, openNavigationGroup } from './auth'

// Putting work away for another day, and taking it back off the shelf.
//
// `Defer` existed in the domain and nothing exposed it: the dashboard counted deferred change requests, the
// history explorer offered a filter for them, and the only way one could exist was for the demonstration seeder
// to make it. This drives the whole round trip through the browser, because reachability is the defect.
test('a change request goes on the shelf with its state remembered, and comes back', async ({ page, request }) => {
  test.setTimeout(120_000)
  await apiLogin(request)
  const suffix = Date.now().toString().slice(-7)
  const programName = `Shelf Program ${suffix}`
  const created = await request.post(`${apiBase}/api/workspaces`, { data: {
    programName, programCode: `SH${suffix}`, projectName: 'FMS Software',
    softwareProduct: 'Flight Management Software', initialRelease: '1.6', initialReleaseIsReleased: false,
  } })
  expect(created.ok(), await created.text()).toBeTruthy()
  const workspace = await created.json()

  const scr = await (await request.post(`${apiBase}/api/scr-drafts`, { data: {
    projectId: workspace.project.id, targetReleaseId: workspace.release.id, type: 'System',
    title: 'Oceanic waypoint sequencing', problem: 'P', analysis: 'A', solution: 'S',
    requirementChanges: [{ level: 'System', kind: 'Introduce', statement: 'The FMS shall sequence oceanic waypoints.', rationale: 'New', verificationMethod: 'Test' }],
  } })).json()

  await login(page)
  await page.locator('.program > select:not(.releaseSelector)').selectOption({ label: programName })
  await page.goto(`/programs/${workspace.program.id}/projects/${workspace.project.id}/releases/${workspace.release.id}/change-requests/${scr.id}`)

  // Allocation and state read as two facts, in the rail and in the header badge.
  await expect(page.getByRole('definition').filter({ hasText: /^1\.6$/ })).toBeVisible()
  await expect(page.locator('.stateBadge')).toHaveText('1.6 · Draft')

  // Onto the shelf. The reason is required, so it is asked for rather than defaulted.
  page.once('dialog', dialog => dialog.accept('Descoped from 1.6.'))
  await page.getByRole('button', { name: 'Defer' }).click()
  await expect(page.locator('.stateBadge')).toHaveText('Deferred · Draft')

  // And back off it, into the state it left. Draft here, because that is where it was.
  await page.getByRole('button', { name: 'Reinstate' }).click()
  await expect(page.locator('.stateBadge')).toHaveText('1.6 · Draft')
  await expect(page.getByRole('button', { name: 'Defer' })).toBeVisible()
})

// A superseded revision is the same work read at an earlier moment. It belongs under the revision that replaced
// it, not beside it in a list of fifty where neither obviously supersedes the other.
test('superseded revisions collapse under the newest one and expand on request', async ({ page, request }) => {
  test.setTimeout(120_000)
  await apiLogin(request)
  const suffix = Date.now().toString().slice(-7)
  const programName = `Revision Program ${suffix}`
  const created = await request.post(`${apiBase}/api/workspaces`, { data: {
    programName, programCode: `RV${suffix}`, projectName: 'FMS Software',
    softwareProduct: 'Flight Management Software', initialRelease: '1.6', initialReleaseIsReleased: false,
  } })
  expect(created.ok(), await created.text()).toBeTruthy()
  const workspace = await created.json()

  const scr = await (await request.post(`${apiBase}/api/scr-drafts`, { data: {
    projectId: workspace.project.id, targetReleaseId: workspace.release.id, type: 'System',
    title: 'Oceanic waypoint sequencing', problem: 'P', analysis: 'A', solution: 'S',
    requirementChanges: [{ level: 'System', kind: 'Introduce', statement: 'The FMS shall sequence oceanic waypoints.', rationale: 'New', verificationMethod: 'Test' }],
  } })).json()
  await request.post(`${apiBase}/api/scrs/${scr.id}/submit`, { data: { approvers: [{ userId: 'admin', name: 'AeroLink Administrator' }] } })
  await request.post(`${apiBase}/api/scrs/${scr.id}/approve`, { data: { password: 'AeroLink!2026', meaning: 'Approved.' } })
  const next = await request.post(`${apiBase}/api/scrs/${scr.id}/next-revision`, { data: {} })
  expect(next.ok(), await next.text()).toBeTruthy()

  await login(page)
  await page.locator('.program > select:not(.releaseSelector)').selectOption({ label: programName })
  await openNavigationGroup(page, 'SYSTEMS ENGINEERING')
  await page.getByRole('link', { name: 'System Change Requests' }).click()

  // One row for the change request, showing where it has got to.
  await expect(page.getByText(`${scr.baseNumber}.01`)).toBeVisible()
  await expect(page.getByText(`${scr.baseNumber}.00`)).toHaveCount(0)

  // Nothing is hidden: the row says what is behind it and opens.
  const toggle = page.getByRole('button', { name: /Show 1 superseded revision/ })
  await expect(toggle).toBeVisible()
  await toggle.click()
  await expect(page.getByText(`${scr.baseNumber}.00`)).toBeVisible()
  // And the earlier revision reads as superseded rather than as the Approved it is stored as, because reading
  // "Approved" against a revision a later one replaced invites working from stale content.
  await expect(page.locator('.historyRow.superseded .historyState')).toHaveText('Superseded')

  await page.getByRole('button', { name: /Hide 1 superseded revision/ }).click()
  await expect(page.getByText(`${scr.baseNumber}.00`)).toHaveCount(0)
})
