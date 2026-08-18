import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, login, showcaseSeed } from './auth'

/**
 * A document reviewer saying what is wrong with the revision they are reading.
 *
 * The change-request journey proves the same rules over a change request. This one proves they carried
 * across to a different aggregate rather than being assumed to: a draft is private until its author
 * decides, and uncontrolled commentary is drawn with a dashed rule against the record's solid ones.
 */
test('a document reviewer comments on a revision, and the owner cannot see it while it is a draft', async ({ page, request }) => {
  test.setTimeout(240_000)
  const showcase = await showcaseSeed(request)
  await apiLogin(request)

  // The showcase seeds a Software Verification Plan in review, with software.lead holding the active
  // technical review step.
  const documents = await (await request.get(`${apiBase}/api/managed-documents?projectId=${showcase.projectId}`)).json()
  const svp = documents.items.find((item: { acronym: string; inWorkState: string }) =>
    item.acronym === 'SVP' && item.inWorkState === 'InReview')
  expect(svp, 'the showcase should seed a document in review').toBeTruthy()

  await login(page, 'software.lead', { openProject: false })
  await page.goto(`/programs/${showcase.programId}/projects/${showcase.projectId}/documentation-center/${svp.id}`, { waitUntil: 'load' })
  await expect(page.getByRole('navigation', { name: 'Document record sections' })).toBeVisible({ timeout: 30_000 })
  await page.getByRole('button', { name: 'Review & release' }).click()

  const comments = page.locator('.reviewComments')
  await expect(comments).toBeVisible({ timeout: 30_000 })
  await comments.getByRole('button', { name: /Add a comment on this revision/ }).click()

  // The section is the reviewer's own words. A checked-in DOCX has no structure this system can address,
  // so the field asks where they were reading rather than offering a record to point at.
  await comments.locator('.reviewCommentSection').fill('3.2 Verification independence')
  await comments.locator('.reviewCommentDraft textarea')
    .fill('3.2.4 still cites the retired full-reload statement, so both obligations appear to apply at once.')
  await comments.getByRole('button', { name: 'Save comment' }).click()

  const comment = comments.locator('.reviewComment').first()
  await expect(comment).toBeVisible({ timeout: 30_000 })
  await expect(comment).toContainText('retired full-reload statement')
  await expect(comment).toContainText('3.2 Verification independence')
  await expect(comment).toContainText('Only you can see this until you decide')

  // Uncontrolled content is dashed against the record's solid rules. A convention nothing checks is one
  // that quietly stops being true.
  await expect(comment).toHaveCSS('border-left-style', 'dashed')

  // The owner cannot see a draft. Deciding is what publishes it, and this reviewer has not decided.
  const ownerContext = await page.context().browser()!.newContext()
  const ownerTab = await ownerContext.newPage()
  await login(ownerTab, 'test.author', { openProject: false })
  await ownerTab.goto(`/programs/${showcase.programId}/projects/${showcase.projectId}/documentation-center/${svp.id}`, { waitUntil: 'load' })
  await expect(ownerTab.getByRole('navigation', { name: 'Document record sections' })).toBeVisible({ timeout: 30_000 })
  await ownerTab.getByRole('button', { name: 'Review & release' }).click()
  await expect(ownerTab.getByRole('heading', { name: 'Review route' })).toBeVisible({ timeout: 30_000 })
  await expect(ownerTab.locator('.reviewComment')).toHaveCount(0)

  await ownerContext.close()
})
