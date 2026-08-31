import { expect, test } from '@playwright/test'
import type { APIRequestContext } from '@playwright/test'
import { apiBase, apiLogin, firstSectionId, login, openNavigationGroup, selectProgram, showcaseSeed } from './auth'
import type { ShowcaseSeed } from './auth'

type CandidateBaseline = { id: string; displayNumber: string; state: string }

/**
 * The open build this journey needs before it authors anything of its own.
 *
 * The journey used to read the showcase's candidate baseline and simply assert it was a Draft. That held only
 * because of where the file happened to land in the Playwright shard: journeys share one API and one
 * database, and any earlier journey that freezes the in-work build — `suspect-verification-coverage` does,
 * to make coverage suspect — left this one reading `Frozen` and failing on its first assertion. Rebalancing
 * the shards was enough to expose it, and a retry could not clear it either, because the state that broke the
 * attempt outlives the attempt.
 *
 * So the precondition is established rather than assumed, through the same governed route a configuration
 * manager would use. Nothing here touches the database directly, deletes history, or resets the showcase: a
 * frozen build is reopened by `POST /api/baselines/{id}/reopen`, with an attributable reason, under the
 * authority that endpoint already enforces.
 *
 * It runs before this journey creates its own change request, deliberately. Reopening dematerializes the
 * revisions a frozen baseline produced, so doing it later would take back work this journey had just done and
 * the test would be unpicking itself.
 */
async function draftCandidateBaselineAsync(request: APIRequestContext, showcase: ShowcaseSeed): Promise<CandidateBaseline> {
  const readAsync = async () => {
    const response = await request.get(
      `${apiBase}/api/baselines?projectId=${showcase.projectId}&releaseId=${showcase.activeReleaseId}`)
    expect(response.ok(), `reading the candidate baselines should succeed: ${await response.text()}`).toBeTruthy()
    return await response.json() as CandidateBaseline[]
  }

  // A build carries exactly one candidate baseline, and everything below depends on that being true rather
  // than on the first row of a list happening to be the right one.
  const baselines = await readAsync()
  expect(baselines, 'the in-work build should carry exactly one candidate baseline').toHaveLength(1)
  const baseline = baselines[0]
  if (baseline.state === 'Draft') return baseline
  if (baseline.state !== 'Frozen') {
    throw new Error(
      `The in-work build's candidate baseline ${baseline.displayNumber} is ${baseline.state}. This journey `
      + 'needs it Draft, and only a Frozen baseline can be reopened, so the precondition cannot be established '
      + 'through the governed route.')
  }

  const reopened = await request.post(`${apiBase}/api/baselines/${baseline.id}/reopen`, {
    data: {
      reason: 'Test precondition: the withdraw-and-reopen journey requires an open build before it authors '
        + 'its own controlled work.',
    },
  })
  expect(reopened.ok(),
    `reopening the frozen candidate baseline should succeed: ${await reopened.text()}`).toBeTruthy()

  const after = await readAsync()
  expect(after, 'the governed reopen must not change how many candidate baselines the build carries').toHaveLength(1)
  expect(after[0].state, 'the candidate baseline should read Draft after the governed reopen').toBe('Draft')
  return after[0]
}

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

  // System change review is governed by the active review procedure. The retired generic Approver role is
  // intentionally not part of the FMS showcase, so establish the same BaseRole authority the product now
  // enforces and retire this test-local procedure once the journey has completed.
  const suffix = Date.now().toString().slice(-6)
  const workflowResponse = await request.post(`${apiBase}/api/review-workflows`, { data: {
    projectId: showcase.projectId,
    name: `Withdraw and reopen fixture ${suffix}`,
    appliesTo: 'System',
    mode: 'Sequential',
    stages: [{
      name: 'Systems assessment',
      kind: 'Review',
      requiredAuthority: { kind: 'BaseRole', role: 'SystemEngineer' },
    }],
  } })
  expect(workflowResponse.ok(), await workflowResponse.text()).toBeTruthy()
  const workflow = await workflowResponse.json()
  const activated = await request.post(`${apiBase}/api/review-workflows/${workflow.id}/activate`, { data: {} })
  expect(activated.ok(), await activated.text()).toBeTruthy()

  // Before this journey authors anything of its own: the build has to be open. Established through the
  // governed reopen rather than assumed, so an earlier journey that froze it cannot decide this one.
  const candidateBaseline = await draftCandidateBaselineAsync(request, showcase)

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
  const baselines = await baselinesResponse.json() as CandidateBaseline[]
  expect(baselines, 'the in-work build should carry exactly one candidate baseline').toHaveLength(1)
  const baseline = baselines[0]
  expect(baseline, 'the in-work build should have a candidate baseline').toBeTruthy()
  // Still the same assertion the journey has always made: nothing here is relaxed, and it is the same
  // baseline the precondition opened.
  expect(baseline.id).toBe(candidateBaseline.id)
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
  await page.getByRole('link', { name: 'Open change request →' }).click()
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
  await page.getByRole('link', { name: 'Open change request →' }).click()
  await expect(page.getByTestId('rebase-required')).toContainText('was reopened', { timeout: 30_000 })
  await expect(page.getByTestId('rebase-required')).toContainText(requirementNumber)

  // And with the build open again, the withdrawal that was refused now goes through — and the record of it
  // stays readable, which is the whole reason it is a withdrawal rather than a delete.
  await page.getByRole('link', { name: 'Change Requests' }).click()
  await expect(page.locator('.historyTable')).toBeVisible({ timeout: 30_000 })
  await page.locator('[data-register-row]', { hasText: `WITHDRAW-REOPEN oceanic annunciation ${suffix}` }).click()
  await page.getByRole('link', { name: 'Open change request →' }).click()
  page.once('dialog', (dialog) => dialog.accept('Superseded by a better approach.'))
  await page.getByRole('button', { name: 'Withdraw' }).click()
  await expect(page.locator('[data-state]').first()).toHaveAttribute('data-state', 'Withdrawn', { timeout: 30_000 })
  // The signatures are the point: withdrawing keeps the record of what was decided, so the cycle and the
  // approval that closed it are both still on the page.
  await expect(page.getByRole('heading', { name: 'Review cycle 1' })).toBeVisible()
  await expect(page.getByText('Approved review cycle 1 stage.')).toBeVisible()

  const retired = await request.post(`${apiBase}/api/review-workflows/${workflow.id}/retire`, { data: {} })
  expect(retired.ok(), await retired.text()).toBeTruthy()
})
