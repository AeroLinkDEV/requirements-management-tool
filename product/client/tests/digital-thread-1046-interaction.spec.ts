import { expect, test, type Page, type TestInfo } from '@playwright/test'
import { waitForCanvasSettled, readCanvasState, observeCanvas } from './digital-thread-rendered-helpers'

// This dense owner-shaped fixture includes the application rail and six lanes. Its gesture
// coordinates require the reviewed desktop frame, independently of the runner's defaults.
test.use({ viewport: { width: 1920, height: 1000 } })

async function open(page: Page, path = '/tests/fixtures/digital-thread-1046.html?page=1') {
  await observeCanvas(page)
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
  return (await page.evaluate(readCanvasState))!
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

test('selection stays readable; manual pan and passive resize keep reader ownership; clear keeps camera', async ({ page }, info) => {
  await open(page)
  await body(page, 'sys-33', true)
  await waitForCanvasSettled(page)
  expect((await paint(page)).display.zoom).toBeGreaterThanOrEqual(0.81)
  const beforePan = (await paint(page)).display
  const c = (await page.locator('.dtCanvas').boundingBox())!
  await page.mouse.move(c.x + 6, c.y + c.height - 50)
  await page.mouse.down()
  await page.mouse.move(c.x + 6, c.y + c.height - 230, { steps: 12 })
  await page.mouse.up()
  await waitForCanvasSettled(page)
  // Ownership is proved by the unchanged painted camera after passive resize and clear below.
  const owned = (await paint(page)).display
  expect(owned).not.toEqual(beforePan)
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


test('external arrival and browser back forward preserve readable exact selection', async ({ page }, info) => {
  await open(page, '/tests/fixtures/digital-thread-1046.html?page=1&focal=sys-33')
  await selectedFits(page)
  expect((await paint(page)).display.zoom).toBeGreaterThanOrEqual(0.86)
  await body(page, 'proc-4', true)
  await waitForCanvasSettled(page)
  expect((await paint(page)).selectedId).toBe('proc-4')
  expect((await paint(page)).display.zoom).toBeGreaterThanOrEqual(0.81)
  await page.goBack()
  await waitForCanvasSettled(page)
  expect((await paint(page)).selectedId).toBe('sys-33')
  expect((await paint(page)).display.zoom).toBeGreaterThanOrEqual(0.86)
  await selectedFits(page)
  await page.goForward()
  await waitForCanvasSettled(page)
  expect((await paint(page)).selectedId).toBe('proc-4')
  expect((await paint(page)).display.zoom).toBeGreaterThanOrEqual(0.86)
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

test('relation-label status stays in toolbar clearance across Artifact docks', async ({ page }, info) => {
  await open(page, '/tests/fixtures/artifact-thread.html?case=hlr')
  let visibleNotices = 0
  for (const dock of ['Bottom', 'Right', 'Auto']) {
    await page.getByRole('button', { name: dock, exact: true }).click()
    await waitForCanvasSettled(page)
    const notice = page.locator('.dtCanvasPlacementNotice')
    if (!await notice.isVisible()) continue
    visibleNotices++
    const bounds = await notice.evaluate(element => {
      const notice = element.getBoundingClientRect()
      const toolbar = element.closest('.dtCanvasControls')!.getBoundingClientRect()
      const painted = { left: Math.max(notice.left, toolbar.left), right: Math.min(notice.right, toolbar.right),
        top: notice.top, bottom: notice.bottom }
      const targets = [...document.querySelectorAll<HTMLElement>('.dtCanvasNode:not(.is-dimmed), .dtaPanel')]
      return { painted, toolbar: toolbar.toJSON(), collisions: targets.filter(target => {
        const r = target.getBoundingClientRect()
        return painted.left < r.right && painted.right > r.left && painted.top < r.bottom && painted.bottom > r.top
      }).map(target => target.dataset.nodeId ?? target.className) }
    })
    expect(bounds.painted.top).toBeGreaterThanOrEqual(bounds.toolbar.top)
    expect(bounds.painted.bottom).toBeLessThanOrEqual(bounds.toolbar.bottom)
    expect(bounds.collisions).toEqual([])
    await info.attach(`notice-${dock}`, { body: await page.screenshot(), contentType: 'image/png' })
  }
  expect(visibleNotices).toBeGreaterThan(0)
})

test('late linked-card height measurement preserves a usable foreground story', async ({ page }, info) => {
  await open(page)
  await body(page, 'pr-6')
  await page.waitForTimeout(1400)
  const source = (await page.locator('[data-node-id="pr-6"]').boundingBox())!
  const before = await paint(page)
  await page.addStyleTag({ content: '[data-node-id="hlr-128"] > * { padding-bottom: 26px !important; font-family: Georgia, serif !important; }' })
  await waitForCanvasSettled(page)
  const after = await paint(page)
  expect(after.display).toEqual(before.display)
  expect(after.emphasisId).toBe('pr-6')
  const now = (await page.locator('[data-node-id="pr-6"]').boundingBox())!
  expect(Math.abs(now.y - source.y)).toBeLessThan(0.5)
  const proof = await page.evaluate(() => {
    const p = (window as any).__1046.filter((e: any) => e.kind === 'paint').at(-1)
    const v = document.querySelector('.dtCanvas')!.getBoundingClientRect()
    return ['hlr-128', 'hlr-134'].map(id => {
      const r = document.querySelector(`[data-node-id="${id}"]`)!.getBoundingClientRect()
      return { id, top: r.top, bottom: r.bottom, frameTop: v.top + p.box.y, frameBottom: v.top + p.box.y + p.box.height }
    })
  })
  expect(after.heights.find(([id]: [string]) => id === 'hlr-128')[1]).toBeGreaterThan(before.heights.find(([id]: [string]) => id === 'hlr-128')[1])
  for (const r of proof) {
    expect(r.top).toBeGreaterThanOrEqual(r.frameTop)
    expect(r.bottom).toBeLessThanOrEqual(r.frameBottom)
  }
  await info.attach('late-measurement', { body: JSON.stringify({ before, after, proof }), contentType: 'application/json' })
  await evidence(page, info)
})

test('external page movement preserves hover until deliberate pointer movement', async ({ page }, info) => {
  await open(page)
  await body(page, 'pr-6')
  await page.waitForTimeout(1400)
  // Simulate surrounding page content changing position, not a reveal moving its own source.
  await page.locator('.dtCanvas').evaluate(element => { (element as HTMLElement).style.transform = 'translateY(180px)' })
  await page.waitForTimeout(900)
  expect((await paint(page)).emphasisId).toBe('pr-6')
  await page.mouse.move(5, 5)
  await page.waitForTimeout(900)
  expect((await paint(page)).emphasisId).toBeNull()
  await evidence(page, info)
})

test('Tab never rests behind foreground and native focusability returns after clearing', async ({ page }, info) => {
  await open(page, '/tests/fixtures/digital-thread-1046.html')
  await body(page, 'pr-6')
  await page.waitForTimeout(1400)
  const covered = await page.locator('[data-occluded="true"]:has(a)').first().getAttribute('data-node-id')
  expect(covered).toBeTruthy()
  const background = page.locator(`[data-node-id="${covered}"]`)
  const originalHref = await background.locator('a').first().getAttribute('href')
  await page.locator('[data-node-id="pr-6"]').focus()
  let canvasStops = 0
  for (let i = 0; i < 24; i++) {
    await page.keyboard.press('Tab')
    await waitForCanvasSettled(page)
    const focus = await page.evaluate(() => {
      const active = document.activeElement as HTMLElement
      const own = active.closest<HTMLElement>('.dtCanvasNode')
      if (!own) return null
      const r = active.getBoundingClientRect()
      const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2)?.closest<HTMLElement>('.dtCanvasNode')
      return { id: own.dataset.nodeId, hit: hit?.dataset.nodeId, name: active.textContent, tab: active.tabIndex }
    })
    if (!focus) continue
    canvasStops++
    expect(focus.hit).toBe(focus.id)
    expect(focus.name?.trim()).toBeTruthy()
    expect(focus.tab).toBeGreaterThanOrEqual(0)
  }
  expect(canvasStops).toBeGreaterThan(2)
  await page.mouse.move(5, 5)
  await page.keyboard.press('Escape')
  await page.waitForTimeout(1000)
  await background.focus() // Deliberate access to its restored canonical row.
  await waitForCanvasSettled(page)
  const native = background.locator('a').first()
  await expect(native).not.toHaveAttribute('tabindex', '-1')
  await expect(native).toHaveAttribute('href', originalHref!)
  await native.focus()
  await expect(native).toBeFocused()
  await evidence(page, info)
})
