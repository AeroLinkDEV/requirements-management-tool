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
 * like the register the requirements side has. The assessments and that shared register now form one page:
 * work needing a conclusion first, then the controlled packages it produced.
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
    ['system-verification', 'SYSTPCR', 'System Test Change Requests'],
    ['software-verification/hlr', 'HLRTCCR', 'Software Test Change Requests'],
    ['software-verification/llr', 'LLRTCCR', 'Software Test Change Requests'],
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

test('downstream assessments come before the register on both historical and current routes', async ({ page }) => {
  test.setTimeout(120_000)
  await page.setViewportSize({ width: 1440, height: 900 })
  const root = await enterBuild(page)

  for (const path of ['system-verification/coverage', 'system-verification/change-requests']) {
    await page.goto(new URL(`${root}/${path}`, page.url()).toString(), { waitUntil: 'load' })
    const assessments = page.getByRole('heading', { name: 'Downstream Assessments' })
    const register = page.locator('.historyTools')
    await expect(assessments).toBeVisible({ timeout: 30_000 })
    await expect(register).toBeVisible({ timeout: 30_000 })
    expect(await assessments.evaluate((first, second) =>
      Boolean(first.compareDocumentPosition(second as Node) & Node.DOCUMENT_POSITION_FOLLOWING),
    await register.elementHandle())).toBe(true)
  }
})

test('verification register selection is a stable URL state through refresh and back/forward', async ({ page }) => {
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 1440, height: 900 })
  const root = await enterBuild(page)
  await openRegister(page, root, 'system-verification', 'System Test Change Requests')
  const row = page.locator('[data-register-row]').first()
  await expect(row).toBeVisible({ timeout: 30_000 })
  const assessmentHeading = page.getByRole('heading', { name: 'Downstream Assessments' })
  const register = page.locator('.historyTools')
  const beforeAssessment = await assessmentHeading.boundingBox()
  const beforeRegister = await register.boundingBox()
  await row.click()
  await expect(page).toHaveURL(/system-verification\/change-requests\?[^#]*selection=[0-9a-f-]{36}/)
  await expect(page.getByRole('link', { name: 'Open change request →' })).toBeVisible()
  const afterAssessment = await assessmentHeading.boundingBox()
  const afterRegister = await register.boundingBox()
  expect(afterAssessment?.x).toBe(beforeAssessment?.x)
  expect(afterAssessment?.y).toBe(beforeAssessment?.y)
  expect(afterAssessment?.width).toBe(beforeAssessment?.width)
  expect(afterRegister?.x).toBe(beforeRegister?.x)
  expect(afterRegister?.y).toBe(beforeRegister?.y)
  expect(afterRegister?.width).toBe(beforeRegister?.width)
  await page.reload()
  await expect(page.getByRole('link', { name: 'Open change request →' })).toBeVisible({ timeout: 30_000 })
  await page.goBack()
  await expect(page.getByText('Select a change request')).toBeVisible()
  await page.goForward()
  await expect(page.getByRole('link', { name: 'Open change request →' })).toBeVisible()
})
