import type { Page } from '@playwright/test'
import { expect } from '@playwright/test'

/** Browser-side observation only: no production transforms, refs or decisions are instrumented. */
export function readCanvasState() {
  const canvas = document.querySelector<HTMLElement>('.dtCanvas')
  const scene = document.querySelector<HTMLElement>('.dtCanvasScene')
  if (!canvas || !scene) return null
  const v = canvas.getBoundingClientRect()
  const toolbar = canvas.querySelector('.dtCanvasControls')!.getBoundingClientRect()
  const heading = canvas.querySelector('.dtCanvasLaneHead')!
  const top = Math.max(40, Math.ceil(toolbar.bottom - v.top + Math.max(0, -parseFloat(getComputedStyle(heading).top)) + 8))
  const panel = document.querySelector<HTMLElement>('.dtnPanel, .dticPanel, .dtaPanel')
  const r = panel?.getBoundingClientRect()
  const left = r && /Panel-left/.test(panel!.className) ? Math.ceil(r.right - v.left + 12) : 0
  const right = r && /Panel-right/.test(panel!.className) ? Math.ceil(v.right - r.left + 12) : 0
  const bottom = r && /Panel-bottom/.test(panel!.className) ? Math.ceil(v.bottom - r.top + 12) : 0
  const matrix = new DOMMatrix(getComputedStyle(scene).transform)
  const cards = [...canvas.querySelectorAll<HTMLElement>('.dtCanvasNode')]
  return { kind: 'paint', t: performance.now(), selectedId: cards.find(c => c.getAttribute('aria-pressed') === 'true')?.dataset.nodeId ?? null,
    emphasisId: cards.find(c => c.style.zIndex === '3')?.dataset.nodeId ?? null,
    box: { x: left, y: top, width: v.width - left - right, height: v.height - top - bottom },
    display: { x: matrix.e, y: matrix.f, zoom: matrix.a },
    heights: cards.map(c => [c.dataset.nodeId!, c.offsetHeight] as const),
    cards: cards.map(c => ({ id: c.dataset.nodeId, rect: c.getBoundingClientRect().toJSON(), transform: c.style.transform, clip: c.style.clipPath })) }
}

export async function observeCanvas(page: Page) {
  await page.addInitScript({ content: `(() => {
    const read = ${readCanvasState.toString()};
    window.__1046 = [];
    let previous = '', last = 0;
    const sample = () => {
      const state = read();
      if (state) {
        const signature = JSON.stringify({...state, t: 0});
        if (signature !== previous || state.t - last > 200) {
          window.__1046.push(state); previous = signature; last = state.t;
        }
      }
      requestAnimationFrame(sample);
    };
    requestAnimationFrame(sample);
  })()` })
}

/** Setup/settlement only. Tests observing motion or delayed callbacks must retain their observation window. */
export async function waitForCanvasSettled(page: Page) {
  await expect(page.locator('.dtCanvas')).toBeVisible()
  await page.evaluate(async () => {
    let previous = ''
    let stable = 0
    const deadline = performance.now() + 15_000
    while (performance.now() < deadline) {
      await new Promise<void>(resolve => requestAnimationFrame(() => resolve()))
      const scene = document.querySelector<HTMLElement>('.dtCanvasScene')
      const signature = [scene ? getComputedStyle(scene).transform : '',
        ...[...document.querySelectorAll<HTMLElement>('.dtCanvasNode')].map(node =>
          `${node.style.transform}|${node.offsetHeight}`)].join(';')
      stable = !scene?.classList.contains('is-easing') && signature === previous ? stable + 1 : 0
      previous = signature
      if (stable >= 10) return
    }
    throw new Error('Digital Thread geometry did not settle across ten consecutive paints')
  })
}
