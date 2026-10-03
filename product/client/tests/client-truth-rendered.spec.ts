import { expect, test, type Page } from '@playwright/test'

// A owns wire validation in workspace-contract; this bounded composition catches loss of
// qualification between that decoder and the actual build card, which a rule assertion cannot see.
test('decoded server qualification reaches the actual released build card', async ({ page }, testInfo) => {
  await page.goto('/tests/fixtures/client-truth.html')
  const qualified = page.locator('[data-build-id="without-readiness"]')
  await expect(qualified.locator('.buildStatus')).toHaveText('Released · no readiness')
  await expect(qualified.locator('.buildLifecycleDescription')).toHaveText('Released without readiness evidence · read-only')
  for (const id of ['with-readiness', 'legacy']) {
    const card = page.locator(`[data-build-id="${id}"]`)
    await expect(card.locator('.buildStatus')).toHaveText('Released')
    await expect(card.locator('.buildLifecycleDescription')).toHaveText('Released build · read-only')
  }
  await page.screenshot({ path: testInfo.outputPath('qualified-builds.png') })
})

type Person = { id: string; userName: string; displayName: string; email: string; title: string; roles: string[] }
type ControlledDirectory = {
  requests: { url: string; read: boolean; resolve: (response: { ok: boolean; json: () => Promise<Person[]> }) => void;
    resolveBody?: (people: Person[]) => void }[]
}
const alice: Person = { id: 'alice', userName: 'alice', displayName: 'Alice Smith', email: '', title: 'Engineer', roles: ['SystemEngineer', 'Reviewer'] }
const bob: Person = { ...alice, id: 'bob', userName: 'bob', displayName: 'Bob Jones' }

async function picker(page: Page) {
  await page.clock.install({ time: new Date('2026-10-02T12:00:00Z') })
  await page.clock.pauseAt(new Date('2026-10-02T12:01:00Z'))
  // Control only the transport, never suggestion ownership. Production PersonPicker owns
  // debounce, request ordering, parsing, filtering and state. No backend or persistent state.
  await page.addInitScript(() => {
    const directory: ControlledDirectory = { requests: [] }
    Object.assign(window, { directory })
    const original = window.fetch.bind(window)
    window.fetch = (input, init) => {
      const url = String(input)
      if (!url.includes('/api/directory?')) return original(input, init)
      return new Promise(resolve => {
        directory.requests.push({ url, read: false, resolve: response => resolve(response as Response) })
      })
    }
  })
  await page.goto('/tests/fixtures/client-truth.html?picker')
  return page.getByRole('textbox', { name: 'Approver 1 search' })
}

async function requests(page: Page, count: number) {
  await expect.poll(() => page.evaluate(() => (window as unknown as { directory: ControlledDirectory }).directory.requests.length)).toBe(count)
}

async function deliver(page: Page, index: number, people: Person[]) {
  await page.evaluate(({ index, people }) => {
    const request = (window as unknown as { directory: ControlledDirectory }).directory.requests[index]
    request.resolve({ ok: true, json: async () => { request.read = true; return people } })
  }, { index, people })
  // Drain the response promise and React's scheduled paint; fake time also keeps the
  // next 150ms debounce pending. Assertions never depend on a wall-clock sleep.
  await page.clock.runFor(32)
}

test('PersonPicker keeps Bob after Bob completes and delayed Alice arrives', async ({ page }, testInfo) => {
  const input = await picker(page)
  await input.fill('alice')
  await page.clock.runFor(150)
  await requests(page, 1)
  await input.fill('bob')
  await page.clock.runFor(150)
  await requests(page, 2)
  await deliver(page, 1, [bob, { ...bob, id: 'excluded', userName: 'excluded' }, { ...bob, id: 'ineligible', userName: 'ineligible', roles: ['Unrelated'] }])
  await expect(page.locator('.personSuggestions button')).toHaveCount(1)
  await expect(page.getByRole('button', { name: /Bob Jones/ })).toBeVisible()
  await deliver(page, 0, [alice])
  await expect(input).toHaveValue('bob')
  await expect(page.getByRole('button', { name: /Bob Jones/ })).toBeVisible()
  await expect(page.getByRole('button', { name: /Alice Smith/ })).toHaveCount(0)
  await page.screenshot({ path: testInfo.outputPath('current-bob.png') })
  await page.getByRole('button', { name: /Bob Jones/ }).click()
  await expect(page.getByLabel('Selected reviewer')).toHaveText('bob')
})

test('PersonPicker invalidates previous query while the next search is still debouncing', async ({ page }) => {
  const input = await picker(page)
  await input.fill('alice')
  await page.clock.runFor(150)
  await requests(page, 1)
  await input.fill('bob')
  await deliver(page, 0, [alice])
  await requests(page, 1)
  await expect(page.locator('.personSuggestions button')).toHaveCount(0)
  await page.clock.runFor(150)
  await requests(page, 2)
  await deliver(page, 1, [bob])
  await expect(page.getByRole('button', { name: /Bob Jones/ })).toBeVisible()
  await input.fill('alice')
  await page.clock.runFor(150)
  await requests(page, 3)
  await input.fill('')
  await deliver(page, 2, [alice])
  await expect(page.locator('.personSuggestions button')).toHaveCount(0)
  await page.clock.runFor(150)
  await requests(page, 3)
  await expect(page.getByLabel('Selected reviewer')).toHaveText('')
  await input.fill('bob')
  await page.clock.runFor(150)
  await requests(page, 4)
  await page.getByRole('button', { name: 'Unmount picker' }).click()
  await deliver(page, 3, [bob])
  await expect(page.getByRole('textbox')).toHaveCount(0)
  await expect(page.locator('.personSuggestions button')).toHaveCount(0)
})

test('PersonPicker rejects an obsolete body even when its response headers were current', async ({ page }) => {
  const input = await picker(page)
  await input.fill('alice')
  await page.clock.runFor(150)
  await requests(page, 1)
  await page.evaluate(() => {
    const request = (window as unknown as { directory: ControlledDirectory }).directory.requests[0]
    request.resolve({ ok: true, json: () => {
      request.read = true
      return new Promise(resolve => { request.resolveBody = resolve })
    } })
  })
  await expect.poll(() => page.evaluate(() => (window as unknown as { directory: ControlledDirectory }).directory.requests[0].read)).toBe(true)
  await input.fill('bob')
  await page.clock.runFor(150)
  await requests(page, 2)
  await deliver(page, 1, [bob])
  await expect(page.getByRole('button', { name: /Bob Jones/ })).toBeVisible()
  await page.evaluate(people => (window as unknown as { directory: ControlledDirectory }).directory.requests[0].resolveBody!(people), [alice])
  await page.clock.runFor(32)
  await expect(page.getByRole('button', { name: /Bob Jones/ })).toBeVisible()
  await expect(page.getByRole('button', { name: /Alice Smith/ })).toHaveCount(0)
})

for (const scope of ['project', 'API', 'authority', 'roles']) {
  test(`PersonPicker clears old choices and rejects in-flight results when ${scope} changes`, async ({ page }) => {
    const input = await picker(page)
    await input.fill('alice')
    await page.clock.runFor(150)
    await requests(page, 1)
    await deliver(page, 0, [alice])
    await expect(page.getByRole('button', { name: /Alice Smith/ })).toBeVisible()
    await page.getByRole('button', { name: `Change ${scope}`, exact: true }).click()
    await expect(page.locator('.personSuggestions button')).toHaveCount(0)
    await page.clock.runFor(150)
    await requests(page, 2)
    await page.getByRole('button', { name: `Change ${scope}`, exact: true }).click()
    await expect(page.locator('.personSuggestions button')).toHaveCount(0)
    await deliver(page, 1, [alice])
    await expect(page.locator('.personSuggestions button')).toHaveCount(0)
    await page.clock.runFor(150)
    await requests(page, 3)
    const urls = await page.evaluate(() => (window as unknown as { directory: ControlledDirectory }).directory.requests.map(request => request.url))
    expect(urls[1]).not.toBe(urls[0])
    expect(urls[2]).toBe(urls[0])
    await deliver(page, 2, [bob])
    await expect(page.getByRole('button', { name: /Bob Jones/ })).toBeVisible()
  })
}
