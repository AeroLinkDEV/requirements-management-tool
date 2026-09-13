import type { Page } from '@playwright/test'
import { expect } from '@playwright/test'

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
