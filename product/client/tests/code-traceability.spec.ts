import { expect, test } from '@playwright/test'
import { login, openNavigationGroup } from './auth'

/**
 * Code says what the release decision says, or it says nothing.
 *
 * The active build has no materialized requirement population of its own, so there is no exact set of LLR
 * revisions owing implementation evidence and the gate cannot be evaluated. It used to answer that question
 * from the baseline the build *inherits* from its predecessor and rendered the result as "RELEASE GATE —
 * BUILD 1.6 — 80%", while the Decision Room reported the same gate unevaluated. A reader had two numbers for
 * one decision and no way to tell which one the release would use.
 */
test('Code reports the active build gate as unevaluated until the build has its own population', async ({ page }) => {
  test.setTimeout(90_000)
  await login(page)

  const nav = page.getByRole('navigation', { name: 'Primary navigation' })
  await nav.getByRole('link', { name: 'Code traceability' }).click()
  await expect(page.getByRole('heading', { name: 'Code', level: 1 })).toBeVisible()
  await expect(page).toHaveURL(/\/code$/)
  await expect(page.getByText('GitLab is the source of truth', { exact: true })).toBeVisible()

  const gate = page.locator('.codeGate')
  await expect(gate).toContainText('Not evaluated yet')
  await expect(gate).toContainText('Waiting for a materialized baseline')
  // No percentage at all: the number is what read as an authoritative release figure.
  await expect(gate).not.toContainText('%')
  await expect(page.locator('.codeRecords article')).toHaveCount(0)
  // Nothing can be mapped against a population that does not exist yet.
  await expect(page.getByRole('button', { name: '+ Record code mapping' })).toHaveCount(0)

  const activeUrl = page.url()
  await page.reload()
  await expect(page).toHaveURL(activeUrl)
  await expect(page.locator('.codeGate')).toContainText('Not evaluated yet')
})

/**
 * The released build has an exact materialized population, so its gate is real, complete, and read-only.
 */
test('Code shows the released build as evaluated, complete, and historical', async ({ page }) => {
  test.setTimeout(90_000)
  await login(page)

  await page.getByRole('button', { name: 'Back to Software Builds' }).click()
  await page.getByRole('button', { name: 'Open build 1.5' }).click()
  await page.getByRole('navigation', { name: 'Primary navigation' }).getByRole('link', { name: 'Code traceability' }).click()

  await expect(page.getByText('Historical · read-only', { exact: true })).toBeVisible()
  await expect(page.getByText('Demonstration data', { exact: true })).toBeVisible()
  // The released build introduced every LLR in its baseline, so it owes evidence for all of them and carries
  // a labelled sample of five. It read '5 of 5, 100%' while 695 introduced requirements owed evidence nobody
  // had recorded, because the projection measured the first five LLRs by number for this Program alone.
  await expect(page.getByRole('heading', { name: '5 of 700 exact LLR revisions mapped' })).toBeVisible()
  await expect(page.locator('.codeGate')).toContainText('0%')
  await expect(page.locator('.codeRecords article')).toHaveCount(700)
  await expect(page.getByRole('button', { name: '+ Record code mapping' })).toHaveCount(0)

  await page.reload()
  await expect(page.getByText('Historical · read-only', { exact: true })).toBeVisible()
  await expect(page.getByRole('heading', { name: '5 of 700 exact LLR revisions mapped' })).toBeVisible()
})

test('Digital Thread shows one exact SYSR-to-build lifecycle path while retaining traversal', async ({ page }) => {
  await login(page)
  await openNavigationGroup(page, 'SYSTEMS ENGINEERING')
  await page.getByRole('link', { name: 'Digital Thread' }).click()

  const path = page.getByRole('list', { name: /Complete digital thread for/ })
  await expect(path).toBeVisible()
  for (const stage of ['SYSTEM REQUIREMENT', 'HLR', 'LLR', 'TEST CASE', 'TEST RESULT', 'TEST EVIDENCE', 'BUILD'])
    await expect(path.getByText(stage, { exact: true })).toBeVisible()
  await expect(path.getByText(/^SYSR-/)).toBeVisible()
  await expect(path.getByText(/^HLR-/)).toBeVisible()
  await expect(path.getByText(/^LLR-/)).toBeVisible()
  await expect(path.getByText(/^LLRTC-/)).toBeVisible()
  await expect(path.getByText('Pass', { exact: true })).toBeVisible()
  const evidenceCard = path.locator('.completeThreadStep').filter({ hasText: 'TEST EVIDENCE' }).locator('article')
  await expect(evidenceCard).toHaveClass(/missing/)
  await expect(evidenceCard).toContainText('Not attached')
  await expect(evidenceCard).toContainText('External reference only:')
  await expect(page.getByText('SYSR → HLR → LLR → case → result → evidence → build')).toBeVisible()

  await path.getByRole('button').first().click()
  await expect(page.getByText('SELECTED CONTROLLED RECORD')).toBeVisible()
  await page.reload()
  await expect(page.getByRole('list', { name: /Complete digital thread for/ })).toBeVisible()
})
