import { expect, test } from '@playwright/test'

test('overlapping upstream reports require explicit acceptance and refresh preserves authored choices', async ({ page }, testInfo) => {
  let refreshed = false
  let unavailable = false
  const report = (id: string, targetReleaseId = 'build') => ({ id, projectId: 'project', displayNumber: `PR-${id}`, title: `${id} anomaly`, state: 'Open', targetReleaseId })
  await page.route('**/api/problem-reports/linked/**', route => {
    if (unavailable) return route.fulfill({ status: 503, body: '{}' })
    const source = route.request().url().split('/').pop()
    const reports = source === 'source-a' ? [report('shared'), report('a-only'), report('earlier', 'old-build')]
      : source === 'source-b' ? [report('shared'), report(refreshed ? 'new-context' : 'b-only')]
        : [report('case-context')]
    return route.fulfill({ json: reports })
  })
  await page.goto('/tests/fixtures/inherited-problem-reports.html')
  const context = page.getByRole('group', { name: 'Inherited Problem Report context' })
  const choice = (id: string) => context.getByRole('checkbox', { name: new RegExp(`PR-${id} `) })
  await expect(choice('shared')).toHaveCount(1)
  await expect(choice('shared')).not.toBeChecked()
  await expect(choice('earlier')).toBeDisabled()
  await expect(context.getByText('Inherited from SRCR-00001.02, SRCR-00002.01.')).toBeVisible()
  await expect(context.getByText('Inherited from HLRTCCR-00003.00.')).toBeVisible()
  await expect(page.getByLabel('Direct selections')).toHaveText('manual')
  await choice('shared').check()
  await choice('a-only').check()
  await choice('case-context').check()
  await page.getByRole('button', { name: 'Select another manual PR' }).click()
  await expect(page.getByLabel('Direct selections')).toHaveText('manual,shared,a-only,case-context,manual-2')
  await page.getByRole('button', { name: 'Remove first source' }).click()
  await expect(choice('a-only')).toHaveCount(0)
  await expect(choice('shared')).toBeChecked()
  refreshed = true
  await context.getByRole('button', { name: 'Refresh inherited context' }).click()
  await expect(choice('new-context')).not.toBeChecked()
  await expect(choice('b-only')).toHaveCount(0)
  await expect(page.getByLabel('Direct selections')).toHaveText('manual,shared,a-only,case-context,manual-2')
  unavailable = true
  await context.getByRole('button', { name: 'Refresh inherited context' }).click()
  await expect(context.getByRole('alert')).toContainText('direct selections are unchanged')
  await expect(choice('shared')).toBeDisabled()
  await expect(page.getByLabel('Direct selections')).toHaveText('manual,shared,a-only,case-context,manual-2')
  await page.screenshot({ path: testInfo.outputPath('inherited-context-explicit-selections.png'), fullPage: true })
})
