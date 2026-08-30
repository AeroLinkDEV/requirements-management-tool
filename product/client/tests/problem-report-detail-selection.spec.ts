import { expect, test } from '@playwright/test'
import { chooseCategory, login, selectProgram } from './auth'

/**
 * The detail pane must keep showing the record the reader selected.
 *
 * A queue page asks for the first record's detail on its own, and a refresh re-asks for whichever record
 * was selected when the refresh started. Those responses can arrive out of order: the pane once showed
 * the queue's first record again after the reader had opened another one, because whichever detail
 * response landed last took the pane — and a lifecycle button then acted on a record the reader had not
 * selected. That is the client half of issue #793's recurrence: the journey clicked Open against a
 * seeded Implementing record while its own record was the one selected in the queue and the address.
 *
 * This journey reproduces that ordering deterministically: the queue auto-selects its first record, a
 * search then commits and the refresh it triggers re-asks for that first record — those responses are
 * held back until after the reader has opened the second record, and the pane must still show the second
 * record when the held responses finally land.
 */
test('a Problem Report stays in the pane while an earlier selection detail response is still arriving', async ({ page }) => {
  test.setTimeout(120_000)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')

  await page.goto(new URL(`${root}/problem-reports`, page.url()).toString(), { waitUntil: 'load' })
  const createDraft = async (title: string) => {
    await page.getByRole('button', { name: '+ Record problem' }).click()
    const dialog = page.getByRole('dialog', { name: 'Record a problem' })
    await dialog.getByLabel('Title').fill(title)
    await dialog.getByRole('group', { name: 'Add content to Problem Description' }).getByRole('button', { name: 'Paragraph' }).click()
    await dialog.getByRole('textbox', { name: 'Problem Description paragraph 1' }).fill('The pane must keep showing the record the reader selected.')
    await chooseCategory(dialog, 'Code Issue — Functional Impact')
    await dialog.getByRole('button', { name: 'Save Draft PR' }).click()
    await expect(page.getByRole('heading', { name: title })).toBeVisible()
  }
  // Two records, so the queue holds a first row the page selects on its own and a second row to open.
  await createDraft(`Pane selection probe ${Date.now()}`)
  await createDraft(`Pane selection probe ${Date.now()}`)

  let releaseParked: (() => void) | undefined
  const parkedReleased = new Promise<void>(resolve => { releaseParked = resolve })
  let searchCommitted = false
  // The record the reader will open, learned from the queue once the search has committed; its own detail
  // request passes straight through while every earlier selection's re-fetch is held back.
  let openedDetailUrl: string | undefined
  const parkedUrls: string[] = []
  await page.route(
    url => {
      if (url.pathname === '/api/problem-reports' && url.searchParams.has('search')) searchCommitted = true
      return /\/api\/problem-reports\/[0-9a-f-]{36}$/.test(url.pathname)
    },
    async route => {
      if (searchCommitted && route.request().url() !== openedDetailUrl) {
        parkedUrls.push(route.request().url())
        await parkedReleased
      }
      await route.continue()
    },
  )

  // Navigating to the plain queue address (no record in the path) makes the page select its first record
  // on its own — the lowest-numbered one, the record the search refresh will then re-ask for.
  await page.goto(new URL(`${root}/problem-reports`, page.url()).toString(), { waitUntil: 'load' })
  const rows = page.locator('.prList > button')
  await expect(rows.nth(1), 'the queue holds at least two Problem Reports').toBeVisible()
  const firstTitle = await rows.nth(0).locator('b').textContent() as string
  const secondTitle = await rows.nth(1).locator('b').textContent() as string
  await expect(page.getByRole('heading', { name: firstTitle })).toBeVisible()

  await page.getByLabel('Search').fill(secondTitle)
  await expect.poll(() => searchCommitted, 'the committed search asked the queue').toBe(true)
  await expect.poll(() => parkedUrls.length, 'the search refresh re-asked for the previously selected record').toBeGreaterThan(0)
  const projectId = new URL(page.url()).pathname.split('/')[4]
  const queue = await (await page.request.get(`${process.env.AEROLINK_E2E_API_BASE ?? 'http://127.0.0.1:5082'}/api/problem-reports?projectId=${projectId}&search=${encodeURIComponent(secondTitle)}`)).json() as { items: { id: string }[] }
  openedDetailUrl = `${process.env.AEROLINK_E2E_API_BASE ?? 'http://127.0.0.1:5082'}/api/problem-reports/${queue.items[0].id}`

  await page.locator('.prList').getByText(secondTitle).click()
  await expect(page.getByRole('heading', { name: secondTitle })).toBeVisible()

  const lastParkedUrl = parkedUrls[parkedUrls.length - 1]
  const lastParkedResponse = page.waitForResponse(response => response.url() === lastParkedUrl)
  releaseParked!()
  await lastParkedResponse

  // The re-fetched detail for the previously selected record has now landed. It must not take the pane
  // back: the heading, the address and the lifecycle actions stay on the record the reader opened.
  await expect(page.getByRole('heading', { name: secondTitle })).toBeVisible()
})
