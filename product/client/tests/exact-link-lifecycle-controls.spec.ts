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
  await page.getByText('Baseline evidence report', { exact: true }).click()
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

test('a recorded relationship refreshes the active Artifact and keeps a follow-up read failure truthful', async ({ page }) => {
  let lifecycleState = 'Suspect'
  let lifecycleGets = 0
  let artifactReads = 0
  let networkReads = 0
  const focalId = 'artifact-focal'
  const focalRevisionId = 'artifact-focal-revision'

  // The active Artifact response deliberately changes only the server-stated edge fact on its second read.
  // The page must reread the projection after the controlled POST; it must not infer the new state locally.
  await page.route('**/api/requirements?*', async route => {
    await route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({ items: [{ id: focalId, revisionId: focalRevisionId }] }),
    })
  })
  await page.route('**/api/artifact-thread?*', async route => {
    const url = new URL(route.request().url())
    artifactReads += 1
    await route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({
        projectId: url.searchParams.get('projectId'),
        baselineId: url.searchParams.get('baselineId'),
        buildId: null,
        focalKind: 'Requirement',
        focalId: focalRevisionId,
        nodes: [
          { id: focalRevisionId, kind: 'Requirement', lane: 2, displayNumber: 'HLR-REFRESH-0001.00',
            title: 'Refresh focal', state: 'Approved', level: 'HighLevel', isFocal: true, evidence: [] },
          { id: 'parent-revision', kind: 'Requirement', lane: 2, displayNumber: 'SYSR-REFRESH-0001.00',
            title: 'Refresh parent', state: 'Approved', level: 'System', isFocal: false, evidence: [] },
        ],
        edges: [{ fromId: 'parent-revision', fromKind: 'Requirement', toId: focalRevisionId,
          toKind: 'Requirement', relation: 'AllocatedFrom', isSuspect: artifactReads === 1 }],
        verification: { isApplicable: true, reason: null },
      }),
    })
  })
  await page.route('**/api/change-requests/network?*', async route => {
    networkReads += 1
    await route.continue()
  })
  await page.route('**/api/traceability?*', async route => {
    const response = await route.fetch()
    const body = await response.json()
    if (body.items?.[0]) body.items[0].parents = [{
      id: 'parent-revision', revisionId: 'parent-revision', artifactId: 'parent-artifact', linkId: 'trace-refresh-link',
      displayNumber: 'SYSR-REFRESH-0001.00', level: 'System', type: 'AllocatedFrom',
      lifecycle: { state: lifecycleState, events: [{
        id: 'trace-raised', type: 'Raised', actorId: 'requirements.materializer', occurredAt: '2026-08-23T00:00:00Z',
        rationale: 'The exact requirement revision changed.',
      }] },
    }]
    await route.fulfill({ response, json: body })
  })
  await page.route('**/api/trace-links/trace-refresh-link/lifecycle', async route => {
    lifecycleGets += 1
    await route.fulfill({ status: 503, contentType: 'application/json', body: '{}' })
  })
  await page.route('**/api/trace-links/trace-refresh-link/lifecycle/resolve', async route => {
    lifecycleState = 'Closed'
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify({
      linkId: 'trace-refresh-link', state: lifecycleState,
    }) })
  })

  await login(page)
  await openNavigationGroup(page, 'RELEASE & CONFIGURATION')
  await page.getByRole('link', { name: 'Digital Thread' }).click()
  const threadRoot = new URL(page.url()).pathname.replace(/\/traceability.*$/, '')
  await page.goto(`${threadRoot}/traceability/${focalId}`)
  await expect(page.locator('.dtaRoot')).toBeVisible()
  const focalCard = page.locator('.dtaCard').filter({ hasText: 'HLR-REFRESH-0001.00' }).first()
  const parentCard = page.locator('.dtaCard').filter({ hasText: 'SYSR-REFRESH-0001.00' }).first()
  await expect(focalCard.locator('.dtaSuspectFlag b')).toHaveText('SUSPECT')
  await expect(parentCard.locator('.dtaSuspectFlag b')).toHaveText('SUSPECT')
  const networkReadsBeforeMutation = networkReads

  await page.getByText('Baseline evidence report', { exact: true }).click()
  await expect(page.getByLabel('Exact link lifecycle Suspect')).toBeVisible()
  await page.getByPlaceholder('Record the controlled disposition and supporting rationale.')
    .fill('The downstream revision remains valid after controlled review.')
  await page.getByRole('button', { name: 'Record resolution' }).click()

  await expect.poll(() => artifactReads).toBeGreaterThan(1)
  await expect(page.getByText('Relationship Closed', { exact: true })).toBeVisible()
  await expect(focalCard.locator('.dtaSuspectFlag')).toHaveCount(0)
  await expect(parentCard.locator('.dtaSuspectFlag')).toHaveCount(0)
  expect(lifecycleGets).toBe(1)
  expect(networkReads).toBe(networkReadsBeforeMutation)
})

test('the active table states loading, error, truncation and no-match without a baseline read', async ({ page }) => {
  let releaseNetwork!: () => void
  let allowNetworkSuccess = false
  let traceabilityReads = 0
  const networkPending = new Promise<void>(resolve => { releaseNetwork = resolve })
  await page.route('**/api/change-requests/network?*', async route => {
    if (!allowNetworkSuccess) {
      await networkPending
      await route.fulfill({ status: 503, contentType: 'application/json', body: '{}' })
      return
    }
    const response = await route.fetch()
    const body = await response.json()
    body.truncated = true
    await route.fulfill({ response, json: body })
  })
  page.on('request', request => {
    if (new URL(request.url()).pathname.endsWith('/api/traceability')) traceabilityReads += 1
  })

  await login(page)
  await openNavigationGroup(page, 'RELEASE & CONFIGURATION')
  await page.getByRole('link', { name: 'Digital Thread' }).click()
  await page.locator('.dtPageToolbar').getByRole('button', { name: 'Table' }).click()
  await expect(page.locator('.dtThreadTableState')).toContainText('Loading this Digital Thread table')

  releaseNetwork()
  await expect(page.locator('.dtThreadTableState-error')).toBeVisible()
  allowNetworkSuccess = true
  await page.getByRole('button', { name: 'Try again' }).click()
  await expect(page.locator('.dtThreadTableTruncated')).toContainText('not shown')
  await expect(page.locator('.dtThreadTableSelection')).toContainText('No record selected')
  await page.locator('.dtnSearch input').fill('no active record has this identifier')
  await expect(page.locator('.dtThreadTableState')).toContainText('No records match')
  expect(traceabilityReads).toBe(0)
})
