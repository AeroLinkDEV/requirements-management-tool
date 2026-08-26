import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, login, selectProgram, showcaseSeed } from './auth'

/**
 * Impact answers and the evidence that arrived under them, on one panel.
 *
 * This replaced three places a reader had to look — an impact matrix, "Approved linked change requests",
 * and "Connected engineering artifacts" — none of which sat next to the answer it was evidence for. The
 * question worth answering is "we said system requirements are impacted; what actually happened about
 * it?", and it should be answerable by looking at one row.
 */
test('a change request raised months later appears under the impact answer it belongs to', async ({ page, request }) => {
  test.setTimeout(300_000)
  await apiLogin(request)
  const showcase = await showcaseSeed(request)
  const stamp = Date.now()
  const title = `Impact evidence journey ${stamp}`

  // Raised saying system requirements are impacted, with nothing linked yet — which is the ordinary case:
  // the assessment is made when the problem is found, and the correction arrives later.
  const raised = await request.post(`${apiBase}/api/problem-reports`, { data: {
    category: 'CodeFunctional',
    projectId: showcase.projectId,
    releaseId: showcase.activeReleaseId,
    title,
    problem: 'The disconnect tone follows the disconnect by about a second.',
    impactAssessmentJson: JSON.stringify({ SystemRequirements: 'Yes', Hlr: 'No' }),
  } })
  expect(raised.ok(), await raised.text()).toBeTruthy()
  const report = await raised.json() as { id: string }

  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
  // Found the way a person finds it: the queue is Project-wide and paged, so search then select.
  const open = async () => {
    await page.goto(new URL(`${root}/problem-reports`, page.url()).toString(), { waitUntil: 'load' })
    await page.getByLabel('Search').fill(title)
    await page.locator('.prList').getByText(title).click()
    await expect(page.getByRole('heading', { name: title })).toBeVisible({ timeout: 30_000 })
  }
  await open()

  // The three superseded sections are gone; one panel carries the answers and the evidence together.
  const panel = page.getByRole('region', { name: 'Impact and linked evidence' })
  await expect(panel).toBeVisible()
  await expect(page.getByText('Approved linked change requests')).toHaveCount(0)
  await expect(page.getByText('Connected engineering artifacts')).toHaveCount(0)

  const systemRow = panel.locator('.impactRow').filter({ hasText: 'System requirements' })
  await expect(systemRow.locator('.impactPill')).toHaveText('Impacted')
  await expect(systemRow.locator('.impactEmpty')).toContainText('Nothing has named this Problem Report yet.')
  // An area nobody assessed says so, rather than reading as "not impacted".
  await expect(panel.locator('.impactRow').filter({ hasText: 'Low-level requirements' }).locator('.impactPill'))
    .toHaveText('Unknown')

  // Months later, in the story: a change request is raised and names this report.
  const draft = await request.post(`${apiBase}/api/change-request-drafts`, { data: {
    projectId: showcase.projectId,
    targetReleaseId: showcase.activeReleaseId,
    title: `Correct the disconnect tone ${stamp}`,
    problem: 'The tone follows the disconnect.',
    analysis: 'The tone is queued behind the annunciator.',
    solution: 'Queue the tone ahead of the annunciator.',
    requirementChanges: [],
    problemReportIds: [report.id],
  } })
  expect(draft.ok(), await draft.text()).toBeTruthy()

  // Nothing edited the report. Its panel says something different because the panel is derived.
  await open()
  const artifact = panel.locator('.impactRow').filter({ hasText: 'System requirements' }).locator('.impactArtifact')
  await expect(artifact).toHaveCount(1)
  await expect(artifact).toContainText('SRCR-')
  await expect(artifact).toContainText(`Correct the disconnect tone ${stamp}`)
  await expect(artifact.locator('.impactState')).toContainText('Draft')
})

test('evidence under a not-impacted answer is shown, with the disagreement named', async ({ page, request }) => {
  test.setTimeout(300_000)
  await apiLogin(request)
  const showcase = await showcaseSeed(request)
  const stamp = Date.now()
  const title = `Impact mismatch journey ${stamp}`

  // Assessed as not impacted, and then a correction is raised against it anyway. Suppressing the link to
  // keep the answer looking right is exactly what this product must not do.
  const raised = await request.post(`${apiBase}/api/problem-reports`, { data: {
    category: 'CodeFunctional',
    projectId: showcase.projectId,
    releaseId: showcase.activeReleaseId,
    title,
    problem: 'The disconnect tone follows the disconnect.',
    impactAssessmentJson: JSON.stringify({ SystemRequirements: 'No' }),
  } })
  expect(raised.ok(), await raised.text()).toBeTruthy()
  const report = await raised.json() as { id: string }

  const draft = await request.post(`${apiBase}/api/change-request-drafts`, { data: {
    projectId: showcase.projectId,
    targetReleaseId: showcase.activeReleaseId,
    title: `Raised despite the answer ${stamp}`,
    problem: 'The tone follows the disconnect.',
    analysis: 'The tone is queued behind the annunciator.',
    solution: 'Queue the tone ahead of the annunciator.',
    requirementChanges: [],
    problemReportIds: [report.id],
  } })
  expect(draft.ok(), await draft.text()).toBeTruthy()

  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
  await page.goto(new URL(`${root}/problem-reports`, page.url()).toString(), { waitUntil: 'load' })
  await page.getByLabel('Search').fill(title)
  await page.locator('.prList').getByText(title).click()
  await expect(page.getByRole('heading', { name: title })).toBeVisible({ timeout: 30_000 })

  const row = page.getByRole('region', { name: 'Impact and linked evidence' })
    .locator('.impactRow').filter({ hasText: 'System requirements' })

  await expect(row.locator('.impactPill')).toHaveText('Not impacted')
  await expect(row.locator('.impactMismatch')).toContainText('Answer and evidence disagree')
  // The link is still there. Both halves are shown; the reader decides which is wrong.
  await expect(row.locator('.impactArtifact')).toHaveCount(1)
  await expect(row.locator('.impactArtifact')).toContainText('SRCR-')
})
