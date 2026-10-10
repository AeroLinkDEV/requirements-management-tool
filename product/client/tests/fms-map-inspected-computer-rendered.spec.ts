import { expect, renderedTest as test, type Page } from './isolated-client-test'
import { installPausedClock } from './paused-clock'

// #1504 owner (#1502 §19 item 1, option a): the engineering map draws one computer, the inspected one: its route, its
// own guidance mode and active leg, and it names that vantage. When the other computer is guiding the aircraft, the map
// adds one separate, labelled marker for the path that computer is actually flying. Engine guidance is owned by the
// logic tier; what only a render sees is which computer's guidance the bench hands the map, so this drives the real bench.
const open = async (page: Page) => {
  await page.addInitScript(() => { try { window.localStorage.clear() } catch { /* private mode */ } })
  await page.goto('/tests/fixtures/fms-cdu.html')
  await expect(page.locator('.fmsCdu')).toBeVisible()
  await page.getByRole('radiogroup', { name: 'Lower display' }).getByText('Engineering map', { exact: true }).click()
}
const map = (page: Page) => page.getByRole('img', { name: /^Navigation map/ })
const vantage = (page: Page) => page.getByTestId('map-computer')
const guidingLeg = (page: Page) => page.getByTestId('guiding-leg')
const guidingLabel = (page: Page) => page.getByTestId('guiding-label')
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

test('SYNC: the map names its vantage, and marks the other computer, under its own leg, only while that one is guiding', async ({ page }) => {
  await page.clock.setFixedTime(new Date('2026-09-27T14:00:00Z'))
  await open(page)
  await expect(map(page)).toHaveAttribute('aria-label', /^Navigation map, FMS 1 inspected and guiding, \d+ NM range, LNAV mode, active waypoint MUN$/)
  await expect(vantage(page)).toHaveText('FMS 1 · INSPECTEDGUIDING') // Two lines: FMS 1 · INSPECTED, then GUIDING.
  await expect(vantage(page).locator('tspan')).toHaveText('GUIDING')
  // The bench opens in this state: the longest vantage label must not run into the north marker.
  const overlap = await vantage(page).evaluate(label => {
    const north = label.ownerSVGElement!.querySelector('text.north') as SVGTextElement
    const a = (label as SVGTextElement).getBBox(), b = north.getBBox()
    return a.x < b.x + b.width && b.x < a.x + a.width && a.y < b.y + b.height && b.y < a.y + a.height
  })
  expect(overlap).toBe(false)
  await page.locator('.fmsMap').screenshot({ path: test.info().outputPath('default-inspected-guiding.png') })
  await expect(guidingLeg(page)).toHaveCount(0)
  await guideWith(page, 2)
  // Synchronized computers fly the same leg; the marker still says which computer the aircraft follows.
  await expect(map(page)).toHaveAttribute('aria-label', /^Navigation map, FMS 1 inspected, \d+ NM range, LNAV mode, active waypoint MUN; FMS 2 guiding to MUN$/)
  await expect(vantage(page)).toHaveText('FMS 1 · INSPECTED')
  await expect(guidingLabel(page)).toHaveText('FMS 2 guiding → MUN')
  await expect(guidingLeg(page)).toHaveCount(1)
  // Where both computers fly one leg, the marker lies under this computer's magenta, which paints over it.
  expect(await guidingLeg(page).evaluate(marker => {
    const own = marker.ownerSVGElement!.querySelector('path.active')!
    return !!(marker.compareDocumentPosition(own) & Node.DOCUMENT_POSITION_FOLLOWING)
  })).toBe(true)
  await inspect(page, 2) // Inspecting the guiding computer: one computer, no marker.
  await expect(map(page)).toHaveAttribute('aria-label', /^Navigation map, FMS 2 inspected and guiding, \d+ NM range, LNAV mode, active waypoint MUN$/)
  await expect(vantage(page)).toHaveText('FMS 2 · INSPECTEDGUIDING')
  await expect(vantage(page).locator('tspan')).toHaveText('GUIDING')
  await expect(guidingLeg(page)).toHaveCount(0)
})

test('INDEPENDENT: inspecting FMS 1 while FMS 2 guides shows FMS 1 direct CYUL and a labelled FMS 2 marker to TOLGU, none in HDG', async ({ page }, testInfo) => {
  await page.clock.setFixedTime(new Date('2026-09-27T14:00:00Z'))
  await open(page)
  await page.getByRole('tab', { name: 'Dual FMS and radios', exact: true }).click()
  await page.getByRole('region', { name: 'Dual computers and radio devices' }).getByRole('button', { name: 'Fail cross-talk link' }).click()
  await directTo(page, 'CYUL')
  await inspect(page, 2)
  await directTo(page, 'TOLGU')
  // FMS 1 still guides: inspecting FMS 2 shows its own TOLGU leg and marks FMS 1's.
  await expect(map(page)).toHaveAttribute('aria-label', /^Navigation map, FMS 2 inspected, \d+ NM range, LNAV mode, active waypoint TOLGU; FMS 1 guiding to CYUL$/)
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
  await expect(map(page)).toHaveAttribute('aria-label', /^Navigation map, FMS 1 inspected, \d+ NM range, LNAV mode, active waypoint CYUL; FMS 2 guiding to TOLGU$/)
  await expect(vantage(page)).toHaveText('FMS 1 · INSPECTED')
  await expect(guidingLabel(page)).toHaveText('FMS 2 guiding → TOLGU')
  await expect(guidingLeg(page)).toHaveCount(1)
  await expect(guidingLeg(page).locator('text')).toHaveText('FMS 2')
  // The magenta active leg ends on FMS 1's CYUL symbol; the lavender marker ends somewhere else, at TOLGU.
  const end = (d: string | null) => (d ?? '').match(/([-\d.]+),([-\d.]+)$/)?.slice(1).map(Number) ?? []
  await expect(page.locator('.fmsMap g.activeWpt')).toHaveText('CYUL')
  const cyul = (await page.locator('.fmsMap g.activeWpt').getAttribute('transform'))?.match(/translate\(([-\d.]+),([-\d.]+)\)/)?.slice(1).map(Number)
  expect(end(await page.locator('.fmsMap path.active').first().getAttribute('d'))).toEqual(cyul)
  const marker = end(await guidingLeg(page).locator('path').getAttribute('d'))
  expect(Math.hypot(marker[0] - cyul![0], marker[1] - cyul![1])).toBeGreaterThan(5)
  await page.locator('.fmsMap').screenshot({ path: testInfo.outputPath('inspect-fms1-fms2-guiding.png') })

  // In heading mode the guiding computer flies no leg: the legend says so and no line is drawn.
  await page.getByRole('button', { name: 'HDG SEL', exact: true }).click()
  await expect(guidingLabel(page)).toHaveText('FMS 2 guiding, HDG mode')
  await expect(map(page)).toHaveAttribute('aria-label', /; FMS 2 guiding, HDG mode$/)
  await expect(guidingLeg(page)).toHaveCount(0)
  await page.locator('.fmsMap').screenshot({ path: testInfo.outputPath('inspect-fms1-fms2-guiding-hdg.png') })

  await inspect(page, 2)
  await expect(map(page)).toHaveAttribute('aria-label', /^Navigation map, FMS 2 inspected and guiding, \d+ NM range, HDG mode/)
  await expect(guidingLeg(page)).toHaveCount(0)
})

// The issue's overlay case: a search pattern flown by FMS 2 must not be drawn over FMS 1's route, nor FMS 1 called SAR.
test('INDEPENDENT: FMS 2 flying a sector search is not drawn as FMS 1 guidance; inspecting FMS 2 shows its pattern', async ({ page }) => {
  const initialUtc = new Date('2026-09-29T15:00:00Z')
  const pause = page.clock.pauseAt.bind(page.clock)
  page.clock.pauseAt = async instant => { await new Promise(resolve => setTimeout(resolve, 750)); await pause(instant) }
  expect(page.url()).toBe('about:blank')
  await installPausedClock(page, new Date(initialUtc.getTime() + 1000))
  await open(page)
  // Capture the existing public IOS placement boundary without changing it. The completed start must be computed
  // at t0 before PASS/map exposure; a later frozen-clock integration must not repair a stale initial position.
  await page.evaluate(async () => {
    const { ScriptedFms } = await import('/src/fmsCdu/scriptedFms.ts')
    const original = ScriptedFms.prototype.placeAircraft
    ScriptedFms.prototype.placeAircraft = function (...args) {
      original.apply(this, args)
      ;(window as unknown as { placedComputers: InstanceType<typeof ScriptedFms>[] }).placedComputers = [this]
    }
    const press = ScriptedFms.prototype.press
    ScriptedFms.prototype.press = function (...args) {
      press.apply(this, args)
      ;(window as unknown as { lastPressed: InstanceType<typeof ScriptedFms> }).lastPressed = this
    }
  })
  await page.getByRole('combobox', { name: 'Hardware variation' }).selectOption('050')
  const scenarios = page.getByRole('region', { name: 'Scenarios' })
  const start = { id: 'map-87n', title: '87N start', objective: 'Start the offshore helicopter mission', maxSeconds: 1, start: '87n-offshore-sar',
    steps: [{ when: { kind: 'start' }, action: { kind: 'expectAircraft', minAltitude: 490, maxAltitude: 510 } }] }
  await scenarios.getByLabel('Scenario file').setInputFiles({ name: 'map-87n.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify(start)) })
  await scenarios.getByRole('button', { name: 'Run the scenario' }).click()
  await expect(scenarios.getByRole('status').filter({ hasText: /^PASS/ })).toBeVisible()
  await inspect(page, 2)
  await key(page, 'LEGS')
  const placed = await page.evaluate(() => (window as unknown as { placedComputers: {
    position: { lat: number, lon: number }, now: Date
  }[], lastPressed: { position: { lat: number, lon: number }, now: Date } }).placedComputers.concat((window as unknown as { lastPressed: { position: { lat: number, lon: number }, now: Date } }).lastPressed).map(f => ({ position: { ...f.position }, utc: f.now.toISOString() })))
  // FAA 2609 87N is N40 50 46.52/W072 27 59.00; the declared start is 10 NM south. These bounds exclude the
  // inherited Ontario seed by hundreds of miles while allowing the laboratory navigation estimate's small error.
  expect(placed).toHaveLength(2)
  for (const f of placed) {
    expect(f.utc).toBe('2026-09-29T15:00:01.000Z')
    expect(f.position.lat).toBeGreaterThan(40.67); expect(f.position.lat).toBeLessThan(40.69)
    expect(f.position.lon).toBeGreaterThan(-72.48); expect(f.position.lon).toBeLessThan(-72.45)
  }
  // Preserve the current clock owner's fixed Date while later SAR/INDEPENDENT timers run at this same epoch.
  await page.clock.setFixedTime(new Date(initialUtc.getTime() + 1000))
  await page.clock.resume()
  expect(await page.evaluate(() => Date.now())).toBe(Date.parse('2026-09-29T15:00:01Z'))
  await page.getByRole('tab', { name: 'Dual FMS and radios', exact: true }).click()
  await page.getByRole('region', { name: 'Dual computers and radio devices' }).getByRole('button', { name: 'Fail cross-talk link' }).click()
  await inspect(page, 2)
  for (const id of ['F2_2', 'LSK4L', 'LSK6R', 'EXEC']) await key(page, id) // TACT on 050: the sector search about the datum.
  await guideWith(page, 2)
  await page.getByLabel('Simulation rate').selectOption('64')
  await page.getByRole('button', { name: 'Fly', exact: true }).click()
  await expect(page.getByLabel('Guidance', { exact: true }).locator('dd').first()).toHaveText('SAR')
  await page.getByRole('button', { name: 'Pause', exact: true }).click()
  await expect(map(page)).toHaveAttribute('aria-label', /^Navigation map, FMS 2 inspected and guiding, \d+ NM range, SAR mode/)
  await expect(page.locator('.fmsMap path.sar')).toHaveCount(1)

  await inspect(page, 1)
  await expect(map(page)).toHaveAttribute('aria-label', /^Navigation map, FMS 1 inspected, \d+ NM range, LNAV mode, active waypoint \w+; FMS 2 guiding, SAR mode$/)
  await expect(page.locator('.fmsMap path.sar')).toHaveCount(0)
  await expect(guidingLeg(page)).toHaveCount(1)
  expect(await page.evaluate(() => Date.now())).toBe(Date.parse('2026-09-29T15:00:01Z'))
})
