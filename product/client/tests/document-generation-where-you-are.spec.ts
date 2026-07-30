import { expect, test } from '@playwright/test'
import { login, openNavigationGroup, selectProgram } from './auth'

// Generating the document for what you are reading, from where you are reading it.
//
// Generation lived only on the Digital Thread, which is the wrong place to look for it: somebody reading the
// system requirements for 1.6 wants the system requirements document for 1.6 and had to leave the requirements
// to go and find it.
//
// The build decides which document you get, rather than the reader choosing and getting it wrong. That
// distinction is the point of the test: an approved document is a controlled record with a content hash, a
// draft is generated on request and deliberately never stored, and the surface must never let one pass for the
// other.
test('the requirements explorer offers the document for the build being read', async ({ page }) => {
  test.setTimeout(120_000)
  await login(page)
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'SYSTEMS ENGINEERING')
  await page.getByRole('link', { name: 'System Requirements Explorer' }).click()

  // 1.6 is in work, so what is on offer is a draft, and it says so rather than saying "generate".
  const drafts = page.getByRole('region', { name: /Draft documents for 1\.6/ })
  await expect(drafts).toBeVisible()
  await expect(drafts.getByText('System Requirements Document')).toBeVisible()
  await expect(drafts.getByRole('link', { name: 'Draft DOCX' })).toHaveAttribute(
    'href', /\/api\/releases\/[0-9a-f-]+\/draft-document\?type=Sysrd&format=docx/)
  await expect(drafts.getByRole('link', { name: 'Draft PDF' })).toBeVisible()
  // The System explorer offers the system document and nothing else — no software documents belong here.
  await expect(drafts.getByText(/High-Level|Low-Level/)).toHaveCount(0)

  // Switch to the released build and the offer changes to the controlled record that was generated for it.
  await page.getByRole('button', { name: 'Back to Software Builds' }).click()
  await page.getByRole('button', { name: 'Open build 1.5' }).click()
  await openNavigationGroup(page, 'SYSTEMS ENGINEERING')
  await page.getByRole('link', { name: 'System Requirements Explorer' }).click()
  const approved = page.getByRole('region', { name: /Approved documents for 1\.5/ })
  await expect(approved).toBeVisible()
  await expect(approved.getByRole('link', { name: 'Draft DOCX' })).toHaveCount(0)
  // Either the approved document is there, or the surface says plainly that none was generated. What it must
  // not do is offer a download that cannot resolve.
  await expect(
    approved.getByRole('link', { name: 'Approved DOCX' }).or(approved.getByText('Not available')),
  ).toBeVisible({ timeout: 30_000 })
})

test('the software explorer offers the document for the level being read', async ({ page }) => {
  test.setTimeout(120_000)
  await login(page)
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'SOFTWARE ENGINEERING')
  await page.getByRole('link', { name: 'Software Requirements Explorer' }).click()

  // Unfiltered, software has two requirement documents, so both are offered.
  const drafts = page.getByRole('region', { name: /Draft documents for 1\.6/ })
  await expect(drafts.getByText(/High-Level/)).toBeVisible()
  await expect(drafts.getByText(/Low-Level/)).toBeVisible()

  // Filtered to high-level, only the high-level document is on offer — offering the other would be offering a
  // document for requirements the reader has just filtered out.
  await page.getByLabel('Level filter').selectOption('HighLevel')
  await expect(drafts.getByText(/High-Level/)).toBeVisible()
  await expect(drafts.getByText(/Low-Level/)).toHaveCount(0)
})

// A third test asserted the opposite contract — a Controlled Documents tab on the Digital Thread with a
// Released/Draft toggle, so the reader chose. Build-scoped workspaces removed both the tab and the choice on
// purpose: the build decides, which is what the two tests above now prove for the in-work and released cases.
// Keeping a test for a question the product deliberately stopped asking would have locked in the old model.
