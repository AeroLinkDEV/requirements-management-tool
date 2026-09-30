import { expect, test } from '@playwright/test'
import { layoutSettled } from './auth'

test('layout measurements may proceed once document height is stable', async ({ page }) => {
  await page.setContent('<div style="height: 1000px">Stable layout</div>')
  await expect(layoutSettled(page, 1_000)).resolves.toBeUndefined()
})

test('a document that keeps growing fails with the settling deadline and recent heights', async ({ page }) => {
  await page.setContent('<div id="growing" style="height: 1000px">Loading layout</div>')
  await page.evaluate(() => {
    const element = document.getElementById('growing')!
    let height = 1000
    const grow = () => {
      element.style.height = `${height += 10}px`
      requestAnimationFrame(grow)
    }
    requestAnimationFrame(grow)
  })
  await expect(layoutSettled(page, 600)).rejects.toThrow(/Layout did not settle within 600 ms \(last heights: \d+, \d+, \d+\)/)
})
