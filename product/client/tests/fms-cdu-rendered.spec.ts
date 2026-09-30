import { readFile } from 'node:fs/promises'
import { expect, renderedTest as test, type Page } from './isolated-client-test'

// The FMS test bench is self-contained: the scripted CMA-9000 runs in the page, so this needs no backend.
// Engine rules are proved in fms-cdu-engine.spec.ts; this proves the rendered panel wires them to real
// pointer, touch-style hold and keyboard input, and that a hardware variation relabels the physical keys.
const open = async (page: Page) => {
  await page.addInitScript(() => { try { window.localStorage.clear() } catch { /* private mode */ } })
  await page.goto('/tests/fixtures/fms-cdu.html')
  await expect(page.locator('.fmsCdu')).toBeVisible()
}
// The bench's tools sit in tabs under the cockpit (Scenarios first).
const tab = (page: Page, name: string) => page.getByRole('tab', { name, exact: true }).click()
const key = (page: Page, id: string) => page.locator(`.fmsCduKey[data-key="${id}"]`)
const screenLines = async (page: Page) => ((await page.locator('.fmsCduScreen').getAttribute('aria-label')) ?? '').split('\n')
const expectLine = async (page: Page, line: number, pattern: RegExp) =>
  expect.poll(async () => (await screenLines(page))[line] ?? '').toMatch(pattern)

test('keys on the rendered panel enter data, make a modification and execute it', async ({ page }) => {
  await open(page)
  await expectLine(page, 0, /^IDENT/)
  await key(page, 'RTE').click()
  await expectLine(page, 0, /^ACT RTE 1/)
  for (const letter of 'CYYZ') await key(page, letter).click()
  await expectLine(page, 13, /^CYYZ/)
  await key(page, 'LSK1R').click()
  await expectLine(page, 0, /^MOD RTE 1/)
  await expect(page.locator('.fmsCduLamp[data-lamp="EXEC_LIGHT"]')).toHaveClass(/\blit\b/)
  await key(page, 'EXEC').click()
  await expectLine(page, 0, /^ACT RTE 1/)
  await expectLine(page, 2, /CYYZ\s*$/)
  await expect(page.locator('.fmsCduLamp[data-lamp="EXEC_LIGHT"]')).not.toHaveClass(/\blit\b/)
  await tab(page, 'Lighting and keys')
  await expect(page.locator('.fmsBenchLog li').first()).toContainText('EXEC')
})

test('a held key shows the pressed render, and CLR held for a second clears the whole scratchpad', async ({ page }) => {
  await open(page)
  for (const letter of 'ABC') await key(page, letter).click()
  const clr = key(page, 'CLR')
  const box = (await clr.boundingBox())!
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2)
  await page.mouse.down()
  await expect(clr).toHaveClass(/\bpressed\b/)
  expect(await clr.evaluate(element => getComputedStyle(element).backgroundImage)).toContain('pressed.webp')
  await expectLine(page, 13, /^\s*$/)
  await page.mouse.up()
  await expect(clr).not.toHaveClass(/\bpressed\b/)
  await expectLine(page, 13, /^\s*$/)

  for (const letter of 'ABC') await key(page, letter).click()
  await clr.click()
  await expectLine(page, 13, /^AB\s*$/)
})

test('choosing a hardware variation relabels the same physical keys and annunciators', async ({ page }) => {
  await open(page)
  await expect(key(page, 'F2_2')).toHaveAttribute('aria-label', 'FUEL')
  await expect(page.locator('.fmsCduLamp').nth(5)).toHaveText('GSM')
  await page.getByRole('combobox', { name: 'Hardware variation' }).selectOption('030/430')
  await expect(key(page, 'F2_2')).toHaveAttribute('aria-label', 'TPDR')
  await expect(page.locator('.fmsCduLamp').nth(5)).toHaveText('TX1')
  await key(page, 'F2_2').click()
  await expectLine(page, 0, /^RADIO\s+2\/2$/)
})

test('the physical keyboard drives the focused panel', async ({ page }) => {
  await open(page)
  await page.locator('.fmsCdu').focus()
  await page.keyboard.type('cyyz')
  await expectLine(page, 13, /^CYYZ/)
  await page.keyboard.press('Backspace')
  await expectLine(page, 13, /^CYY\s*$/)
  await page.keyboard.press('Backspace')
  await page.keyboard.press('Backspace')
  await page.keyboard.press('Backspace')
  await key(page, 'RTE').click()
  await page.keyboard.press('F1')
  await expectLine(page, 13, /^CYOW/)
})

// R18: a press the panel loses (focus moves away, the pointer is cancelled) is abandoned; its CLR hold never fires later.
test('a CLR hold interrupted by leaving the panel or a cancelled pointer never clears the scratchpad later (R18)', async ({ page }) => {
  await open(page)
  const panel = page.locator('.fmsCdu')
  const clrEvents = () => page.locator('.fmsBenchLog li', { hasText: 'CLR' }).count()
  await panel.focus()
  await page.keyboard.type('abc')
  await expectLine(page, 13, /^ABC\s*$/)
  const before = await clrEvents()

  // Hold Backspace (CLR), move focus to the heading field before the one-second hold completes, release it there.
  await page.keyboard.down('Backspace')
  await page.waitForTimeout(150)
  await page.getByLabel('Selected heading').focus()
  await page.keyboard.up('Backspace')
  await page.waitForTimeout(1300)
  expect((await screenLines(page))[13]).toMatch(/^ABC\s*$/)
  expect(await clrEvents()).toBe(before)
  await expect(key(page, 'CLR')).not.toHaveClass(/\bpressed\b/)

  // A pointer press on CLR that the browser cancels neither completes as a short press nor as a hold.
  const clr = key(page, 'CLR')
  const box = (await clr.boundingBox())!
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2)
  await page.mouse.down()
  await clr.dispatchEvent('pointercancel', { pointerId: 1, bubbles: true })
  await page.waitForTimeout(1300)
  await page.mouse.up()
  expect((await screenLines(page))[13]).toMatch(/^ABC\s*$/)
  expect(await clrEvents()).toBe(before)
  await expect(clr).not.toHaveClass(/\bpressed\b/)
})

test('an alert raised from the bench lights MSG until CLR on the panel acknowledges it', async ({ page }) => {
  await open(page)
  const msg = page.locator('.fmsCduLamp[data-lamp="MSG"]')
  await expect(msg).not.toHaveClass(/\blit\b/)
  await tab(page, 'Conditions')
  await page.getByLabel('Alert message to raise').fill('unable rnp')
  await page.getByRole('button', { name: 'Raise alert' }).click()
  await expect(msg).toHaveClass(/\blit\b/)
  await expectLine(page, 13, /^UNABLE RNP/)
  await key(page, 'CLR').click()
  await expect(msg).not.toHaveClass(/\blit\b/)
})

test('the Conditions tab says which sensor failures v1 does not model, and offers none of them (F10)', async ({ page }) => {
  await open(page)
  await tab(page, 'Conditions')
  await expect(page.getByTestId('fms-unmodelled-conditions')).toHaveText(
    'Not modelled in v1: barometric altitude invalid, heading invalid, attitude invalid. A scenario that injects one is refused.',
  )
  for (const name of [/barometric/i, /^heading invalid/i, /attitude/i]) await expect(page.getByRole('checkbox', { name })).toHaveCount(0)
})

test('every physical key on the rendered panel can be clicked and reaches the simulation', async ({ page }) => {
  await open(page)
  await page.getByRole('combobox', { name: 'Hardware variation' }).selectOption('050')
  const keys = page.locator('.fmsCduKey')
  const count = await keys.count()
  expect(count).toBe(68)
  // A real pointer click at each key's centre. force skips only Playwright's per-click readiness waits: a key covered by
  // anything else would take no click, and the count below would say so.
  for (let i = 0; i < count; i += 1) await keys.nth(i).click({ force: true })
  await tab(page, 'Lighting and keys')
  await expect(page.locator('.fmsBenchLog h2 small')).toHaveText(String(count))
})

test('conditions from the bench light the panel annunciators, and FMS failure blanks the display', async ({ page }) => {
  await open(page)
  const lamp = (code: string) => page.locator(`.fmsCduLamp[data-lamp="${code}"]`)
  // With GPS lost the FMS updates from radio; only with the DMEs lost as well does it dead reckon and light POS.
  await tab(page, 'Conditions')
  await page.getByRole('checkbox', { name: /^GPS lost sensor/ }).check()
  await expectLine(page, 13, /^GPS NAV LOST/)
  await expect(lamp('POS')).not.toHaveClass(/\blit\b/)
  await page.getByRole('checkbox', { name: /^DME outage/ }).check()
  await expect(lamp('POS')).toHaveClass(/\blit\b/)
  await page.getByLabel('Subsystem request').check()
  await expect(lamp('MENU_LIGHT')).toHaveClass(/\blit\b/)

  await page.getByLabel('FMS failure').check()
  await expect(lamp('FAIL')).toHaveClass(/\blit\b/)
  await expect(lamp('POS')).not.toHaveClass(/\blit\b/)
  await expect.poll(async () => (await screenLines(page)).join('').trim()).toBe('')
  await key(page, 'RTE').click()
  await expect.poll(async () => (await screenLines(page)).join('').trim()).toBe('')
  await page.getByLabel('FMS failure').uncheck()
  await expectLine(page, 0, /^IDENT/)
})

test('a library alert and a sequenced waypoint reach the panel', async ({ page }) => {
  await open(page)
  await tab(page, 'Conditions')
  await page.getByRole('combobox', { name: 'Alert from the manual' }).selectOption('TIMER ALARM')
  await page.getByRole('button', { name: 'Raise', exact: true }).click()
  await expectLine(page, 13, /^TIMER ALARM/)
  await expect(page.locator('.fmsCduLamp[data-lamp="MSG"]')).toHaveClass(/\blit\b/)
  await expect(page.locator('.fmsBench')).toContainText('Active waypoint MUN')
  await page.getByRole('button', { name: 'Jump to next waypoint' }).click()
  await expect(page.locator('.fmsBench')).toContainText('Active waypoint RDG')
  await key(page, 'LEGS').click()
  await expectLine(page, 2, /^RDG/)
})

test('NVG lighting backlights the legends green and holds the display in the NVG range; ambient light moves it', async ({ page }) => {
  await open(page)
  const panel = page.locator('.fmsCdu')
  const luminance = async () => Number(await panel.getAttribute('data-luminance'))
  const day = await luminance()
  await tab(page, 'Lighting and keys')
  await page.getByText('NVG', { exact: true }).click()
  await expect(panel).toHaveClass(/\bmode-nvg\b/)
  expect(await luminance()).toBeLessThanOrEqual(3)
  const legend = await key(page, 'A').evaluate(element => getComputedStyle(element).color)
  expect(legend).toBe('rgb(140, 245, 106)')
  const dim = await luminance()
  await page.getByRole('slider', { name: 'Ambient light' }).fill('100')
  await expect.poll(luminance).toBeGreaterThan(dim)
  expect(await luminance()).toBeLessThanOrEqual(3)
  await page.getByText('Day', { exact: true }).click()
  await expect.poll(luminance).toBe(day)
})

test('Fly moves the aircraft along the route on the map at the chosen rate, and Pause stops it', async ({ page }) => {
  await open(page)
  // The engineering map shares the lower display beside the CDU with the ND; the ND is shown first.
  await expect(page.getByRole('img', { name: /^Navigation display/ })).toBeVisible()
  await page.getByRole('radiogroup', { name: 'Lower display' }).getByText('Engineering map').click()
  const map = page.getByRole('img', { name: /^Navigation map/ })
  await expect(map).toHaveAttribute('aria-label', /LNAV mode, active waypoint MUN/)
  const readout = page.locator('.fmsBench').getByText(/^Active waypoint/)
  const toGo = async () => Number((await readout.innerText()).match(/([\d.]+) NM/)?.[1] ?? NaN)
  const start = await toGo()
  await page.getByLabel('Simulation rate').selectOption('64')
  await page.getByRole('button', { name: 'Fly' }).click()
  // At 64 times real time the aircraft closes on MUN at about two nautical miles a second (sequencing past a
  // waypoint is proved in the logic tier).
  await expect.poll(toGo).toBeLessThan(start - 2)
  await expect(map).toHaveAttribute('aria-label', /LNAV mode, active waypoint MUN/)
  await expect(page.getByLabel('Guidance')).toContainText('LNAV')
  await page.getByRole('button', { name: 'Pause' }).click()
  const paused = await readout.innerText()
  // Unpaused, a second at 64 times would move the aircraft about two miles.
  await page.waitForTimeout(1000)
  await expect(page.locator('.fmsBench').getByText(/^Active waypoint/)).toHaveText(paused)
  await page.getByLabel('Map range').selectOption('80')
  await expect(map).toHaveAttribute('aria-label', /80 NM range/)
})

test('IDENT shows both database cycles, and the maintenance page follows a self test and independent operation', async ({ page }) => {
  await open(page)
  await expectLine(page, 4, /^DEMO-2609\s+03SEP-30SEP$/)
  await expectLine(page, 6, /^DEMO-2610\s+01OCT-28OCT$/)
  await key(page, 'INIT_REF').click()
  await key(page, 'LSK6L').click()
  await expectLine(page, 0, /^MAINTENANCE/)
  await page.getByLabel('Simulation rate').selectOption('64')
  await key(page, 'LSK2L').click()
  await expectLine(page, 4, /(IN PROG|PASS)$/)
  // The self test runs for five seconds of simulation time: flying at 64 times, a fraction of a second.
  await page.getByRole('button', { name: 'Fly' }).click()
  await expect.poll(async () => (await screenLines(page))[4] ?? "").toMatch(/PASS$/)
  await page.getByRole('button', { name: 'Pause' }).click()
  await expectLine(page, 6, /^DUAL SYNC\s+RTE MATCH$/)
  await tab(page, 'Conditions')
  await page.getByLabel('Independent operation').check()
  await expectLine(page, 6, /^INDEPENDENT\s+RTE MATCH$/)
  await expectLine(page, 8, /^\d{4}Z X-SIDE SYNC LOST/)
})

test('a built-in scenario runs on the bench with its steps checked live, and gives a report and procedure text', async ({ page }) => {
  await open(page)
  const card = page.getByRole('region', { name: 'Scenarios' })
  await page.getByLabel('Simulation rate').selectOption('64')
  await card.getByLabel('Scenario', { exact: true }).selectOption({ label: 'Crew RNP the navigation cannot meet' })
  await card.getByRole('button', { name: 'Run the scenario' }).click()
  await expectLine(page, 0, /PROGRESS/)
  await expect(card.getByRole('status').filter({ hasText: /^PASS/ })).toBeVisible({ timeout: 30_000 })
  await expect(card.getByRole('list', { name: 'Scenario steps' }).locator('li[data-status="pass"]')).toHaveCount(5)
  const [report] = await Promise.all([page.waitForEvent('download'), card.getByRole('button', { name: 'Download run report' }).click()])
  expect(report.suggestedFilename()).toBe('crew-rnp-the-navigation-cannot-meet-run.md')
  await card.getByRole('button', { name: 'Test procedure text' }).click()
  await expect(card.getByLabel('Test procedure text')).toHaveValue(/Steps:\n1\. At the start, press PROG\.\n2\. Then type \.01 into the scratchpad\./)
})

test('a recording of panel keys and a screen check plays back as a scenario and passes', async ({ page }) => {
  await open(page)
  const card = page.getByRole('region', { name: 'Scenarios' })
  await card.getByRole('button', { name: 'Record' }).click()
  await card.getByLabel('Recording name').fill('Open PROG')
  await key(page, 'PROG').click()
  await expectLine(page, 0, /PROGRESS/)
  await card.getByLabel('Screen line to check').selectOption('0')
  await card.getByRole('button', { name: 'Add screen check' }).click()
  await card.getByRole('button', { name: 'Stop recording' }).click()
  await expect(card.getByRole('status').filter({ hasText: 'Recorded 2 steps as “Open PROG”.' })).toBeVisible()
  await expect(card.getByLabel('Scenario', { exact: true })).toHaveValue(/^recorded-/)
  await card.getByRole('button', { name: 'Run the scenario' }).click()
  await expect(card.getByRole('status').filter({ hasText: /^PASS/ })).toBeVisible({ timeout: 15_000 })
})

test('while recording, the GPS sensors tab records its stimuli and clears; replayed, the tab shows what the scenario applied', async ({ page }) => {
  await page.clock.setFixedTime(new Date('2026-09-27T14:00:00Z'))
  await open(page)
  const card = page.getByRole('region', { name: 'Scenarios' })
  await card.getByRole('button', { name: 'Record' }).click()
  await card.getByLabel('Recording name').fill('GPS stimuli')
  await tab(page, 'GPS sensors')
  const stopped = page.getByLabel('GPS 1 Stop transmitting')
  await stopped.check()
  await page.getByText('GPS 2 bus monitor').click()
  const hil = page.getByRole('table', { name: 'GPS 2 bus monitor' }).locator('tr[data-label="130"]')
  await hil.getByLabel('Override 130', { exact: true }).selectOption('FORCE')
  await hil.getByLabel('Override 130 amount').fill('99')
  await hil.getByRole('button', { name: 'Set' }).click()
  await expect(hil.locator('td.value')).toHaveText('99')
  await stopped.uncheck()
  await tab(page, 'Scenarios')
  await card.getByRole('button', { name: 'Stop recording' }).click()
  await expect(card.getByRole('status').filter({ hasText: 'Recorded 3 steps as “GPS stimuli”.' })).toBeVisible()
  await card.getByRole('button', { name: 'Test procedure text' }).click()
  const procedure = card.getByLabel('Test procedure text')
  await expect(procedure).toHaveValue(/The simulated clock starts at 2026-09-27T14:00:00\.000Z, which fixes the GPS sky the receivers see\./)
  await expect(procedure).toHaveValue(/\n1\. (At the start|At [\d.]+ s), on GPS 1, set the stop-transmitting fault\.\n2\. (At [\d.]+ s|Then), on GPS 2, override 130: FORCE 99\.\n3\. (At [\d.]+ s|Then), on GPS 1, clear the stop-transmitting fault\./)

  // Played back on a restarted bench, a day later by the wall clock: it starts at the recorded time (the same GPS sky),
  // and the tab shows the scripted override as its own, and the cleared fault as clear.
  await page.clock.setFixedTime(new Date('2026-09-28T09:30:00Z'))
  await card.getByRole('button', { name: 'Run the scenario' }).click()
  await expect(card.getByRole('status').filter({ hasText: /^NO CHECKS/ })).toBeVisible({ timeout: 15_000 })
  const [download] = await Promise.all([page.waitForEvent('download'), card.getByRole('button', { name: 'Download run report' }).click()])
  const report = await readFile(await download.path(), 'utf8')
  expect(report).toContain('- Started: 2026-09-27T14:00:00.000Z')
  expect(report).toMatch(/\| 2 \| GPS 2 \| [\d.]+ s \| override \| label 130; kind FORCE; amount 99 \| override 130: FORCE 99 \|/)
  expect(report).toMatch(/\| 3 \| GPS 1 \| [\d.]+ s \| fault \| fault STOP_TRANSMITTING; on false \| clear the stop-transmitting fault \|/)
  await tab(page, 'GPS sensors')
  await page.getByText('GPS 2 bus monitor').click()
  await expect(hil.locator('td.value')).toHaveText('99')
  await expect(hil).toContainText('FORCE 99')
  await expect(page.getByLabel('GPS 1 Stop transmitting')).not.toBeChecked()
})

test('an FMS failure in flight reverts the flight modes, and Pause still works (R02)', async ({ page }) => {
  await open(page)
  await page.getByLabel('Simulation rate').selectOption('16')
  await page.getByRole('button', { name: 'Fly' }).click()
  const modes = page.getByRole('status', { name: 'Flight modes' })
  await expect(modes).toContainText('LNAV')
  await tab(page, 'Conditions')
  await page.getByLabel('FMS failure').check()
  await expect(modes).toContainText('HDG HOLD')
  await expect(modes).toContainText('ALT HOLD')
  await expect(page.getByText(/^Last mode change: FMS FAILURE/)).toBeVisible()
  // Pause is a bench control: it stays usable whatever has failed in the simulated aircraft.
  const pause = page.getByRole('button', { name: 'Pause' })
  await expect(pause).toBeEnabled()
  await pause.click()
  await expect(page.getByRole('button', { name: 'Fly' })).toBeVisible()
  await expect(page.getByRole('button', { name: 'LNAV', exact: true })).toBeDisabled()
})

test('the PFD draws the selected heading: a cyan bug on the heading scale and its value, following HDG SEL (B4.4)', async ({ page }) => {
  await open(page)
  const efis = page.getByRole('region', { name: 'EFIS' })
  const bug = efis.getByTestId('pfd-selected-heading')
  await expect(bug).toBeVisible()
  await page.getByLabel('Selected heading').fill('45')
  await page.getByRole('button', { name: 'HDG SEL' }).click()
  await expect(efis.getByTestId('pfd-selected-heading-value')).toHaveText('HDG 045')
  await expect(efis.getByTestId('fma-roll')).toHaveText('HDG')
  await expect(bug).toHaveAttribute('fill', '#48d4ff')
})

test('the EFIS shows the FMS modes, route and TO waypoint, and flags them when the FMS fails', async ({ page }) => {
  await open(page)
  const efis = page.getByRole('region', { name: 'EFIS' })
  // The helicopter profile: the autopilot's axes, collective, pitch and roll/yaw.
  await expect(efis.getByTestId('fma-collective')).toHaveText('ALT')
  await expect(efis.getByTestId('fma-pitch')).toHaveText('IAS')
  await expect(efis.getByTestId('fma-roll')).toHaveText('NAV')
  await expect(efis.getByTestId('nd-to-wpt')).toContainText('MUN')
  await expect(efis.getByTestId('nd-route')).toBeVisible()
  await expect(efis.getByTestId('nav-source')).toHaveText(/^FMS1 TERM$/)
  await tab(page, 'Conditions')
  await page.getByLabel('FMS failure').check()
  await page.getByLabel('Simulation rate').selectOption('4')
  await page.getByRole('button', { name: 'Fly' }).click()
  await expect(efis.getByTestId('pfd-fms-flag')).toHaveText('FMS FAIL')
  await expect(efis.getByTestId('nd-map-flag')).toHaveText('MAP')
  await expect(efis.getByTestId('nd-route')).toHaveCount(0)
  await expect(efis.getByTestId('fma-roll')).toHaveText('HDG')
  await expect(efis.getByTestId('fma-collective')).toHaveText('ALT')
})

// Rev 3 B1.7 (h) and D-R: the aircraft freeze. Not flying and with no run, the bench's clock runs while the aircraft and
// its fuel stand still; a moving waypoint, placed by the simulation clock from its epoch (#1306), keeps moving.
test('the aircraft freeze: the clock runs, the fuel and aircraft stand still, and a moving waypoint keeps moving (B1.7 h)', async ({ page }) => {
  await open(page)
  await expect(page.getByText('Aircraft frozen: the clock runs.')).toBeVisible()
  const panel = page.locator('.fmsCdu')
  const typeIn = async (text: string) => { await panel.focus(); await page.keyboard.type(text) }
  // MOVING WPT (INIT/REF 2/2, 6L): SHIP1 at a position, moving east at 60 kt.
  const movingPage = async () => { await key(page, 'INIT_REF').click(); await key(page, 'NEXT').click(); await key(page, 'LSK6L').click(); await expectLine(page, 0, /^MOVING WPT/) }
  await movingPage()
  // Each entry leaves the scratchpad empty once taken.
  for (const [text, lsk] of [['SHIP1', 'LSK1L'], ['N4520.0W07540.0', 'LSK2L'], ['090/60', 'LSK1R']] as const) {
    await typeIn(text)
    await expectLine(page, 13, new RegExp(`^${text.replace('/', '\\/')}`))
    await key(page, lsk).click()
    await expectLine(page, 13, /^\s*$/)
  }
  await key(page, 'LSK6R').click()
  await expectLine(page, 6, /^SHIP1 090°\/60KT/)
  // Its position, on the line below, now; the motion line carries nothing over it.
  await expectLine(page, 6, /^SHIP1 090°\/60KT\s*$/)
  await expectLine(page, 7, /^N4520\.0W075\d\d\.\d\s*$/)
  const start = (await screenLines(page))[7]
  // The fuel on PROGRESS 2/4, read before and after the waypoint has moved.
  const fuelNow = async () => {
    await key(page, 'PROG').click()
    await key(page, 'NEXT').click()
    await expectLine(page, 0, /PROGRESS\s+2\/4/)
    return (await screenLines(page))[2]
  }
  const fuel = await fuelNow()
  await movingPage()
  await expect.poll(async () => (await screenLines(page))[7], { timeout: 20_000 }).not.toBe(start)
  expect(await fuelNow()).toBe(fuel)
  // Still frozen: nothing started the flight.
  await expect(page.getByText('Aircraft frozen: the clock runs.')).toBeVisible()
})

test('the bench tools are tabs under the cockpit, keyboard-navigable, and the chosen one is remembered', async ({ page }) => {
  // A fresh context starts with no remembered tab; this test keeps what it stores across the reload.
  await page.goto('/tests/fixtures/fms-cdu.html')
  await expect(page.locator('.fmsCdu')).toBeVisible()
  const tabs = page.getByRole('tablist', { name: 'Bench tools' }).getByRole('tab')
  await expect(tabs).toHaveText(['Scenarios', 'Conditions', 'GPS sensors', 'Nav data', 'Lighting and keys'])
  await expect(page.getByRole('tab', { name: 'Scenarios' })).toHaveAttribute('aria-selected', 'true')
  await expect(page.getByRole('region', { name: 'Scenarios' })).toBeVisible()
  await tab(page, 'GPS sensors')
  await expect(page.getByRole('tab', { name: 'GPS sensors' })).toHaveAttribute('aria-selected', 'true')
  await expect(page.getByRole('region', { name: 'GPS 1', exact: true })).toBeVisible()
  await expect(page.getByRole('region', { name: 'GPS 2', exact: true })).toBeVisible()
  await expect(page.getByRole('region', { name: 'Scenarios' })).toBeHidden()
  await page.reload()
  await expect(page.getByRole('tab', { name: 'GPS sensors' })).toHaveAttribute('aria-selected', 'true')
  await expect(page.getByRole('region', { name: 'Sensor routing' })).toBeVisible()
  await page.getByRole('tab', { name: 'GPS sensors' }).focus()
  await page.keyboard.press('ArrowRight')
  await expect(page.getByRole('tab', { name: 'Nav data' })).toHaveAttribute('aria-selected', 'true')
  await expect(page.getByLabel('ARINC 424 navigation data file')).toBeVisible()
})

test('the GPS sensors tab drives the FMS receivers: a fault on GPS 1 moves the FMS to GPS 2, both give GPS NAV LOST', async ({ page }) => {
  // The bench starts its simulated time at the wall clock, and the sky moves with it: pin it, so the geometry is the same
  // whenever the test runs.
  await page.clock.setFixedTime(new Date('2026-09-28T14:00:00Z'))
  await open(page)
  await tab(page, 'GPS sensors')
  const gps1 = page.getByRole('region', { name: 'GPS 1', exact: true })
  const gps2 = page.getByRole('region', { name: 'GPS 2', exact: true })
  const routing = page.getByRole('img', { name: /^Sensor routing/ })
  // The FMS's receivers start warm, navigating; the FMS is on GPS 1.
  await expect(gps1.getByTestId('gps-mode')).toHaveText(/^(NAV|SBAS NAV)$/)
  await expect(gps2.getByTestId('gps-mode')).toHaveText(/^(NAV|SBAS NAV)$/)
  await expect(routing).toHaveAttribute('aria-label', /FMS on GPS1$/)
  await expect(page.getByTestId('route-link-gps1')).not.toHaveClass(/dashed/)
  await expect(gps1.getByTestId('gps-in-use')).toBeVisible()

  const used = async () => Number(((await gps1.getByTestId('gps-used').innerText()).split('/'))[0])
  const before = await used()
  // Mask one satellite GPS 1 is using: whatever the sky, it is then used one fewer.
  const prn = await gps1.getByRole('list', { name: 'Signal strength, dB-Hz' }).locator('li:has(.bar.used) small').first().innerText()
  await page.getByRole('group', { name: 'GPS 1 mask satellites' }).getByRole('button', { name: prn, exact: true }).click()
  await expect.poll(used).toBe(before - 1)
  await expect(gps1.getByRole('list', { name: 'GPS 1 active faults' })).toContainText('1 MASKED')

  // A GPS 1 receiver fault reaches the FMS at once: it navigates on GPS 2, and the strip shows it.
  await page.getByLabel('GPS 1 Receiver fault').check()
  await expect(gps1.getByTestId('gps-mode')).toHaveText('FAULT')
  await expect(gps1.getByRole('list', { name: 'GPS 1 active faults' })).toContainText('RECEIVER FAULT')
  await expect(routing).toHaveAttribute('aria-label', /FMS on GPS2$/)
  await expect(page.getByTestId('route-link-gps1')).toHaveClass(/dashed/)
  await expect(page.getByTestId('route-link-gps2')).not.toHaveClass(/dashed/)
  await expect(gps2.getByTestId('gps-in-use')).toBeVisible()
  // Its bus: position words Failure Warning, the status word still Normal.
  await page.getByText('GPS 1 bus monitor').click()
  const monitor = page.getByRole('table', { name: 'GPS 1 bus monitor' })
  await expect(monitor.locator('tr[data-label="110"] .fmsGpsSsm')).toHaveText('FW')
  await expect(monitor.locator('tr[data-label="273"] .fmsGpsSsm')).toHaveText('NORMAL')

  // Both faulted: the FMS has no GPS and says so on the CDU.
  await page.getByLabel('GPS 2 Receiver fault').check()
  await expectLine(page, 13, /^GPS NAV LOST/)
  await expect(routing).not.toHaveAttribute('aria-label', /FMS on GPS/)

  // The product's 12 px text floor (tests/production) holds across the tab, open monitor and chips included.
  const small = await page.locator('.fmsGps').evaluate(root => [...root.querySelectorAll('*')]
    .filter(element => element.children.length === 0 && (element.textContent ?? '').trim() && parseFloat(getComputedStyle(element).fontSize) < 12)
    .map(element => `${element.tagName}.${element.getAttribute('class') ?? ''} ${getComputedStyle(element).fontSize}`))
  expect(small).toEqual([])
})

test('AUTO keeps GPS 2 after GPS 1 recovers: the strip says so, GPS 1 shows as available, and the CDU shows the source and the note', async ({ page }) => {
  await open(page)
  await tab(page, 'GPS sensors')
  const routing = page.getByRole('img', { name: /^Sensor routing/ })
  await expect(page.getByTestId('route-current-source')).toHaveText('AUTO — FMS on GPS1')
  await page.getByText('GPS 1 bus monitor').click()
  const hil = page.getByRole('table', { name: 'GPS 1 bus monitor' }).locator('tr[data-label="130"]')
  await hil.getByLabel('Override 130', { exact: true }).selectOption('FORCE')
  await hil.getByLabel('Override 130 amount').fill('99')
  await hil.getByRole('button', { name: 'Set' }).click()
  await expect(routing).toHaveAttribute('aria-label', /AUTO — FMS on GPS2$/)
  await hil.getByRole('button', { name: 'Clear' }).click()
  await expect(page.getByTestId('route-gps1')).toContainText('available / standby')
  await expect(page.getByTestId('route-current-source')).toHaveText('AUTO — FMS on GPS2')
  await expect(page.getByTestId('route-source-note')).toContainText('Last transfer: GPS1 to GPS2 (GPS1 NOT USABLE: HIL 99.00 > HAL')
  // The CDU's own presentation: NAV OPTIONS under GPS NAV, not only the bench.
  for (const id of ['INIT_REF', 'NEXT', 'LSK5R', 'LSK6R']) await key(page, id).click()
  await expectLine(page, 0, /NAV OPTIONS/)
  await expectLine(page, 8, /^AUTO GPS2\s+GPS1 STBY$/)
  await expectLine(page, 9, /^AUTO KEEPS SUITABLE RCVR$/)
  await expectLine(page, 10, /^GPS1 INITIAL IF EQUAL$/)
  // The product's 12 px floor holds for every cell on the screen, the note included.
  const sizes = await page.locator('.fmsCduScreen .cduCell').evaluateAll(cells => cells.map(cell => parseFloat(getComputedStyle(cell).fontSize)))
  expect(sizes.length).toBeGreaterThan(0)
  expect(Math.min(...sizes)).toBeGreaterThanOrEqual(12)
})

test('the FMS GPS selection is set from the routing strip, and the integrity condition holds the satellite masking', async ({ page }) => {
  await open(page)
  await tab(page, 'GPS sensors')
  const routing = page.getByRole('img', { name: /^Sensor routing/ })
  // In use and standby are the FMS's own judgement of each receiver: GPS 2 usable, so an eligible standby.
  await expect(page.getByTestId('route-gps1')).toContainText('in use')
  await expect(page.getByTestId('route-gps2')).toContainText('standby')
  // GPS 1's HIL forced to 99 NM: still navigating internally, but the FMS rejects it for integrity and says why.
  await page.getByText('GPS 1 bus monitor').click()
  const hil = page.getByRole('table', { name: 'GPS 1 bus monitor' }).locator('tr[data-label="130"]')
  await hil.getByLabel('Override 130', { exact: true }).selectOption('FORCE')
  await hil.getByLabel('Override 130 amount').fill('99')
  await hil.getByRole('button', { name: 'Set' }).click()
  await expect(routing).toHaveAttribute('aria-label', /FMS on GPS2$/)
  await expect(page.getByTestId('route-gps1')).toContainText('not usable · HIL over limit')
  await expect(page.getByTestId('route-gps1')).not.toContainText('standby')
  await expect(page.getByTestId('route-gps2')).toContainText('in use')
  await hil.getByRole('button', { name: 'Clear' }).click()
  // GPS 1 usable again: AUTO keeps GPS 2 (no needless switching back, the approach-aware AUTO policy), GPS 1 on standby.
  await expect(page.getByTestId('route-gps1')).toContainText('standby')
  await expect(routing).toHaveAttribute('aria-label', /FMS on GPS2$/)
  await page.getByLabel('FMS GPS selection').selectOption('GPS1')
  await expect(routing).toHaveAttribute('aria-label', /FMS on GPS1$/)
  await page.getByLabel('FMS GPS selection').selectOption('GPS2')
  await expect(routing).toHaveAttribute('aria-label', /FMS on GPS2$/)
  await page.getByLabel('FMS GPS selection').selectOption('OFF')
  await expect(routing).not.toHaveAttribute('aria-label', /FMS on GPS/)
  await page.getByLabel('FMS GPS selection').selectOption('AUTO')
  await expect(routing).toHaveAttribute('aria-label', /FMS on GPS1$/)
  const faults = page.getByRole('region', { name: 'GPS 1 faults' })
  await expect(faults.getByRole('button', { name: /^Mask low satellites/ })).toBeEnabled()
  await tab(page, 'Conditions')
  await page.getByRole('checkbox', { name: /^GPS integrity lost/ }).check()
  await tab(page, 'GPS sensors')
  await expect(faults.getByRole('note')).toContainText('GPS integrity lost condition holds')
  await expect(faults.getByRole('button', { name: /^Mask low satellites/ })).toBeDisabled()
  await expect(page.getByTestId('route-current-source')).toHaveText('AUTO — FMS on GPS1 (uncertain)')
  await expect(page.getByTestId('route-gps1')).toContainText('in use · uncertain')
  await expect(page.getByTestId('route-link-gps1')).not.toHaveClass(/dashed/)
})

test('a status word is overridden field by field from the bus monitor: what is transmitted changes, not what the receiver knows', async ({ page }) => {
  await open(page)
  await tab(page, 'GPS sensors')
  const gps1 = page.getByRole('region', { name: 'GPS 1', exact: true })
  await page.getByLabel('Simulation rate').selectOption('64')
  await page.getByRole('button', { name: 'Fly' }).click()
  await expect(gps1.getByTestId('gps-mode')).toHaveText(/^(NAV|SBAS NAV)$/, { timeout: 20_000 })
  await page.getByRole('button', { name: 'Pause' }).click()
  await page.getByText('GPS 1 bus monitor').click()
  const monitor = page.getByRole('table', { name: 'GPS 1 bus monitor' })
  const row = monitor.locator('tr[data-label="273"]')
  await row.getByLabel('Status field 273').selectOption('mode')
  await row.getByLabel('Status value 273').selectOption('FAULT')
  await row.getByRole('button', { name: 'Set' }).click()
  await expect(row.locator('td.value')).toContainText('mode FAULT')
  await expect(row).toContainText('FORCE mode')
  // The card shows the receiver itself, still navigating; the scaling word has no approach to scale.
  await expect(gps1.getByTestId('gps-mode')).toHaveText(/^(NAV|SBAS NAV)$/)
  await expect(monitor.locator('tr[data-label="scale"] .fmsGpsSsm')).toHaveText('NCD')
  // The FMS reads the transmitted word: GPS 1 declares a fault, so it is not usable, and the routing strip names the word.
  await expect(page.getByTestId('route-gps1')).toContainText('not usable · receiver fault (273 mode fault)')
  await row.getByRole('button', { name: 'Clear' }).click()
  await expect(row.locator('td.value')).not.toContainText('mode FAULT')
  await expect(page.getByTestId('route-gps1')).not.toContainText('not usable')
})

test('GPS faults and overrides survive leaving the tab: shown, cleared one at a time, and an unrelated change leaves them alone', async ({ page }) => {
  await open(page)
  await tab(page, 'GPS sensors')
  const gps1 = page.getByRole('region', { name: 'GPS 1', exact: true })
  const stopped = page.getByLabel('GPS 1 Stop transmitting')
  await stopped.check()
  await expect(gps1).toContainText('NOT TRANSMITTING')
  await page.getByText('GPS 2 bus monitor').click()
  const hil = page.getByRole('table', { name: 'GPS 2 bus monitor' }).locator('tr[data-label="130"]')
  await hil.getByLabel('Override 130', { exact: true }).selectOption('FORCE')
  await hil.getByLabel('Override 130 amount').fill('99')
  await hil.getByRole('button', { name: 'Set' }).click()
  await expect(hil.locator('td.value')).toHaveText('99')

  // A real round trip: the tab is unmounted and mounted again.
  await tab(page, 'Conditions')
  await tab(page, 'GPS sensors')
  await expect(stopped).toBeChecked()
  await expect(gps1.getByRole('list', { name: 'GPS 1 active faults' })).toContainText('STOPPED TRANSMITTING')
  await expect(gps1).toContainText('NOT TRANSMITTING')
  await page.getByText('GPS 2 bus monitor').click()
  await expect(hil.locator('td.value')).toHaveText('99')
  await expect(hil).toContainText('FORCE 99')
  await expect(hil.getByRole('button', { name: 'Clear' })).toBeVisible()

  // An unrelated control changes only its own fault: GPS 1 stays silent.
  await page.getByLabel('GPS 1 Baro lost').check()
  await expect(gps1).toContainText('NOT TRANSMITTING')
  await expect(gps1.getByRole('list', { name: 'GPS 1 active faults' })).toContainText('BARO LOST')
  await expect(gps1.getByRole('list', { name: 'GPS 1 active faults' })).toContainText('STOPPED TRANSMITTING')

  // A targeted clear removes that override only.
  await hil.getByRole('button', { name: 'Clear' }).click()
  await expect(hil.locator('td.value')).not.toHaveText('99')
  await expect(hil.getByRole('button', { name: 'Clear' })).toHaveCount(0)
  await expect(stopped).toBeChecked()
  await expect(gps1).toContainText('NOT TRANSMITTING')
})

test('a numeric word can be forced with a status from the bus monitor, and the FMS rejects the receiver for it', async ({ page }) => {
  await open(page)
  await tab(page, 'GPS sensors')
  await page.getByText('GPS 1 bus monitor').click()
  const latitude = page.getByRole('table', { name: 'GPS 1 bus monitor' }).locator('tr[data-label="110"]')
  await latitude.getByLabel('Override 110', { exact: true }).selectOption('FORCE')
  await latitude.getByLabel('Override 110 amount').fill('45.3')
  await latitude.getByLabel('Override 110 status').selectOption('NCD')
  await latitude.getByRole('button', { name: 'Set' }).click()
  await expect(latitude.locator('.fmsGpsSsm')).toHaveText('NCD')
  await expect(latitude).toContainText('FORCE 45.3 NCD')
  // Without a NORMAL latitude there is no fix: the FMS navigates on GPS 2 and says why GPS 1 is not usable.
  await expect(page.getByRole('img', { name: /^Sensor routing/ })).toHaveAttribute('aria-label', /FMS on GPS2$/)
  await expect(page.getByTestId('route-gps1')).toContainText('not usable · no fix')
})

test('the KBTV demonstration defaults to S300 advisory VNAV and its explicit later-SBAS LPV scenario passes on the bench', async ({ page }) => {
  await open(page)
  await tab(page, 'Nav data')
  const demo = page.getByRole('group', { name: 'Real-data demonstration' })
  await expect(demo).toContainText(/public domain, for demonstration only, not for navigation/)
  const load = demo.getByRole('button', { name: 'Load the KBTV demonstration (FAA CIFP 2609)' })
  await load.click()
  await expect(page.getByRole('status').filter({ hasText: /^KBTV demonstration loaded and active: cycle CIFP2609/ })).toBeVisible()
  await expect(page.getByText(/^Active CIFP2609 \(FAA CIFP 2609, KBTV extract/)).toBeVisible()
  await expect(load).toBeDisabled()
  // The one-click start: a restarted simulation, KBTV loaded, the aircraft before STAEV with the approach armed.
  await demo.getByRole('button', { name: 'Set up KBTV RNAV RWY 15' }).click()
  await expect(demo.getByRole('status')).toHaveText(/^Set up: KBTV RNAV \(GPS\) RWY 15/)
  await key(page, 'PROG').click()
  await expectLine(page, 2, /^STAEV\b/)
  for (const id of ['INIT_REF', 'NEXT', 'LSK1R']) await key(page, id).click()
  await expectLine(page, 0, /^ACT VNAV R15\s+1\/1$/)
  await expect(page.getByTestId('fms-bench-profile')).toContainText('cma9000-s300-heli-civil v5')
  await page.screenshot({ path: 'test-results/s300-kbtv-advisory.png', fullPage: true })
  // The library scenario flies it from the same start state, on a restarted simulation.
  await tab(page, 'Scenarios')
  const card = page.getByRole('region', { name: 'Scenarios' })
  await page.getByLabel('Simulation rate').selectOption('64')
  await card.getByLabel('Scenario', { exact: true }).selectOption({ label: 'KBTV RNAV (GPS) RWY 15, LPV on the published FAS' })
  await card.getByRole('button', { name: 'Run the scenario' }).click()
  await expect(page.getByTestId('fms-bench-profile')).toContainText('cma9000-later-sbas-heli v1')
  await expect(card.getByRole('status').filter({ hasText: /^PASS/ })).toBeVisible({ timeout: 45_000 })
  await expect(card.getByRole('list', { name: 'Scenario steps' }).locator('li[data-status="pass"]')).toHaveCount(5)
})

test('the helicopter autopilot fields keep only what they accept: digits, and a sign for the vertical speed (review of B3a)', async ({ page }) => {
  await open(page)
  await page.getByLabel('Preselected altitude').fill('12a3')
  await expect(page.getByLabel('Preselected altitude')).toHaveValue('123')
  await page.getByLabel('Vertical speed').fill('-8x00')
  await expect(page.getByLabel('Vertical speed')).toHaveValue('-800')
  await page.getByLabel('Selected speed').fill('9z0')
  await expect(page.getByLabel('Selected speed')).toHaveValue('90')
  // GSPD: a ground speed, digits only; in cruise it is refused and the pitch axis stays IAS (B3.1).
  const gspd = page.getByRole('button', { name: 'GSPD', exact: true })
  await expect(gspd).toBeDisabled()
  await page.getByLabel('Selected ground speed').fill('x5')
  await expect(page.getByLabel('Selected ground speed')).toHaveValue('5')
  await page.getByLabel('Selected ground speed').fill('15')
  await expect(page.getByLabel('Selected ground speed')).toHaveValue('15')
  await gspd.click()
  await expect(page.getByRole('region', { name: 'EFIS' }).getByTestId('fma-pitch')).toHaveText('IAS')
  await expect(page.getByLabel('Selected ground speed')).toHaveValue('15')
})

test('the PinS crew continuation requires MAP passage and the actual chart condition, then leaves instrument guidance', async ({ page }) => {
  await open(page)
  const card = page.getByRole('region', { name: 'Scenarios' })
  const scenario = { id: 'pins-ui', title: 'PinS crew controls', objective: 'UI wiring at the published MAP', maxSeconds: 1,
    start: '87n-rnav190-final', steps: [{ when: { kind: 'start' }, action: { kind: 'expectActive', waypoint: 'STAYS' } }] }
  await card.getByLabel('Scenario file').setInputFiles({ name: 'pins-ui.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify(scenario)) })
  await card.getByRole('button', { name: 'Run the scenario' }).click()
  await expect(card.getByRole('status').filter({ hasText: /^PASS/ })).toBeVisible()
  const continueButton = page.getByRole('button', { name: 'Continue from MAP' })
  await expect(continueButton).toBeDisabled()
  await expect(page.getByLabel('Landing area in sight')).toHaveCount(0)
  await page.getByRole('button', { name: 'Jump to next waypoint' }).click()
  await page.getByRole('button', { name: 'Jump to next waypoint' }).click()
  await expect(continueButton).toBeDisabled()
  await page.getByLabel('Basic VFR conditions met').check()
  await expect(continueButton).toBeEnabled()
  await continueButton.locator('..').screenshot({ path: test.info().outputPath('pins-crew-conditions.png') })
  await continueButton.click()
  await expect(page.getByLabel('Guidance')).toContainText('crew flying the visual segment')
  await expect(page.getByLabel('Guidance')).toContainText('HDG')
  await page.locator('.fmsBench').screenshot({ path: test.info().outputPath('pins-crew-continuation.png') })
})

test('the 87N mission: after ACTIVATE and EXEC over the mark, the map draws the FMS joining path to JN (Phase 1)', async ({ page }) => {
  // The scenario plays at 16× to the join, which takes 28-29 s alone: over the 30 s default, and longer under load (#1305).
  test.setTimeout(120_000)
  await open(page)
  // The moving map as the lower display (it draws the route, holds, search patterns and the join).
  await page.getByRole('radiogroup', { name: 'Lower display' }).getByText('Engineering map', { exact: true }).click()
  await page.getByLabel('Map range').selectOption('5')
  await tab(page, 'Scenarios')
  const card = page.getByRole('region', { name: 'Scenarios' })
  await page.getByLabel('Simulation rate').selectOption('16')
  await card.getByLabel('Scenario', { exact: true }).selectOption({ label: '87N offshore SAR: search, hover at the mark, the Copter RNAV 190 and its missed approach' })
  await card.getByRole('button', { name: 'Run the scenario' }).click()
  const join = page.locator('[data-testid="hover-join"]')
  await expect(join).toBeVisible({ timeout: 60_000 })
  await page.getByRole('button', { name: 'Pause' }).click()
  // A curved path: the turns are drawn as arcs, many points, not a straight line to JN.
  const points = ((await join.getAttribute('d')) ?? '').split(/[ML]/).filter(Boolean).length
  expect(points).toBeGreaterThan(8)
  const box = (await join.boundingBox())!
  expect(box.width).toBeGreaterThan(10)
  expect(box.height).toBeGreaterThan(10)
  await page.locator('.fmsMap').screenshot({ path: test.info().outputPath('hover-join-map.png') })
})

// Plan B4.1, B3.4: below each FMA column, the modes armed on that axis in white and a mode a failure just took away in
// amber (the logic is fms-heli-displays.spec.ts).
const WHITE = 'rgb(242, 244, 247)', AMBER = 'rgb(255, 176, 32)', GREEN = 'rgb(67, 227, 124)'
const fill = (locator: import('@playwright/test').Locator) => locator.evaluate(element => getComputedStyle(element).fill)

test('the helicopter FMA shows NAV armed in white on the roll axis, and NAV lost to an FMS failure in amber (B4.1, B3.4)', async ({ page }) => {
  await open(page)
  const efis = page.getByRole('region', { name: 'EFIS' })
  await expect(efis.getByTestId('fma-roll')).toHaveText('NAV')
  await expect(efis.getByTestId('fma-roll-armed')).toHaveText('')
  await expect(efis.getByTestId('fma-roll-degraded')).toHaveCount(0)
  await page.getByLabel('Selected heading').fill('090')
  await page.getByRole('button', { name: 'HDG SEL' }).click()
  await expect(efis.getByTestId('fma-roll')).toHaveText('HDG')
  await page.getByRole('button', { name: 'LNAV', exact: true }).click()
  await expect(efis.getByTestId('fma-roll-armed')).toHaveText('NAV')
  expect(await fill(efis.getByTestId('fma-roll-armed'))).toBe(WHITE)
  // The collective and pitch columns carry nothing armed here.
  await expect(efis.getByTestId('fma-pitch-armed')).toHaveText('')
  // Captured: engaged green in the top line, the armed line empty.
  await page.getByLabel('Simulation rate').selectOption('16')
  await page.getByRole('button', { name: 'Fly' }).click()
  await expect(efis.getByTestId('fma-roll')).toHaveText('NAV', { timeout: 60_000 })
  await expect(efis.getByTestId('fma-roll-armed')).toHaveText('')
  // The FMS fails: HDG engaged, NAV amber beside it for the capture-box time, then gone.
  await page.getByLabel('Simulation rate').selectOption('1')
  await tab(page, 'Conditions')
  await page.getByLabel('FMS failure').check()
  await expect(efis.getByTestId('fma-roll')).toHaveText('HDG')
  await expect(efis.getByTestId('fma-roll-degraded')).toHaveText('NAV')
  expect(await fill(efis.getByTestId('fma-roll-degraded'))).toBe(AMBER)
  await page.getByLabel('Simulation rate').selectOption('16')
  await expect(efis.getByTestId('fma-roll-degraded')).toHaveCount(0, { timeout: 30_000 })
})

test('in the low-speed regime the ND draws the ground velocity, not the bank trend: green, 3 px a knot (B4.5)', async ({ page }) => {
  test.setTimeout(240_000)
  await open(page)
  const efis = page.getByRole('region', { name: 'EFIS' })
  await expect(efis.getByTestId('nd-trend')).toHaveCount(1)
  await expect(efis.getByTestId('nd-ground-velocity')).toHaveCount(0)
  await tab(page, 'Scenarios')
  const card = page.getByRole('region', { name: 'Scenarios' })
  await page.getByLabel('Simulation rate').selectOption('64')
  await card.getByLabel('Scenario', { exact: true }).selectOption({ label: '87N offshore SAR: search, hover at the mark, the Copter RNAV 190 and its missed approach' })
  await card.getByRole('button', { name: 'Run the scenario' }).click()
  const vector = efis.getByTestId('nd-ground-velocity')
  await expect(vector).toBeVisible({ timeout: 180_000 })
  await page.getByRole('button', { name: 'Pause' }).click()
  await expect(efis.getByTestId('nd-trend')).toHaveCount(0)
  expect(await fill(vector.locator('polygon, text').first())).toBe(GREEN)
  await expect(vector).toContainText('GND VEL (BENCH)')
  // The arrow's length is the labelled ground speed at 3 px a knot, up to 40 kt.
  const knots = Number(/(\d+) KT/.exec((await vector.textContent()) ?? '')![1])
  const line = vector.locator('line')
  const [y1, y2] = await Promise.all([line.getAttribute('y1'), line.getAttribute('y2')])
  expect(Number(y1) - Number(y2)).toBeCloseTo(Math.min(knots, 40) * 3, -0.5)
  await efis.screenshot({ path: test.info().outputPath('nd-ground-velocity.png') })
})

test('C.10: the executed 87N approach shows its chart notes on the Nav data tab, for reference only', async ({ page }) => {
  await open(page)
  await tab(page, 'Nav data')
  // The invented demonstration approach has no chart: no notes.
  await expect(page.getByRole('region', { name: 'Procedure notes' })).toHaveCount(0)
  await page.getByRole('group', { name: 'Real-data demonstration' }).getByRole('button', { name: 'Set up 87N COPTER RNAV 190 final' }).click()
  const notes = page.getByRole('region', { name: 'Procedure notes' })
  await expect(notes).toBeVisible()
  await expect(notes).toContainText('87N R190, from the chart (FAA AL-9013 COPTER RNAV (GPS) 190, Orig-B, d-TPP 2609)')
  await expect(notes).toContainText('never enforced')
  const items = notes.getByTestId('fms-procedure-notes').getByRole('listitem')
  await expect(items).toHaveCount(9)
  await expect(items.nth(1)).toHaveText('Procedure NA at night.')
  await expect(items.nth(5)).toHaveText('Limit final and missed approach to 70K.')
  await expect(items.nth(8)).toHaveText('LNAV MDA 560-1.')
})

test('B1.1: the PFD writes the altimeter setting beside the altitude; setting STD or injecting an error changes the reading, never the radio or physical height', async ({ page }) => {
  await open(page)
  const efis = page.getByRole('region', { name: 'EFIS' })
  const pfdBaro = efis.getByTestId('pfd-baro')
  await expect(pfdBaro).toHaveText('QNH 1013')
  await tab(page, 'Conditions')
  const card = page.getByRole('region', { name: 'Barometric altitude' })
  const readout = card.getByTestId('baro-readout')
  const heights = async () => {
    const text = (await readout.textContent()) ?? ''
    const [, physical, baro, indicated] = /Physical height (-?\d+) ft, barometric (-?\d+) ft,\s*indicated (-?\d+) ft/.exec(text.replace(/\s+/g, ' '))!
    return { physical: Number(physical), baro: Number(baro), indicated: Number(indicated) }
  }
  const before = await heights()
  expect(before.baro).toBe(before.physical)
  expect(before.indicated).toBe(before.physical)
  // A low declared: the altimeter, still set to 1013, reads high by about 27 ft a hectopascal.
  await card.getByLabel('Declared QNH (hPa)').fill('1003')
  await card.getByRole('button', { name: 'Declare the QNH' }).click()
  await expect.poll(async () => (await heights()).indicated - before.physical).toBeGreaterThan(260)
  await card.getByLabel('Altimeter setting (QNH, hPa)').fill('1003')
  await card.getByRole('button', { name: 'Set QNH' }).click()
  await expect(pfdBaro).toHaveText('QNH 1003')
  await expect.poll(async () => (await heights()).indicated).toBe(before.physical)
  await card.getByRole('button', { name: 'STD' }).click()
  await expect(pfdBaro).toHaveText('STD')
  await expect(card.getByRole('button', { name: 'STD' })).toHaveAttribute('aria-pressed', 'true')
  // An injected error: the barometric reading moves by it, the physical height does not.
  await card.getByLabel('Baro error (ft)').fill('-200')
  await card.getByRole('button', { name: 'Inject the error' }).click()
  const after = await heights()
  expect(after.physical).toBe(before.physical)
  expect(after.baro).toBe(before.physical - 200)
  // Out of range: the buttons stay disabled.
  await card.getByLabel('Altimeter setting (QNH, hPa)').fill('800')
  await expect(card.getByRole('button', { name: 'Set QNH' })).toBeDisabled()
  await card.getByLabel('Baro error (ft)').fill('3000')
  await expect(card.getByRole('button', { name: 'Inject the error' })).toBeDisabled()
})

test('D-R: the Nav data tab shows each moving waypoint\'s age as a bench aid; it never expires', async ({ page }) => {
  await open(page)
  await key(page, 'INIT_REF').click()
  await key(page, 'NEXT').click()
  await key(page, 'LSK6L').click()
  await expectLine(page, 0, /^MOVING WPT/)
  await page.locator('.fmsCdu').focus()
  for (const [text, lsk] of [['SHIP1', 'LSK1L'], ['RDG180/5', 'LSK2L'], ['270/20', 'LSK1R']]) {
    await page.keyboard.type(text)
    await key(page, lsk).click()
  }
  await key(page, 'LSK6R').click()
  await tab(page, 'Nav data')
  const card = page.getByRole('region', { name: 'Moving waypoints' })
  await expect(card).toContainText('Bench aid')
  const item = card.getByTestId('fms-moving-waypoints').getByRole('listitem')
  await expect(item).toHaveText(/^SHIP1 270°\/20 kt, age 0:00:\d\d$/)
  // Flying on, it ages on the simulation clock (64 times real time): minutes, not seconds.
  await page.getByLabel('Simulation rate').selectOption('64')
  await page.getByRole('button', { name: 'Fly' }).click()
  await expect(item).toHaveText(/age 0:0[1-9]:\d\d|age 0:[1-5]\d:\d\d/, { timeout: 15_000 })
  await page.getByRole('button', { name: 'Pause' }).click()
})
