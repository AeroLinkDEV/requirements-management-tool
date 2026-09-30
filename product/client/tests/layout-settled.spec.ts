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

test('an equal height returned after the deadline does not authorize a measurement', async ({ page }) => {
  await page.setContent('<div style="height: 1000px">Stable but starved renderer</div>')
  const evaluate = page.evaluate.bind(page)
  let probes = 0
  // Retain actual DOM measurements, but deliver the final browser reply late.
  page.evaluate = (async (...args: Parameters<typeof page.evaluate>) => {
    const result = await evaluate(...args)
    if (++probes === 3) await new Promise(resolve => setTimeout(resolve, 500))
    return result
  }) as typeof page.evaluate
  await expect(layoutSettled(page, 400)).rejects.toThrow(/Layout did not settle within 400 ms/)
})
