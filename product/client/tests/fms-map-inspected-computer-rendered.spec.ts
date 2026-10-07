import { expect, renderedTest as test, type Page } from './isolated-client-test'

// #1504 owner (#1502 §19 item 1, option a): the engineering map draws one computer, the inspected one: its route, its
// own guidance mode and active leg. When the other computer is guiding the aircraft, the map adds one separate,
// labelled marker for the leg that computer is flying. Engine guidance is owned by the logic tier; what only a render
// sees is which computer's guidance the bench hands the map, so this drives the real bench.
const open = async (page: Page) => {
  await page.addInitScript(() => { try { window.localStorage.clear() } catch { /* private mode */ } })
  await page.goto('/tests/fixtures/fms-cdu.html')
  await expect(page.locator('.fmsCdu')).toBeVisible()
  await page.getByRole('radiogroup', { name: 'Lower display' }).getByText('Engineering map', { exact: true }).click()
}
const map = (page: Page) => page.getByRole('img', { name: /^Navigation map/ })
const guidingLeg = (page: Page) => page.getByTestId('guiding-leg')
const inspect = (page: Page, side: 1 | 2) => page.getByLabel('CDU inspected', { exact: true }).selectOption(String(side))
const guideWith = (page: Page, side: 1 | 2) => page.getByLabel('FMS guidance source', { exact: true }).selectOption(String(side))
const key = (page: Page, id: string) => page.getByTestId('fms-cdu-inspected').locator(`.fmsCduKey[data-key="${id}"]`).click()
/** DIRECT-TO from the inspected CDU: the ident on line 1 of LEGS, then EXEC. */
const directTo = async (page: Page, ident: string) => {
  await key(page, 'LEGS')
  for (const letter of ident) await key(page, letter)
  await key(page, 'LSK1L')
  await key(page, 'EXEC')
}

test('SYNC: the map names its computer, and marks the other computer only while that one is guiding', async ({ page }) => {
  await open(page)
  await expect(map(page)).toHaveAttribute('aria-label', /^Navigation map, FMS 1, \d+ NM range, LNAV mode, active waypoint MUN$/)
  await expect(guidingLeg(page)).toHaveCount(0)
  await guideWith(page, 2)
  // Synchronized computers fly the same leg; the marker still says which computer the aircraft follows.
  await expect(map(page)).toHaveAttribute('aria-label', /^Navigation map, FMS 1, \d+ NM range, LNAV mode, active waypoint MUN; FMS 2 guiding → MUN$/)
  await expect(page.getByTestId('guiding-label')).toHaveText('FMS 2 guiding → MUN')
  await expect(guidingLeg(page)).toHaveCount(1)
  await inspect(page, 2) // Inspecting the guiding computer: one computer, no marker.
  await expect(map(page)).toHaveAttribute('aria-label', /^Navigation map, FMS 2, \d+ NM range, LNAV mode, active waypoint MUN$/)
  await expect(guidingLeg(page)).toHaveCount(0)
})

test('INDEPENDENT: inspecting FMS 1 while FMS 2 guides shows FMS 1 direct CYUL and a labelled FMS 2 marker to TOLGU', async ({ page }, testInfo) => {
  await open(page)
  await page.getByRole('tab', { name: 'Dual FMS and radios', exact: true }).click()
  await page.getByRole('region', { name: 'Dual computers and radio devices' }).getByRole('button', { name: 'Fail cross-talk link' }).click()
  await directTo(page, 'CYUL')
  await inspect(page, 2)
  await directTo(page, 'TOLGU')
  // FMS 1 still guides: inspecting FMS 2 shows its own TOLGU leg and marks FMS 1's.
  await expect(map(page)).toHaveAttribute('aria-label', /^Navigation map, FMS 2, \d+ NM range, LNAV mode, active waypoint TOLGU; FMS 1 guiding → CYUL$/)
  await guideWith(page, 2)
  // Fly FMS 2's leg for a while, as the issue reproduces it: the readout follows the guiding computer.
  const readout = page.locator('.fmsBench').getByText(/^Active waypoint/)
  await expect(readout).toHaveText(/^Active waypoint TOLGU/)
  const toGo = async () => Number((await readout.innerText()).match(/([\d.]+) NM/)?.[1] ?? NaN)
  const start = await toGo()
  await page.getByLabel('Simulation rate').selectOption('16')
  await page.getByRole('button', { name: 'Fly', exact: true }).click()
  await expect.poll(toGo).toBeLessThan(start - 1)
  await page.getByRole('button', { name: 'Pause', exact: true }).click()

  await inspect(page, 1)
  // FMS 1's own guidance and active leg; FMS 2's leg only as its labelled marker.
  await expect(map(page)).toHaveAttribute('aria-label', /^Navigation map, FMS 1, \d+ NM range, LNAV mode, active waypoint CYUL; FMS 2 guiding → TOLGU$/)
  await expect(page.getByTestId('map-computer')).toHaveText('FMS 1')
  await expect(page.getByTestId('guiding-label')).toHaveText('FMS 2 guiding → TOLGU')
  await expect(guidingLeg(page)).toHaveCount(1)
  // The magenta active leg ends on FMS 1's CYUL symbol; the amber marker ends somewhere else, at TOLGU.
  const end = (d: string | null) => (d ?? '').match(/([-\d.]+),([-\d.]+)$/)?.slice(1).map(Number) ?? []
  await expect(page.locator('.fmsMap g.activeWpt')).toHaveText('CYUL')
  const cyul = (await page.locator('.fmsMap g.activeWpt').getAttribute('transform'))?.match(/translate\(([-\d.]+),([-\d.]+)\)/)?.slice(1).map(Number)
  expect(end(await page.locator('.fmsMap path.active').first().getAttribute('d'))).toEqual(cyul)
  const marker = end(await guidingLeg(page).locator('path').getAttribute('d'))
  expect(Math.hypot(marker[0] - cyul![0], marker[1] - cyul![1])).toBeGreaterThan(5)
  await page.locator('.fmsMap').screenshot({ path: testInfo.outputPath('inspect-fms1-fms2-guiding.png') })

  await inspect(page, 2)
  await expect(map(page)).toHaveAttribute('aria-label', /^Navigation map, FMS 2, \d+ NM range, LNAV mode, active waypoint TOLGU$/)
  await expect(guidingLeg(page)).toHaveCount(0)
})

// The issue's overlay case: a search pattern flown by FMS 2 must not be drawn over FMS 1's route, nor FMS 1 called SAR.
test('INDEPENDENT: FMS 2 flying a sector search is not drawn as FMS 1 guidance; inspecting FMS 2 shows its pattern', async ({ page }) => {
  await page.clock.setFixedTime(new Date('2026-09-29T15:00:00Z'))
  await open(page)
  await page.getByRole('combobox', { name: 'Hardware variation' }).selectOption('050')
  const scenarios = page.getByRole('region', { name: 'Scenarios' })
  const start = { id: 'map-87n', title: '87N start', objective: 'Start the offshore helicopter mission', maxSeconds: 1, start: '87n-offshore-sar',
    steps: [{ when: { kind: 'start' }, action: { kind: 'expectAircraft', minAltitude: 490, maxAltitude: 510 } }] }
  await scenarios.getByLabel('Scenario file').setInputFiles({ name: 'map-87n.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify(start)) })
  await scenarios.getByRole('button', { name: 'Run the scenario' }).click()
  await expect(scenarios.getByRole('status').filter({ hasText: /^PASS/ })).toBeVisible()
  await page.getByRole('tab', { name: 'Dual FMS and radios', exact: true }).click()
  await page.getByRole('region', { name: 'Dual computers and radio devices' }).getByRole('button', { name: 'Fail cross-talk link' }).click()
  await inspect(page, 2)
  for (const id of ['F2_2', 'LSK4L', 'LSK6R', 'EXEC']) await key(page, id) // TACT on 050: the sector search about the datum.
  await guideWith(page, 2)
  await page.getByLabel('Simulation rate').selectOption('64')
  await page.getByRole('button', { name: 'Fly', exact: true }).click()
  await expect(page.getByLabel('Guidance', { exact: true }).locator('dd').first()).toHaveText('SAR')
  await page.getByRole('button', { name: 'Pause', exact: true }).click()
  await expect(map(page)).toHaveAttribute('aria-label', /^Navigation map, FMS 2, \d+ NM range, SAR mode/)
  await expect(page.locator('.fmsMap path.sar')).toHaveCount(1)

  await inspect(page, 1)
  await expect(map(page)).toHaveAttribute('aria-label', /^Navigation map, FMS 1, \d+ NM range, LNAV mode, active waypoint \w+; FMS 2 guiding, SAR mode$/)
  await expect(page.locator('.fmsMap path.sar')).toHaveCount(0)
  await expect(guidingLeg(page)).toHaveCount(1)
})
