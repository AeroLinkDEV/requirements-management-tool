import { expect, test } from '@playwright/test'
import { login, openNavigationGroup, selectProgram } from './auth'

/**
 * Browsing controlled procedures the way requirements are browsed.
 *
 * The requirements explorer answers what an artifact says, what it traces to, what happened to it, and what
 * anybody has said about it. Those are the same four questions asked of a procedure, so this page uses that
 * component's inspector rather than a second one that resembles it.
 */
test('a procedure opens onto the same four-tab inspector a requirement does', async ({ page }) => {
  test.setTimeout(120_000)
  await login(page, 'admin')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Test Procedure Explorer' }).click()

  await expect(page.getByRole('heading', { name: 'Test Procedure Explorer' })).toBeVisible({ timeout: 30_000 })

  const emptyInspector = page.getByRole('complementary', { name: 'Procedure detail' })
  await expect(emptyInspector.getByText('Select a procedure')).toBeVisible()
  await expect(page.getByRole('separator')).toHaveCount(2, { timeout: 30_000 })

  const rows = page.locator('.procedureRow')
  await expect(rows.first()).toBeVisible({ timeout: 30_000 })
  const number = (await rows.first().locator('b').textContent())!.trim()
  expect(number).toMatch(/^SYSTP-\d{6}/)

  await rows.first().click()
  const inspector = page.getByRole('complementary', { name: new RegExp(`${number.replace('.', '\\.')} detail`) })
  await expect(inspector).toBeVisible()

  // The same four, in the same order, from the same stylesheet.
  for (const tab of ['Overview', 'Trace & impact', 'History']) {
    await expect(inspector.getByRole('tab', { name: tab })).toBeVisible()
  }
  await expect(inspector.getByRole('tab', { name: /^Discussion/ })).toBeVisible()

  await expect(inspector.getByText('Objective', { exact: true })).toBeVisible()

  // Trace runs the other way from a requirement's: a procedure shows what it exists to verify.
  await inspector.getByRole('tab', { name: 'Trace & impact' }).click()
  await expect(inspector).toContainText('verifies')

  await inspector.getByRole('tab', { name: 'History' }).click()
  await expect(inspector.locator('.revisionList li').first()).toBeVisible({ timeout: 30_000 })

  // Discussion is the requirement pane's own form and article markup, so what is asserted below is what would
  // hold on a requirement: an attributable comment that can then be dispositioned.
  await inspector.getByRole('tab', { name: /^Discussion/ }).click()
  const comments = inspector.locator('.discussionPane article')
  const saidBefore = await comments.count()
  await inspector.locator('.discussionPane textarea').fill('Confirmed against the oceanic rig on the 6th.')
  await inspector.getByRole('button', { name: 'Add comment' }).click()
  await expect(comments).toHaveCount(saidBefore + 1, { timeout: 30_000 })
  await expect(comments.last()).toContainText('Confirmed against the oceanic rig on the 6th.')

  // It is a controlled record, not view state: it survives a reload.
  await page.reload()
  await expect(page.getByRole('heading', { name: 'Test Procedure Explorer' })).toBeVisible({ timeout: 30_000 })
  await page.locator('.procedureRow').filter({ hasText: number }).first().click()
  const reopened = page.getByRole('complementary', { name: new RegExp(`${number.replace('.', '\\.')} detail`) })
  await reopened.getByRole('tab', { name: /^Discussion/ }).click()
  const reloaded = reopened.locator('.discussionPane article').last()
  await expect(reloaded).toContainText('Confirmed against the oceanic rig on the 6th.', { timeout: 30_000 })

  // Resolving goes through the artifact-comment route the requirements pane uses, not a procedure-only twin.
  page.once('dialog', dialog => void dialog.accept('Rig log attached.'))
  await reloaded.getByRole('button', { name: 'Resolve / disposition' }).click()
  await expect(reopened.locator('.discussionPane article').last()).toContainText('Rig log attached.')
})

test('System Procedure rows retain their requirement coverage count', async ({ page }) => {
  test.setTimeout(120_000)
  await login(page, 'admin')
  await page.route('**/api/test-procedures?*', async route => {
    const requestUrl = new URL(route.request().url())
    if (requestUrl.searchParams.get('scope') !== 'System') return route.continue()
    await route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({
        page: 1, pageSize: 25, totalCount: 1, totalPages: 1, views: [],
        items: [{
          id: 'system-procedure', revisionId: 'system-procedure-revision', displayNumber: 'SYSTP-999999.00',
          title: 'System coverage count regression', state: 'Approved', requirementCount: 3, parentCount: 99,
          ownerId: 'test.engineer', level: 'System', artifactKind: 'Procedure',
        }],
      }),
    })
  })
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Test Procedure Explorer' }).click()

  const row = page.locator('.procedureRow').first()
  await expect(row).toBeVisible({ timeout: 30_000 })
  await expect(row.locator('span').nth(2)).toHaveText('3')
})

/**
 * Who wrote a procedure, and what made them change it.
 *
 * A procedure is read by somebody deciding whether to trust it, and its revisions were once reachable only
 * one at a time with no way to see what drove any of them. The change request behind a revision is reached
 * through the verification decision that resolved to it, which is the record that actually connects the two.
 *
 * Asked here rather than on the change request page, which used to carry a procedure library and no longer
 * does. The question did not move; the library did.
 */
test('a procedure says who wrote it and what drove each revision', async ({ page }) => {
  test.setTimeout(180_000)
  await login(page, 'admin')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Test Procedure Explorer' }).click()
  await expect(page.getByRole('heading', { name: 'Test Procedure Explorer' })).toBeVisible({ timeout: 30_000 })

  await page.getByLabel('Find a procedure').fill('SYSTP-000001')
  const row = page.locator('.procedureRow').filter({ hasText: 'SYSTP-000001' }).first()
  await expect(row).toBeVisible({ timeout: 30_000 })
  await row.click()

  const inspector = page.locator('.requirementInspector')
  await expect(inspector).toBeVisible({ timeout: 30_000 })
  await inspector.getByRole('tab', { name: 'History' }).click()

  // Every revision, newest first, each saying who wrote it — a name, not an account handle.
  const revisions = inspector.locator('.revisionList li')
  await expect(revisions.first()).toBeVisible({ timeout: 30_000 })
  await expect(inspector).not.toContainText(/exact Case parent/)
  await expect(revisions.first()).toContainText('written by')
  await expect(revisions.first()).toContainText(/SYSTP-000001\.\d{2}/)
  await expect(revisions.first().locator('.personName')).toBeVisible()
  // A revision driven by a controlled package names it rather than leaving the reader to guess.
  await expect(inspector.locator('.revisionDriver').first()).toBeVisible({ timeout: 30_000 })
})

/**
 * Software procedures share the same Explorer and can still be narrowed to either controlled level.
 */
test('the Software Explorer opens on all artifacts and can move to the configured LLR level', async ({ page }) => {
  test.setTimeout(180_000)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('button', { name: 'Software' }).last().click()

  await page.getByRole('link', { name: 'Test Case/Procedure Explorer' }).click()
  await expect(page).toHaveURL(/software-verification\/test-artifacts$/, { timeout: 30_000 })
  await expect(page.getByText('CONTROLLED TEST ARTIFACTS / READ-ONLY EXPLORER')).toBeVisible()
  await expect(page.getByLabel('Level filter')).toHaveValue('Software')
  await expect(page.getByLabel('Artifact filter')).toHaveValue('all')
  const documentRail = page.getByRole('navigation', { name: 'test artifact documents' })
  await expect(documentRail).toHaveCount(1)
  // The showcase is intentionally Case-only; the activated Case + Procedure profile is covered by #726.
  await expect(documentRail.locator('.railDocument')).toHaveCount(2)
  await expect(documentRail.getByRole('button', { name: /HLR.*Case|HLR.*Procedure|LLR.*Case|LLR.*Procedure/ }).first()).toBeVisible()

  await page.getByLabel('Level filter').selectOption('LowLevel')
  await expect(page).toHaveURL(/artifactLevel=LowLevel/, { timeout: 30_000 })
  await expect(page.getByLabel('Level filter')).toHaveValue('LowLevel')
  await expect(page.locator('.pager')).toContainText('of 280', { timeout: 30_000 })
  await expect(page.locator('.procedureList')).not.toContainText('HLRTC-')
  await expect(page.getByRole('tablist', { name: 'Test case views' })).toHaveCount(0)
})

test('the shared Explorer deep-link can inspect active software Procedures with exact Case parents', async ({ page }) => {
  test.setTimeout(180_000)
  let relationshipState = 'Suspect'
  let relationshipOutcome: string | undefined
  const lifecycleEvents = [{
    id: 'raised-event', type: 'Raised', actorId: 'baseline.materializer',
    occurredAt: '2026-08-23T00:00:00Z', rationale: 'The exact Case revision changed.',
  }]
  await login(page, 'admin', { openProject: false })
  await page.route('**/api/projects/*/configuration', async route => {
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify({ effectiveSteps: [
      { catalogueEntry: 'System', capabilities: 2, enabledArtifactKinds: ['Procedure'] },
      { catalogueEntry: 'HighLevel', capabilities: 7, enabledArtifactKinds: ['Case', 'Procedure'] },
      { catalogueEntry: 'LowLevel', capabilities: 7, enabledArtifactKinds: ['Case', 'Procedure'] },
    ] }) })
  })
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('button', { name: 'Software' }).last().click()
  await page.getByRole('link', { name: 'Test Case/Procedure Explorer' }).click()
  await expect(page).toHaveURL(/software-verification\/test-artifacts/, { timeout: 30_000 })

  await page.route('**/api/verification-artifacts?*', async route => {
    const requestUrl = new URL(route.request().url())
    if (requestUrl.searchParams.get('artifactKind') !== 'Procedure') return route.continue()
    await route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({
        page: 1, pageSize: 25, totalCount: 1, totalPages: 1, views: [],
        items: [{
          id: 'active-procedure', revisionId: 'active-revision', displayNumber: 'HLRTP-000001.00',
          title: 'Active procedural verification', state: 'Draft', requirementCount: 0, parentCount: 2,
          ownerId: 'test.engineer', level: 'HighLevel', artifactKind: 'Procedure',
          objective: 'Demonstrate the procedure', preconditions: 'Environment is available',
          steps: 'Follow the ordered procedure', expectedResult: 'Expected observation recorded',
          environmentSetup: 'Bench setup', testData: 'Known data', orderedSteps: '1. Execute',
          expectedObservations: 'Expected result', cleanup: 'Restore bench', toolingAutomation: 'Runner',
          parentKind: 'Allocated', lastOutcome: undefined,
        }],
      }),
    })
  })
  await page.route('**/api/test-procedures/active-procedure/history*', async route => {
    await route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({
        artifactId: 'active-procedure', artifactKind: 'Procedure', id: 'active-procedure',
        baseNumber: 'HLRTP-000001', title: 'Active procedural verification', level: 'HighLevel',
        ownerId: 'test.engineer', createdAt: '2026-08-23T00:00:00Z', selectedRevisionId: 'active-revision',
        revisions: [{
          id: 'active-revision', displayNumber: 'HLRTP-000001.00', revision: 0,
          title: 'Active procedural verification', state: 'Draft', authorId: 'test.engineer',
          createdAt: '2026-08-23T00:00:00Z', objective: 'Demonstrate the procedure', preconditions: '',
          steps: '', expectedResult: '', environmentSetup: 'Bench setup', testData: 'Known data',
          orderedSteps: '1. Execute', expectedObservations: 'Expected result', cleanup: 'Restore bench',
          toolingAutomation: 'Runner', parentKind: 'Allocated', caseRevisionIds: ['case-a', 'case-b'],
          caseParents: [{ linkId: 'case-procedure-link', caseRevisionId: 'case-b',
            state: relationshipState, outcome: relationshipOutcome }],
          drivenBy: [], covers: [],
        }],
      }),
    })
  })
  await page.route('**/api/case-procedure-links/case-procedure-link/lifecycle', async route => {
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify({
      linkId: 'case-procedure-link', linkKind: 'CaseProcedure', state: relationshipState,
      outcome: relationshipOutcome, events: lifecycleEvents,
    }) })
  })
  await page.route('**/api/case-procedure-links/case-procedure-link/lifecycle/acknowledge', async route => {
    const request = route.request().postDataJSON() as { rationale: string }
    expect(request.rationale).toBe('Assess the carried exact Case relationship.')
    relationshipState = 'Acknowledged'
    lifecycleEvents.push({ id: 'ack-event', type: 'Acknowledged', actorId: 'test.engineer',
      occurredAt: '2026-08-23T00:01:00Z', rationale: request.rationale })
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify({
      linkId: 'case-procedure-link', state: relationshipState,
    }) })
  })
  await page.route('**/api/case-procedure-links/case-procedure-link/lifecycle/resolve', async route => {
    const request = route.request().postDataJSON() as { rationale: string; outcome: string }
    expect(request.outcome).toBe('ExistingDownstreamRevisionRemainsValid')
    relationshipState = 'Closed'; relationshipOutcome = request.outcome
    lifecycleEvents.push({ id: 'resolve-event', type: 'ResolutionRecorded', actorId: 'test.engineer',
      occurredAt: '2026-08-23T00:02:00Z', rationale: request.rationale })
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify({
      linkId: 'case-procedure-link', state: relationshipState, outcome: relationshipOutcome,
    }) })
  })
  await page.route('**/api/test-procedures/active-procedure/comments', async route => {
    await route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify([{
        id: 'active-comment', revisionId: 'active-revision', body: 'Existing active discussion',
        state: 'Open', createdBy: 'test.engineer', createdAt: '2026-08-23T00:00:00Z'
      }, {
        id: 'legacy-comment', body: 'Historical discussion without revision context',
        state: 'Open', createdBy: 'test.engineer', createdAt: '2026-08-22T00:00:00Z'
      }]),
    })
  })
  const procedureUrl = new URL(page.url())
  procedureUrl.searchParams.set('artifactKind', 'Procedure')
  await page.goto(procedureUrl.toString())
  await expect(page.getByRole('heading', { name: 'Software Test Case/Procedure Explorer' })).toBeVisible({ timeout: 30_000 })
  await expect(page).toHaveURL(/software-verification\/test-artifacts.*artifactKind=Procedure/)
  await expect(page.getByRole('button', { name: 'Advanced' })).toHaveCount(0)
  await expect(page.getByRole('navigation', { name: 'test procedure documents' })).toHaveCount(1)
  await expect(page.locator('.procedureRow')).toContainText('Procedure')
  await expect(page.locator('.procedureRow')).toContainText('HLRTP-000001.00')
  await page.locator('.procedureRow').click()
  await expect(page.getByText('Environment / setup')).toBeVisible()
  await page.getByRole('tab', { name: 'History' }).click()
  await expect(page.getByText('Allocated · 2 exact Case parents')).toBeVisible()
  await expect(page.getByText(/Exact Case case-b/)).toBeVisible()
  await expect(page.getByLabel('Exact link lifecycle Suspect')).toBeVisible()
  await page.getByPlaceholder('Record why this exact relationship is under assessment.')
    .fill('Assess the carried exact Case relationship.')
  await page.getByRole('button', { name: 'Acknowledge relationship' }).click()
  await expect(page.getByLabel('Exact link lifecycle Acknowledged')).toBeVisible()
  await page.getByPlaceholder('Record the controlled disposition and supporting rationale.')
    .fill('The existing controlled Procedure revision remains valid.')
  await page.getByRole('button', { name: 'Record resolution' }).click()
  await expect(page.getByLabel('Exact link lifecycle Closed')).toContainText(/Existing Downstream Revision Remains Valid/i)
  await expect(page.getByLabel('Exact link lifecycle Closed')).toContainText('baseline.materializer')
  await page.getByRole('tab', { name: /^Discussion/ }).click()
  await expect(page.locator('.discussionPane textarea')).toHaveCount(1)
  await expect(page.getByRole('button', { name: 'Add comment' })).toHaveCount(1)
  const historicalComment = page.locator('.discussionPane article').filter({ hasText: 'Historical discussion without revision context' })
  await expect(historicalComment).toContainText('Historical comment has no exact revision context; it is read-only.')
  await expect(historicalComment.getByRole('button', { name: 'Resolve / disposition' })).toHaveCount(0)
})

/**
 * Nothing here writes a procedure either.
 *
 * The library moved to this page, and the rule moved with it: a procedure is introduced, modified or retired
 * by a test change request and by nothing else. Browsing procedures is reading, so the page that browses them
 * offers no way to write one — the same guarantee the change request page makes, asserted where the list now
 * actually is.
 */
test('the Explorer browses procedures without offering a way to write one', async ({ page }) => {
  test.setTimeout(180_000)
  await login(page, 'admin')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Test Procedure Explorer' }).click()
  await expect(page.getByRole('heading', { name: 'Test Procedure Explorer' })).toBeVisible({ timeout: 30_000 })

  await expect(page.getByRole('button', { name: /New test procedure/ })).toHaveCount(0)
  await expect(page.getByRole('dialog', { name: 'Create a test procedure' })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Check out & edit' })).toHaveCount(0)

  // It still reads, which is the point: procedures are browsable without being writable.
  await page.getByLabel('Find a procedure').fill('SYSTP-000001')
  await expect(page.locator('.procedureRow').filter({ hasText: 'SYSTP-000001' }).first())
    .toBeVisible({ timeout: 30_000 })
})

test('released Build 1.5 procedures remain readable without create or edit actions', async ({ page }) => {
  test.setTimeout(180_000)
  await login(page)
  await page.getByRole('button', { name: /Back to Software Builds/ }).click()
  await page.getByRole('button', { name: 'Open build 1.5' }).click()
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Test Procedure Explorer' }).click()
  await expect(page.getByRole('heading', { name: 'Test Procedure Explorer' })).toBeVisible({ timeout: 30_000 })

  await expect(page.getByRole('button', { name: /New test procedure/ })).toHaveCount(0)
  // Build 1.6 carries a later draft of this stable procedure identity. The released Build 1.5 Explorer must
  // keep its exact manifest revision primary, including after the selection becomes a reloadable deep link.
  await page.getByLabel('Find a procedure').fill('SYSTP-000040')
  const row = page.locator('.procedureRow').filter({ hasText: 'SYSTP-000040.00' }).first()
  await expect(row).toBeVisible({ timeout: 30_000 })
  await expect(page.locator('.procedureList')).not.toContainText('SYSTP-000040.01')
  await row.click()
  const inspector = page.locator('.requirementInspector')
  await expect(inspector).toBeVisible({ timeout: 30_000 })
  await expect(inspector).toContainText('SYSTP-000040.00')
  await expect(page).toHaveURL(/procedure=SYSTP-000040\.00.*procedureId=.*procedureRevisionId=/)
  const exactUrl = page.url()
  await page.reload()
  await expect(page).toHaveURL(exactUrl)
  await expect(page.locator('.requirementInspector')).toContainText('SYSTP-000040.00', { timeout: 30_000 })
  await expect(inspector).toContainText('Objective')
  await expect(page.getByRole('button', { name: 'Check out & edit' })).toHaveCount(0)
  // A released build is read-only, so its procedures cannot be discussed either.
  await inspector.getByRole('tab', { name: /^Discussion/ }).click()
  await expect(inspector.locator('.discussionPane textarea')).toHaveCount(0)
})
