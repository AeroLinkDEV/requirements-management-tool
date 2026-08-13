import { expect, test, type Page } from '@playwright/test'
import { login } from './auth'

/**
 * Stage 2 of raising a test change request is controlled procedure authoring.
 *
 * It was a single `+ Add a procedure decision` button and a flat form: the reader committed to a card and
 * then told it what it was, typed a controlled identifier by hand, and got no help finding the procedure they
 * meant. The requirements editor has had controlled authoring for a while — three acts offered as three
 * buttons, a proposal card that states the identity it has taken, and a search that locks the exact identity
 * and next revision.
 *
 * These assert the same structure and the same behaviour on the verification side, from the same locators the
 * requirements editor uses.
 */

const openEditor = async (page: Page, branch: string) => {
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
  await page.goto(new URL(`${root}/${branch}/change-requests/new`, page.url()).toString(), { waitUntil: 'load' })
  await expect(page.getByRole('heading', { name: 'Procedure changes', level: 2 })).toBeVisible({ timeout: 30_000 })
}

test('the three acts are offered as three buttons, not chosen after the fact', async ({ page }) => {
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 1600, height: 900 })
  await login(page)
  await openEditor(page, 'system-verification')

  const actions = page.getByLabel('Add procedure proposal')
  await expect(actions).toBeVisible()
  await expect(actions.getByRole('button', { name: '+ Introduce System test procedure' })).toBeVisible()
  await expect(actions.getByRole('button', { name: 'Modify existing' })).toBeVisible()
  await expect(actions.getByRole('button', { name: 'Retire existing' })).toBeVisible()
  // The button that made the reader choose the act from a dropdown afterwards is gone.
  await expect(page.getByRole('button', { name: '+ Add a procedure decision' })).toHaveCount(0)
})

test('a proposal card states the identity it has taken', async ({ page }) => {
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 1600, height: 900 })
  await login(page)
  await openEditor(page, 'system-verification')

  await page.getByRole('button', { name: '+ Introduce System test procedure' }).click()
  const proposal = page.locator('[data-procedure-proposal="0"]')
  await expect(proposal).toBeVisible()
  await expect(proposal.getByText('PROPOSAL 1')).toBeVisible()
  // A new procedure has no controlled number until check-in assigns one, and the card says so rather than
  // showing an empty box that looks like something to fill in.
  await expect(proposal.getByRole('heading', { name: 'New System test procedure' })).toBeVisible()
  await expect(proposal.getByLabel('Identifier 1')).toHaveValue('Provisional — assigned at check-in')
  await expect(proposal.getByLabel('Revision 1')).toHaveValue('Pending')
})

test('modifying searches the controlled library and locks the exact identity and next revision', async ({ page }) => {
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 1600, height: 900 })
  await login(page)
  await openEditor(page, 'system-verification')

  await page.getByRole('button', { name: 'Modify existing' }).click()
  const proposal = page.locator('[data-procedure-proposal="0"]')
  await expect(proposal).toBeVisible()
  await expect(proposal.getByText('Select the procedure to modify')).toBeVisible()

  // Populates as you type, against the controlled library rather than a list the reader has to know.
  await proposal.getByLabel('Find controlled procedure 1').fill('SYSTP-000001')
  const result = proposal.locator('.proposalLookupResults button').first()
  await expect(result).toBeVisible({ timeout: 30_000 })
  await expect(result).toContainText('SYSTP-000001')
  await result.click()

  // The identity and the next revision are locked from what the library carries, not typed by hand.
  await expect(proposal.getByLabel('Identifier 1')).toHaveValue('SYSTP-000001')
  await expect(proposal.getByLabel('Revision 1')).not.toHaveValue('Pending')
  await expect(proposal.locator('.proposalLookup')).toHaveCount(0)
})

test('a retirement asks for the procedure and the reason, not for steps to invent', async ({ page }) => {
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 1600, height: 900 })
  await login(page)
  await openEditor(page, 'system-verification')

  await page.getByRole('button', { name: 'Retire existing' }).click()
  const proposal = page.locator('[data-procedure-proposal="0"]')
  await expect(proposal.getByText('Select the procedure to retire')).toBeVisible()

  // Asking an engineer for the steps of a procedure they are withdrawing is asking them to invent content.
  await expect(proposal.getByLabel('Steps 1')).toHaveCount(0)
  await expect(proposal.getByLabel('Expected result 1')).toHaveCount(0)
  await expect(proposal.getByLabel('Rationale 1')).toBeVisible()
})
