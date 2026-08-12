import { expect, test } from '@playwright/test'
import { login } from './auth'

/**
 * The verification Change Requests page lists the change requests it controls.
 *
 * The page was named Change Requests but showed only downstream assessments — a test change request could be
 * seen only from inside the assessment that raised it, so "what packages does this build have, and where has
 * each one got to" could not be answered without opening every assessment in turn. The requirements side has
 * always listed its change requests; this asserts the verification side now does the same.
 */

/** Signs in once and returns the build root, so a walk across disciplines does not sign in three times. */
const enterBuild = async (page: import('@playwright/test').Page) => {
  await login(page)
  return new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
}

const openVerificationChangeRequests = async (page: import('@playwright/test').Page, root: string, branch: string) => {
  await page.goto(new URL(`${root}/${branch}/coverage`, page.url()).toString(), { waitUntil: 'load' })
  await expect(page.getByRole('heading', { name: 'Change Requests', level: 1 })).toBeVisible({ timeout: 30_000 })
}

test('every discipline lists the packages controlling its build test procedures', async ({ page }) => {
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 1440, height: 900 })
  const root = await enterBuild(page)

  for (const [branch, acronym] of [
    ['system-verification', 'SYSTCR'],
    ['software-verification/hlr', 'HLRTCR'],
    ['software-verification/llr', 'LLRTCR'],
  ] as const) {
    await openVerificationChangeRequests(page, root, branch)

    // The section exists on every discipline, named for what it lists.
    const register = page.getByRole('heading', { name: `${acronym}s in this build` })
    await expect(register).toBeVisible({ timeout: 30_000 })

    const rows = page.locator('[data-tcr]')
    const empty = page.getByText(`No ${acronym}s in this build yet`)
    // One of the two must be shown. A section that renders neither rows nor an explanation would be a
    // heading over nothing, which is what the page did before.
    await expect(rows.first().or(empty)).toBeVisible({ timeout: 30_000 })

    if (await rows.count()) {
      // Every listed package carries its own controlled number rather than the change request's. An
      // unconcluded assessment is not a package and belongs in the section above.
      const numbers = await rows.evaluateAll(items => items.map(item => item.getAttribute('data-tcr')))
      for (const number of numbers) expect(number).toContain(acronym)
      await expect(page.getByRole('columnheader', { name: 'Procedure decisions' })).toBeVisible()
      await expect(page.getByRole('columnheader', { name: 'State' })).toBeVisible()
    }
  }
})

test('the assessments section and the register are different sections', async ({ page }) => {
  test.setTimeout(120_000)
  await page.setViewportSize({ width: 1440, height: 900 })
  const root = await enterBuild(page)
  await openVerificationChangeRequests(page, root, 'system-verification')

  // Both are present: the register lists packages, the queue above lists approved changes awaiting a
  // conclusion. Replacing one with the other would lose a question the page answers.
  await expect(page.getByRole('heading', { name: 'Downstream test assessments' })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'SYSTCRs in this build' })).toBeVisible()
})
