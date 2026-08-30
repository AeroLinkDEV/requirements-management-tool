import { expect, test, type Page } from '@playwright/test'
import { apiBase, chooseCategory, login, selectProgram } from './auth'

/**
 * The detail pane, the address and the lifecycle controls must always be about the same Problem Report —
 * the one the reader selected last.
 *
 * A queue page asks for the first record's detail on its own, and a refresh re-asks for whichever record
 * was selected when the refresh started. Those responses can arrive out of order: the pane once showed
 * the queue's first record again after the reader had opened another one, because whichever detail
 * response landed last took the pane — and a lifecycle button then acted on a record the reader had not
 * selected. That is the client half of issue #793's recurrence: the journey clicked Open against a
 * seeded Implementing record while its own record was the one selected in the queue and the address.
 *
 * These journeys reproduce the dangerous orderings deterministically, holding request responses back
 * with Playwright routing until the journey has made the reader's newer decision, and then require the
 * pane, the address and the lifecycle state to still name the record the reader opened.
 *
 * Every journey names its own records with one run-unique stamp and isolates them with the queue's
 * search, so the scenario never depends on queue position or on the state of records other journeys
 * created. Every identity under assertion comes from the address, which the product writes when a
 * record is opened — never from a fuzzy title search.
 */

const detailUrl = (id: string) => `${apiBase}/api/problem-reports/${id}`

const createDraft = async (page: Page, title: string) => {
  await page.getByRole('button', { name: '+ Record problem' }).click()
  const dialog = page.getByRole('dialog', { name: 'Record a problem' })
  await dialog.getByLabel('Title').fill(title)
  await dialog.getByRole('group', { name: 'Add content to Problem Description' }).getByRole('button', { name: 'Paragraph' }).click()
  await dialog.getByRole('textbox', { name: 'Problem Description paragraph 1' }).fill('The pane must keep showing the record the reader selected.')
  await chooseCategory(dialog, 'Code Issue — Functional Impact')
  await dialog.getByRole('button', { name: 'Save Draft PR' }).click()
  await expect(page.getByRole('heading', { name: title })).toBeVisible()
}

/**
 * Opens a queue row and waits for the pane to settle on the record it names; the identity then comes
 * from the address, which the product writes when the record is opened. Re-opening the record the pane
 * already shows is fine — the address already names it.
 */
const openRow = async (page: Page, title: string) => {
  await page.locator('.prList').getByText(title).click()
  await expect(page.getByRole('heading', { name: title })).toBeVisible()
  return recordIdFromAddress(page)
}

const recordIdFromAddress = async (page: Page) => {
  const pathname = new URL(page.url()).pathname
  expect(pathname, 'the address names the opened record').toMatch(/\/problem-reports\/[0-9a-f-]{36}$/)
  return pathname.split('/').pop()!
}

const queueOf = (page: Page) => page.locator('.prList > button')

/**
 * Race A — an implicit queue refresh re-asks for the previously selected record while the reader is
 * opening another one. Its late response must not take the pane, the address or the lifecycle controls.
 */
test('a Problem Report stays in the pane while an earlier selection detail response is still arriving', async ({ page }) => {
  test.setTimeout(120_000)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
  const stamp = Date.now()
  const firstTitle = `Pane selection probe ${stamp} first`
  const secondTitle = `Pane selection probe ${stamp} second`

  await page.goto(new URL(`${root}/problem-reports`, page.url()).toString(), { waitUntil: 'load' })
  await createDraft(page, firstTitle)
  await createDraft(page, secondTitle)

  // Isolate this journey's records; the pane is still on whatever the queue auto-selected first.
  await page.getByLabel('Search').fill(stamp.toString())
  const rows = queueOf(page)
  await expect(rows.nth(1), 'the queue holds both of this journey\'s records').toBeVisible()
  // Name both records in the address, then leave the pane on the first record — the one the refresh
  // will re-ask for while the reader is opening the second.
  const secondId = await openRow(page, secondTitle)
  await openRow(page, firstTitle)
  const firstId = await recordIdFromAddress(page)
  await expect(page.getByRole('heading', { name: firstTitle })).toBeVisible()

  let releaseParked: (() => void) | undefined
  const parkedReleased = new Promise<void>(resolve => { releaseParked = resolve })
  const parkedUrls: string[] = []
  // The record the reader is about to open passes straight through; every other detail response waits.
  await page.route(
    url => /\/api\/problem-reports\/[0-9a-f-]{36}$/.test(url.pathname),
    async route => {
      if (route.request().url() !== detailUrl(secondId)) {
        parkedUrls.push(route.request().url())
        await parkedReleased
      }
      await route.continue()
    },
  )

  // Re-asking the queue commits a refresh that re-fetches the currently selected first record — the
  // response is held back before it can apply.
  await page.getByRole('button', { name: 'Apply filters' }).click()
  await expect.poll(() => parkedUrls.length, 'the refresh re-asked for the previously selected record').toBeGreaterThan(0)

  // The reader opens the second record while the first record's re-fetch is still in flight.
  await page.locator('.prList').getByText(secondTitle).click()
  await expect(page.getByRole('heading', { name: secondTitle })).toBeVisible()

  const lastParkedResponse = page.waitForResponse(response => response.url() === parkedUrls[parkedUrls.length - 1])
  releaseParked!()
  await lastParkedResponse

  // The re-fetched detail for the previously selected record has now landed. It must not take the pane
  // back: the heading, the address and the lifecycle actions stay on the record the reader opened.
  await expect(page.getByRole('heading', { name: secondTitle })).toBeVisible()
  // Pane, address and lifecycle controls must all still name the opened record — #793 was dangerous
  // precisely because the queue and the address named one record while the pane named another.
  await expect(new URL(page.url()).pathname, 'the address stays on the opened record').toContain(secondId)
})

/**
 * Race B — an open that fails after the reader has already opened a different record. The stale failure
 * must not revert the newer selection's identity, and its error must not be reported as the current one.
 */
test('a failed open the reader already superseded must not revert the selection or report itself', async ({ page }) => {
  test.setTimeout(120_000)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
  const stamp = Date.now()
  const supersededTitle = `Pane selection probe ${stamp} superseded`
  const latestTitle = `Pane selection probe ${stamp} latest`

  await page.goto(new URL(`${root}/problem-reports`, page.url()).toString(), { waitUntil: 'load' })
  await createDraft(page, supersededTitle)
  await createDraft(page, latestTitle)

  await page.getByLabel('Search').fill(stamp.toString())
  const rows = queueOf(page)
  await expect(rows.nth(1), 'the queue holds both of this journey\'s records').toBeVisible()
  const supersededId = await openRow(page, supersededTitle)
  const latestId = await openRow(page, latestTitle)
  await expect(page.getByRole('heading', { name: latestTitle })).toBeVisible()

  const parkedUrls: string[] = []
  let releaseParked: (() => void) | undefined
  const parkedReleased = new Promise<void>(resolve => { releaseParked = resolve })
  await page.route(url => url.pathname === `/api/problem-reports/${supersededId}`, async route => {
    parkedUrls.push(route.request().url())
    await parkedReleased
    await route.abort('failed')
  })

  // Re-open the first record and hold its response; the reader then opens the second record, and only
  // afterwards does the held request fail.
  await page.locator('.prList').getByText(supersededTitle).click()
  await expect.poll(() => parkedUrls.length, 'the superseded open asked for its record').toBeGreaterThan(0)
  await page.locator('.prList').getByText(latestTitle).click()
  await expect(page.getByRole('heading', { name: latestTitle })).toBeVisible()

  releaseParked!()
  // A superseded failure is not the current request's error: the pane must not report it.
  await expect(page.locator('.workspaceError')).toHaveCount(0)

  // The identity the queue re-asks for must still be the reader's record: re-commit the queue and
  // require the pane to stay on the record the reader opened last.
  await page.getByRole('button', { name: 'Apply filters' }).click()
  await expect(page.getByRole('heading', { name: latestTitle })).toBeVisible()
  await expect(new URL(page.url()).pathname, 'the address stays on the opened record').toContain(latestId)
})

/**
 * Race C — a lifecycle action's own refresh for the record it acted on arrives after the reader has
 * opened a different record. The mutation's refresh must not drag the pane back.
 */
test('a lifecycle action refresh cannot drag the pane back to the record that was acted on', async ({ page }) => {
  test.setTimeout(120_000)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
  const stamp = Date.now()
  const actedTitle = `Pane selection probe ${stamp} acted`
  const openedTitle = `Pane selection probe ${stamp} opened`

  await page.goto(new URL(`${root}/problem-reports`, page.url()).toString(), { waitUntil: 'load' })
  await createDraft(page, actedTitle)
  await createDraft(page, openedTitle)

  await page.getByLabel('Search').fill(stamp.toString())
  const rows = queueOf(page)
  await expect(rows.nth(1), 'the queue holds both of this journey\'s records').toBeVisible()
  const actedId = await openRow(page, actedTitle)
  await expect(page.getByRole('heading', { name: actedTitle })).toBeVisible()

  const parkedUrls: string[] = []
  let releaseParked: (() => void) | undefined
  const parkedReleased = new Promise<void>(resolve => { releaseParked = resolve })
  let transitionSeen = false
  await page.route(
    url => /\/api\/problem-reports(\/|$)/.test(url.pathname),
    async route => {
      const request = route.request()
      if (request.method() === 'POST' && request.url().endsWith('/transition')) {
        transitionSeen = true
        await route.continue()
        return
      }
      // The action's own re-fetch of the acted-on record is the one held back; everything else — the
      // acted-on record's earlier detail load, the queue, the opened record — passes through.
      if (transitionSeen && request.url() === detailUrl(actedId)) {
        parkedUrls.push(request.url())
        await parkedReleased
      }
      await route.continue()
    },
  )

  // The acted-on record transitions; its re-fetch is held back before it can apply.
  await page.locator('.prFlow').getByRole('button', { name: 'Ready for SCCB →', exact: true }).click()
  await page.waitForResponse(response => response.request().method() === 'POST' && response.url().endsWith('/transition'))
  await expect.poll(() => parkedUrls.length, 'the action refresh re-asked for the acted-on record').toBeGreaterThan(0)

  // The reader opens a different record while the action's refresh is still in flight.
  await openRow(page, openedTitle)
  const openedId = await recordIdFromAddress(page)
  await expect(page.getByRole('heading', { name: openedTitle })).toBeVisible()

  const lastParkedResponse = page.waitForResponse(response => response.url() === parkedUrls[parkedUrls.length - 1])
  releaseParked!()
  await lastParkedResponse

  // The acted-on record's re-fetch has now landed. The reader's newer decision wins: pane, address and
  // lifecycle state stay on the opened record — a Draft, not the acted-on record's Ready for SCCB.
  await expect(page.getByRole('heading', { name: openedTitle })).toBeVisible()
  await expect(page.locator('.prState')).toHaveText('Draft')
  await expect(new URL(page.url()).pathname, 'the address stays on the opened record').toContain(openedId)
})

/**
 * Race D — two rapid row selections whose responses arrive in the opposite order. The reader's last
 * click wins, even though the earlier click's response landed later.
 */
test('the last record the reader clicked wins when two open responses arrive in the opposite order', async ({ page }) => {
  test.setTimeout(120_000)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
  const stamp = Date.now()
  const earlierTitle = `Pane selection probe ${stamp} earlier`
  const latestTitle = `Pane selection probe ${stamp} latest`

  await page.goto(new URL(`${root}/problem-reports`, page.url()).toString(), { waitUntil: 'load' })
  await createDraft(page, earlierTitle)
  await createDraft(page, latestTitle)

  await page.getByLabel('Search').fill(stamp.toString())
  const rows = queueOf(page)
  await expect(rows.nth(1), 'the queue holds both of this journey\'s records').toBeVisible()
  const earlierId = await openRow(page, earlierTitle)

  const parkedUrls: string[] = []
  let releaseParked: (() => void) | undefined
  const parkedReleased = new Promise<void>(resolve => { releaseParked = resolve })
  await page.route(url => url.pathname === `/api/problem-reports/${earlierId}`, async route => {
    parkedUrls.push(route.request().url())
    await parkedReleased
    await route.continue()
  })

  await page.locator('.prList').getByText(earlierTitle).click()
  await expect.poll(() => parkedUrls.length, 'the earlier open asked for its record').toBeGreaterThan(0)
  const latestId = await openRow(page, latestTitle)
  await expect(page.getByRole('heading', { name: latestTitle })).toBeVisible()

  const lastParkedResponse = page.waitForResponse(response => response.url() === parkedUrls[parkedUrls.length - 1])
  releaseParked!()
  await lastParkedResponse

  // The earlier click's response landed after the later click's. The later click still wins.
  await expect(page.getByRole('heading', { name: latestTitle })).toBeVisible()
  await expect(new URL(page.url()).pathname, 'the address stays on the opened record').toContain(latestId)
})

/**
 * Race F — the queue clears (the selected record excluded by a target-build filter with nothing
 * else matching) while an open is still in flight. The late open response must not resurrect the
 * excluded record against the active filter.
 */

/**
 * Race E — a refresh commits the very record a held open asked for. The commit must carry the
 * address with it, and the duplicate open's eventual failure must neither revert the selection
 * nor report an error for a record that is already on screen.
 */
test('a refresh committing a held open\'s record keeps the address aligned and the duplicate failure invisible', async ({ page }) => {
  test.setTimeout(120_000)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
  const stamp = Date.now()
  const firstTitle = `Pane selection probe ${stamp} first`
  const secondTitle = `Pane selection probe ${stamp} second`

  await page.goto(new URL(`${root}/problem-reports`, page.url()).toString(), { waitUntil: 'load' })
  await createDraft(page, firstTitle)
  await createDraft(page, secondTitle)

  await page.getByLabel('Search').fill(stamp.toString())
  const rows = queueOf(page)
  await expect(rows.nth(1), 'the queue holds both of this journey\'s records').toBeVisible()
  // Name both records in the address, then leave the pane on the first record.
  const secondId = await openRow(page, secondTitle)
  await openRow(page, firstTitle)
  const firstId = await recordIdFromAddress(page)
  await expect(page.getByRole('heading', { name: firstTitle })).toBeVisible()

  // Hold the open's own request for the second record; every later request for it passes through.
  const parkedUrls: string[] = []
  let releaseParked: (() => void) | undefined
  const parkedReleased = new Promise<void>(resolve => { releaseParked = resolve })
  let heldServed = false
  await page.route(url => url.pathname === `/api/problem-reports/${secondId}`, async route => {
    if (heldServed) {
      await route.continue()
      return
    }
    heldServed = true
    parkedUrls.push(route.request().url())
    await parkedReleased
    await route.abort('failed')
  })

  // The reader re-opens the second record; its response is held. Re-asking the queue then commits a
  // refresh that serves the same record — and must carry the address from the first record to it.
  await page.locator('.prList').getByText(secondTitle).click()
  await expect.poll(() => parkedUrls.length, 'the held open asked for its record').toBeGreaterThan(0)
  await page.getByRole('button', { name: 'Apply filters' }).click()
  await expect(page.getByRole('heading', { name: secondTitle })).toBeVisible()
  await expect(new URL(page.url()).pathname, 'the address follows the committed record').toContain(secondId)
  await expect(new URL(page.url()).pathname, 'the address left the previously applied record').not.toContain(firstId)

  // The held duplicate then fails. The record is already committed, so the failure stays invisible
  // and must not drag the selection back to the record that was applied when the open began.
  releaseParked!()
  await expect(page.locator('.workspaceError')).toHaveCount(0)
  await expect(page.getByRole('heading', { name: secondTitle })).toBeVisible()

  // The identity the queue re-asks for is still the committed record.
  await page.getByRole('button', { name: 'Apply filters' }).click()
  await expect(page.getByRole('heading', { name: secondTitle })).toBeVisible()
  await expect(new URL(page.url()).pathname, 'the address stays on the committed record').toContain(secondId)
})
test('a queue clear committed while an open is in flight supersedes that open', async ({ page }) => {
  test.setTimeout(120_000)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
  const stamp = Date.now()
  const heldTitle = `Pane selection probe ${stamp} held`

  await page.goto(new URL(`${root}/problem-reports`, page.url()).toString(), { waitUntil: 'load' })
  await createDraft(page, heldTitle)
  await page.getByLabel('Search').fill(stamp.toString())
  await expect(queueOf(page).nth(0)).toBeVisible()
  const heldId = await openRow(page, heldTitle)
  await expect(page.getByRole('heading', { name: heldTitle })).toBeVisible()

  const parkedUrls: string[] = []
  let releaseParked: (() => void) | undefined
  const parkedReleased = new Promise<void>(resolve => { releaseParked = resolve })
  let heldServed = false
  await page.route(url => url.pathname === `/api/problem-reports/${heldId}`, async route => {
    if (heldServed) {
      await route.continue()
      return
    }
    heldServed = true
    parkedUrls.push(route.request().url())
    await parkedReleased
    await route.continue()
  })

  // The reader re-opens the record; its response is held while the target-build filter changes to a
  // build that excludes it and matches nothing else.
  await page.locator('.prList').getByText(heldTitle).click()
  await expect.poll(() => parkedUrls.length, 'the held open asked for its record').toBeGreaterThan(0)
  await page.locator('.prFilters label').filter({ hasText: 'Target build' }).getByRole('combobox').selectOption({ label: '1.5 · released' })

  // The refresh cannot serve the held record under the new filter and no fallback exists, so the
  // queue clears — pane and address together.
  await expect(page.locator('.prBlank')).toBeVisible()
  await expect(new URL(page.url()).pathname, 'the address no longer names the excluded record').not.toContain(heldId)

  // The held open's response lands after the clear committed. The excluded record stays excluded.
  const heldResponse = page.waitForResponse(response => response.url() === parkedUrls[parkedUrls.length - 1])
  releaseParked!()
  await heldResponse
  await expect(page.locator('.prBlank')).toBeVisible()
  await expect(new URL(page.url()).pathname, 'the address still names no excluded record').not.toContain(heldId)
})
