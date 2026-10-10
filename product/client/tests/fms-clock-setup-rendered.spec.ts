import { setTimeout as delay } from 'node:timers/promises'
import { expect, renderedTest as test } from './isolated-client-test'
import { installPausedClock } from './paused-clock'

test('paused fixture clock preserves its exact epoch across a delayed pause command, then advances only explicitly', async ({ page }) => {
  const start = new Date('2026-09-27T14:00:00Z')
  // A busy runner can delay the second RPC. Keep the actual Playwright pause implementation and expose that gap.
  const pauseAt = page.clock.pauseAt.bind(page.clock)
  page.clock.pauseAt = async time => { await delay(100); await pauseAt(time) }
  await installPausedClock(page, start)
  await page.goto('/tests/fixtures/fms-cdu.html') // Navigation must replay the paused clock at the same exact epoch.
  expect(await page.evaluate(() => Date.now())).toBe(start.getTime())
  await page.setContent('<output id="ticks">0</output><script>setInterval(() => { ticks.textContent = String(Number(ticks.textContent) + 1) }, 250)</script>')
  const read = () => page.evaluate(() => ({ utc: Date.now(), ticks: Number(document.querySelector('output')!.textContent) }))
  expect(await read()).toEqual({ utc: start.getTime(), ticks: 0 })
  await delay(100)
  expect(await read()).toEqual({ utc: start.getTime(), ticks: 0 })
  await page.clock.runFor(249)
  expect(await read()).toEqual({ utc: start.getTime() + 249, ticks: 0 })
  await page.clock.runFor(1)
  expect(await read()).toEqual({ utc: start.getTime() + 250, ticks: 1 })
})
