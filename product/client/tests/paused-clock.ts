import type { Page } from '@playwright/test'

/** Freeze before loading the fixture; explicit runFor calls must still advance UTC and timers together. */
export const installPausedClock = async (page: Page, start: Date) => {
  // setFixedTime installs Playwright's clock but leaves timers running. Before the fixture exists, hold Date steady
  // across the pause RPC, then restore advancing Date while the timer clock stays paused. No future margin or retry.
  await page.clock.setFixedTime(start)
  await page.clock.pauseAt(start)
  await page.clock.setSystemTime(start)
}
