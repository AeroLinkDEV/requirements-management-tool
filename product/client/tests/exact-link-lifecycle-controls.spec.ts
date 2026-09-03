import { expect, test } from '@playwright/test'
import { login, openNavigationGroup } from './auth'

test('Digital Thread reuses projected lifecycle data and the shared exact-link controls', async ({ page }) => {
  let state = 'Suspect'
  let lifecycleGets = 0
  const events = [{
    id: 'trace-raised', type: 'Raised', actorId: 'requirements.materializer',
    occurredAt: '2026-08-23T00:00:00Z', rationale: 'The exact requirement revision changed.',
  }]
  await page.route('**/api/traceability?*', async route => {
    const response = await route.fetch()
    const body = await response.json()
    if (body.items?.[0]) body.items[0].parents = [{
      id: 'parent-revision', revisionId: 'parent-revision', artifactId: 'parent-artifact', linkId: 'trace-lifecycle-link', displayNumber: 'SYSR-000001.00',
      level: 'System', type: 'AllocatedFrom', lifecycle: {
        state, causeKind: 'InternalRequirementRevision', causeRequirementRevisionId: 'cause-revision',
        events,
      },
    }]
    await route.fulfill({ response, json: body })
  })
  await page.route('**/api/trace-links/trace-lifecycle-link/lifecycle', async route => {
    lifecycleGets++
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify({
      linkId: 'trace-lifecycle-link', state, events,
    }) })
  })
  await page.route('**/api/trace-links/trace-lifecycle-link/lifecycle/acknowledge', async route => {
    const request = route.request().postDataJSON() as { rationale: string }
    state = 'Acknowledged'
    events.push({ id: 'trace-acknowledged', type: 'Acknowledged', actorId: 'admin',
      occurredAt: '2026-08-23T00:01:00Z', rationale: request.rationale })
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify({
      linkId: 'trace-lifecycle-link', state,
    }) })
  })

  await login(page)
  // #880 §4.3 moved the Digital Thread into RELEASE and §4.5 kept the evidence table as the list
  // alternative, reached from the representation toggle. The exact-link lifecycle controls are a function
  // rather than chrome, so they came with it: the relationship is still acknowledged where it is read.
  await openNavigationGroup(page, 'RELEASE & CONFIGURATION')
  await page.getByRole('link', { name: 'Digital Thread' }).click()
  await page.locator('.dtPageToolbar').getByRole('button', { name: 'Table' }).click()
  await expect(page.locator('.dtPageTable tbody tr').first()).toBeVisible()
  await expect(page.getByRole('link', { name: 'SYSR-000001.00 · System' }))
    .toHaveAttribute('href', /\/requirements\/parent-artifact\?discipline=system&requirementRevisionId=parent-revision$/)
  await expect(page.getByLabel('Exact link lifecycle Suspect')).toBeVisible()
  expect(lifecycleGets).toBe(0)
  await page.getByPlaceholder('Record why this exact relationship is under assessment.')
    .fill('Review this shared exact-link relationship.')
  await page.getByRole('button', { name: 'Acknowledge relationship' }).click()
  await expect(page.getByLabel('Exact link lifecycle Acknowledged')).toBeVisible()
  expect(lifecycleGets).toBe(1)
})
