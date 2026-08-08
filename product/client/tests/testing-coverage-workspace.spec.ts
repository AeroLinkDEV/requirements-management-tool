import { expect, test } from '@playwright/test'
import type { Page } from '@playwright/test'
import { apiBase, apiLogin, firstSectionId, login, openNavigationGroup, selectProgram } from './auth'

/**
 * Opens an assessment this reader can actually decide, and claims it.
 *
 * The row deliberately no longer says who holds a package — the requirements queue does not either — so
 * whether one is free is only knowable by opening it. A package somebody else holds offers no decisions, so
 * picking the first row and hoping tests nothing.
 */
async function openClaimableAssessment(page: Page) {
  const drawer = page.getByRole('dialog', { name: /test impact/ })
  for (const row of await page.locator('.downstreamAssessment').all()) {
    await row.getByRole('button', { name: 'Open assessment' }).click()
    await expect(drawer).toBeVisible({ timeout: 30_000 })
    // Decidable, not merely open. There is no claim step any more — answering an unheld package is what takes
    // it on — so what makes a package usable to this reader is an enabled Decide, and a package somebody else
    // already holds offers none. Testing for presence rather than for enabled is what made this hang for a
    // full timeout on a held package instead of moving to the next row.
    const decide = drawer.getByRole('button', { name: 'Decide' })
    if (await decide.count() > 0 && await decide.first().isEnabled()) return drawer
    await drawer.getByRole('button', { name: 'Close test assessment' }).click()
  }
  throw new Error('No test assessment on this page is free to decide.')
}

/**
 * The change requests controlling this build's test procedures.
 *
 * The page is named for what it holds, as the requirements-side Change Requests page is: a SYSTCR and an SRCR
 * present the same way, and the material difference is only which kind of change each controls. Coverage and
 * the procedure library used to sit underneath the queue here; both are reports about procedures as they
 * stand, so both moved to the Test Procedure Explorer beside the procedures they describe.
 */
test('the page lists the change requests controlling test work, and nothing else', async ({ page }) => {
  test.setTimeout(180_000)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Test Change Requests' }).click()

  await expect(page.getByRole('heading', { name: 'Change Requests' })).toBeVisible({ timeout: 30_000 })

  // The queue: the packages this build's approved changes created.
  // Named for the question the page asks, matching the requirements-side queue word for word.
  await expect(page.getByRole('heading', { name: 'Downstream test assessments' })).toBeVisible()
  // Numbered as controlled records rather than borrowing the number of the change that raised them. The
  // showcase raises one System package for the in-work build, so this is a fact about the page.
  const packages = page.locator('.downstreamAssessment').filter({ hasText: /TCR-/ })
  await expect(packages.first()).toContainText(/SYSTCR-\d{6}\.\d{2}/, { timeout: 30_000 })

  // What is no longer here, because it moved rather than being duplicated. A second procedure list, or a
  // second coverage report, would be a second answer to the same question and the two would drift.
  await expect(page.locator('.procedureLibrary')).toHaveCount(0)
  await expect(page.getByRole('heading', { name: 'Test procedures' })).toHaveCount(0)
  await expect(page.getByRole('heading', { name: 'Requirement coverage' })).toHaveCount(0)
  await expect(page.getByRole('region', { name: 'Coverage summary' })).toHaveCount(0)
  await expect(page.getByLabel('Find a procedure')).toHaveCount(0)
})

/**
 * The decisions inside a package, worked on this page.
 *
 * A test change request is a package of decisions and its state is the sum of them, so a queue that could
 * only be looked at was a queue nobody could act on — and the tabbed workspace could not be retired while
 * the only place to record a decision was inside it.
 */
test('a package opens onto its decisions, and each one is an explicit judgement', async ({ page }) => {
  test.setTimeout(180_000)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Test Change Requests' }).click()
  await expect(page.getByRole('heading', { name: 'Change Requests' })).toBeVisible({ timeout: 30_000 })

  // One control per row, in every state — the requirements queue's anatomy. Everything the assessment offers
  // is inside it, so the row stays a summary and stops being a control panel.
  const claimable = page.locator('.downstreamAssessment').filter({ hasText: /SYSTCR-/ }).first()
  await expect(claimable).toBeVisible({ timeout: 30_000 })
  await expect(claimable.getByRole('button', { name: 'Take it on' })).toHaveCount(0)
  await expect(claimable.getByRole('button')).toHaveCount(2)  // Open assessment, and the link to the package
  const first = await openClaimableAssessment(page)
  await expect(first.getByRole('button', { name: 'Decisions' })).toHaveCount(0)
  await expect(first.getByText('Source change requests', { exact: true })).toBeVisible()
  await expect(first.getByText('Responsibility', { exact: true })).toBeVisible()
  await expect(first.getByText('Linked Problem Reports', { exact: true })).toBeVisible()
  // And what already tests each requirement, which is the question the decision is an answer to.
  await expect(first.locator('.decisionList .existingCoverage').first()).toBeVisible({ timeout: 30_000 })

  // Closing returns to the queue and leaves nothing behind, so the drawer is a drawer and not a one-way door.
  await first.getByRole('button', { name: 'Close test assessment' }).click()
  await expect(page.locator('.decisionList')).toHaveCount(0)
  const reopened = await openClaimableAssessment(page)

  const undecided = reopened.locator('.decisionList li').filter({ has: page.getByRole('button', { name: 'Decide' }) })
  await expect(undecided.first()).toBeVisible({ timeout: 30_000 })
  await undecided.first().getByRole('button', { name: 'Decide' }).click()

  const decide = page.getByRole('dialog', { name: /Decide / })
  await expect(decide).toBeVisible({ timeout: 30_000 })
  // Every value is a judgement somebody made. There is deliberately none meaning "nobody looked", because a
  // requirement must never reach an approved baseline without a decision against it.
  await decide.getByLabel('Decision').selectOption('NoTestRequired')
  await decide.getByLabel('Rationale').fill('Verified by inspection against the approved design note.')
  await decide.getByRole('button', { name: 'Record decision' }).click()

  await expect(decide).toHaveCount(0, { timeout: 30_000 })
  await expect(page.getByRole('status')).toContainText('Decision recorded')
})

test('software HLR and LLR each have their own change request page', async ({ page }) => {
  test.setTimeout(180_000)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('button', { name: 'Software' }).last().click()

  await page.getByRole('link', { name: 'Software HLR Test Change Requests' }).click()
  await expect(page).toHaveURL(/software-verification\/hlr\/coverage$/, { timeout: 30_000 })
  await expect(page.getByText('VERIFICATION / SOFTWARE HLR')).toBeVisible()
  // The showcase raises one HLR package for this build. This page reported none for a while, and nothing had
  // failed: two loaders on the page shared one "only the newest reply may write the screen" counter, so the
  // procedure search cancelled the load that was fetching the packages. Asserting the package here is what
  // stops that returning as an empty queue nobody can distinguish from having no work.
  await expect(page.locator('.downstreamAssessment').filter({ hasText: /TCR-/ }).first())
    .toContainText(/HLRTCR-\d{6}\.\d{2}/, { timeout: 30_000 })
  // The other discipline's packages are not on this page.
  await expect(page.locator('.downstreamAssessment').filter({ hasText: /LLRTCR-/ })).toHaveCount(0)

  await page.getByRole('link', { name: 'Software LLR Test Change Requests' }).click()
  await expect(page).toHaveURL(/software-verification\/llr\/coverage$/, { timeout: 30_000 })
  await expect(page.getByText('VERIFICATION / SOFTWARE LLR')).toBeVisible()
  // The showcase raises no LLR package for this build, so what this asserts is isolation rather than presence:
  // whatever the LLR page shows, the HLR package is not on it. Asserting an LLRTCR here would be asserting
  // something the demonstration data does not contain.
  await expect(page.locator('.downstreamAssessment').filter({ hasText: /HLRTCR-/ })).toHaveCount(0)
})

/**
 * The answer a new requirement usually needs: a test is required and nobody has written the procedure yet.
 *
 * Until this existed the outcomes were "an approved procedure covers this" and "no test required", so an
 * engineer whose honest answer was "one has to be written" had to leave the item unanswered, go to the
 * library, author a procedure and come back. Nothing could tell that apart from an item nobody had looked at.
 *
 * The decision is recorded and the procedure is authored from it, so the chain from change request to
 * procedure stays intact rather than depending on the engineer remembering why they were writing it.
 */
test('a decision can ask for a procedure that does not exist, and author it from there', async ({ page, request }) => {
  test.setTimeout(180_000)
  // This journey depends on a build whose requirements have not been materialized: a decision that a new
  // procedure is required is recorded, and authoring waits for an exact governed revision. The shared
  // showcase seed is not a safe home for that state — the suspect-coverage journey materializes the seeded
  // in-work baseline, so this test builds its own brand-new Program whose release has no baseline at all.
  await apiLogin(request)
  const suffix = Date.now().toString().slice(-7)
  const workspaceResponse = await request.post(`${apiBase}/api/workspaces`, { data: {
    programName: `Decision Authoring ${suffix}`,
    programCode: `DA${suffix}`,
    projectName: 'Decision Authoring Project',
    softwareProduct: 'Decision Authoring Product',
    initialRelease: '1.0',
    initialReleaseIsReleased: false,
  } })
  expect(workspaceResponse.ok(), await workspaceResponse.text()).toBeTruthy()
  const workspace = await workspaceResponse.json()
  const impacts = JSON.stringify({
    trace: 'Not Affected', verification: 'Not Affected', documents: 'Not Affected',
    baseline: 'Not Affected', collaboration: 'Not Affected',
  })
  const draftResponse = await request.post(`${apiBase}/api/change-request-drafts`, { data: {
    projectId: workspace.project.id,
    targetReleaseId: workspace.release.id,
    type: 'System',
    title: `Decision authoring ${suffix}`,
    problem: 'A procedure must be written for the new behavior.',
    analysis: 'No procedure exists for this behavior yet.',
    solution: 'Author one from the decision that asks for it.',
    requirementChanges: [{
      level: 'System', kind: 'Introduce',
      targetSectionId: await firstSectionId(request, workspace.project.id),
      statement: `The ${suffix} product shall expose a new verification target.`,
      rationale: 'Capability qualification.',
      verificationMethod: 'Test',
      impactDispositionJson: impacts,
    }],
  } })
  expect(draftResponse.ok(), await draftResponse.text()).toBeTruthy()
  const draft = await draftResponse.json()
  const submitted = await request.post(`${apiBase}/api/change-requests/${draft.id}/submit`, {
    data: { approvers: [{ userId: 'admin', name: 'AeroLink Administrator' }] },
  })
  expect(submitted.ok(), await submitted.text()).toBeTruthy()
  const approved = await request.post(`${apiBase}/api/change-requests/${draft.id}/approve`, {
    data: { password: 'AeroLink!2026', meaning: 'Approved for decision-authoring journey verification.' },
  })
  expect(approved.ok(), await approved.text()).toBeTruthy()

  await login(page, 'admin', { openProject: false })
  await selectProgram(page, `Decision Authoring ${suffix}`)
  await openNavigationGroup(page, 'VERIFICATION')
  await page.getByRole('link', { name: 'System Test Change Requests' }).click()
  await expect(page.getByRole('heading', { name: 'Change Requests' })).toBeVisible({ timeout: 30_000 })

  // A row leads with the change it is assessing, not with a test change request number it may not have yet:
  // the number is what an assessment produces once it concludes test work is required.
  const firstRow = page.locator('.downstreamAssessment').first()
  await expect(firstRow).toBeVisible({ timeout: 30_000 })
  expect(((await firstRow.locator('b').first().textContent()) ?? '').trim()).toMatch(/^SRCR-/)

  // Any package with an undecided item will do; the point is the outcome, not which requirement it is on.
  const packageRow = await openClaimableAssessment(page)
  await expect(packageRow.locator('.decisionList')).toBeVisible({ timeout: 30_000 })
  const undecided = packageRow.locator('.decisionList li').filter({ has: page.getByRole('button', { name: 'Decide' }) }).first()
  await expect(undecided).toBeVisible({ timeout: 30_000 })
  await undecided.getByRole('button', { name: 'Decide' }).click()

  const decide = page.getByRole('dialog', { name: /Decide / })
  await decide.getByLabel('Decision').selectOption('NewProcedureRequired')
  // Naming a procedure here would claim coverage the decision explicitly says does not exist.
  await expect(decide.getByLabel('Covering procedure')).toHaveCount(0)
  await decide.getByLabel('Rationale').fill('No procedure exists for this behavior yet; one must be written.')
  await decide.getByRole('button', { name: 'Record decision' }).click()
  await expect(decide).toHaveCount(0, { timeout: 30_000 })

  // Recorded rather than left blank, and the work it names is offered where the decision was made.
  const decided = packageRow.locator('.decisionList li').filter({ hasText: 'No procedure exists for this behavior yet' }).first()
  await expect(decided).toBeVisible({ timeout: 30_000 })
  // A procedure binds to an exact approved revision, and this build has not materialized its requirements —
  // so the decision stands and the page says why the authoring cannot start yet, rather than the action
  // being silently absent. Where an exact revision exists, this is the 'Author the procedure' button.
  await expect(decided).toContainText('once this build materializes its requirements')
})

/**
 * A test change request opens as a workbench, not as a numbered row with a panel behind a button.
 *
 * The record already had source change requests, requirement changes and one decision per requirement, and
 * none of it was visible: everything sat behind a control labelled "Decisions" styled as a peer of the real
 * actions, which is why the product owner read a TCR as "just a numbered artifact". Worse, the coverage each
 * requirement already had — the question the decision is an answer to — was not shown at all, so an engineer
 * deciding whether a procedure must be written had to leave the page to find out.
 */
test('a test change request opens onto its source changes, its requirements and the coverage they already have', async ({ page }) => {
  test.setTimeout(180_000)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Test Change Requests' }).click()
  await expect(page.getByRole('heading', { name: 'Change Requests' })).toBeVisible({ timeout: 30_000 })

  const queueRow = page.locator('.downstreamAssessment').filter({ hasText: /SYSTCR-/ }).first()
  await expect(queueRow).toBeVisible({ timeout: 30_000 })

  // The row summarises; the assessment is where anything is done. There is no button called "Decisions", and
  // nothing that looks like an action opens a panel in place.
  await expect(page.getByRole('button', { name: 'Decisions' })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Hide decisions' })).toHaveCount(0)
  await expect(page.locator('.packageDisclosure')).toHaveCount(0)
  await queueRow.getByRole('button', { name: 'Open assessment' }).click()

  // What an engineer needs in order to decide, on open: where the work came from, who holds it, and every
  // requirement it created with the coverage that requirement already has.
  const row = page.getByRole('dialog', { name: /test impact/ })
  await expect(row).toBeVisible({ timeout: 30_000 })
  await expect(row.getByText('Source change requests', { exact: true })).toBeVisible()
  await expect(row.getByText('Responsibility', { exact: true })).toBeVisible()
  await expect(row).toContainText(/SRCR-\d{5}/)

  const decisions = row.locator('.decisionList li')
  await expect(decisions.first()).toBeVisible({ timeout: 30_000 })
  const count = await decisions.count()
  expect(count).toBeGreaterThan(0)
  // Every requirement-driven decision states its coverage. None is left for the reader to go and look up.
  for (let index = 0; index < count; index++) {
    const decision = decisions.nth(index)
    await expect(decision.locator('.existingCoverage')).toHaveCount(1)
    // One of the three honest answers — covered, suspect, none, or not yet materializable — never silence.
    await expect(decision.locator('.existingCoverage')).toContainText(
      /Covered by|written against earlier wording|No approved procedure covers this requirement yet|once this build materializes its requirements/)
  }
})
