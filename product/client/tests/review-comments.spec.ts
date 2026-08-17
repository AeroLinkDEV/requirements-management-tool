import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, firstSectionId, login, selectProgram, showcaseSeed } from './auth'

/**
 * A reviewer saying which requirement is wrong, on the page where they decide.
 *
 * The other review journeys prove the record page still works. This one drives the comment controls
 * themselves: that a draft is private until its author decides, that deciding hands it to the package's
 * author, and that the two are visually distinguishable from controlled content.
 */
test('a reviewer comments on a requirement, and the author sees it only once the reviewer decides', async ({ page, request }) => {
  test.setTimeout(180_000)
  const showcase = await showcaseSeed(request)
  await apiLogin(request)

  const created = await request.post(`${apiBase}/api/change-request-drafts`, { data: {
    projectId: showcase.projectId,
    targetReleaseId: showcase.activeReleaseId,
    type: 'System',
    title: `Commented review ${Date.now()}`,
    problem: 'A controlled change is needed.',
    analysis: 'The downstream effect has been assessed.',
    solution: 'Introduce the behaviour under change control.',
    requirementChanges: [{
      level: 'System',
      kind: 'Introduce',
      targetSectionId: await firstSectionId(request, showcase.projectId),
      statement: 'The FMS shall resynchronise the active flight plan incrementally.',
      rationale: 'Latency.',
      verificationMethod: 'Test',
    }],
  } })
  expect(created.ok(), await created.text()).toBeTruthy()
  const draft = await created.json()

  const submitted = await request.post(`${apiBase}/api/change-requests/${draft.id}/submit`, { data: {
    expectedVersion: draft.version,
    mode: 'Sequential',
    approvers: [{ userId: 'lead.reviewer', name: 'Maya Patel' }],
  } })
  expect(submitted.ok(), await submitted.text()).toBeTruthy()

  await login(page, 'lead.reviewer', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
  const record = new URL(`${root}/systems/change-requests/${draft.id}`, page.url()).toString()
  await page.goto(record, { waitUntil: 'load' })
  await expect(page.locator('[data-state="InReview"]').first()).toBeVisible({ timeout: 30_000 })

  // The affordance sits under the requirement it concerns, not in a margin — that is what makes the
  // anchoring worth having when somebody reads this back weeks later.
  const requirement = page.locator('.requirementView').first()
  await requirement.getByRole('button', { name: /Add a comment on/ }).click()
  await requirement.locator('.reviewCommentDraft textarea')
    .fill('1.5s is asserted, not derived. The procedure measures to 100ms.')
  await requirement.getByRole('button', { name: 'Save comment' }).click()

  const comment = requirement.locator('.reviewComment').first()
  await expect(comment).toBeVisible({ timeout: 30_000 })
  await expect(comment).toContainText('asserted, not derived')
  // Said in the interface, not just enforced in the server: the reviewer should know before they type.
  await expect(comment).toContainText('Only you can see this until you decide')

  // Uncontrolled content is drawn with a dashed rule against the solid rules the record uses. If that ever
  // stops being true the whole convention stops meaning anything, so it is asserted rather than assumed.
  await expect(comment).toHaveCSS('border-left-style', 'dashed')

  // The author cannot see it yet. A draft is nobody's but its author's.
  const authorPage = await page.context().browser()!.newContext()
  const authorTab = await authorPage.newPage()
  await login(authorTab, 'admin', { openProject: false })
  await selectProgram(authorTab, 'Flight Management System Live Program')
  await authorTab.goto(record, { waitUntil: 'load' })
  await expect(authorTab.locator('.requirementView').first()).toBeVisible({ timeout: 30_000 })
  await expect(authorTab.locator('.reviewComment')).toHaveCount(0)

  // Deciding is what publishes it. Requesting changes closes the cycle and hands the remark over.
  await page.getByPlaceholder('Reason for requested changes').fill('Settle the tolerance before this can be approved.')
  await page.getByRole('button', { name: 'Request changes' }).click()
  await expect(page.locator('[data-state="Draft"]').first()).toBeVisible({ timeout: 30_000 })

  await authorTab.reload({ waitUntil: 'load' })
  const published = authorTab.locator('.reviewComment').first()
  await expect(published).toBeVisible({ timeout: 30_000 })
  await expect(published).toContainText('asserted, not derived')
  // The controlled reason stays where it always was, separate from the commentary.
  await expect(authorTab.getByRole('heading', { name: 'Audit history' }).locator('../../..'))
    .toContainText('Settle the tolerance')

  await authorPage.close()
})
