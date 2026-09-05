import { expect, test } from '@playwright/test'
import { login, showcaseSeed } from './auth'

/**
 * #925 F1 — the controlled target picker is scoped to the proposal's exact level.
 *
 * An LLR change request's Modify/Retire picker must offer LLR identities only, and selecting a target
 * must lock identity, revision, and level without re-leveling the proposal. The HLR and System
 * workspaces are constrained to their own levels the same way, while the upstream allocation picker
 * deliberately keeps offering the parent level: an LLR's HLR is a valid upstream, not a valid target.
 *
 * The journey runs against the seeded FMS showcase project, whose stored project ladder is the
 * configured NonDefault ladder, so the exact-level constraint is exercised through the configured
 * policy resolver rather than a test double. Nothing is saved: every authoring segment is cancelled,
 * and the browser-local draft is cleared at the start so reruns are order-independent.
 */

// Evidence screenshots are written only when a run asks for them, so routine lanes stay clean.
const evidenceDir = process.env.AEROLINK_F1_PICKER_EVIDENCE

test('the target picker offers only the proposal exact level across LLR, HLR, and System workspaces', async ({ page, request }) => {
  test.setTimeout(240_000)
  const showcase = await showcaseSeed(request)
  const root = `/programs/${showcase.programId}/projects/${showcase.projectId}/releases/${showcase.activeReleaseId}`

  await login(page, 'admin', { openProject: false })
  await page.evaluate(() => window.localStorage.clear())

  // LLR workspace: the issue's reproduction. Searching "33" used to return eight HLRs; the picker must
  // answer with LLRs only, and selecting one must not re-level the proposal.
  await page.goto(`${root}/software/change-requests/new?level=LLR`)
  await expect(page.getByRole('heading', { name: 'Create LLR Change Request' })).toBeVisible()
  await page.getByRole('button', { name: 'Modify existing LLR' }).click()
  const picker = page.getByRole('textbox', { name: 'Find controlled requirement 1' })
  await picker.fill('33')
  const results = page.locator('.proposalLookupResults button')
  await expect(results.first()).toBeVisible({ timeout: 15_000 })
  const llrTexts = await results.allInnerTexts()
  expect(llrTexts.length).toBeGreaterThanOrEqual(1)
  for (const text of llrTexts) {
    expect(text).toMatch(/^LLR-\d{6}\.\d{2}/)
    expect(text).not.toContain('HLR-')
  }
  if (evidenceDir) await page.screenshot({ path: `${evidenceDir}/llr-modify-picker.png`, fullPage: false })

  await results.filter({ hasText: 'LLR-000033.02' }).click()
  await expect(page.getByRole('textbox', { name: 'Identifier', exact: true })).toHaveValue('LLR-000033')
  await expect(page.getByRole('textbox', { name: 'Revision', exact: true })).toHaveValue('03')
  await expect(page.getByRole('textbox', { name: 'Level', exact: true })).toHaveValue('Software LLR')
  await expect(page.getByRole('heading', { name: 'Create LLR Change Request' })).toBeVisible()

  // The upstream allocation picker is a different picker: an LLR's HLR remains a valid upstream.
  const upstream = page.getByRole('textbox', { name: 'Find upstream requirement 1' })
  await upstream.fill('33')
  const upstreamResults = page
    .getByRole('region', { name: 'Upstream allocation for proposal 1' })
    .locator('.proposalLookupResults button')
  await expect(upstreamResults.first()).toBeVisible({ timeout: 15_000 })
  for (const text of await upstreamResults.allInnerTexts()) {
    expect(text).toMatch(/HLR-\d{6}\.\d{2}/)
  }

  // Switching the proposal kind resets identity and must re-query with the proposal's own level.
  await page.getByRole('combobox', { name: 'Change type' }).selectOption('Retire')
  await expect(page.getByRole('heading', { name: 'Select an existing controlled requirement' })).toBeVisible()
  const retirePicker = page.getByRole('textbox', { name: 'Find controlled requirement 1' })
  await retirePicker.fill('650')
  await expect(results.first()).toBeVisible({ timeout: 15_000 })
  for (const text of await results.allInnerTexts()) {
    expect(text).toMatch(/^LLR-\d{6}\.\d{2}/)
    expect(text).not.toContain('HLR-')
  }
  await results.filter({ hasText: 'LLR-000650' }).first().click()
  await expect(page.getByRole('textbox', { name: 'Identifier', exact: true })).toHaveValue('LLR-000650')
  await expect(page.getByRole('textbox', { name: 'Level', exact: true })).toHaveValue('Software LLR')
  if (evidenceDir) await page.screenshot({ path: `${evidenceDir}/llr-retire-picker.png`, fullPage: false })

  // HLR workspace: the same constraint on the sibling software level, on the same search.
  await page.goto(`${root}/software/change-requests/new?level=HLR`)
  await expect(page.getByRole('heading', { name: 'Create HLR Change Request' })).toBeVisible()
  await page.getByRole('button', { name: 'Modify existing HLR' }).click()
  await page.getByRole('textbox', { name: 'Find controlled requirement 1' }).fill('33')
  await expect(results.first()).toBeVisible({ timeout: 15_000 })
  for (const text of await results.allInnerTexts()) {
    expect(text).toMatch(/^HLR-\d{6}\.\d{2}/)
    expect(text).not.toContain('LLR-')
  }
  await results.filter({ hasText: 'HLR-000033.02' }).click()
  await expect(page.getByRole('textbox', { name: 'Identifier', exact: true })).toHaveValue('HLR-000033')
  await expect(page.getByRole('textbox', { name: 'Level', exact: true })).toHaveValue('Software HLR')
  if (evidenceDir) await page.screenshot({ path: `${evidenceDir}/hlr-modify-picker.png`, fullPage: false })

  // System workspace: constrained to System requirements by the same level contract.
  await page.goto(`${root}/systems/change-requests/new`)
  await expect(page.getByRole('heading', { name: 'Create System Change Request' })).toBeVisible()
  await page.getByRole('button', { name: 'Modify existing', exact: true }).click()
  await page.getByRole('textbox', { name: 'Find controlled requirement 1' }).fill('SYSR-0000')
  await expect(results.first()).toBeVisible({ timeout: 15_000 })
  for (const text of await results.allInnerTexts()) {
    expect(text).toMatch(/^SYSR-\d{6}\.\d{2}/)
  }
  await results.first().click()
  await expect(page.getByRole('textbox', { name: 'Identifier', exact: true })).toHaveValue(/^SYSR-\d{6}$/)
  await expect(page.getByRole('textbox', { name: 'Level', exact: true })).toHaveValue('System')
  if (evidenceDir) await page.screenshot({ path: `${evidenceDir}/system-modify-picker.png`, fullPage: false })

  // Nothing is saved: the whole journey cancels out of every workspace.
  await page.getByRole('button', { name: 'Cancel' }).click()
  await expect(page.getByRole('heading', { name: 'Command Center' })).toBeVisible({ timeout: 15_000 })
})
