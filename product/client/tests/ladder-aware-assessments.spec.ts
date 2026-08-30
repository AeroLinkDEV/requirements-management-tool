import { expect, test } from '@playwright/test'
import { login, showcaseSeed } from './auth'

const buildPath = (showcase: { programId: string; projectId: string; activeReleaseId: string }, area: string) =>
  `/programs/${showcase.programId}/projects/${showcase.projectId}/releases/${showcase.activeReleaseId}/${area}`

type EffectiveStep = { catalogueEntry: string; position: number; capabilities: number | string }
type EffectiveRelationship = { parent: string; child: string }

async function useEffectiveLadder(
  page: import('@playwright/test').Page,
  projectId: string,
  effectiveSteps: EffectiveStep[],
  effectiveRelationships: EffectiveRelationship[],
) {
  await page.route(`**/api/projects/${projectId}/configuration`, async route => {
    const response = await route.fetch()
    const body = await response.json() as Record<string, unknown>
    await route.fulfill({ response, json: { ...body, effectiveSteps, effectiveRelationships } })
  })
}

const draftQueueRequest = (url: string) =>
  url.includes('/api/history/change-requests') && url.includes('state=Draft') && url.includes('pageSize=100')

test('the effective ladder keeps the FMS System root free of downstream-assessment requests', async ({ page, request }) => {
  const showcase = await showcaseSeed(request)
  await login(page)
  let assessmentRequests = 0
  let draftRequests = 0
  page.on('request', outgoing => {
    if (outgoing.url().includes('/api/downstream-assessments')) assessmentRequests++
    if (draftQueueRequest(outgoing.url())) draftRequests++
  })

  await page.goto(buildPath(showcase, 'systems/change-requests'))
  await expect(page.getByRole('heading', { name: 'System Change Requests', level: 1 })).toBeVisible()
  await expect(page.locator('.downstreamQueue')).toHaveCount(0)
  expect(assessmentRequests).toBe(0)
  expect(draftRequests).toBe(0)
})

test('the Interface change-request register never borrows the software assessment queue', async ({ page, request }) => {
  const showcase = await showcaseSeed(request)
  await login(page)
  let assessmentRequests = 0
  let draftRequests = 0
  page.on('request', outgoing => {
    if (outgoing.url().includes('/api/downstream-assessments')) assessmentRequests++
    if (draftQueueRequest(outgoing.url())) draftRequests++
  })

  await page.goto(buildPath(showcase, 'interfaces/change-requests'))
  await expect(page.getByRole('heading', { name: 'Interface / ICD Change Requests', level: 1 })).toBeVisible()
  await expect(page.locator('.downstreamQueue')).toHaveCount(0)
  expect(assessmentRequests).toBe(0)
  expect(draftRequests).toBe(0)
})

test('a direct System to LowLevel effective edge makes one LLR queue request', async ({ page, request }) => {
  const showcase = await showcaseSeed(request)
  await login(page, 'admin', { openProject: false })
  await useEffectiveLadder(page, showcase.projectId, [
    { catalogueEntry: 'System', position: 1, capabilities: 15 },
    { catalogueEntry: 'LowLevel', position: 2, capabilities: 15 },
  ], [{ parent: 'System', child: 'LowLevel' }])
  let assessmentRequests = 0
  page.on('request', outgoing => {
    if (outgoing.url().includes('/api/downstream-assessments') && outgoing.url().includes('targetLevel=LowLevel')) assessmentRequests++
  })

  await page.goto(buildPath(showcase, 'software/change-requests?level=LLR'))
  const queue = page.locator('.downstreamQueue')
  await expect(queue).toBeVisible()
  await expect(queue).toContainText('LLR engineering conclusion')
  await expect(queue).toHaveAttribute('data-queue-state', /empty|rows/)
  expect(assessmentRequests).toBe(1)
})

test('an Interface parent makes System assessments applicable without a System special case', async ({ page, request }) => {
  const showcase = await showcaseSeed(request)
  await login(page, 'admin', { openProject: false })
  await useEffectiveLadder(page, showcase.projectId, [
    { catalogueEntry: 'Interface', position: 1, capabilities: 15 },
    { catalogueEntry: 'System', position: 2, capabilities: 15 },
  ], [{ parent: 'Interface', child: 'System' }])
  let assessmentRequests = 0
  page.on('request', outgoing => {
    if (outgoing.url().includes('/api/downstream-assessments') && outgoing.url().includes('targetLevel=System')) assessmentRequests++
  })

  await page.goto(buildPath(showcase, 'systems/change-requests'))
  const queue = page.locator('.downstreamQueue')
  await expect(queue).toBeVisible()
  await expect(queue).toContainText('System engineering conclusion')
  await expect(queue).toHaveAttribute('data-queue-state', /empty|rows/)
  expect(assessmentRequests).toBe(1)
})

test('multiple effective parents still mount one queue and issue one target request', async ({ page, request }) => {
  const showcase = await showcaseSeed(request)
  await login(page, 'admin', { openProject: false })
  await useEffectiveLadder(page, showcase.projectId, [
    { catalogueEntry: 'Interface', position: 1, capabilities: 15 },
    { catalogueEntry: 'System', position: 2, capabilities: 15 },
    { catalogueEntry: 'LowLevel', position: 3, capabilities: 15 },
  ], [{ parent: 'Interface', child: 'LowLevel' }, { parent: 'System', child: 'LowLevel' }])
  let assessmentRequests = 0
  page.on('request', outgoing => {
    if (outgoing.url().includes('/api/downstream-assessments') && outgoing.url().includes('targetLevel=LowLevel')) assessmentRequests++
  })

  await page.goto(buildPath(showcase, 'software/change-requests?level=LLR'))
  const queue = page.locator('.downstreamQueue')
  await expect(queue).toHaveCount(1)
  await expect(queue).toHaveAttribute('data-queue-state', /empty|rows/)
  expect(assessmentRequests).toBe(1)
})

test('an incoming parent without ChangeControl makes no queue and issues no queue reads', async ({ page, request }) => {
  const showcase = await showcaseSeed(request)
  await login(page, 'admin', { openProject: false })
  await useEffectiveLadder(page, showcase.projectId, [
    { catalogueEntry: 'System', position: 1, capabilities: 2 },
    { catalogueEntry: 'HighLevel', position: 2, capabilities: 15 },
  ], [{ parent: 'System', child: 'HighLevel' }])
  let assessmentRequests = 0
  let draftRequests = 0
  page.on('request', outgoing => {
    if (outgoing.url().includes('/api/downstream-assessments')) assessmentRequests++
    if (draftQueueRequest(outgoing.url())) draftRequests++
  })

  await page.goto(buildPath(showcase, 'software/change-requests?level=HLR'))
  await expect(page.getByRole('heading', { name: 'Software Change Requests', level: 1 })).toBeVisible()
  await expect(page.locator('.downstreamQueue')).toHaveCount(0)
  expect(assessmentRequests).toBe(0)
  expect(draftRequests).toBe(0)
})

test('an applicable HLR queue distinguishes loading and a successful empty result with level-aware copy', async ({ page, request }) => {
  const showcase = await showcaseSeed(request)
  await login(page)
  let assessmentRequests = 0
  await page.route('**/api/downstream-assessments*', async route => {
    assessmentRequests++
    await new Promise(resolve => setTimeout(resolve, 2_000))
    await route.fulfill({ status: 200, contentType: 'application/json', body: '[]' })
  })

  await page.goto(buildPath(showcase, 'software/change-requests?level=HLR'), { waitUntil: 'domcontentloaded' })
  const queue = page.locator('.downstreamQueue')
  await expect(queue).toHaveAttribute('data-queue-state', 'loading')
  await expect(queue).toContainText('Approved upstream changes waiting for an explicit HLR engineering conclusion.')
  await expect(queue).toHaveAttribute('data-queue-state', 'empty')
  await expect(queue).toContainText('No HLR downstream assessments are currently recorded.')
  expect(assessmentRequests).toBe(1)
})

test('an applicable HLR queue reports a read failure and can retry into empty state', async ({ page, request }) => {
  const showcase = await showcaseSeed(request)
  await login(page)
  let assessmentRequests = 0
  await page.route('**/api/downstream-assessments*', async route => {
    assessmentRequests++
    if (assessmentRequests === 1) {
      await route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ error: 'Temporary test outage.' }) })
      return
    }
    await route.fulfill({ status: 200, contentType: 'application/json', body: '[]' })
  })

  await page.goto(buildPath(showcase, 'software/change-requests?level=HLR'))
  const queue = page.locator('.downstreamQueue')
  await expect(queue).toHaveAttribute('data-queue-state', 'error')
  await expect(queue.getByRole('alert')).toContainText('Temporary test outage.')
  await queue.getByRole('button', { name: 'Retry loading assessments' }).click()
  await expect(queue).toHaveAttribute('data-queue-state', 'empty')
  expect(assessmentRequests).toBe(2)
})
