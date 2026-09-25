import { expect, test } from '@playwright/test'
import { login, showcaseSeed } from './auth'

/**
 * #1114 — Problem Report import. The journey previews a CSV export against the shared showcase project and
 * stops before signing, so it never adds records other journeys count; the commit, signature, source facts
 * and re-import rules are covered by ProblemReportImportApiTests.
 */
test('a CSV export is mapped and previewed row by row before anything is imported', async ({ page, request }) => {
  const showcase = await showcaseSeed(request)
  const root = `/programs/${showcase.programId}/projects/${showcase.projectId}/releases/${showcase.activeReleaseId}`
  await login(page, 'admin', { openProject: false })
  await page.goto(`${root}/problem-reports`)
  await page.getByRole('button', { name: 'Import…' }).click()
  const panel = page.getByRole('region', { name: 'Import Problem Reports' })
  await expect(panel).toBeVisible()

  await panel.getByLabel('Source system').fill('Jira')
  await panel.getByLabel('Export file (.csv or .xlsx)').setInputFiles({
    name: 'jira-export.csv',
    mimeType: 'text/csv',
    buffer: Buffer.from([
      'Key,Summary,Description,Status,Severity',
      'JIRA-1,Nav freeze,Display freezes on waypoint insert,Closed,High',
      'JIRA-2,Odd status,Something happened,Weird,Major',
      '',
    ].join('\n')),
  })
  await panel.getByRole('button', { name: 'Read the file' }).click()

  // The obvious columns are recognised from the headers.
  const columns = panel.getByRole('group', { name: 'Columns' })
  await expect(columns.getByLabel('Source key *')).toHaveValue('Key')
  await expect(columns.getByLabel('Title *')).toHaveValue('Summary')
  const rows = panel.locator('.prImportRows tbody tr')
  await expect(rows).toHaveCount(2)
  await expect(rows.nth(0)).toContainText('Skip · Map the source status "Closed".')

  await panel.getByLabel('Map Closed').selectOption('ClosedInSource')
  await expect(panel.getByText('The mapping changed. Preview again to see the result.')).toBeVisible()
  await panel.getByRole('button', { name: 'Preview again' }).click()
  await expect(rows.nth(0)).toContainText('Create · Closed in source')
  await expect(rows.nth(1)).toContainText('Skip · Map the source status "Weird".')
  await expect(panel.locator('caption')).toHaveText('1 will be created · 1 will be skipped')

  // Nothing is written without the importer's signature.
  await expect(panel.getByRole('button', { name: /Sign and import 1 Problem Report/ })).toBeDisabled()
  if (process.env.AEROLINK_1113_EVIDENCE) await page.screenshot({ path: `${process.env.AEROLINK_1113_EVIDENCE}/problem-report-import.png`, fullPage: true, animations: 'disabled' })
})
