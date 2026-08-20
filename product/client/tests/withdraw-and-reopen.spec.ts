import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, firstSectionId, login, openNavigationGroup, selectProgram, showcaseSeed } from './auth'

/**
 * Taking work back, and unsealing a build so that becomes possible.
 *
 * Approving a change request does not move a requirement: the revision appears when a baseline is frozen and
 * materialized. So withdrawal from an open build has nothing to unwind, and withdrawal from a frozen one is
 * refused until somebody reopens it — a deliberate act with a name and a reason on it rather than a side
 * effect of an author changing their mind.
 *
 * The journey is therefore about the two halves the interface has to get right: the refusal has to name the
 * way out, and the reopen has to say what it will disturb before it disturbs any of it. It proves the whole
 * path through the interface rather than the endpoints — the consequence preview is read on screen, confirmed
 * on screen, and the change request it stranded is found flagged on screen afterwards.
 */
test('a frozen build refuses a withdrawal, says what reopening costs, and the work it strands is flagged', async ({ page, request }) => {
  test.setTimeout(300_000)
  const showcase = await showcaseSeed(request)
  await apiLogin(request)

  const suffix = Date.now().toString().slice(-6)
  // A new requirement cannot be sent for review without a place in the document. Which section is not what
  // this journey is about, so it takes the first one.
  const section = await firstSectionId(request, showcase.projectId, 'System')

  // The work that goes into the build: one change request introducing a requirement of its own, so nothing
  // this journey does depends on what the showcase already contains. The identifier is allocated rather than
  // chosen, which is also how an author gets one.
  const createdResponse = await request.post(`${apiBase}/api/change-request-drafts`, {
    data: {
      projectId: showcase.projectId,
      targetReleaseId: showcase.activeReleaseId,
      type: 'System',
      title: `WITHDRAW-REOPEN oceanic annunciation ${suffix}`,
      problem: 'A sequencing failure is not annunciated.',
      analysis: 'Nothing in the system requirements asks for it.',
      solution: 'Introduce the annunciation requirement.',
      requirementChanges: [{
        level: 'System',
        kind: 'Introduce',
        targetSectionId: section,
        statement: 'The system shall annunciate a sequencing failure within one second.',
        rationale: 'Crew awareness of a failed sequence.',
        verificationMethod: 'Test',
      }],
    },
  })
  expect(createdResponse.status(), `creating the change request should succeed: ${await createdResponse.text()}`).toBe(201)
  const scr = await createdResponse.json()
  const requirementNumber = scr.requirementChanges[0].baseNumber as string

  const submitted = await request.post(`${apiBase}/api/change-requests/${scr.id}/submit`, {
    data: { expectedVersion: scr.version, mode: 'Sequential', approvers: [{ userId: 'systems.reviewer', name: 'Systems Engineer' }] },
  })
  expect(submitted.ok(), `submitting should succeed: ${await submitted.text()}`).toBeTruthy()
  const inReview = await submitted.json()

  const reviewer = await request.post(`${apiBase}/api/auth/login`, { data: { userName: 'systems.reviewer', password: 'AeroLink!2026' } })
  expect(reviewer.ok(), await reviewer.text()).toBeTruthy()
  const approved = await request.post(`${apiBase}/api/change-requests/${scr.id}/approve`, {
    data: { password: 'AeroLink!2026', meaning: 'Approved as the exact reviewed snapshot.', expectedVersion: inReview.version },
  })
  expect(approved.ok(), `approving should succeed: ${await approved.text()}`).toBeTruthy()
  await apiLogin(request)

  // Sealed: selected into the build's candidate baseline, frozen, and materialized. Only now does the
  // requirement revision exist, which is the whole reason withdrawal cannot reach it. A build carries one
  // candidate baseline, so this is the one the showcase already opened rather than a second one.
  const baselinesResponse = await request.get(
    `${apiBase}/api/baselines?projectId=${showcase.projectId}&releaseId=${showcase.activeReleaseId}`)
  expect(baselinesResponse.ok(), `reading the baselines should succeed: ${await baselinesResponse.text()}`).toBeTruthy()
  const baseline = (await baselinesResponse.json())[0]
  expect(baseline, 'the in-work build should have a candidate baseline').toBeTruthy()
  expect(baseline.state).toBe('Draft')

  const selected = await request.post(`${apiBase}/api/baselines/${baseline.id}/selections`, { data: { changeRequestId: scr.id } })
  expect(selected.ok(), `selecting into the baseline should succeed: ${await selected.text()}`).toBeTruthy()
  const frozen = await request.post(`${apiBase}/api/baselines/${baseline.id}/freeze`, { data: {} })
  expect(frozen.ok(), `freezing should succeed: ${await frozen.text()}`).toBeTruthy()
  const materialized = await request.post(`${apiBase}/api/baselines/${baseline.id}/materialize-requirements`, { data: {} })
  expect(materialized.ok(), `materializing should succeed: ${await materialized.text()}`).toBeTruthy()

  // Somebody else, writing against what the build produced. This is what the reopen will strand.
  const dependentResponse = await request.post(`${apiBase}/api/change-request-drafts`, {
    data: {
      projectId: showcase.projectId,
      targetReleaseId: showcase.activeReleaseId,
      type: 'System',
      title: `WITHDRAW-REOPEN dependent tightening ${suffix}`,
      problem: 'One second is not tight enough.',
      analysis: 'The crew alerting budget allows less.',
      solution: 'Tighten the annunciation deadline.',
      requirementChanges: [{
        baseNumber: requirementNumber,
        level: 'System',
        kind: 'Modify',
        statement: 'The system shall annunciate a sequencing failure within half a second.',
        rationale: 'Crew alerting budget.',
        verificationMethod: 'Test',
      }],
    },
  })
  expect(dependentResponse.status(), `creating the dependent should succeed: ${await dependentResponse.text()}`).toBe(201)
  const dependent = await dependentResponse.json()

  // The refusal, on screen and in the reader's way. It has to name reopening rather than leaving them stuck.
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'SYSTEMS ENGINEERING')
  await page.getByRole('link', { name: 'Change Requests' }).click()
  await expect(page.locator('.historyTable')).toBeVisible({ timeout: 30_000 })

  await page.locator('[data-register-row]', { hasText: `WITHDRAW-REOPEN oceanic annunciation ${suffix}` }).click()
  await expect(page.getByRole('button', { name: 'Withdraw' })).toBeVisible({ timeout: 30_000 })
  page.once('dialog', (dialog) => dialog.accept('Superseded by a better approach.'))
  await page.getByRole('button', { name: 'Withdraw' }).click()
  await expect(page.locator('.workspaceError')).toContainText('Reopen it before withdrawing work from it', { timeout: 30_000 })

  // The way out, which is where the reader was just sent.
  await openNavigationGroup(page, 'RELEASE')
  await page.getByRole('link', { name: 'Configuration Baselines' }).click()
  await expect(page.getByRole('heading', { name: 'Candidate Baselines' })).toBeVisible({ timeout: 30_000 })

  await page.getByTestId('reopen-baseline').click()
  const preview = page.getByTestId('reopen-preview')
  await expect(preview).toBeVisible({ timeout: 30_000 })

  // AC 12: every consequence stated before the act. The requirement ceases to exist, and the change request
  // written against it is named as the work that will have to be re-pointed.
  const introducedRevision = scr.requirementChanges[0].displayNumber as string
  await expect(preview).toContainText(introducedRevision)
  await expect(preview).toContainText(`${requirementNumber} — introduced by this build`)
  await expect(page.getByTestId('reopen-stranded')).toContainText(dependent.displayNumber)

  await preview.getByRole('textbox').fill('The annunciation requirement was wrong and this build has not shipped.')
  await page.getByTestId('reopen-confirm').click()
  await expect(preview).toBeHidden({ timeout: 30_000 })

  // AC 9: the dependent is flagged where its author will find it, rather than silently re-pointed.
  await openNavigationGroup(page, 'SYSTEMS ENGINEERING')
  await page.getByRole('link', { name: 'Change Requests' }).click()
  await expect(page.locator('.historyTable')).toBeVisible({ timeout: 30_000 })
  await page.locator('[data-register-row]', { hasText: `WITHDRAW-REOPEN dependent tightening ${suffix}` }).click()
  await expect(page.getByTestId('rebase-required')).toContainText('was reopened', { timeout: 30_000 })
  await expect(page.getByTestId('rebase-required')).toContainText(requirementNumber)

  // And with the build open again, the withdrawal that was refused now goes through — and the record of it
  // stays readable, which is the whole reason it is a withdrawal rather than a delete.
  await page.getByRole('link', { name: 'Change Requests' }).click()
  await expect(page.locator('.historyTable')).toBeVisible({ timeout: 30_000 })
  await page.locator('[data-register-row]', { hasText: `WITHDRAW-REOPEN oceanic annunciation ${suffix}` }).click()
  page.once('dialog', (dialog) => dialog.accept('Superseded by a better approach.'))
  await page.getByRole('button', { name: 'Withdraw' }).click()
  await expect(page.locator('[data-state]').first()).toHaveAttribute('data-state', 'Withdrawn', { timeout: 30_000 })
  // The signatures are the point: withdrawing keeps the record of what was decided, so the cycle and the
  // approval that closed it are both still on the page.
  await expect(page.getByRole('heading', { name: 'Review cycle 1' })).toBeVisible()
  await expect(page.getByText('Approved review cycle 1 stage.')).toBeVisible()
})
