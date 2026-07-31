import { expect, test } from '@playwright/test'
import { login, openNavigationGroup, selectProgram } from './auth'

/**
 * What a build's requirements are tested by, and what still has nobody looking at it.
 *
 * Two different questions are asked on this page. "Is this requirement covered by a procedure?" is about the
 * library as it stands. "Has the test work this build's changes created been picked up?" is about people and
 * queues. A page answering only the first would show a wall of green while nobody had started on the changes
 * about to ship, which is why the queue is above the inventory.
 */
test('coverage opens on the work the build created, then the inventory behind it', async ({ page }) => {
  test.setTimeout(180_000)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Testing Coverage' }).click()

  await expect(page.getByRole('heading', { name: 'Testing Coverage' })).toBeVisible({ timeout: 30_000 })

  // The queue first: the packages this build's approved changes created.
  await expect(page.getByRole('heading', { name: 'Test change requests' })).toBeVisible()
  // Numbered as controlled records rather than borrowing the number of the change that raised them. The
  // showcase raises one System package for the in-work build, so this is a fact about the page.
  const packages = page.locator('.coverageRow').filter({ hasText: /TCR-/ })
  await expect(packages.first()).toContainText(/SYSTCR-\d{6}\.\d{2}/, { timeout: 30_000 })

  // The counts a reader plans against.
  const summary = page.getByRole('region', { name: 'Coverage summary' })
  await expect(summary).toBeVisible()
  await expect(summary).toContainText('Requirements')

  // And the browsable procedure library, which used to be a tab of its own and was the only way to find a
  // procedure by number.
  await expect(page.getByRole('heading', { name: 'Test procedures' })).toBeVisible()
  await page.getByLabel('Find a procedure').fill('SYSTP-000001')
  const row = page.locator('.procedureLibrary .coverageRow').filter({ hasText: 'SYSTP-000001' })
  await expect(row.first()).toBeVisible({ timeout: 30_000 })
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
  await page.getByRole('link', { name: 'System Testing Coverage' }).click()
  await expect(page.getByRole('heading', { name: 'Testing Coverage' })).toBeVisible({ timeout: 30_000 })

  const claimable = page.locator('.coverageRow').filter({ hasText: /SYSTCR-/ })
    .filter({ has: page.getByRole('button', { name: 'Take it on' }) }).first()
  await expect(claimable).toBeVisible({ timeout: 30_000 })
  const displayNumber = (await claimable.locator('b').first().textContent())!.trim()
  await claimable.getByRole('button', { name: 'Take it on' }).click()
  const first = page.locator('.coverageRow').filter({ hasText: displayNumber }).first()
  await first.getByRole('button', { name: 'Decisions' }).click()

  const undecided = first.locator('.decisionList li').filter({ has: page.getByRole('button', { name: 'Decide' }) })
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

/**
 * Who wrote a procedure, and what made them change it.
 *
 * A procedure is read by somebody deciding whether to trust it, and its revisions were reachable only one at
 * a time with no way to see what drove any of them. The change request behind a revision is reached through
 * the verification decision that resolved to it, which is the record that actually connects the two.
 */
test('a procedure says who wrote it and what drove each revision', async ({ page }) => {
  test.setTimeout(180_000)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Testing Coverage' }).click()
  await expect(page.getByRole('heading', { name: 'Testing Coverage' })).toBeVisible({ timeout: 30_000 })

  await page.getByLabel('Find a procedure').fill('SYSTP-000001')
  const row = page.locator('.procedureLibrary .coverageRow').filter({ hasText: 'SYSTP-000001' }).first()
  await expect(row).toBeVisible({ timeout: 30_000 })
  await row.getByRole('button', { name: 'History' }).click()

  const history = page.getByRole('dialog', { name: /History of SYSTP-000001/ })
  await expect(history).toBeVisible({ timeout: 30_000 })
  await expect(history).toContainText('Created by')
  // Every revision, newest first, each saying who wrote it — a name, not an account handle.
  await expect(history.locator('.revisionList li').first()).toContainText('Written by')
  await expect(history.locator('.revisionList li').first()).toContainText(/SYSTP-000001\.\d{2}/)
  // A revision written outside a change request says so rather than leaving the reader to guess.
  await expect(history.locator('.revisionDriver').first()).toBeVisible()

  await history.getByRole('button', { name: 'Close' }).click()
  await expect(history).toHaveCount(0)
})

test('software HLR and LLR each have their own coverage page', async ({ page }) => {
  test.setTimeout(180_000)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('button', { name: 'Software' }).last().click()

  await page.getByRole('link', { name: 'Software HLR Testing Coverage' }).click()
  await expect(page).toHaveURL(/software-verification\/hlr\/coverage$/, { timeout: 30_000 })
  await expect(page.getByText('VERIFICATION / SOFTWARE HLR')).toBeVisible()
  // The showcase raises one HLR package for this build. This page reported none for a while, and nothing had
  // failed: two loaders on the page shared one "only the newest reply may write the screen" counter, so the
  // procedure search cancelled the load that was fetching the packages. Asserting the package here is what
  // stops that returning as an empty queue nobody can distinguish from having no work.
  await expect(page.locator('.coverageRow').filter({ hasText: /TCR-/ }).first())
    .toContainText(/HLRTCR-\d{6}\.\d{2}/, { timeout: 30_000 })

  await page.getByRole('link', { name: 'Software LLR Testing Coverage' }).click()
  await expect(page).toHaveURL(/software-verification\/llr\/coverage$/, { timeout: 30_000 })
  await expect(page.getByText('VERIFICATION / SOFTWARE LLR')).toBeVisible()
})

/**
 * Creating a procedure, and approving somebody else's.
 *
 * Both lived only in the tabbed workspace, which is why it could not be retired. A procedure is created as a
 * Draft and cannot be run until somebody other than its author has signed for it — the product refuses an
 * author approving their own, which is what makes the approval independent rather than a formality.
 */
test('a procedure is created here as a Draft and needs somebody else to approve it', async ({ page }) => {
  test.setTimeout(180_000)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Testing Coverage' }).click()
  await expect(page.getByRole('heading', { name: 'Testing Coverage' })).toBeVisible({ timeout: 30_000 })

  await page.getByRole('button', { name: '+ New test procedure' }).click()
  const form = page.getByRole('dialog', { name: 'Create a test procedure' })
  await expect(form).toBeVisible({ timeout: 30_000 })
  const title = `Oceanic sequencing regression ${Date.now()}`
  await form.getByLabel('Title').fill(title)
  await form.getByLabel('Objective').fill('Prove sequencing holds across the oceanic transition.')
  await form.getByLabel('Preconditions').fill('FMS rig 2 loaded with the current build.')
  await form.getByLabel('Steps').fill('Enter the oceanic route, then force a transition.')
  await form.getByLabel('Expected result').fill('No waypoint is dropped.')
  // A procedure that verifies nothing is not a controlled procedure, so this is required rather than linked
  // afterwards — an unlinked procedure never counts as coverage and would look like work already done.
  await form.getByLabel('Requirements it verifies').selectOption({ index: 0 })
  await form.getByLabel('Independent procedure approver').fill('systems.lead')
  await form.locator('.personSuggestions button[data-user-name="systems.lead"]').click()
  await form.getByRole('button', { name: 'Create procedure' }).click()

  await expect(form).toHaveCount(0, { timeout: 30_000 })
  await expect(page.getByRole('status')).toContainText('needs independent approval')

  // It is findable by its own controlled number, in Draft, with the approval offered.
  await page.getByLabel('Find a procedure').fill(title)
  const created = page.locator('.procedureLibrary .coverageRow').filter({ hasText: title }).first()
  await expect(created).toBeVisible({ timeout: 30_000 })
  await expect(created).toContainText('Awaiting approval')
  // Offered to somebody else, not to the author. The product refuses an author signing for their own work, so
  // the page says why the control is absent rather than presenting one the server would refuse.
  await expect(created.getByRole('button', { name: 'Review & approve' })).toHaveCount(0)
  await expect(created).toContainText('Awaiting')
})

/**
 * The approval, given by somebody other than the author.
 *
 * A Draft procedure cannot be run, and the signature that lifts it is what makes the coverage it claims worth
 * anything. It is recorded here rather than in a separate workspace, because the person who sees the gap is
 * the person who closes it.
 */
test('a Draft procedure is approved here by a second person, and only then counts as coverage', async ({ page, browser }) => {
  test.setTimeout(180_000)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Testing Coverage' }).click()
  await expect(page.getByRole('heading', { name: 'Testing Coverage' })).toBeVisible({ timeout: 30_000 })

  await page.getByRole('button', { name: '+ New test procedure' }).click()
  const form = page.getByRole('dialog', { name: 'Create a test procedure' })
  const title = `Transition hold regression ${Date.now()}`
  await form.getByLabel('Title').fill(title)
  await form.getByLabel('Objective').fill('Prove the transition hold is honoured.')
  await form.getByLabel('Preconditions').fill('FMS rig 1 loaded with the current build.')
  await form.getByLabel('Steps').fill('Arm the hold, then cross the transition.')
  await form.getByLabel('Expected result').fill('The hold is honoured.')
  await form.getByLabel('Requirements it verifies').selectOption({ index: 0 })
  await form.getByLabel('Independent procedure approver').fill('systems.lead')
  await form.locator('.personSuggestions button[data-user-name="systems.lead"]').click()
  await form.getByRole('button', { name: 'Create procedure' }).click()
  await expect(form).toHaveCount(0, { timeout: 30_000 })

  // The author cannot sign for their own work, so the approval is taken by somebody else — in their own
  // browser context rather than by signing out of this one. Signing out and straight back in raced the
  // session cookie on CI: the sign-in page had not replaced the workspace by the time the next journey step
  // looked for the username field, and the journey sat there until it timed out.
  const second = await browser.newContext()
  const approver = await second.newPage()
  await login(approver, 'systems.lead', { openProject: false })
  await selectProgram(approver, 'Flight Management System Live Program')
  await openNavigationGroup(approver, 'ASSURANCE')
  await approver.getByRole('link', { name: 'System Testing Coverage' }).click()
  await approver.getByLabel('Find a procedure').fill(title)
  const row = approver.locator('.procedureLibrary .coverageRow').filter({ hasText: title }).first()
  await expect(row).toBeVisible({ timeout: 30_000 })
  await row.getByRole('button', { name: 'Review & approve' }).click()

  const signature = approver.locator('.signatureModal')
  await signature.getByLabel('Re-enter your password').fill('AeroLink!2026')
  await signature.getByRole('button', { name: 'Sign & approve' }).click()
  await expect(signature).toHaveCount(0, { timeout: 30_000 })
  await expect(row).toContainText('Approved')
  await expect(row.getByRole('button', { name: 'Review & approve' })).toHaveCount(0)
  await second.close()
})

/**
 * The whole inventory, on request.
 *
 * The page opens on what is not covered, because that is the work. The full list is still needed to answer
 * "is this specific requirement tested?", so it is one toggle away rather than a separate page.
 */
test('the full requirement coverage table is one toggle away', async ({ page }) => {
  test.setTimeout(120_000)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Testing Coverage' }).click()
  await expect(page.getByRole('heading', { name: 'Testing Coverage' })).toBeVisible({ timeout: 30_000 })

  const toggle = page.getByRole('button', { name: /Show all \d+ requirements/ })
  await expect(toggle).toBeVisible({ timeout: 30_000 })
  const listed = Number(/Show all (\d+)/.exec((await toggle.textContent()) ?? '')?.[1] ?? 0)
  await toggle.click()
  await expect(page.getByRole('button', { name: 'Show only what needs attention' })).toBeVisible()
  await expect(page.locator('.fullCoverage .coverageRow')).toHaveCount(listed, { timeout: 30_000 })
})
