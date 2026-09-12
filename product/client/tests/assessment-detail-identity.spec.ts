import { expect, test } from '@playwright/test'

for (const [discipline, artifactKind, outcome, state, label] of [
  ['System', 'Procedure', 'Pending', 'Draft', 'Pending assessment'],
  ['HighLevelSoftware', 'Case', 'Pending', 'Draft', 'Pending assessment'],
  ['LowLevelSoftware', 'Procedure', 'NoChangeRequired', 'Approved', 'No change required'],
  ['HighLevelSoftware', 'Procedure', 'Pending', 'Superseded', 'Superseded assessment'],
  ['System', 'Procedure', 'ChangeRequired', 'Draft', 'Change required; number not assigned'],
]) test(`unnumbered ${discipline} ${artifactKind} ${state}/${outcome} keeps source identity separate`, async ({ page }, testInfo) => {
  const detail = {
    id: 'assessment', projectId: 'project', releaseId: 'build', baseNumber: '', revision: 0,
    displayNumber: 'SRCR-00143.00', sourceChangeRequestNumber: 'SRCR-00143.00',
    title: '', problem: '', analysis: '', solution: '', authorId: '', version: 1,
    discipline, artifactKind, outcome, state,
    originKind: 'ChangeRequest', originDisplayIdentity: 'SRCR-00143.00', originDisplayTitle: 'Exact driving change',
    coveredChangeRequests: [{ id: 'source', number: 'SRCR-00143.00', title: 'Exact driving change', originating: true }],
    artifactChanges: [], capabilities: {},
  }
  await page.route('**/api/**', route => {
    if (route.request().method() !== 'GET') throw new Error('Read-only assessment navigation must not mutate a record')
    const path = new URL(route.request().url()).pathname
    if (path === '/api/test-change-reviews/assessment/case-changes' && artifactKind === 'Procedure')
      return route.fulfill({ status: 404, json: { error: 'Use Procedure detail' } })
    if (path.startsWith('/api/test-change-reviews/assessment/') && /(?:case|procedure)-changes$/.test(path))
      return route.fulfill({ json: detail })
    if (path === '/api/releases/build/test-change-reviews') return route.fulfill({ json: { items: [detail] } })
    if (path === '/api/controlled-editing/status') return route.fulfill({ json: { editable: false, locked: false, mine: false } })
    return route.fulfill({ json: [] })
  })
  await page.goto(`/tests/fixtures/assessment-page.html?discipline=${discipline}`)
  await expect(page.getByText('VERIFICATION ASSESSMENT / Unnumbered assessment', { exact: true })).toBeVisible()
  const status = page.locator('.controlStatusCard')
  await expect(status.getByText('Unnumbered assessment', { exact: true })).toBeVisible()
  await expect(status.getByText(label, { exact: true })).toBeVisible()
  await expect(status.getByText('Not assigned', { exact: true })).toBeVisible()
  await expect(status.getByText('SRCR-00143.00', { exact: true })).toHaveCount(0)
  await expect(page.getByText('Exact driving change')).toBeVisible()
  await expect(page.getByRole('link', { name: 'Download DOCX', exact: true })).toHaveCount(0)
  await expect(page.getByRole('link', { name: 'Download PDF', exact: true })).toHaveCount(0)
  await page.screenshot({ path: testInfo.outputPath('assessment-detail-identity.png'), fullPage: true })
})
