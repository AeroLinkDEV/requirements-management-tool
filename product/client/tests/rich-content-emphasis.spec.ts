import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, chooseCategory, login, selectProgram, showcaseSeed } from './auth'

/** Selects an exact substring of a contenteditable body by character offset. */
async function selectWithin(page: import('@playwright/test').Page,
  body: import('@playwright/test').Locator, needle: string) {
  await body.evaluate((root, text) => {
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT)
    let node = walker.nextNode()
    while (node) {
      const index = (node.textContent ?? '').indexOf(text)
      if (index >= 0) {
        const range = document.createRange()
        range.setStart(node, index)
        range.setEnd(node, index + text.length)
        const selection = document.getSelection()
        selection?.removeAllRanges()
        selection?.addRange(range)
        return
      }
      node = walker.nextNode()
    }
    throw new Error(`${text} is not in the editor`)
  }, needle)
  await page.waitForTimeout(50)
}

/**
 * Pastes text without the real clipboard, which the test browser refuses write permission for. This
 * dispatches the same event the browser would, carrying only text/plain — which is exactly the shape a
 * paste of foreign markup arrives in once the editor has asked for text.
 */
async function pasteText(body: import('@playwright/test').Locator, text: string) {
  await body.evaluate((root, value) => {
    const data = new DataTransfer()
    data.setData('text/plain', value)
    root.dispatchEvent(new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true }))
  }, text)
}

/**
 * Emphasis inside authored content, and the property it must not cost.
 *
 * The content model is structure rather than markup because this content is written by one engineer and
 * read by the approver who signs for it — an approver whose session can be driven by the content they are
 * approving is a signature that means nothing. These two journeys cover both halves of that: emphasis a
 * person applied survives the controlled round trip, and text that looks like markup stays text.
 */

test('emphasis applied in the browser survives check-in and reload', async ({ page }) => {
  test.setTimeout(240_000)
  const stamp = Date.now()
  const title = `Emphasis round trip ${stamp}`

  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
  const problemReports = new URL(`${root}/problem-reports`, page.url()).toString()
  await page.goto(problemReports, { waitUntil: 'load' })

  await page.getByRole('button', { name: '+ Record problem' }).click()
  const raise = page.getByRole('dialog', { name: 'Record a problem' })
  await raise.getByLabel('Title').fill(title)

  const body = raise.getByRole('textbox', { name: 'Problem Description paragraph 1' })
  // The document-like editor owns an initial paragraph directly; fill that paragraph rather than
  // manufacturing a Paragraph block through the old block-model toolbar.
  await body.fill('The tone is late on every approach.')
  await expect(body).toHaveText('The tone is late on every approach.')
  // Emphasis applies to a selection, which is how the toolbar works: select the word, press Bold.
  await selectWithin(page, body, 'late')
  await body.locator('xpath=..').getByRole('group', { name: 'Emphasis for Problem Description paragraph 1' })
    .getByRole('button', { name: 'Bold', exact: true }).click()

  await expect(body.locator('strong')).toHaveText('late')
  await chooseCategory(raise, 'Code Issue — Functional Impact')
  await raise.getByRole('button', { name: 'Save Draft PR' }).click()

  // The record renders the emphasis, and the whole sentence is still the readable text.
  await expect(page.getByRole('heading', { name: title })).toBeVisible({ timeout: 30_000 })
  const description = page.locator('.prNarrative article').first()
  await expect(description.locator('strong')).toHaveText('late')
  await expect(description).toContainText('The tone is late on every approach.')

  // A record, not a screen state.
  await page.reload({ waitUntil: 'load' })
  await expect(page.locator('.prNarrative article').first().locator('strong')).toHaveText('late', { timeout: 30_000 })
})

test('text that looks like markup is stored and shown as text', async ({ page, request }) => {
  test.setTimeout(240_000)
  await apiLogin(request)
  const showcase = await showcaseSeed(request)
  const stamp = Date.now()
  const title = `Hostile content ${stamp}`
  const hostile = '<img src=x onerror=alert(1)><script>alert(2)</script>'

  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
  await page.goto(new URL(`${root}/problem-reports`, page.url()).toString(), { waitUntil: 'load' })

  // Anything the page executed would surface here rather than passing silently.
  const alerts: string[] = []
  page.on('dialog', async dialog => { alerts.push(dialog.message()); await dialog.dismiss() })
  const pageErrors: string[] = []
  page.on('pageerror', error => pageErrors.push(error.message))

  await page.getByRole('button', { name: '+ Record problem' }).click()
  const raise = page.getByRole('dialog', { name: 'Record a problem' })
  await raise.getByLabel('Title').fill(title)

  const body = raise.getByRole('textbox', { name: 'Problem Description paragraph 1' })
  await body.click()
  // Pasted, because paste is the realistic way foreign markup arrives, and the handler that reads it as
  // text/plain is the first of the two defences this is checking.
  await pasteText(body, hostile)
  await expect(body).toHaveText(hostile)
  // Pasting markup creates no elements: it is one text node, and nothing was parsed.
  expect(await body.locator('img, script').count()).toBe(0)

  // Emphasise it too: hostile text that carries a mark is the harder case, because it exercises the
  // run split as well as the text itself.
  await selectWithin(page, body, hostile)
  await body.locator('xpath=..').getByRole('group', { name: 'Emphasis for Problem Description paragraph 1' })
    .getByRole('button', { name: 'Bold', exact: true }).click()
  await chooseCategory(raise, 'Code Issue — Functional Impact')
  await raise.getByRole('button', { name: 'Save Draft PR' }).click()

  await expect(page.getByRole('heading', { name: title })).toBeVisible({ timeout: 30_000 })
  const description = page.locator('.prNarrative article').first()
  await expect(description).toContainText(hostile)
  expect(await description.locator('img, script').count()).toBe(0)

  await page.reload({ waitUntil: 'load' })
  await expect(page.locator('.prNarrative article').first()).toContainText(hostile, { timeout: 30_000 })
  expect(await page.locator('.prNarrative img, .prNarrative script').count()).toBe(0)

  // The stored record is text too, not markup that merely happens to render safely today.
  const list = await request.get(`${apiBase}/api/problem-reports?projectId=${showcase.projectId}&search=${encodeURIComponent(title)}`)
  expect(list.ok(), await list.text()).toBeTruthy()
  const found = (await list.json()).items[0] as { id: string }
  const detail = await request.get(`${apiBase}/api/problem-reports/${found.id}`)
  const stored = (await detail.json()).problemRich as string
  expect(stored).toContain('"runs"')
  expect(JSON.parse(stored).blocks[0].runs[0].text).toBe(hostile)

  expect(alerts, `content executed: ${alerts.join(', ')}`).toEqual([])
  expect(pageErrors.filter(message => message.includes('alert'))).toEqual([])
})
