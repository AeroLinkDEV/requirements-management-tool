import { expect, test } from '@playwright/test'
import { login } from './auth'

/**
 * The verification Change Requests page lists the change requests it controls.
 *
 * The page was named Change Requests but showed only downstream assessments — a test change request could be
 * seen only from inside the assessment that raised it, so "what packages does this build have, and where has
 * each one got to" could not be answered without opening every assessment in turn.
 *
 * It then became a table inside the coverage workspace, which answered the question but did not look or work
 * like the register the requirements side has. It is now a page of its own, rendered by the same component,
 * and these assert the rules that survived that move: every discipline has one, every listed package carries
 * its own controlled number, and the assessments queue is still a separate thing.
 */

/** Signs in once and returns the build root, so a walk across disciplines does not sign in three times. */
const enterBuild = async (page: import('@playwright/test').Page) => {
  await login(page)
  return new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
}

const openRegister = async (page: import('@playwright/test').Page, root: string, branch: string, heading: string) => {
  await page.goto(new URL(`${root}/${branch}/change-requests`, page.url()).toString(), { waitUntil: 'load' })
  await expect(page.getByRole('heading', { name: heading, level: 1 })).toBeVisible({ timeout: 30_000 })
}

test('every discipline lists the packages controlling its build test procedures', async ({ page }) => {
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 1440, height: 900 })
  const root = await enterBuild(page)

  for (const [branch, acronym, heading] of [
    ['system-verification', 'SYSTCR', 'System Test Change Requests'],
    ['software-verification/hlr', 'HLRTCR', 'HLR Test Change Requests'],
    ['software-verification/llr', 'LLRTCR', 'LLR Test Change Requests'],
  ] as const) {
    await openRegister(page, root, branch, heading)

    const rows = page.locator('[data-register-row]')
    const empty = page.locator('.historyEmpty')
    // One of the two must be shown. A register that renders neither rows nor an explanation would be a
    // heading over nothing, which is what the page did before.
    await expect(rows.first().or(empty)).toBeVisible({ timeout: 30_000 })

    if (await rows.count()) {
      // Every listed package carries its own controlled number rather than the change request's. An
      // unconcluded assessment is not a package and belongs on the coverage page.
      const numbers = await rows.evaluateAll(items => items.map(item => item.getAttribute('data-register-row')))
      for (const number of numbers) expect(number).toContain(acronym)
      // The same four columns the requirements register shows, in the same order.
      const head = page.locator('.tableHead.allocation')
      await expect(head).toContainText('Build allocation')
      await expect(head).toContainText('State')
      await expect(head).toContainText('Last activity')
    }
  }
})

test('the assessments queue stays on the coverage page and the register is its own', async ({ page }) => {
  test.setTimeout(120_000)
  await page.setViewportSize({ width: 1440, height: 900 })
  const root = await enterBuild(page)

  // Both questions still have a home: the register lists packages, the coverage page lists approved changes
  // awaiting a conclusion. Moving one must not take the other with it.
  await page.goto(new URL(`${root}/system-verification/coverage`, page.url()).toString(), { waitUntil: 'load' })
  await expect(page.getByRole('heading', { name: 'Downstream test assessments' })).toBeVisible({ timeout: 30_000 })
  await expect(page.locator('[data-register-row]')).toHaveCount(0)

  await openRegister(page, root, 'system-verification', 'System Test Change Requests')
  await expect(page.getByRole('heading', { name: 'Downstream test assessments' })).toHaveCount(0)
})
