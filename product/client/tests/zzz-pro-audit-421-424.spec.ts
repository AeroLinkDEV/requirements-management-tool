import { expect, test } from '@playwright/test'
import { login, openNavigationGroup } from './auth'

const procedureId = '42142400-0000-4000-8000-000000000001'
const revision00Id = '42142400-0000-4000-8000-000000000002'
const revision01Id = '42142400-0000-4000-8000-000000000003'

const titleA = 'Verify legacy route sequencing'
const titleB = 'Verify route sequencing and discontinuities'

/**
 * #421/#424 browser contract. Server-backed API tests prove the persistence projection; this focused browser
 * journey fixes both build responses so it can exercise predecessor/successor navigation, Trace -> History,
 * refresh and exact deep-link restoration without creating controlled records in the showcase database.
 */
test('predecessor and successor keep exact titles and folded provenance through deep links', async ({ page }) => {
  test.setTimeout(180_000)
  await login(page)
  await page.getByRole('button', { name: /Back to Software Builds/ }).click()
  await page.getByRole('button', { name: 'Open build 1.5' }).click()
  const predecessorReleaseId = new URL(page.url()).pathname.split('/')[6]
  expect(predecessorReleaseId).toMatch(/^[0-9a-f-]{36}$/)

  const revision = (successor: boolean) => ({
    id: successor ? revision01Id : revision00Id,
    displayNumber: `SYSTP-42499.${successor ? '01' : '00'}`,
    revision: successor ? 1 : 0,
    title: successor ? titleB : titleA,
    titleIsExact: true,
    titleIsLegacy: false,
    titleNote: null,
    state: 'Approved',
    authorId: 'test.author',
    createdAt: '2026-08-10T12:00:00Z',
    objective: 'Verify exact build behavior.',
    preconditions: 'The configured build is available.',
    steps: 'Exercise route sequencing.',
    expectedResult: 'Sequencing remains deterministic.',
    sourceTestChangeRequestId: successor ? revision01Id : revision00Id,
    package: `SYSTCR-42499.${successor ? '01' : '00'}`,
    provenanceNote: null,
    drivenBy: successor
      ? [
          { changeRequest: 'SRCR-42410.00', package: 'SYSTCR-42499.01', subjectDisplayNumber: 'SYSR-42410.00', action: 'ModifyExisting' },
          { changeRequest: 'SRCR-42420.00', package: 'SYSTCR-42499.01', subjectDisplayNumber: 'SYSR-42420.00', action: 'ModifyExisting' },
        ]
      : [{ changeRequest: 'SRCR-42400.00', package: 'SYSTCR-42499.00', subjectDisplayNumber: 'SYSR-42400.00', action: 'CreateNew' }],
    covers: successor ? ['SYSR-42410.00', 'SYSR-42420.00'] : ['SYSR-42400.00'],
  })

  await page.route(url => url.pathname.startsWith(`/api/test-procedures/${procedureId}`)
      || url.pathname === '/api/test-procedures', async route => {
    const url = new URL(route.request().url())
    const successor = url.searchParams.get('releaseId') !== predecessorReleaseId
    const selected = revision(successor)
    if (url.pathname === '/api/test-procedures') {
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
        page: 1, pageSize: 25, totalCount: 1, totalPages: 1,
        items: [{
          id: procedureId, revisionId: selected.id, displayNumber: selected.displayNumber,
          title: selected.title, titleIsExact: true, titleIsLegacy: false, titleNote: null,
          state: 'Approved', requirementCount: selected.covers.length, ownerId: 'test.author',
          objective: selected.objective, preconditions: selected.preconditions,
          steps: selected.steps, expectedResult: selected.expectedResult,
        }],
      }) })
      return
    }
    if (url.pathname.endsWith('/comments')) {
      await route.fulfill({ status: 200, contentType: 'application/json', body: '[]' })
      return
    }
    if (url.pathname.endsWith('/history')) {
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
        id: procedureId, baseNumber: 'SYSTP-42499', title: selected.title,
        titleIsExact: true, titleIsLegacy: false, titleNote: null,
        ownerId: 'test.author', createdAt: '2026-08-10T12:00:00Z',
        revisions: successor ? [revision(true), revision(false)] : [revision(false)],
      }) })
      return
    }
    if (url.pathname.endsWith('/trace')) {
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
        procedureId, baseNumber: 'SYSTP-42499', title: selected.title,
        titleIsExact: true, titleIsLegacy: false, titleNote: null,
        level: 'System', revisionId: selected.id, displayNumber: selected.displayNumber,
        revision: selected.revision, state: 'Approved', authorId: 'test.author',
        createdAt: selected.createdAt, sourceTestChangeRequestId: selected.sourceTestChangeRequestId,
        package: selected.package, provenanceNote: null, requirements: [],
        provenance: selected.drivenBy,
        build: { releaseId: url.searchParams.get('releaseId'), effectiveBaselineId: revision00Id,
          requirementBaselineId: revision00Id, isExactManifest: true },
      }) })
      return
    }
    await route.continue()
  })

  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Test Procedure Explorer' }).click()
  const predecessorRow = page.locator('.procedureRow').filter({ hasText: 'SYSTP-42499.00' })
  await expect(predecessorRow).toContainText(titleA, { timeout: 30_000 })
  await expect(page.locator('.procedureList')).not.toContainText(titleB)
  await predecessorRow.click()
  const predecessorInspector = page.locator('.requirementInspector')
  await predecessorInspector.getByRole('button', { name: 'Trace & impact' }).click()
  await expect(predecessorInspector.locator('.traceRevisionIdentity')).toContainText(titleA)
  await expect(predecessorInspector).toContainText('SYSTCR-42499.00 (SRCR-42400.00)')
  await predecessorInspector.getByRole('button', { name: 'History' }).click()
  await expect(predecessorInspector.locator('.revisionList')).toContainText(`SYSTP-42499.00 — ${titleA}`)
  const predecessorDeepLink = page.url()
  expect(predecessorDeepLink).toContain('procedureTab=history')
  await page.reload()
  await expect(page).toHaveURL(predecessorDeepLink)
  await expect(page.locator('.requirementInspector .revisionList')).toContainText(titleA, { timeout: 30_000 })

  await page.getByRole('button', { name: /Back to Software Builds/ }).click()
  await page.getByRole('button', { name: 'Open build 1.6' }).click()
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Test Procedure Explorer' }).click()
  const successorRow = page.locator('.procedureRow').filter({ hasText: 'SYSTP-42499.01' })
  await expect(successorRow).toContainText(titleB, { timeout: 30_000 })
  await successorRow.click()
  const successorInspector = page.locator('.requirementInspector')
  await successorInspector.getByRole('button', { name: 'Trace & impact' }).click()
  await expect(successorInspector.locator('.traceRevisionIdentity')).toContainText(titleB)
  await expect(successorInspector).toContainText('SYSTCR-42499.01 (SRCR-42410.00)')
  await expect(successorInspector).toContainText('SYSTCR-42499.01 (SRCR-42420.00)')
  await successorInspector.getByRole('button', { name: 'History' }).click()
  await expect(successorInspector.locator('.revisionList')).toContainText(`SYSTP-42499.00 — ${titleA}`)
  await expect(successorInspector.locator('.revisionList')).toContainText(`SYSTP-42499.01 — ${titleB}`)
  const successorDeepLink = page.url()
  await page.reload()
  await expect(page).toHaveURL(successorDeepLink)
  await expect(page.locator('.requirementInspector .revisionList')).toContainText('SYSTCR-42499.01 · SRCR-42420.00',
    { timeout: 30_000 })
})
