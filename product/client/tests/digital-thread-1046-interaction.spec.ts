import { expect, test, type Page, type TestInfo } from '@playwright/test'
import { waitForCanvasSettled } from './digital-thread-rendered-helpers'

async function open(page: Page, path = '/tests/fixtures/digital-thread-1046.html?page=1') {
  await page.addInitScript(() => { (window as any).__1046 = [] })
  await page.goto(path)
  await waitForCanvasSettled(page)
}
async function body(page: Page, id: string, click = false) {
  const r = (await page.locator(`[data-node-id="${id}"]`).boundingBox())!
  const x = r.x + r.width / 2, y = r.y + Math.min(48, r.height - 12)
  await page.mouse.move(x, y)
  if (click) await page.mouse.click(x, y)
}
async function paint(page: Page) {
  return page.evaluate(() => (window as any).__1046.filter((e: any) => e.kind === 'paint').at(-1))
}
async function evidence(page: Page, info: TestInfo) {
  await info.attach('settled', { body: await page.screenshot(), contentType: 'image/png' })
  await info.attach('lifecycle', { body: JSON.stringify(await page.evaluate(() => (window as any).__1046)), contentType: 'application/json' })
}
async function selectedFits(page: Page) {
  await expect.poll(async () => page.evaluate(() => {
    const p = (window as any).__1046.filter((e: any) => e.kind === 'paint').at(-1)
    const c = document.querySelector(`[data-node-id="${p.selectedId}"]`)!.getBoundingClientRect()
    const v = document.querySelector('.dtCanvas')!.getBoundingClientRect()
    return c.top >= v.top + p.box.y - 1 && c.bottom <= v.top + p.box.y + p.box.height + 1 &&
      c.left >= v.left + p.box.x - 1 && c.right <= v.left + p.box.x + p.box.width + 1
  })).toBe(true)
}

test('foreground pixels win overlap; covered controls leave Tab and exposed background selects normally', async ({ page }, info) => {
  await open(page, '/tests/fixtures/digital-thread-1046.html')
  await body(page, 'pr-6')
  await page.waitForTimeout(1500)
  const proof = await page.evaluate(() => {
    const linked = document.querySelector<HTMLElement>('[data-node-id="hlr-128"]')!
    const r = linked.getBoundingClientRect(), x = r.left + r.width / 2, y = r.top + r.height / 2
    const covered = [...document.querySelectorAll<HTMLElement>('[data-occluded="true"]')]
    return { foreground: r.toJSON(), hit: document.elementFromPoint(x, y)?.closest('[data-node-id]')?.getAttribute('data-node-id'),
      covered: covered.map(e => ({ id: e.dataset.nodeId, tab: e.tabIndex,
        controls: [...e.querySelectorAll<HTMLElement>('a,button')].map(a => ({ tab: a.tabIndex, rect: a.getBoundingClientRect().toJSON() })) })) }
  })
  expect(proof.hit).toBe('hlr-128')
  expect(proof.covered.length).toBeGreaterThan(0)
  let coveredControls = 0
  for (const card of proof.covered) {
    expect(card.tab).toBe(-1)
    for (const control of card.controls) {
      const a = control.rect, b = proof.foreground
      if (a.left < b.right && a.right > b.left && a.top < b.bottom && a.bottom > b.top) {
        coveredControls++
        expect(control.tab).toBe(-1)
      }
    }
  }
  expect(coveredControls).toBeGreaterThan(0)
  await info.attach('occlusion', { body: JSON.stringify(proof), contentType: 'application/json' })
  // Different lane, fully exposed body. A real click may select another subject during emphasis.
  await body(page, 'sys-31', true)
  await expect(page.locator('[data-node-id="sys-31"]')).toHaveAttribute('aria-pressed', 'true')
  await evidence(page, info)
})

test('stationary hover survives settled motion beyond dwell and deliberate motion selects a new hover', async ({ page }, info) => {
  await open(page)
  await body(page, 'pr-6')
  await page.waitForTimeout(1800)
  expect((await paint(page)).emphasisId).toBe('pr-6')
  const start = await paint(page)
  await page.waitForTimeout(650)
  expect((await paint(page)).emphasisId).toBe('pr-6')
  expect((await paint(page)).display).toEqual(start.display)
  await body(page, 'sys-33')
  await page.waitForTimeout(350)
  expect((await paint(page)).emphasisId).toBe('sys-33')
  // Activate during link motion rather than waiting for a settled board.
  await body(page, 'sys-33', true)
  await expect(page.locator('[data-node-id="sys-33"]')).toHaveAttribute('aria-pressed', 'true')
  await waitForCanvasSettled(page)
  await selectedFits(page)
  await evidence(page, info)
})

test('selection uses selection intent; manual pan and passive resize keep reader ownership; clear keeps camera', async ({ page }, info) => {
  await open(page)
  await body(page, 'sys-33', true)
  await waitForCanvasSettled(page)
  expect((await paint(page)).framedFor).toContain('|selection|')
  const c = (await page.locator('.dtCanvas').boundingBox())!
  await page.mouse.move(c.x + 6, c.y + c.height - 50)
  await page.mouse.down()
  await page.mouse.move(c.x + 6, c.y + c.height - 230, { steps: 12 })
  await page.mouse.up()
  await waitForCanvasSettled(page)
  expect((await paint(page)).cameraOwned).toBe(true)
  const owned = (await paint(page)).display
  await page.setViewportSize({ width: 1920, height: 990 })
  await waitForCanvasSettled(page)
  expect((await paint(page)).display).toEqual(owned)
  await page.keyboard.press('Escape')
  await page.waitForTimeout(1000)
  expect((await paint(page)).display).toEqual(owned)
  expect((await paint(page)).selectedId).toBeNull()
  await evidence(page, info)
})

for (const view of ['network', 'inside', 'artifact']) test(`${view} selected geometry across Bottom Right Auto docks`, async ({ page }, info) => {
  await open(page, view === 'network' ? '/tests/fixtures/digital-thread-1046.html?page=1' :
    `/tests/fixtures/${view === 'inside' ? 'inside-change' : 'artifact-thread'}.html?case=hlr`)
  if (view === 'network') await body(page, 'sys-33', true)
  await waitForCanvasSettled(page)
  const selected = page.locator('.dtCanvasNode[aria-pressed="true"]')
  await expect(selected).toHaveCount(1)
  const identity = await selected.getAttribute('data-node-id')
  for (const dock of ['Bottom', 'Right', 'Auto']) {
    await page.getByRole('button', { name: dock, exact: true }).click()
    await waitForCanvasSettled(page)
    await selectedFits(page)
    await expect(selected).toHaveAttribute('data-node-id', identity!)
    await expect(page.locator('.dtCanvasOffscreen')).toHaveCount(0)
    await info.attach(dock, { body: await page.screenshot(), contentType: 'image/png' })
  }
  await evidence(page, info)
})

test('native keyboard activation and touch preserve exact selection under reduced motion', async ({ page, browser, baseURL }, info) => {
  await page.emulateMedia({ reducedMotion: 'reduce' })
  await open(page, '/tests/fixtures/digital-thread-1046.html')
  const pr = page.locator('[data-node-id="pr-6"]')
  // Focus itself is a supported deliberate keyboard navigation, not automatic-fit setup.
  await pr.focus()
  await page.keyboard.press('Enter')
  await expect(pr).toHaveAttribute('aria-pressed', 'true')
  await waitForCanvasSettled(page)
  await selectedFits(page)
  const link = pr.locator('a').first()
  await expect(link).not.toHaveAttribute('tabindex', '-1')
  const href = await link.getAttribute('href')
  expect(href).toBeTruthy()
  await link.click()
  expect(new URL(page.url()).hash).toBe(href)
  await evidence(page, info)
  const context = await browser.newContext({ baseURL, hasTouch: true, viewport: { width: 1920, height: 1000 }, reducedMotion: 'reduce' })
  const touch = await context.newPage()
  try {
    await open(touch)
    const target = touch.locator('[data-node-id="pr-6"]')
    const r = (await target.boundingBox())!
    await touch.touchscreen.tap(r.x + r.width / 2, r.y + 48)
    await expect(target).toHaveAttribute('aria-pressed', 'true')
    await waitForCanvasSettled(touch)
    await selectedFits(touch)
    await info.attach('touch', { body: await touch.screenshot(), contentType: 'image/png' })
  } finally { await context.close() }
})


test('external arrival and browser back forward retain landing intent after internal selection', async ({ page }, info) => {
  await open(page, '/tests/fixtures/digital-thread-1046.html?page=1&focal=sys-33')
  await selectedFits(page)
  expect((await paint(page)).framedFor).toContain('|landing|')
  await body(page, 'proc-4', true)
  await waitForCanvasSettled(page)
  expect((await paint(page)).selectedId).toBe('proc-4')
  expect((await paint(page)).framedFor).toContain('|selection|')
  await page.goBack()
  await waitForCanvasSettled(page)
  expect((await paint(page)).selectedId).toBe('sys-33')
  expect((await paint(page)).framedFor).toContain('|landing|')
  await selectedFits(page)
  await page.goForward()
  await waitForCanvasSettled(page)
  expect((await paint(page)).selectedId).toBe('proc-4')
  expect((await paint(page)).framedFor).toContain('|landing|')
  await selectedFits(page)
  await evidence(page, info)
})


test('a fixture card parks beneath an unmoved pointer beyond dwell without creating hover', async ({ page }, info) => {
  await open(page, '/tests/fixtures/digital-thread-contract.html?case=parking')
  const source = (await page.locator('[data-node-id="subj"]').boundingBox())!
  const linked = (await page.locator('[data-node-id="link"]').boundingBox())!
  await page.getByRole('button', { name: 'Move fixture card' }).click()
  const x = linked.x + linked.width / 2, y = source.y + 30
  await page.mouse.move(x, y)
  await page.waitForTimeout(1800)
  expect(await page.evaluate(({ x, y }) => document.elementFromPoint(x, y)?.closest('[data-node-id]')?.getAttribute('data-node-id'), { x, y })).toBe('link')
  expect((await paint(page)).emphasisId).toBeNull()
  await page.mouse.move(x + 3, y)
  await page.waitForTimeout(400)
  expect((await paint(page)).emphasisId).toBe('link')
  await evidence(page, info)
})

test('recovered strip space fits the selected card without an unnecessary bottom-dock change', async ({ page }, info) => {
  await page.setViewportSize({ width: 1920, height: 830 })
  await open(page, '/tests/fixtures/digital-thread-1046.html?page=1&focal=proc-4')
  await selectedFits(page)
  await expect(page.locator('.dtnPanel')).toHaveClass(/dtnPanel-bottom/)
  const current = await paint(page)
  const card = (await page.locator('[data-node-id="proc-4"]').boundingBox())!
  expect(card.height + 24).toBeLessThanOrEqual(current.box.height)
  expect(card.height + 24).toBeGreaterThan(current.box.height - 64)
  await evidence(page, info)
})
