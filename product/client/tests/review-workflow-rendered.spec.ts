import { expect, test } from '@playwright/test'
import { auditSurface } from './helpers/design-audit'

// The real component and cascade, with server-shaped retained history. The audit's
// broad route sweep does not open disclosures, and can sample before data arrives.
test('retained review history is audited only when painted and stays readable when opened', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await page.route('**/api/review-workflows?projectId=fixture-project', route => route.fulfill({ json: [{
    id: 'retired-workflow', logicalId: 'workflow', name: 'Retained systems review',
    appliesTo: 'System', state: 'Retired', version: 1, mode: 'Sequential',
    createdBy: 'admin', createdAt: '2026-09-28T12:00:00Z',
    stages: [{ position: 0, name: 'Engineering review', kind: 'Review', requiredRole: 'SystemEngineer' }],
  }] }))
  await page.goto('/tests/fixtures/review-workflow.html')
  const history = page.locator('.workflowHistory')
  const summary = history.locator('summary')
  const author = history.locator('.personName')
  await expect(summary).toHaveText('1 other version retained')

  for (const density of ['comfortable', 'compact']) {
    await page.evaluate(value => { document.documentElement.dataset.density = value }, density)
    // A deliberately undersized history probes the audit independently of the CSS fix.
    // Chromium can give descendants of closed details nonzero boxes after layout.
    const tinyStyle = await page.addStyleTag({ content: '.workflowHistory small { font-size: 9px !important; }' })
    await expect(history).not.toHaveAttribute('open')
    await author.evaluate(el => el.getBoundingClientRect())
    expect((await page.evaluate(auditSurface, 12)).tiny, `hidden history [${density}]`).toEqual([])
    await summary.click()
    await expect(author).toBeVisible()
    expect((await page.evaluate(auditSurface, 12)).tiny).toContain('admin @ 9px')
    await tinyStyle.evaluate(el => el.remove())

    // The visible product contract: includes the inherited author font, not just the
    // parent's explicit size. This failed at 11px on the pre-fix component.
    expect(await author.evaluate(el => parseFloat(getComputedStyle(el).fontSize))).toBeGreaterThanOrEqual(12)
    expect((await page.evaluate(auditSurface, 12)).tiny, `open history [${density}]`).toEqual([])
    await page.screenshot({ path: `test-results/review-history-${density}.png` })
    await summary.click()
  }
})
