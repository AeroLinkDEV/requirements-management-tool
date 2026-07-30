import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, login, openNavigationGroup, selectProgram, showcaseSeed } from './auth'

/**
 * Surfaces that describe people, history and builds have to do it in the words a colleague would use.
 *
 * Each check here replaced something the product had derived from its own internals and printed at the
 * reader: an account handle where a name belonged, a job title standing in for a person, a stored enum name
 * title-cased by a regular expression that had never heard of the product's own abbreviation, and an
 * internal step reported in place of the outcome somebody came to read.
 */

const draftBody = (projectId: string, releaseId: string, title: string) => ({
  projectId,
  targetReleaseId: releaseId,
  type: 'System',
  title,
  problem: 'A controlled change is needed.',
  analysis: 'The downstream effect has been assessed.',
  solution: 'Introduce the behaviour under change control.',
  requirementChanges: [{
    level: 'System',
    kind: 'Introduce',
    statement: 'The FMS shall record who is being waited on.',
    rationale: 'Review legibility.',
    verificationMethod: 'Test',
  }],
})

test('a review cycle names the person waiting, their role, and whose turn it is', async ({ page, request }) => {
  test.setTimeout(180_000)
  const showcase = await showcaseSeed(request)
  await apiLogin(request)

  // Built here rather than found, so the queue wording is exercised against a review that is genuinely
  // mid-flight with somebody second in line.
  const created = await request.post(`${apiBase}/api/scr-drafts`,
    { data: draftBody(showcase.projectId, showcase.activeReleaseId, `Review legibility ${Date.now()}`) })
  expect(created.ok(), await created.text()).toBeTruthy()
  const draft = await created.json()
  const submitted = await request.post(`${apiBase}/api/scrs/${draft.id}/submit`, { data: {
    expectedVersion: draft.version,
    mode: 'Sequential',
    approvers: [{ userId: 'lead.reviewer', name: 'Maya Patel' }, { userId: 'manager.reviewer', name: 'Olivia Chen' }],
  } })
  expect(submitted.ok(), await submitted.text()).toBeTruthy()

  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
  await page.goto(new URL(`${root}/systems/change-requests/${draft.id}`, page.url()).toString(), { waitUntil: 'load' })

  const review = page.getByRole('heading', { name: /Review cycle/ }).locator('..').locator('..').locator('..')
  await expect(review).toBeVisible({ timeout: 30_000 })

  // The people, by name. The showcase used to submit its reviews naming the approvers "Engineering Lead" and
  // "Engineering Manager" — jobs, not people, in the one panel whose whole purpose is to say who.
  await expect(review).toContainText('Maya Patel')
  await expect(review).toContainText('Olivia Chen')
  // "Authority unresolved" is an empty column admitting it is empty, printed where a role belongs.
  await expect(review).not.toContainText('Authority unresolved')
  // Whose move it is. "Pending" was given to second-in-line and sixth-in-line alike, so it told nobody.
  await expect(review).toContainText('Awaiting approval')
  await expect(review).toContainText('Next in line for approval')
})

test('audit history reads as events, in the product\'s own abbreviation', async ({ page, request }) => {
  test.setTimeout(180_000)
  const showcase = await showcaseSeed(request)
  await apiLogin(request)
  const created = await request.post(`${apiBase}/api/scr-drafts`,
    { data: draftBody(showcase.projectId, showcase.activeReleaseId, `Audit legibility ${Date.now()}`) })
  expect(created.ok(), await created.text()).toBeTruthy()
  const draft = await created.json()

  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
  await page.goto(new URL(`${root}/systems/change-requests/${draft.id}`, page.url()).toString(), { waitUntil: 'load' })

  const history = page.getByRole('heading', { name: 'Audit history' }).locator('../../..')
  await expect(history).toBeVisible({ timeout: 30_000 })
  // Splitting the stored event name on its capitals title-cased the product's own abbreviation.
  await expect(history.locator('.auditRow b').filter({ hasText: /^SCR created$/ })).toBeVisible({ timeout: 30_000 })
  // A subtitle describing the storage model rather than the contents.
  await expect(history).not.toContainText('Append-only material events')
})

test('an allocated change says which build it went into', async ({ page }) => {
  test.setTimeout(180_000)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'ENGINEERING')
  await page.getByRole('link', { name: 'Change Requests' }).click()
  await page.getByLabel('Lifecycle state filter').selectOption({ label: 'Allocated to a build' })

  const allocated = page.locator('.historyRow').first()
  await expect(allocated).toBeVisible({ timeout: 30_000 })
  await allocated.click()

  const history = page.getByRole('heading', { name: 'Audit history' }).locator('../../..')
  await expect(history).toBeVisible({ timeout: 30_000 })
  // Selection into a candidate baseline is an internal step. Which build the change is going into is the
  // thing a reader opened the history to find out.
  await expect(history).toContainText(/Allocated to Build \d/)
  await expect(history.locator('.auditRow b').filter({ hasText: 'Selected For Baseline' })).toHaveCount(0)
})

test('the approver search answers on the first letter and never shows an account handle', async ({ page, request }) => {
  test.setTimeout(180_000)
  const showcase = await showcaseSeed(request)
  await apiLogin(request)
  const created = await request.post(`${apiBase}/api/scr-drafts`,
    { data: draftBody(showcase.projectId, showcase.activeReleaseId, `Approver search ${Date.now()}`) })
  expect(created.ok(), await created.text()).toBeTruthy()
  const draft = await created.json()

  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
  await page.goto(new URL(`${root}/systems/change-requests/${draft.id}`, page.url()).toString(), { waitUntil: 'load' })

  await page.getByRole('button', { name: 'Configure & Submit Review' }).click()
  await page.getByRole('button', { name: '+ Add approver' }).click()
  const search = page.getByLabel('Approver 1 search')
  await expect(search).toBeVisible({ timeout: 30_000 })
  // One letter. Two meant typing "A" for Alex and being shown nothing, which reads as "no such person".
  await search.fill('a')
  const suggestions = page.locator('.personSuggestions button')
  await expect(suggestions.first()).toBeVisible({ timeout: 30_000 })
  // `software.engineer.044` is how the database refers to somebody, not how anybody else does.
  await expect(suggestions.first()).not.toContainText(/\.[a-z]+\.\d{3}|@/)
})
