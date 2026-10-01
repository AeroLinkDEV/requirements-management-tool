import { expect, renderedTest as test, type Page } from './isolated-client-test'

// #1369 rendered proof: the crew's indication of an airborne route append on the real panel. The engine rules are owned
// by fms-route-append.spec.ts; this proves the keys reach them and the RTE page shows the "+" and the discontinuity,
// with guidance left on the active waypoint.
const open = async (page: Page) => {
  await page.addInitScript(() => { try { window.localStorage.clear() } catch { /* private mode */ } })
  await page.goto('/tests/fixtures/fms-cdu.html')
  await expect(page.locator('.fmsCdu')).toBeVisible()
}
const key = (page: Page, id: string) => page.locator(`.fmsCduKey[data-key="${id}"]`)
const screenLines = async (page: Page) => ((await page.locator('.fmsCduScreen').getAttribute('aria-label')) ?? '').split('\n')
const expectLine = async (page: Page, line: number, pattern: RegExp) =>
  expect.poll(async () => (await screenLines(page))[line] ?? '').toMatch(pattern)

test('an airborne CO ROUTE load shows "+" and a discontinuity after the active waypoint, and keeps guidance', async ({ page }) => {
  await open(page)
  const flight = page.getByRole('region', { name: 'Flight', exact: true })
  const active = flight.locator('.fmsBenchReadout').first()
  await expect(active).toHaveText(/^Active waypoint MUN/)
  await key(page, 'RTE').click()
  await expectLine(page, 0, /^ACT RTE 1/)
  await key(page, 'LSK4R').click()
  await expectLine(page, 0, /^SELECT CO ROUTE/)
  const row = (await screenLines(page)).findIndex(line => /^OWUL2\b|\bOWUL2$/.test(line))
  expect(row).toBeGreaterThan(0)
  await key(page, `LSK${row / 2}${/^OWUL2\b/.test((await screenLines(page))[row]) ? 'L' : 'R'}`).click()
  await expectLine(page, 0, /^MOD RTE 1/)
  await expectLine(page, 4, /^OWUL2\+/)
  await key(page, 'NEXT').click()
  await expectLine(page, 4, /DISCONTINUITY/)
  await key(page, 'EXEC').click()
  await expectLine(page, 0, /^ACT RTE 1/)
  await key(page, 'PREV').click()
  await expectLine(page, 4, /^OWUL2\+/)
  await expect(active).toHaveText(/^Active waypoint MUN/)
})
