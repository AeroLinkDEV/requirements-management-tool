import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, login, selectProgram, showcaseSeed } from './auth'

test('Problem Report impact lays out an exact recorded file reference across desktop widths', async ({ page, request }, testInfo) => {
  test.setTimeout(300_000)

  const pageErrors: string[] = []
  page.on('pageerror', error => pageErrors.push(error.message))

  await apiLogin(request)
  const showcase = await showcaseSeed(request)
  const title = `Recorded Code layout ${Date.now()}`
  const created = await request.post(`${apiBase}/api/problem-reports`, { data: {
    category: 'CodeFunctional',
    projectId: showcase.projectId,
    releaseId: showcase.activeReleaseId,
    title,
    problem: 'A recorded source reference must remain readable in the impact row.',
    impactAssessmentJson: JSON.stringify({ Code: 'Yes' }),
  } })
  expect(created.ok(), await created.text()).toBeTruthy()
  const { id: reportId } = await created.json() as { id: string }

  const reportResponse = await request.get(`${apiBase}/api/problem-reports/${reportId}`)
  expect(reportResponse.ok(), await reportResponse.text()).toBeTruthy()
  const report = await reportResponse.json() as { displayNumber: string }
  const relationshipId = '44444444-4444-4444-8444-444444444444'
  const sourceSnapshotId = '55555555-5555-4555-8555-555555555555'
  const targetSnapshotId = '66666666-6666-4666-8666-666666666666'
  const reference = {
    id: relationshipId,
    relationshipKind: 'File',
    version: 1,
    isActive: true,
    releaseId: showcase.activeReleaseId,
    releaseVersion: '1.6',
    meaning: 'RelatedContext',
    recordedBy: 'admin',
    recordedAt: '2026-09-21T12:00:00Z',
    targetKind: 'ProblemReportRevision',
    targetIdentityId: targetSnapshotId,
    targetOwnerIdentityId: reportId,
    targetRevisionNumber: 0,
    targetStableIdentity: `ProblemReportRevision:${targetSnapshotId}`,
    targetDisplaySnapshot: report.displayNumber,
    instanceBaseUrl: 'https://gitlab.example',
    remoteProjectId: 42,
    repositoryPathSnapshot: 'seanmccarthyns/aerolink-fms-trace-demo',
    sourceSnapshotId,
    sourceSelectionEventId: null,
    mergeRequestIid: null,
    mergeRequestId: null,
    mergeRequestUrlSnapshot: null,
    mergeRequestTitleSnapshot: null,
    commitSha: 'a'.repeat(40),
    path: 'src/common/result_status.c',
    startLine: 4,
    endLine: 12,
    fileMergeRequestIid: null,
  }

  // Keep the real report and API response shape, adding only the valid relationship DTO that exercises
  // this display path. The intercepted API response and all report writes belong to Playwright's disposable
  // SQLite database; no HOME data or persistent product evidence is involved.
  await page.route(`**/api/problem-reports/${reportId}`, async route => {
    const response = await route.fetch()
    const body = await response.json() as {
      impactAreas: { key: string; artifacts: unknown[] }[]
    }
    const codeArea = body.impactAreas.find(area => area.key === 'Code')
    if (!codeArea) throw new Error('The Problem Report detail did not include its Code impact area.')
    codeArea.artifacts = [{
      artifactType: 'GitLabReference',
      artifactId: relationshipId,
      identifier: 'Stored file reference',
      title: '',
      state: 'Reference recorded',
      targetBuild: '1.6',
      relationship: 'RelatedContext',
      detail: 'Stored source snapshot',
      codeReference: reference,
    }]
    await route.fulfill({ response, json: body })
  })

  await page.setViewportSize({ width: 2048, height: 1000 })
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
  await page.goto(new URL(`${root}/problem-reports`, page.url()).toString(), { waitUntil: 'load' })
  await page.getByLabel('Search').fill(title)
  await page.locator('.prList').getByText(title).click()

  const panel = page.getByRole('region', { name: 'Impact and linked evidence' })
  const codeRow = panel.locator('.impactRow').filter({ hasText: 'Code' })
  const artifact = codeRow.locator('.impactArtifact.readOnly')
  const exactReference = artifact.locator('.recordedCodeReference')
  await expect(exactReference).toHaveCount(1)
  await expect(exactReference.locator('.recordedCodeReferenceTarget')).toContainText(report.displayNumber)
  await expect(exactReference).toContainText('Not accepted implementation evidence')
  await expect(exactReference.getByRole('link', { name: 'Open stored GitLab reference ↗' }))
    .toHaveAttribute('href', `https://gitlab.example/seanmccarthyns/aerolink-fms-trace-demo/-/blob/${'a'.repeat(40)}/src/common/result_status.c#L4-L12`)

  for (const width of [2048, 1386, 1087]) {
    await page.setViewportSize({ width, height: 1000 })
    await exactReference.scrollIntoViewIfNeeded()
    const layout = await exactReference.evaluate(element => {
      const artifactElement = element.closest('.impactArtifact')
      if (!artifactElement) throw new Error('The recorded Code reference is detached from its impact artifact.')
      const card = element.getBoundingClientRect()
      const parent = artifactElement.getBoundingClientRect()
      const summaryTitle = artifactElement.querySelector(':scope > span > b')
      if (!summaryTitle) throw new Error('The impact artifact summary title is missing.')
      return {
        viewportWidth: window.innerWidth,
        cardWidth: card.width,
        artifactWidth: parent.width,
        leftOffset: card.left - parent.left,
        summaryTitleWidth: summaryTitle.getBoundingClientRect().width,
        scrollWidth: element.scrollWidth,
        clientWidth: element.clientWidth,
      }
    })

    expect(layout.viewportWidth).toBe(width)
    expect(layout.cardWidth).toBeGreaterThan(layout.artifactWidth * 0.85)
    expect(layout.cardWidth).toBeGreaterThanOrEqual(width === 1087 ? 180 : 300)
    expect(layout.leftOffset).toBeLessThan(20)
    expect(layout.summaryTitleWidth).toBeGreaterThan(100)
    expect(layout.scrollWidth).toBeLessThanOrEqual(layout.clientWidth)
    await expect(exactReference.getByRole('link', { name: 'Open stored GitLab reference ↗' })).toBeVisible()
    await page.screenshot({ path: testInfo.outputPath(`pr-impact-layout-${width}.png`) })
  }

  const storedReferenceLink = exactReference.getByRole('link', { name: 'Open stored GitLab reference ↗' })
  await page.evaluate(() => {
    const link = document.querySelector<HTMLAnchorElement>('.recordedCodeReference a[href]')
    if (!link) throw new Error('The exact stored reference link is missing from the tab order.')
    const focusable = Array.from(document.querySelectorAll<HTMLElement>(
      'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])',
    )).filter(element => {
      const style = window.getComputedStyle(element)
      return element.tabIndex >= 0 && style.display !== 'none' && style.visibility !== 'hidden' && element.getClientRects().length > 0
    })
    const linkIndex = focusable.indexOf(link)
    if (linkIndex < 1) throw new Error('The exact stored reference link has no preceding keyboard-focusable control.')
    focusable[linkIndex - 1].focus()
  })
  await page.keyboard.press('Tab')
  await expect(storedReferenceLink).toBeFocused()
  await page.evaluate(() => window.scrollTo(0, 0))
  await page.screenshot({ path: testInfo.outputPath('pr-impact-layout-1087-full.png'), fullPage: true })
  await page.setViewportSize({ width: 2048, height: 1000 })
  await exactReference.scrollIntoViewIfNeeded()
  await exactReference.screenshot({ path: testInfo.outputPath('pr-impact-layout-2048-card.png') })

  expect(pageErrors).toEqual([])
})
