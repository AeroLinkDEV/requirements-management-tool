import { expect, test } from '@playwright/test'
import { chooseCategory, login, selectProgram } from './auth'

/**
 * The whole record, on both ways in, and the three controls that end an edit.
 *
 * A Problem Report used to open into a 720px column that showed a fraction of itself: Workaround, Root
 * cause, Effects and Containment were on the record and on neither form, or on one and not the other, and
 * the create endpoint had nowhere to put them even if a form had asked. Raising a report and correcting
 * one now show the same fields, because both read one list.
 */

const NARRATIVE = [
  'Analysis',
  'Root cause',
  'Effects',
  'Containment',
  'Workaround',
  'Corrective-action narrative',
  'System / aircraft impact',
]

/** Types into a document-like field's first paragraph; the block primitive is intentionally hidden. */
async function writeField(scope: import('@playwright/test').Locator, label: string, text: string) {
  await scope.getByRole('textbox', { name: `${label} paragraph 1` }).fill(text)
}

test('raising a Problem Report shows every field it has, and saves them all', async ({ page }) => {
  test.setTimeout(240_000)
  const stamp = Date.now()
  const title = `Whole record on create ${stamp}`

  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
  await page.goto(new URL(`${root}/problem-reports`, page.url()).toString(), { waitUntil: 'load' })

  await page.getByRole('button', { name: '+ Record problem' }).click()
  const raise = page.getByRole('dialog', { name: 'Record a problem' })

  // Every field is present without expanding anything. There is no <details> left to open.
  for (const label of NARRATIVE)
    await expect(raise.getByRole('group', { name: `Add content to ${label}` })).toBeVisible()
  await expect(raise.locator('details')).toHaveCount(0)

  await raise.getByLabel('Title').fill(title)
  await writeField(raise, 'Problem Description', 'The tone follows the disconnect by about a second.')
  await writeField(raise, 'Workaround', 'Use the redundant aural channel.')
  await writeField(raise, 'Containment', 'Crews are briefed before dispatch.')
  await chooseCategory(raise, 'Code Issue — Functional Impact')
  await raise.getByRole('button', { name: 'Save Draft PR' }).click()

  // The fields a create form could not previously reach are on the record.
  await expect(page.getByRole('heading', { name: title })).toBeVisible({ timeout: 30_000 })
  const narrative = page.locator('.prNarrative')
  await expect(narrative).toContainText('Use the redundant aural channel.')
  await expect(narrative).toContainText('Crews are briefed before dispatch.')

  await page.reload({ waitUntil: 'load' })
  await expect(page.locator('.prNarrative')).toContainText('Use the redundant aural channel.', { timeout: 30_000 })
})

test('the checkout editor shows the whole record and its three closing controls', async ({ page }) => {
  test.setTimeout(300_000)
  const stamp = Date.now()
  const title = `Whole record on checkout ${stamp}`

  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
  await page.goto(new URL(`${root}/problem-reports`, page.url()).toString(), { waitUntil: 'load' })

  await page.getByRole('button', { name: '+ Record problem' }).click()
  const raise = page.getByRole('dialog', { name: 'Record a problem' })
  await raise.getByLabel('Title').fill(title)
  await writeField(raise, 'Problem Description', 'The tone follows the disconnect.')
  await chooseCategory(raise, 'Code Issue — Functional Impact')
  await raise.getByRole('button', { name: 'Save Draft PR' }).click()
  await expect(page.getByRole('heading', { name: title })).toBeVisible({ timeout: 30_000 })

  await page.getByRole('button', { name: 'Check out & edit' }).click()
  const editor = page.getByRole('dialog', { name: /^Edit PR-/ })
  await expect(editor).toBeVisible({ timeout: 30_000 })

  // Same fields as the create form, from the same list.
  for (const label of NARRATIVE)
    await expect(editor.getByRole('group', { name: `Add content to ${label}` })).toBeVisible()

  // Nothing changed yet: Save is unavailable and the window offers to Close, not to commit.
  await expect(editor.getByRole('button', { name: 'Save', exact: true })).toBeDisabled()
  await expect(editor.locator('.prCheckoutFoot').getByRole('button', { name: 'Close', exact: true })).toBeVisible()
  await expect(editor.getByRole('button', { name: 'Save and check in' })).toHaveCount(0)

  // One edit, and the footer changes what it offers: there is now something a check-in would commit.
  await writeField(editor, 'Root cause', 'The tone is queued behind the annunciator.')
  await expect(editor.getByRole('button', { name: 'Save and check in' })).toBeVisible()
  await expect(editor.locator('.prCheckoutFoot').getByRole('button', { name: 'Close', exact: true })).toHaveCount(0)

  // A saved snapshot is not a committed record. Whether it was written by the autosave or by pressing
  // Save, the window stays open and the record still says nothing about the edit — deliberately not
  // asserting the transient "unsaved" state, which the autosave clears within a second.
  await expect(editor.getByText(/✓ Saved /)).toBeVisible({ timeout: 30_000 })
  await expect(editor).toBeVisible()
  await expect(page.locator('.prNarrative')).not.toContainText('queued behind the annunciator')

  await editor.getByRole('button', { name: 'Save and check in' }).click()
  await expect(editor).toHaveCount(0, { timeout: 30_000 })
  await expect(page.locator('.prNarrative')).toContainText('The tone is queued behind the annunciator.', { timeout: 30_000 })

  // One editing session is one entry in History, however many times Save was pressed.
  await page.getByRole('button', { name: /^History/ }).click()
  await expect(page.locator('.prTimeline article').filter({ hasText: 'Details Checked In' })).toHaveCount(1)
})

test('a delayed inline image upload keeps create save behind the pending upload', async ({ page }) => {
  test.setTimeout(240_000)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
  await page.goto(new URL(`${root}/problem-reports`, page.url()).toString(), { waitUntil: 'load' })
  await page.getByRole('button', { name: '+ Record problem' }).click()
  const raise = page.getByRole('dialog', { name: 'Record a problem' })
  await raise.getByLabel('Title').fill(`Delayed image upload ${Date.now()}`)
  await writeField(raise, 'Problem Description', 'The screenshot is still being stored.')

  let uploadStarted = () => {}
  const started = new Promise<void>(resolve => { uploadStarted = resolve })
  let releaseUpload = () => {}
  const gate = new Promise<void>(resolve => { releaseUpload = resolve })
  await page.route('**/api/content/images', async route => {
    uploadStarted()
    await gate
    await route.continue()
  })
  const imageInput = raise.locator('.richEditor').first().locator('input[type=file]')
  await imageInput.setInputFiles({
    name: 'recovery.png',
    mimeType: 'image/png',
    // A valid 1x1 PNG keeps this a real upload while the route is held in-flight.
    buffer: Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=', 'base64'),
  })
  await started
  const save = raise.getByRole('button', { name: /Save Draft PR|Waiting for image/ })
  await expect(save).toBeDisabled()
  await expect(raise.getByText(/Storing .*inline image/)).toBeVisible()

  releaseUpload()
  await expect(raise.getByRole('button', { name: 'Save Draft PR', exact: true })).toBeEnabled({ timeout: 30_000 })
  await page.unroute('**/api/content/images')
})

test('a delayed inline image upload keeps checkout save and check-in behind the pending upload', async ({ page }) => {
  test.setTimeout(300_000)
  const stamp = Date.now()
  const title = `Checkout image upload ${stamp}`

  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
  await page.goto(new URL(`${root}/problem-reports`, page.url()).toString(), { waitUntil: 'load' })

  await page.getByRole('button', { name: '+ Record problem' }).click()
  const raise = page.getByRole('dialog', { name: 'Record a problem' })
  await raise.getByLabel('Title').fill(title)
  await writeField(raise, 'Problem Description', 'The checked-out screenshot is still being stored.')
  await chooseCategory(raise, 'Code Issue — Functional Impact')
  await raise.getByRole('button', { name: 'Save Draft PR' }).click()
  await expect(page.getByRole('heading', { name: title })).toBeVisible({ timeout: 30_000 })
  await page.getByRole('button', { name: 'Check out & edit' }).click()
  const editor = page.getByRole('dialog', { name: /^Edit PR-/ })
  await expect(editor).toBeVisible({ timeout: 30_000 })

  let uploadStarted = () => {}
  const started = new Promise<void>(resolve => { uploadStarted = resolve })
  let released = false
  let releaseUpload = () => {}
  const release = () => {
    if (released) return
    released = true
    releaseUpload()
  }
  const gate = new Promise<void>(resolve => { releaseUpload = resolve })
  await page.route('**/api/content/images', async route => {
    uploadStarted()
    await gate
    await route.continue()
  })
  try {
    const imageInput = editor.locator('.richEditor').first().locator('input[type=file]')
    await imageInput.setInputFiles({
      name: 'checkout-recovery.png',
      mimeType: 'image/png',
      buffer: Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=', 'base64'),
    })
    await started
    await expect(editor.locator('.workspaceNotice')).toHaveText(/Storing 1 inline image/)
    await expect(editor.locator('.prCheckoutFoot').getByRole('button', { name: 'Save', exact: true })).toBeDisabled()
    await expect(editor.locator('.prCheckoutFoot').getByRole('button', { name: 'Save and check in' })).toHaveCount(0)

    release()
    await expect(editor.locator('.prCheckoutFoot').getByRole('button', { name: 'Save and check in' })).toBeEnabled({ timeout: 30_000 })
    await expect(editor.locator('.workspaceNotice')).toHaveCount(0)
  } finally {
    release()
    await page.unroute('**/api/content/images')
  }
})

test('Problem Report output links distinguish the current record from its exact historical revision', async ({ page }) => {
  test.setTimeout(240_000)
  const title = `Output identity ${Date.now()}`
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
  await page.goto(new URL(`${root}/problem-reports`, page.url()).toString(), { waitUntil: 'load' })
  await page.getByRole('button', { name: '+ Record problem' }).click()
  const raise = page.getByRole('dialog', { name: 'Record a problem' })
  await raise.getByLabel('Title').fill(title)
  await writeField(raise, 'Problem Description', 'The rendered output must retain its exact record identity.')
  await chooseCategory(raise, 'Code Issue — Functional Impact')
  await raise.getByRole('button', { name: 'Save Draft PR' }).click()
  await expect(page.getByRole('heading', { name: title })).toBeVisible({ timeout: 30_000 })

  await expect(page.getByRole('link', { name: 'Download DOCX' }))
    .toHaveAttribute('href', /\/api\/problem-reports\/[0-9a-f-]{36}\/download\?format=docx$/)
  await expect(page.getByRole('link', { name: 'Download PDF' }))
    .toHaveAttribute('href', /\/api\/problem-reports\/[0-9a-f-]{36}\/download\?format=pdf$/)

  await page.getByRole('button', { name: /^History/ }).click()
  await expect(page.getByRole('link', { name: 'DOCX · rev 00' }))
    .toHaveAttribute('href', /\/api\/problem-reports\/[0-9a-f-]{36}\/download\?format=docx&revision=0$/)
  await expect(page.getByRole('link', { name: 'PDF · rev 00' }))
    .toHaveAttribute('href', /\/api\/problem-reports\/[0-9a-f-]{36}\/download\?format=pdf&revision=0$/)
})
