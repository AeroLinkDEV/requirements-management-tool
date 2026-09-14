import { expect, test, type Page, type TestInfo } from '@playwright/test'

async function snapshot(page: Page, info: TestInfo, label: string) {
  await info.attach(label, { body: await page.screenshot(), contentType: 'image/png' })
  const geometry = await page.evaluate(() => {
    const rect = (e: Element) => e.getBoundingClientRect().toJSON()
    return { label: '', t: performance.now(), scene: document.querySelector('.dtCanvasScene')?.getAttribute('style'),
      cards: [...document.querySelectorAll('[data-node-id]')].map(e => ({ id: e.getAttribute('data-node-id'), class: e.className, rect: rect(e) })),
      panels: [...document.querySelectorAll('.dtnPanel,.dtCanvas,.dtCanvasControls,.dtCanvasLaneHead')].map(e => ({ class: e.className, rect: rect(e) })) }
  })
  await info.attach(`${label}-geometry`, { body: JSON.stringify(geometry, null, 2), contentType: 'application/json' })
}

async function usable(page: Page, id: string) {
  return page.evaluate(id => {
    const e = document.querySelector(`[data-node-id="${id}"]`)!
    const r = e.getBoundingClientRect()
    const canvas = document.querySelector('.dtCanvas')!.getBoundingClientRect()
    const paints = (window as any).__1046.filter((v: any) => v.kind === 'paint')
    const box = paints.at(-1).box
    const clip = getComputedStyle(e).clipPath
    return { id, rect: r.toJSON(), frame: { left: canvas.left + box.x, top: canvas.top + box.y,
      right: canvas.left + box.x + box.width, bottom: canvas.top + box.y + box.height }, clip,
      fits: r.left >= canvas.left + box.x - 1 && r.right <= canvas.left + box.x + box.width + 1 &&
        r.top >= canvas.top + box.y - 1 && r.bottom <= canvas.top + box.y + box.height + 1 }
  }, id)
}

for (const mode of ['page-detailed', 'page-compact', 'standalone-control']) test(`owner sequence ${mode}`, async ({ page }, info) => {
  const compact = mode === 'page-compact'
  await page.addInitScript(() => { (window as any).__1046 = []; for (const name of ['pointerdown', 'pointerup', 'pointerover', 'pointerout', 'click', 'focusin']) document.addEventListener(name, e => {
    const p = e as PointerEvent; (window as any).__1046.push({ kind: name, t: performance.now(), id: (e.target as Element)?.closest?.('[data-node-id]')?.getAttribute('data-node-id'), x: p.clientX, y: p.clientY })
  }, true) })
  await page.goto(`/tests/fixtures/digital-thread-1046.html${mode === 'standalone-control' ? '' : '?page=1'}`)
  await page.locator('[data-node-id="pr-6"]').waitFor()
  await page.waitForTimeout(1000)
  if (compact) {
    // The owner captured compact hover followed by detailed selection. Use the real zoom control;
    // no Fit/Show action or direct camera mutation establishes this reader-owned starting state.
    await page.getByRole('button', { name: 'Zoom out', exact: true }).click()
    await page.waitForTimeout(1000)
  }
  await snapshot(page, info, '01-quiet')
  const select = async (id: string, action: 'hover' | 'click', label: string) => {
    const card = page.locator(`[data-node-id="${id}"]`)
    // Normal body gesture, avoiding the native identifier link. No focus, Fit, Show or force.
    const box = (await card.boundingBox())!
    const cameraBefore = await page.locator('.dtCanvasScene').getAttribute('style')
    await page.mouse.move(box.x + box.width / 2, box.y + Math.min(box.height - 12, 48))
    if (action === 'click') await page.mouse.click(box.x + box.width / 2, box.y + Math.min(box.height - 12, 48))
    await page.waitForTimeout(100)
    await snapshot(page, info, `${label}-during`)
    await page.waitForTimeout(1100)
    await snapshot(page, info, label)
    if (action === 'hover') {
      expect(await page.evaluate(() => (window as any).__1046.filter((v: any) => v.kind === 'paint').at(-1).emphasisId)).toBe(id)
      expect(await page.locator('.dtCanvasScene').getAttribute('style')).toBe(cameraBefore)
      const after = (await card.boundingBox())!
      expect.soft(Math.abs(after.y - box.y), `${id} hover source y must stay fixed`).toBeLessThanOrEqual(1)
      expect.soft(Math.abs(after.x - box.x), `${id} hover source x must stay fixed`).toBeLessThanOrEqual(1)
    }
  }
  try {
    await select('pr-6', 'hover', '02-pr-hover')
    const prHover = [await usable(page, 'hlr-128'), await usable(page, 'hlr-134')]
    await info.attach('PR-hover-usability', { body: JSON.stringify(prHover), contentType: 'application/json' })
    for (const card of prHover) expect.soft(card.fits, `${card.id} must reveal over background on hover`).toBe(true)
    await select('pr-6', 'click', '03-pr-click')
    await expect(page.locator('[data-node-id="pr-6"]')).toHaveAttribute('aria-pressed', 'true')
    for (const id of ['hlr-128', 'hlr-134']) expect.soft((await usable(page, id)).fits, `${id} after PR click`).toBe(true)
    await page.keyboard.press('Escape')
    await page.mouse.move(285, 155)
    await page.waitForTimeout(1000)
    if (compact) {
      await info.attach('lifecycle-before-reload', { body: JSON.stringify(await page.evaluate(() => (window as any).__1046)), contentType: 'application/json' })
      // Reopen a fresh board then use one ordinary zoom gesture, avoiding accumulated scale changes.
      await page.reload()
      await page.locator('[data-node-id="sys-33"]').waitFor()
      await page.waitForTimeout(1000)
      await page.getByRole('button', { name: 'Zoom out', exact: true }).click()
      await page.waitForTimeout(1000)
    }
    await select('sys-33', 'hover', '04-srcr-hover')
    await select('sys-33', 'click', '05-srcr-click')
    await expect(page.locator('[data-node-id="sys-33"]')).toHaveAttribute('aria-pressed', 'true')
    expect.soft((await usable(page, 'hlr-122')).fits, 'HLR122 after SRCR click').toBe(true)
    const beforeProcedure = await usable(page, 'proc-4')
    expect(beforeProcedure.fits, 'Procedure must actually be usable before its normal click').toBe(true)
    await select('proc-4', 'click', '06-procedure-click')
    await expect(page.locator('[data-node-id="proc-4"]')).toHaveAttribute('aria-pressed', 'true')
    const afterProcedure = [await usable(page, 'proc-4'), await usable(page, 'sys-33')]
    await info.attach('Procedure-usability', { body: JSON.stringify({ beforeProcedure, afterProcedure }), contentType: 'application/json' })
    for (const card of afterProcedure) expect.soft(card.fits, `${card.id} must remain usable after Procedure click`).toBe(true)
  } finally {
    await info.attach('lifecycle', { body: JSON.stringify(await page.evaluate(() => (window as any).__1046)), contentType: 'application/json' })
  }
})
