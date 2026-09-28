import { expect, test } from '@playwright/test'
import { execFileSync } from 'node:child_process'
import { mkdtempSync, readFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { resolve, join } from 'node:path'
import { login, showcaseSeed } from './auth'

// Authoring gate: HTTP tests do not prove that the review renders images/outcomes or invalidates
// confirmation after an operator changes mapping. No import is committed into the shared showcase.
test('Integrity package review shows preserved images and requires review after mapping changes', async ({ page, request }, testInfo) => {
  test.setTimeout(120_000)
  const fixture = mkdtempSync(join(tmpdir(), 'aerolink-integrity-browser-'))
  const externalRequests: string[] = []
  page.on('request', outgoing => { if (outgoing.url().startsWith('https://example.invalid/')) externalRequests.push(outgoing.url()) })
  const ownedRoot = resolve(fixture)
  if (!ownedRoot.startsWith(resolve(tmpdir()) + (process.platform === 'win32' ? '\\' : '/')) ||
      !ownedRoot.includes('aerolink-integrity-browser-')) throw new Error('Unexpected fixture cleanup path')
  try {
    execFileSync('pwsh', ['-NoProfile', '-File', resolve('../tools/integrity-extractor/Build-Fixture.ps1'), '-OutputDirectory', fixture],
      { timeout: 90_000, windowsHide: true })
    const showcase = await showcaseSeed(request)
    await login(page, 'admin', { openProject: false })
    await page.goto(`/programs/${showcase.programId}/projects/${showcase.projectId}/releases/${showcase.activeReleaseId}/problem-reports`)
    await page.getByRole('button', { name: 'Import from Integrity…' }).click()
    const panel = page.getByRole('region', { name: 'Import from Integrity', exact: true })
    await panel.getByLabel('Integrity source package').setInputFiles(join(fixture, 'source.zip'))
    await panel.getByLabel('Recorded manifest SHA-256').fill(readFileSync(join(fixture, 'source.zip.manifest.sha256'), 'utf8').trim())
    await panel.getByRole('button', { name: 'Read package' }).click()
    await panel.getByLabel('Map statuses: Active').selectOption('Open')
    await panel.getByLabel('Map statuses: Closed').selectOption('ClosedInSource')
    await panel.getByLabel('AeroLink account for Source Engineer').fill('admin')
    await panel.getByRole('button', { name: 'Review updated mapping' }).click()
    await expect(panel.getByText(/2 to create · 0 already imported · 0 need attention/)).toBeVisible()
    const first = panel.locator('tbody > tr').first()
    await first.getByText('Content and findings', { exact: true }).click()
    const image = first.getByRole('img', { name: 'Preserved source image' })
    await expect(image).toBeVisible()
    await expect(image).toHaveJSProperty('naturalWidth', 1)
    await expect(first).toContainText('Time with a known offset')
    await expect(first).toContainText('2024-03-01T10:34:56.123456')
    await expect(panel.locator('tbody > tr').nth(1)).toContainText('Date only')
    await first.getByText(/source outcomes/).click()
    await expect(first).toContainText('Preserved as a source reference')
    expect(externalRequests).toEqual([])
    await panel.getByLabel('Confirm with your password').fill('not-submitted')
    await expect(panel.getByRole('button', { name: 'Confirm import', exact: true })).toBeEnabled()
    await page.screenshot({ path: testInfo.outputPath('integrity-package-review.png'), fullPage: true, animations: 'disabled' })
    await panel.getByLabel('Map statuses: Active').selectOption('Draft')
    await expect(panel.getByRole('button', { name: 'Confirm import', exact: true })).toBeDisabled()
    await expect(panel.getByText('Mapping changed. Review the updated result before confirming.')).toBeVisible()
  } finally {
    rmSync(ownedRoot, { recursive: true, force: true })
  }
})
