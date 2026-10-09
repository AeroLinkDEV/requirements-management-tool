import { readFile } from 'node:fs/promises'
import { expect, renderedTest as test } from './isolated-client-test'
import type { Page } from '@playwright/test'

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

// F14 primary browser owner: actual forms dispatch admitted stimuli, show independent measured/physical effects,
// record their payloads and timing, and replay those records through the runner. Engine-only tests cannot see this wiring.
test('F14 real bench sensor controls drive the full fault matrix and recorded replay reports the applied values', async ({ page }) => {
  test.setTimeout(120_000)
  await page.clock.setFixedTime(new Date('2026-09-27T14:00:00Z'))
  await open(page)
  const scenarios = page.getByRole('region', { name: 'Scenarios' })
  await scenarios.getByRole('button', { name: 'Record', exact: true }).click()
  await scenarios.getByLabel('Recording name').fill('F14 sensor controls')
  await tab(page, 'Conditions')
  const card = page.getByRole('region', { name: 'Sensor fault laboratory' })
  await card.getByLabel('Fault station ident').fill('ZZZZ')
  await card.getByLabel('Ground station stimulus').selectOption('DME_NO_REPLY')
  await card.getByRole('button', { name: 'Apply ground stimulus' }).click()
  await expect(card.getByTestId('sensor-stimulus-result')).toContainText('Refused: DME ZZZZ requires one unambiguous compatible facility')
  // Built-in BOBTU is a unique five-character NDB; the same admitted ident must fit the actual form.
  await card.getByLabel('Ground station stimulus').selectOption('NDB_OFF')
  await card.getByLabel('Fault station ident').fill('BOBTU')
  await expect(card.getByLabel('Fault station ident')).toHaveValue('BOBTU')
  await card.getByRole('button', { name: 'Apply ground stimulus' }).click()
  await expect(card.getByTestId('sensor-stimulus-result')).toContainText('take NDB BOBTU off the air')
  await card.getByLabel('Ground station stimulus').selectOption('NDB_ON')
  await card.getByRole('button', { name: 'Apply ground stimulus' }).click()
  await expect(card.getByTestId('sensor-stimulus-result')).toContainText('restore NDB BOBTU on the air')
  const radio = card.getByTestId('sensor-radio-readout')
  for (const device of ['nav1', 'nav2', 'dme1', 'dme2', 'tacan', 'adf', 'adf2']) {
    await card.getByLabel('Fault radio').selectOption(device)
    if (device === 'dme1' || device === 'dme2') {
      await expect(card.getByRole('button', { name: 'Apply control path' })).toBeDisabled()
      await expect(card).toContainText('DME tuning follows its paired NAV receiver.')
      if (device === 'dme1') await card.screenshot({ path: test.info().outputPath('f14-dme-control-boundary.png') })
    }
    for (const receiver of ['SILENT', 'FAILED', 'NORMAL']) {
      await card.getByLabel('Radio receiver state').selectOption(receiver)
      await card.getByRole('button', { name: 'Apply receiver state' }).click()
      await expect(radio).toContainText(`receiver ${receiver}`)
      await expect(radio).toContainText(receiver === 'NORMAL' ? /reported frequency (?!none)/ : 'reported frequency none')
    }
  }
  await card.getByLabel('Fault radio').selectOption('nav1')
  for (const control of ['LOST', 'NORMAL']) {
    await card.getByLabel('Radio control path').selectOption(control)
    await card.getByRole('button', { name: 'Apply control path' }).click()
    await expect(radio).toContainText(`control ${control}`)
    await expect(radio).toContainText(/reported frequency (?!none)/)
  }
  for (const bus of ['LOST', 'NORMAL']) {
    await card.getByLabel('Radio measurement bus').selectOption(bus)
    await card.getByRole('button', { name: 'Apply measurement bus' }).click()
    await expect(radio).toContainText(`bus ${bus}`)
    await expect(radio).toContainText(bus === 'NORMAL' ? /reported frequency (?!none)/ : 'reported frequency none')
  }
  await card.getByLabel('Fault station ident').fill('YOW')
  const world = card.getByTestId('sensor-world-readout')
  await expect(world).toContainText(/range NORMAL .*bearing NORMAL/)
  const before = await world.textContent()
  const bearing = (text: string) => Number(/bearing NORMAL ([\d.]+)/.exec(text)![1])
  const range = (text: string) => /range NORMAL ([\d.]+)/.exec(text)![1]
  for (const [operation, status] of [['DME_NO_REPLY', 'NCD'], ['DME_REPLY', 'NORMAL']] as const) {
    await card.getByLabel('Ground station stimulus').selectOption(operation)
    await card.getByRole('button', { name: 'Apply ground stimulus' }).click()
    await expect(world).toContainText(`range ${status}`)
    await expect(world).toContainText('bearing NORMAL')
  }
  await card.getByLabel('Ground station stimulus').selectOption('DME_IDENT')
  await card.getByLabel('DME reported ident').fill('BAD')
  await card.getByRole('button', { name: 'Apply ground stimulus' }).click()
  await card.getByLabel('Fault radio').selectOption('dme1')
  await expect(radio).toContainText('Range — NM; ident BAD')
  await card.getByLabel('DME reported ident').fill('')
  await card.getByRole('button', { name: 'Apply ground stimulus' }).click()
  await expect(radio).toContainText(/Range \d/)
  await card.getByLabel('Ground station stimulus').selectOption('VOR_BIAS')
  await card.getByLabel('VOR radial bias degrees').fill('10')
  await card.getByRole('button', { name: 'Apply ground stimulus' }).click()
  await expect.poll(async () => (bearing((await world.textContent())!) - bearing(before!) + 360) % 360).toBeCloseTo(10, 1)
  expect(range((await world.textContent())!)).toBe(range(before!))
  await card.getByLabel('VOR radial bias degrees').fill('0')
  await card.getByRole('button', { name: 'Apply ground stimulus' }).click()
  const air = card.getByTestId('sensor-air-readout')
  await card.getByLabel('Navigation TAS valid', { exact: true }).uncheck()
  await expect(air).toContainText(/TAS [\d.]+ kt \(invalid\)/)
  await card.getByLabel('Navigation TAS valid', { exact: true }).check()
  await card.getByLabel('Navigation heading valid', { exact: true }).uncheck()
  await expect(air).toContainText(/heading [\d.]+° \(invalid\)/)
  await card.getByLabel('Navigation heading valid', { exact: true }).check()
  await card.getByLabel('Navigation heading bias degrees').fill('30')
  await card.getByRole('button', { name: 'Apply heading bias' }).click()
  await expect.poll(async () => {
    const value = (await air.textContent())!
    const measured = Number(/heading ([\d.]+)°/.exec(value)![1])
    const physical = Number(/Physical heading ([\d.]+)°/.exec(value)![1])
    return (measured - physical + 360) % 360
  }).toBeCloseTo(30, 1)
  await card.getByLabel('Navigation heading bias degrees').fill('0')
  await card.getByRole('button', { name: 'Apply heading bias' }).click()
  for (const [label, sensor] of [['APIRS failed', 'APIRS'], ['Doppler (DVS) failed', 'DVS']] as const) {
    await page.getByLabel(label).check()
    await expect(air).toContainText(`${sensor} FAIL`)
    await page.getByLabel(label).uncheck()
    await expect(air).toContainText(`${sensor} NORMAL`)
  }
  await card.getByLabel('Power interruption duration ms').fill('51')
  await expect(card.getByTestId('sensor-power-readout')).toContainText('available: yes')
  await card.getByRole('button', { name: 'Interrupt KALMAN power' }).click()
  await expect(card.getByTestId('sensor-stimulus-result')).toContainText('for 51 ms (C2 laboratory rule)')
  await expect(card.getByTestId('sensor-power-readout')).toContainText('available: no')
  await card.getByRole('button', { name: 'GPS pair integrity only' }).click()
  await expect(card.getByTestId('sensor-gps-readout')).toContainText('GPS1: position NORMAL, HIL NCD; GPS2: position NORMAL, HIL NCD')
  await card.getByRole('button', { name: 'GPS pair position gone' }).click()
  await expect(card.getByTestId('sensor-gps-readout')).toContainText('GPS1: position NCD, HIL NORMAL; GPS2: position NCD, HIL NORMAL')
  await card.getByRole('button', { name: 'Restore GPS pair words' }).click()
  await expect(card.getByTestId('sensor-gps-readout')).toContainText('GPS1: position NORMAL, HIL NORMAL; GPS2: position NORMAL, HIL NORMAL')
  // Demonstration OW is an independently named NDB. Tune it through real crew keys; outage/removal is visible on RMI.
  await page.getByRole('button', { name: 'RADIO', exact: true }).click(); await key(page, 'NEXT').click()
  await page.keyboard.type('236'); await key(page, 'LSK1L').click()
  await expect(page.getByTestId('rmi-adf-needle')).toBeVisible()
  await card.getByLabel('Fault station ident').fill('OW')
  await card.getByLabel('Ground station stimulus').selectOption('NDB_OFF')
  await card.getByRole('button', { name: 'Apply ground stimulus' }).click()
  await expect(page.getByTestId('rmi-adf-value')).toHaveText('ADF1 NCD')
  await expect(page.getByTestId('rmi-adf-needle')).toHaveCount(0)
  await card.getByLabel('Ground station stimulus').selectOption('NDB_ON')
  await card.getByRole('button', { name: 'Apply ground stimulus' }).click()
  await expect(page.getByTestId('rmi-adf-needle')).toBeVisible()
  await card.screenshot({ path: test.info().outputPath('f14-sensor-card.png') })
  await tab(page, 'Scenarios')
  await scenarios.getByRole('button', { name: 'Stop recording' }).click()
  const [saved] = await Promise.all([page.waitForEvent('download'), scenarios.getByRole('button', { name: 'Save as JSON' }).click()])
  const recording = JSON.parse(await readFile(await saved.path(), 'utf8')) as { steps: { action: { kind: string; [key: string]: unknown } }[] }
  for (const kind of ['radioFault', 'stationFault', 'airInput', 'powerInterrupt', 'gpsPair', 'ndb']) expect(recording.steps.some(step => step.action.kind === kind), kind).toBe(true)
  expect(recording.steps.filter(step => step.action.kind === 'radioFault' && step.action.receiver === 'SILENT')).toHaveLength(7)
  expect(recording.steps.some(step => step.action.ident === 'ZZZZ')).toBe(false)
  expect(recording.steps.filter(step => step.action.kind === 'ndb' && step.action.ident === 'BOBTU').map(step => step.action.offAir)).toEqual([true, false])
  await saved.saveAs(test.info().outputPath('f14-recorded.json'))
  await scenarios.getByRole('button', { name: 'Run the scenario' }).click()
  // These are stimuli, with external effect assertions above; a recording with no checks is honestly NO CHECKS.
  await expect(scenarios.getByRole('status').filter({ hasText: /^NO CHECKS/ })).toBeVisible({ timeout: 60_000 })
  const [report] = await Promise.all([page.waitForEvent('download'), scenarios.getByRole('button', { name: 'Download run report' }).click()])
  const reportText = await readFile(await report.path(), 'utf8')
  expect(reportText).toMatch(/\| [\d.]+ s \| DONE \| set measured navigation air inputs: heading bias 30 degrees \(laboratory\)/)
  expect(reportText).toContain('for 51 ms (C2 laboratory rule)')
  expect(reportText).toContain('take NDB OW off the air')
  await report.saveAs(test.info().outputPath('f14-recorded-run.md'))
})

// F16 primary rendered owner: C4 requires bus-only RMI raw bearings and flags (M300 13-23/24, plan C3/F16).
// Receiver/bus tests cannot catch a missing needle, track-relative rotation or stale needle after invalidity.
// This uses the real Nd with detached words, without a production seam or a live ScriptedFms side channel.
test('F16 RMI draws both relative bus bearings and replaces an invalid bearing with its NCD or FAIL flag', async ({ page }) => {
  await page.goto('/tests/fixtures/fms-rmi.html')
  const rmi = page.getByTestId('nd-rmi')
  await expect(rmi).toBeVisible()
  await expect(rmi.getByTestId('rmi-heading')).toHaveText('120T')
  const second = rmi.getByTestId('rmi-adf2-needle')
  const rotation = (needle: import('@playwright/test').Locator) => needle.evaluate(element => {
    const matrix = (element as SVGGElement).transform.baseVal.consolidate()!.matrix
    return { cosine: matrix.a, sine: matrix.b }
  })
  await expect(rmi.getByTestId('rmi-adf-needle')).toBeVisible()
  const firstRotation = await rotation(rmi.getByTestId('rmi-adf-needle'))
  expect(firstRotation.cosine).toBeCloseTo(0, 7); expect(firstRotation.sine).toBeCloseTo(1, 7)
  const secondRotation = await rotation(second)
  expect(secondRotation.cosine).toBeCloseTo(Math.SQRT1_2, 7); expect(secondRotation.sine).toBeCloseTo(-Math.SQRT1_2, 7)
  await expect(rmi.getByTestId('rmi-adf-value')).toHaveText('ADF1 090 REL')
  await rmi.screenshot({ path: test.info().outputPath('rmi-normal.png') })
  await page.locator('.efisNd').screenshot({ path: test.info().outputPath('nd-rmi-normal.png') })
  for (const [action, status] of [['NDB off air', 'NCD'], ['Receiver failed', 'FAIL'], ['Measurement bus lost', 'FAIL']] as const) {
    await page.getByRole('button', { name: action, exact: true }).click()
    await expect(rmi.getByTestId('rmi-adf-needle')).toHaveCount(0)
    await expect(rmi.getByTestId('rmi-adf-value')).toHaveText(`ADF1 ${status}`)
    expect(await rmi.getByTestId('rmi-adf-value').evaluate(element => getComputedStyle(element).fill)).toBe('rgb(255, 176, 32)')
    await expect(second).toBeVisible()
    expect(await rotation(second)).toEqual(secondRotation)
    await rmi.screenshot({ path: test.info().outputPath(`rmi-${action.replaceAll(' ', '-')}.png`) })
    await page.getByRole('button', { name: 'Valid bearing', exact: true }).click()
    await expect(rmi.getByTestId('rmi-adf-needle')).toBeVisible()
    expect(await rotation(rmi.getByTestId('rmi-adf-needle'))).toEqual(firstRotation)
    await expect(rmi.getByTestId('rmi-adf-value')).toHaveText('ADF1 090 REL')
  }
})

// Pointer owner: real ACT RTE 5L opens airborne MOD LEGS; ERASE leaves guidance alone and only EXEC activates it.
test('BACKTRACK on the actual CDU reviews airborne history before EXEC', async ({ page }) => {
  await open(page)
  await page.getByRole('button', { name: 'Jump to next waypoint' }).click()
  const flight = page.getByRole('region', { name: 'Flight', exact: true })
  // Jump changes the route immediately; the paused quarter-second tick refreshes its guidance sample.
  // MUN to RDG is 21.6 NM in this demonstration. Capture the post-jump state before testing MOD isolation.
  await expect(flight.locator('.fmsBenchReadout').first()).toHaveText('Active waypoint RDG, 21.6 NM')
  const activeBefore = await flight.locator('.fmsBenchReadout').first().innerText()
  await key(page, 'RTE').click()
  await expectLine(page, 10, /^<BACKTRACK/)
  await key(page, 'LSK5L').click()
  await expectLine(page, 0, /^MOD RTE 1 LEGS/)
  await expect(page.locator('.fmsCduScreen')).toHaveAttribute('aria-label', /BT001/)
  await expect(flight.locator('.fmsBenchReadout').first()).toHaveText(activeBefore)
  await key(page, 'LSK6L').click()
  await expectLine(page, 0, /^ACT RTE 1 LEGS/)
  await expect(page.locator('.fmsCduLamp[data-lamp="EXEC_LIGHT"]')).not.toHaveClass(/\blit\b/)
  await expect(page.getByLabel('On ground (live bench input)', { exact: true })).toHaveCount(0) // DEC-148.
  await key(page, 'RTE').click(); await key(page, 'LSK5L').click()
  await expectLine(page, 0, /^MOD RTE 1 LEGS/)
  await expect(page.locator('.fmsCduScreen')).toHaveAttribute('aria-label', /BT001/)
  await page.locator('.fmsCdu').screenshot({ path: test.info().outputPath('backtrack-CDU.png') })
  await key(page, 'EXEC').click()
  await expectLine(page, 0, /^ACT RTE 1 LEGS/)
  await expect(page.locator('.fmsCduLamp[data-lamp="EXEC_LIGHT"]')).not.toHaveClass(/\blit\b/)
})

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
    'Not modelled in v1 for aircraft/AFCS: barometric altitude invalid, heading invalid, attitude invalid. A scenario that injects one is refused. Navigation input validity is controlled in the sensor fault laboratory.',
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
  // With GPS lost the FMS updates from radio; only with the DMEs lost as well does it dead reckon and light POS
  // (the APIRS and Doppler failed first, since KALMAN and DVS would otherwise carry navigation, DEC-150).
  await tab(page, 'Conditions')
  await page.getByRole('checkbox', { name: /^APIRS failed/ }).check()
  await page.getByRole('checkbox', { name: /^Doppler \(DVS\) failed/ }).check()
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

test('FMS NAV deviation stays visible in HDG without a coupled roll cue, then NAV capture shows the cue', async ({ page }, testInfo) => {
  await page.clock.setFixedTime(new Date('2026-09-27T14:00:00Z'))
  await open(page)
  const pfd = page.locator('.efisPfd')
  await expect(pfd.getByTestId('ldev')).toBeVisible()
  await expect(pfd.getByTestId('fd-roll')).toHaveCount(1)
  await page.getByLabel('Selected heading').fill('110')
  await page.getByRole('button', { name: 'HDG SEL', exact: true }).click()
  await expect(pfd.getByTestId('fma-roll')).toHaveText('HDG')
  await expect(pfd.getByTestId('ldev')).toBeVisible()
  await expect(pfd.getByTestId('fd-roll')).toHaveCount(0)
  await pfd.screenshot({ path: testInfo.outputPath('advisory-nav-in-hdg.png') })
  await page.getByRole('button', { name: 'LNAV', exact: true }).click()
  await expect(pfd.getByTestId('fma-roll-armed')).toHaveText('NAV')
  await expect(pfd.getByTestId('ldev')).toBeVisible()
  await expect(pfd.getByTestId('fd-roll')).toHaveCount(0)
  await page.getByRole('button', { name: 'Fly', exact: true }).click()
  await expect(pfd.getByTestId('fma-roll')).toHaveText('NAV')
  await expect(pfd.getByTestId('fd-roll')).toHaveCount(1)
  await page.getByRole('button', { name: 'Pause', exact: true }).click()
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
  await expect(page.getByLabel('Guidance', { exact: true })).toContainText('LNAV')
  await page.getByRole('button', { name: 'Pause' }).click()
  const paused = await readout.innerText()
  // Unpaused, a second at 64 times would move the aircraft about two miles.
  await page.waitForTimeout(1000)
  await expect(page.locator('.fmsBench').getByText(/^Active waypoint/)).toHaveText(paused)
  await page.getByLabel('Map range').selectOption('80')
  await expect(map).toHaveAttribute('aria-label', /80 NM range/)
})

test('IDENT and preflight wire the consumed MAGVAR loader, reference displays, FMS startup and maintenance controls', async ({ page }) => {
  // The demonstration cycles follow the clock the bench starts with: pinned inside DEMO-2609.
  await page.clock.setFixedTime(new Date('2026-09-30T14:00:00Z'))
  await open(page)
  await expectLine(page, 4, /^DEMO-2609\s+03SEP-30SEP$/)
  await expectLine(page, 6, /^DEMO-2610\s+01OCT-28OCT$/)
  await key(page, 'NEXT').click()
  await expectLine(page, 2, /^WMM2025\s+2025$/)
  await expectLine(page, 4, /^2024-11-13\s+1A6D50A7$/)
  await key(page, 'LSK3R').click() // No cycle-activation prompt on MAGVAR page.
  await key(page, 'PREV').click()
  await expectLine(page, 4, /^DEMO-2609\s+03SEP-30SEP$/)
  await key(page, 'INIT_REF').click(); await key(page, 'LSK5L').click()
  await expectLine(page, 2, /^>MAG$/)
  const magneticHeading = (await page.locator('.efisPfd').getByText(/^\d{3}°$/).textContent())!
  await expect(page.locator('.efisNd').getByText(/^\d{3}° TRK$/)).toBeVisible()
  await key(page, 'LSK1L').click()
  await expect(page.locator('.efisPfd').getByText(/^\d{3}T$/)).toBeVisible()
  await expect(page.locator('.efisNd').getByText(/^\d{3}T TRK$/)).toBeVisible()
  await expect(page.locator('.efisNd').getByText(/^\d{3}T\/\d+$/)).toBeVisible() // Wind remains TRUE.
  await key(page, 'LSK1L').click()
  await expect(page.locator('.efisPfd').getByText(magneticHeading, { exact: true })).toBeVisible()
  await tab(page, 'Nav data')
  const preflight = page.getByRole('region', { name: 'FMS initialization and preflight' })
  await page.screenshot({ path: test.info().outputPath('preflight-MAG.png'), fullPage: true })
  const [packageDownload] = await Promise.all([page.waitForEvent('download'), preflight.getByRole('button', { name: 'Export MAGVAR package' }).click()])
  const packageData = JSON.parse(await readFile((await packageDownload.path())!, 'utf8'))
  packageData.coefficients += ' ' // CRC remains unchanged; this is the actual loader boundary.
  await preflight.getByLabel('Load magnetic model package').setInputFiles({ name: 'corrupt-magvar.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify(packageData)) })
  await expect(preflight.getByRole('status')).toHaveText('MAG VAR CRC FAILED: FMS navigation withdrawn.')
  await expect(page.getByTestId('pfd-fms-flag')).toBeVisible()
  await expect(preflight.getByRole('button', { name: 'POS INIT', exact: true })).toBeDisabled()
  await preflight.getByRole('button', { name: 'Restore WMM2025' }).click()
  await expect(page.getByTestId('pfd-fms-flag')).not.toBeVisible()
  await preflight.getByRole('button', { name: 'POS INIT', exact: true }).click()
  await expectLine(page, 0, /^POS INIT/)
  await preflight.getByLabel('On ground at power-up (bench input)').check()
  await preflight.getByRole('button', { name: 'FMS power off', exact: true }).click()
  await expect(page.locator('.fmsCduLamp.lit')).toHaveCount(0)
  await preflight.getByRole('button', { name: 'Cold start FMS', exact: true }).click()
  await expect(page.locator('.fmsCduScreen .cduInverse')).toHaveCount(336)
  await page.getByLabel('Simulation rate').selectOption('64')
  await page.getByRole('button', { name: 'Fly', exact: true }).click()
  await expectLine(page, 0, /^IDENT/)
  await page.getByRole('button', { name: 'Pause', exact: true }).click()
  await key(page, 'INIT_REF').click(); await key(page, 'LSK5L').click()
  await expectLine(page, 10, /^>INDEPENDENT$/)
  await key(page, 'LSK5L').click(); await key(page, 'LSK6R').click()
  await key(page, 'CLR').click()
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

// Owner: pointer input addresses the inspected computer, peer renders its own state, and RMS feedback survives link loss.
// Engine owners cannot see a selector accidentally wired to CDU 1 or an active-frequency display wired to the request.
test('the two CDU panels target separate computers and shared radio feedback remains available without cross-talk', async ({ page }) => {
  await open(page)
  await page.getByRole('button', { name: 'Show peer CDU' }).click()
  const inspected = page.getByTestId('fms-cdu-inspected'), peer = page.getByTestId('fms-cdu-peer')
  const click = (panel: ReturnType<Page['getByTestId']>, id: string) => panel.locator(`.fmsCduKey[data-key="${id}"]`).click()
  const screen = (panel: ReturnType<Page['getByTestId']>) => panel.locator('.fmsCduScreen')
  await click(inspected, 'RTE'); await click(peer, 'PROG')
  await expect(screen(inspected)).toHaveAttribute('aria-label', /^ACT RTE 1/)
  await expect(screen(peer)).toHaveAttribute('aria-label', /^ACT PROGRESS/)
  await page.getByLabel('CDU inspected', { exact: true }).selectOption('2')
  await expect(screen(inspected)).toHaveAttribute('aria-label', /^ACT PROGRESS/)
  await expect(screen(peer)).toHaveAttribute('aria-label', /^ACT RTE 1/)
  await click(inspected, 'INIT_REF'); await click(inspected, 'LSK5L')
  await click(inspected, 'LSK5L'); await click(inspected, 'LSK6R')
  await click(inspected, 'RTE')
  for (const letter of 'CYYZ') await click(inspected, letter)
  await click(inspected, 'LSK1R')
  await expect(screen(inspected)).toHaveAttribute('aria-label', /^MOD RTE 1/)
  await expect(screen(peer)).toHaveAttribute('aria-label', /^ACT RTE 1/)
  await click(inspected, 'EXEC'); await click(inspected, 'LSK4L')
  await expect(screen(peer)).toHaveAttribute('aria-label', /^MOD RTE 1/)
  await click(peer, 'EXEC'); await expect(screen(peer)).toHaveAttribute('aria-label', /^ACT RTE 1/)
  await tab(page, 'Dual FMS and radios')
  const devices = page.getByRole('region', { name: 'Dual computers and radio devices' })
  await devices.getByRole('button', { name: 'Fail cross-talk link' }).click()
  await click(inspected, 'F2_1'); await click(peer, 'F2_1') // The physical first row-two key is RADIO on this variation.
  for (const letter of '123.450') await click(inspected, letter === '.' ? 'DOT' : letter)
  await click(inspected, 'LSK1L')
  await page.getByLabel('Simulation rate').selectOption('16')
  await page.getByRole('button', { name: 'Fly', exact: true }).click()
  await expect(devices.getByRole('list', { name: 'RMS tuning feedback' })).toContainText('FMS 2: COM1 123.450 — ACK')
  await page.getByRole('button', { name: 'Pause', exact: true }).click()
  await expect(screen(peer)).toHaveAttribute('aria-label', /123\.450/)
  await devices.getByLabel('COM1 device feedback').selectOption('failed')
  for (const letter of '124.000') await click(inspected, letter === '.' ? 'DOT' : letter)
  await click(inspected, 'LSK1L')
  await page.getByRole('button', { name: 'Fly', exact: true }).click()
  await expect(devices.getByRole('list', { name: 'RMS tuning feedback' })).toContainText('FMS 2: COM1 124.000 — TIMEOUT')
  await page.getByRole('button', { name: 'Pause', exact: true }).click()
  await expect(screen(inspected)).toHaveAttribute('aria-label', /123\.450/)
  await expect(screen(peer)).toHaveAttribute('aria-label', /123\.450/)
  await page.screenshot({ path: test.info().outputPath('dual-CDUs-RMS.png'), fullPage: true })
})

// Owner: common sensor-fault controls must address the physical generator even when CDU 2 is inspected.
test('shared sensor faults from CDU 2 affect both computers actual observations', async ({ page }) => {
  await open(page)
  await page.getByRole('combobox', { name: 'Hardware variation' }).selectOption('050')
  const card = page.getByRole('region', { name: 'Scenarios' })
  const scenario = { id: 'shared-sensors', title: 'Shared offshore inputs', objective: 'Two computers observe the same radio altimeter',
    maxSeconds: 1, start: '87n-offshore-sar', steps: [{ when: { kind: 'start' }, action: { kind: 'expectAircraft', minAltitude: 490, maxAltitude: 510 } }] }
  await card.getByLabel('Scenario file').setInputFiles({ name: 'shared-sensors.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify(scenario)) })
  await card.getByRole('button', { name: 'Run the scenario' }).click()
  await expect(card.getByRole('status').filter({ hasText: /^PASS/ })).toBeVisible()
  await page.getByLabel('CDU inspected', { exact: true }).selectOption('2')
  await key(page, 'F2_2').click(); await key(page, 'LSK1R').click() // TACT on the selected 050 hardware.
  const radAlt = async () => { const lines = await screenLines(page); return lines[lines.findIndex(line => /RAD ALT/.test(line)) + 1] }
  await expect.poll(radAlt).toMatch(/^\s*500FT/)
  await tab(page, 'Conditions')
  await page.getByRole('checkbox', { name: /^Radio altimeter failed/ }).check()
  await expect.poll(radAlt).toMatch(/^\s*----FT/)
  await page.getByLabel('CDU inspected', { exact: true }).selectOption('1')
  await expect(page.getByRole('checkbox', { name: /^Radio altimeter failed/ })).toBeChecked()
  await page.getByRole('checkbox', { name: /^Radio altimeter failed/ }).uncheck()
  await page.getByLabel('CDU inspected', { exact: true }).selectOption('2')
  await expect.poll(radAlt).toMatch(/^\s*500FT/)
  await page.getByRole('checkbox', { name: /^GPS integrity lost/ }).check()
  await tab(page, 'GPS sensors')
  const faults = page.getByRole('region', { name: 'GPS 1 faults' })
  await expect(faults.getByRole('note')).toContainText('GPS integrity lost condition holds')
  await expect(faults.getByRole('button', { name: /^Mask low satellites/ })).toBeDisabled()
  await tab(page, 'Conditions')
  await page.getByRole('checkbox', { name: /^GPS integrity lost/ }).uncheck()
  await tab(page, 'GPS sensors')
  await page.getByLabel('GPS 1 Baro lost').check()
  await page.getByLabel('CDU inspected', { exact: true }).selectOption('1')
  await expect(page.getByLabel('GPS 1 Baro lost')).toBeChecked()
  await page.getByLabel('GPS 1 Baro lost').uncheck()
  await page.getByLabel('CDU inspected', { exact: true }).selectOption('2')
  await expect(page.getByLabel('GPS 1 Baro lost')).not.toBeChecked()
  await tab(page, 'Conditions')
  await page.getByLabel('Baro error (ft)').fill('1000')
  await page.getByRole('button', { name: 'Inject the error' }).click()
  await expect(page.getByTestId('baro-readout')).toContainText('barometric 1500 ft')
  await page.getByLabel('CDU inspected', { exact: true }).selectOption('1')
  await expect(page.getByTestId('baro-readout')).toContainText('baro error +1000 ft')
  await page.getByLabel('Baro error (ft)').fill('0')
  await page.getByRole('button', { name: 'Inject the error' }).click()
  await page.getByLabel('CDU inspected', { exact: true }).selectOption('2')
  await expect(page.getByTestId('baro-readout')).toContainText('barometric 500 ft')
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
  await expect(efis.getByTestId('pfd-selected-heading-value')).toHaveText('HDG 045°')
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
  await page.getByRole('button', { name: 'FTR', exact: true }).focus()
  await page.keyboard.press('Tab')
  await expect(page.getByRole('tab', { name: 'Scenarios', exact: true })).toBeFocused()
  await expect(tabs).toHaveText(['Scenarios', 'Conditions', 'GPS sensors', 'Dual FMS and radios', 'Nav data', 'Lighting and keys'])
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
  await expect(page.getByRole('tab', { name: 'Dual FMS and radios' })).toHaveAttribute('aria-selected', 'true')
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
  await expect(page.getByTestId('route-source-note')).toContainText('Last transfer: GPS1 to GPS2 (GPS1 NOT USABLE: HIL 99.00 >= HAL')
  // The CDU's own presentation: NAV OPTIONS under GPS NAV, not only the bench.
  for (const id of ['INIT_REF', 'NEXT', 'LSK5R', 'LSK4R', 'LSK6R']) await key(page, id).click()
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
  // This owner tests local receiver AUTO selection. Synchronized best-source selection has its own dual-FMS owner.
  await key(page, 'INIT_REF').click()
  await key(page, 'LSK5L').click() // SETUP
  await key(page, 'LSK5L').click() // Request INDEPENDENT
  await key(page, 'LSK6R').click() // Confirm without interrupting the shared receiver environment.
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

test('the KBTV demonstration defaults to S300 advisory VNAV after its manual setup', async ({ page }) => {
  // The simulated satellite sky follows UTC. Keep this scenario proof independent of the host's time of day.
  await page.clock.setFixedTime(new Date('2026-09-27T14:00:00Z'))
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
  await expect(page.getByTestId('fms-bench-profile')).toContainText('cma9000-s300-heli-civil v10')
  await page.screenshot({ path: test.info().outputPath('s300-kbtv-advisory.png'), fullPage: true })
})

test('the KBTV later-SBAS LPV library scenario passes all five checks from a fresh bench', async ({ page }) => {
  // The library start state configures a fresh FMS system independently of the manual demonstration setup.
  await page.clock.setFixedTime(new Date('2026-09-27T14:00:00Z'))
  await open(page)
  await expect(page.getByTestId('fms-bench-profile')).toContainText('cma9000-s300-heli-civil v10')
  await tab(page, 'Scenarios')
  const card = page.getByRole('region', { name: 'Scenarios' })
  await page.getByLabel('Simulation rate').selectOption('64')
  await card.getByLabel('Scenario', { exact: true }).selectOption({ label: 'KBTV RNAV (GPS) RWY 15, LPV on the published FAS' })
  await card.getByRole('button', { name: 'Run the scenario' }).click()
  await expect(page.getByTestId('fms-bench-profile')).toContainText('cma9000-later-sbas-heli v6')
  // Run every 250 ms callback at 64x through the scenario's 900 s horizon, independent of host timer pacing.
  await page.clock.runFor(14_250)
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
  await expect(page.getByLabel('Guidance', { exact: true })).toContainText('crew flying the visual segment')
  await expect(page.getByLabel('Guidance', { exact: true })).toContainText('HDG')
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

test('D-R: the Nav data tab shows each moving waypoint\'s age as a bench aid; it never expires', async ({ page }) => {
  await open(page)
  // This owner checks age and the simulation clock; enter the trajectory explicitly in TRUE.
  await key(page, 'INIT_REF').click()
  await key(page, 'LSK5L').click()
  await key(page, 'LSK1L').click()
  await expectLine(page, 2, /^>TRUE$/)
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
  await expect(item).toHaveText(/^SHIP1 270T\/20 kt, age 0:00:\d\d$/)
  // Flying on, it ages on the simulation clock (64 times real time): minutes, not seconds.
  await page.getByLabel('Simulation rate').selectOption('64')
  await page.getByRole('button', { name: 'Fly' }).click()
  await expect(item).toHaveText(/age 0:0[1-9]:\d\d|age 0:[1-5]\d:\d\d/, { timeout: 15_000 })
  await page.getByRole('button', { name: 'Pause' }).click()
  // The same stored 270 TRUE trajectory is 283 MAG at the fixture's declared WMM2025 position (~12.5 W).
  await key(page, 'INIT_REF').click()
  await key(page, 'LSK5L').click()
  await key(page, 'LSK1L').click()
  await expectLine(page, 2, /^>MAG$/)
  await expect(item).toHaveText(/^SHIP1 283°\/20 kt, age 0:\d\d:\d\d$/)
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
test('B1.7: a paused run stops its clock: the predictions and the fuel on FUEL and PROGRESS read the same after a wait', async ({ page }) => {
  await open(page)
  const readout = page.locator('.fmsBench').getByText(/^Active waypoint/)
  const toGo = async () => Number((await readout.innerText()).match(/([\d.]+) NM/)?.[1] ?? NaN)
  const start = await toGo()
  await page.getByLabel('Simulation rate').selectOption('16')
  await tab(page, 'Scenarios')
  await page.getByRole('region', { name: 'Scenarios' }).getByRole('button', { name: 'Run the scenario' }).click()
  await expect.poll(toGo).toBeLessThan(start - 0.3)
  await page.getByRole('button', { name: 'Pause' }).click()
  await expect(page.getByText('Run paused: its clock is stopped.')).toBeVisible()
  // Each page is drawn afresh after the wait (FUEL is the second key of the second row on the standard panel), so a
  // moving clock or a burning fuel would show.
  const read = async (id: string) => { await key(page, id).click(); return (await screenLines(page)).join('\n') }
  const fuel = await read('F2_2'), progress = await read('PROG')
  expect(fuel).toMatch(/\d+KG/)
  expect(progress).toMatch(/\d{4}\.\dZ/)
  // Longer than the ETA field's 6 s resolution: a clock left running, even at 1 times (an aircraft freeze), would move
  // the ETAs; flying at 16 times would also burn fuel.
  await page.waitForTimeout(6500)
  expect(await read('F2_2')).toBe(fuel)
  expect(await read('PROG')).toBe(progress)
  // Resumed, they move again.
  await page.getByRole('button', { name: 'Fly' }).click()
  await expect.poll(async () => read('F2_2')).not.toBe(fuel)
})

// F9 rendered owner: real keys/status colours and the physical 24-column table. Solver/source rules stay in logic owners.
test('F9 Conditions applies and records measured Doppler surface on FMS1 while CDU2 shows the shared word', async ({ page }) => {
  const start = new Date('2026-09-27T14:00:00Z')
  await page.clock.install({ time: start }); await page.clock.pauseAt(start)
  await open(page)
  await page.getByLabel('CDU inspected').selectOption('2')
  const scenarios = page.getByRole('region', { name: 'Scenarios' })
  await scenarios.getByRole('button', { name: 'Record', exact: true }).click()
  await tab(page, 'Conditions')
  const card = page.getByRole('region', { name: 'Sensor fault laboratory' })
  await card.getByLabel('Measured Doppler surface').selectOption('SEA')
  await card.getByRole('button', { name: 'Apply Doppler surface' }).click()
  await page.clock.runFor(250) // One declared bench tick samples the shared word on the peer, then time stays frozen.
  await expect(card.getByTestId('sensor-stimulus-result')).toContainText('set the measured Doppler surface to SEA (laboratory)')
  await expect(card.getByTestId('sensor-dvs-readout')).toContainText(/Doppler SEA; VX .*sample 2026-09-27T14:00:00\.250Z; status NORMAL; source native laboratory/)
  await card.screenshot({ path: test.info().outputPath('f9-measured-dvs-conditions.png') })
  await key(page, 'INIT_REF').click(); await key(page, 'NEXT').click(); await key(page, 'LSK5R').click(); await key(page, 'LSK3L').click()
  await expectLine(page, 0, /^DVS STATUS\s+1\/2/)
  await expectLine(page, 6, /^SEA/)
  await key(page, 'NEXT').click()
  for (const digit of '090/10') await key(page, digit === '/' ? 'SLASH' : digit).click()
  await key(page, 'LSK2L').click()
  await expectLine(page, 4, /^090°\/10\.0 KTS/)
  await page.locator('.fmsCdu').screenshot({ path: test.info().outputPath('f9-dvs-water-current-cdu2.png') })
  await tab(page, 'Scenarios'); await scenarios.getByRole('button', { name: 'Stop recording' }).click()
  const [saved] = await Promise.all([page.waitForEvent('download'), scenarios.getByRole('button', { name: 'Save as JSON' }).click()])
  const recorded = JSON.parse(await readFile(await saved.path(), 'utf8')) as { steps: { action: { kind: string; surface?: string } }[] }
  expect(recorded.steps.filter(entry => entry.action.kind === 'dvsInput').map(entry => entry.action)).toEqual([{ kind: 'dvsInput', surface: 'SEA' }])
  await saved.saveAs(test.info().outputPath('f9-measured-dvs-recorded.json'))
})

test('F679 navigation status keys show actual GPS loss, crew deselection and the bearing table', async ({ page }) => {
  await open(page)
  const row = (index: number) => page.locator('.fmsCduScreen .cduLine').nth(index)
  await key(page, 'INIT_REF').click(); await key(page, 'NEXT').click(); await key(page, 'LSK5R').click()
  await expectLine(page, 0, /^NAV STATUS INDEX/)
  await key(page, 'LSK6R').click(); await expectLine(page, 0, /^DESELECT/)
  await tab(page, 'Conditions')
  const airControls = page.getByRole('region', { name: 'Sensor fault laboratory' })
  await airControls.getByLabel('Navigation TAS valid', { exact: true }).uncheck()
  await expectLine(page, 2, /^>ACQ/)
  await page.locator('.fmsCdu').screenshot({ path: test.info().outputPath('f679-tas-invalid-acq.png') })
  await airControls.getByLabel('Navigation TAS valid', { exact: true }).check()
  await expectLine(page, 2, /^>VALID/)
  await page.locator('.fmsCdu').screenshot({ path: test.info().outputPath('f679-tas-recovered-valid.png') })
  await key(page, 'LSK1R').click(); await expectLine(page, 0, /^GPS DESELECT/)
  await expectLine(page, 2, /VALID/)
  await expect(row(2).locator('.cdu-green')).toHaveCount(5)
  await tab(page, 'Conditions'); await page.getByRole('checkbox', { name: /^GPS lost sensor/ }).check()
  await expectLine(page, 2, /ACQ/)
  await expect(row(2).locator('.cdu-amber')).toHaveCount(3)
  await key(page, 'LSK1R').click(); await expectLine(page, 2, /DESEL/)
  await expect(row(2).locator('.cdu-amber')).toHaveCount(5)
  await page.locator('.fmsCdu').screenshot({ path: test.info().outputPath('f679-gps-desel.png') })
  await page.getByRole('checkbox', { name: /^GPS lost sensor/ }).uncheck()
  await expectLine(page, 4, /VALID/)
  await expectLine(page, 2, /DESEL/)
  await key(page, 'LSK1R').click(); await expectLine(page, 2, /VALID/)
  await key(page, 'CLR').click()
  await expect(page.locator('.fmsCduLamp[data-lamp="MSG"]')).not.toHaveClass(/\blit\b/)
  await key(page, 'INIT_REF').click(); await key(page, 'LSK2L').click(); await key(page, 'NEXT').click()
  await expectLine(page, 0, /^POS INIT\s+2\/2/)
  await expectLine(page, 3, /^MODE    STS DIS BRG  ACC/)
  await expectLine(page, 4, /^GPS     NAV 0\.00----/)
  await expect(row(4).locator('.cduCell')).toHaveCount(24)
  await page.locator('.fmsCdu').screenshot({ path: test.info().outputPath('f679-pos-bearing.png') })
  await tab(page, 'GPS sensors')
  for (const receiver of [1, 2]) {
    await page.getByText(`GPS ${receiver} bus monitor`).click()
    const hfom = page.getByRole('table', { name: `GPS ${receiver} bus monitor` }).locator('tr[data-label="247"]')
    await hfom.getByLabel('Override 247', { exact: true }).selectOption('FORCE')
    await hfom.getByLabel('Override 247 amount').fill('100')
    await hfom.getByRole('button', { name: 'Set' }).click()
  }
  await expectLine(page, 4, /^GPS     NAV 0\.00---- 100$/)
  await page.locator('.fmsCdu').screenshot({ path: test.info().outputPath('f679-pos-large-accuracy.png') })
  await key(page, 'INIT_REF').click(); await key(page, 'NEXT').click(); await key(page, 'LSK5R').click(); await key(page, 'LSK3R').click()
  await expectLine(page, 0, /^VOR\/DME\/TCN STATUS/)
  await expectLine(page, 6, /^NAV1 AUTO\s+NAV2 AUTO/)
  await page.locator('.fmsCdu').screenshot({ path: test.info().outputPath('f679-vor-sources.png') })
  await tab(page, 'Conditions')
  const sensors = page.getByRole('region', { name: 'Sensor fault laboratory' })
  await sensors.getByLabel('Fault radio').selectOption('tacan')
  await expect(sensors.getByTestId('sensor-radio-readout')).toContainText('TACAN station none; paired navigation measurements unavailable.')
})

// Browser owner for native TACAN CDU/Conditions wiring. Fixed-column laboratory data enters through the actual loader.
test('F7 native TACAN status and Conditions show bearing to station and withhold contradictory identity', async ({ page }) => {
  await page.clock.setFixedTime(new Date('2026-09-27T14:00:00Z'))
  await open(page); await tab(page, 'Nav data')
  const record = Array<string>(132).fill(' ')
  const put = (column: number, text: string) => { for (let i = 0; i < text.length; i++) record[column - 1 + i] = text[i] }
  // Station five NM north of the frozen physical start; ICAO pairing115.00 is97X. Surveyed elevation0ft.
  for (const [column, text] of [[1, 'S'], [5, 'D'], [14, 'T1'], [22, '0'], [23, '11500'], [28, ' T'],
    [33, 'N45233600W075405412'], [56, 'N45233600W075405412'], [80, '00000'], [94, 'LABORATORY NORTH TACAN']] as const) put(column, text)
  await page.getByLabel('ARINC 424 navigation data file').setInputFiles({ name: 'laboratory-north-tacan.424', mimeType: 'text/plain',
    buffer: Buffer.from(`HDR01 LAB 2609 03-SEP-2026\n${record.join('')}\n`) })
  await page.getByRole('button', { name: 'Activate CIFP2609', exact: true }).click()
  await tab(page, 'Conditions')
  const card = page.getByRole('region', { name: 'Sensor fault laboratory' })
  await card.getByLabel('Fault radio').selectOption('tacan')
  await expect(card.getByTestId('sensor-radio-readout')).toContainText(/TACAN station T1; paired navigation measurements bearing [\d.]+°, range [\d.]+ NM/)
  await key(page, 'INIT_REF').click(); await key(page, 'NEXT').click(); await key(page, 'LSK5R').click(); await key(page, 'LSK3R').click()
  // UI wiring proves bearing-to is northerly under Ottawa's magnetic reference. Exact004 MAG is owned by
  // the fixed equatorial native fixture in fms-vor-dme; this browser does not duplicate the magnetic model oracle.
  await expectLine(page, 7, /^TCN  T1  97X 0[0-2]\d°\/5NM/)
  await page.locator('.fmsCdu').screenshot({ path: test.info().outputPath('f7-native-tacan-bearing-to.png') })
  await card.screenshot({ path: test.info().outputPath('f7-native-tacan-conditions.png') })
  await card.getByLabel('Fault station ident').fill('T1')
  await card.getByLabel('Ground station stimulus').selectOption('DME_IDENT')
  await card.getByLabel('DME reported ident').fill('BAD')
  await card.getByRole('button', { name: 'Apply ground stimulus' }).click()
  await expect(card.getByTestId('sensor-radio-readout')).toContainText('paired navigation measurements unavailable')
  await expectLine(page, 7, /^TCN  T1  97X\s*$/)
  await page.locator('.fmsCdu').screenshot({ path: test.info().outputPath('f7-native-tacan-ident-contradiction.png') })
  await card.getByLabel('DME reported ident').fill('')
  await card.getByRole('button', { name: 'Apply ground stimulus' }).click()
  await expectLine(page, 7, /^TCN  T1  97X 0[0-2]\d°\/5NM/)
  await expect(card.getByTestId('sensor-radio-readout')).toContainText(/paired navigation measurements bearing [\d.]+°, range [\d.]+ NM/)
})

// #1382: these rendered contracts own view/focus/state and geometry failures that engine tests cannot see.
test('cockpit and engineering views preserve CDU state and unfinished instructor forms; Escape returns focus', async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await open(page)
  await page.locator('.fmsCdu').focus()
  await page.keyboard.type('abc')
  await page.getByRole('button', { name: 'Cockpit view', exact: true }).click()
  const left = page.locator('.fmsBenchCduStation[data-side="1"]')
  await expect(left.locator('.fmsCduScreen')).toHaveAttribute('aria-label', /ABC\s*$/)
  const ios = page.getByRole('region', { name: 'Instructor station', exact: true })
  await expect(ios).toBeHidden()
  await page.getByRole('button', { name: 'Instructor station', exact: true }).click()
  await expect(ios).toBeFocused()
  await ios.getByRole('tab', { name: 'Scenarios', exact: true }).click()
  await ios.getByRole('button', { name: 'Record', exact: true }).click()
  await ios.getByLabel('Recording name').fill('Keep this draft')
  await page.keyboard.press('Escape')
  await expect(ios).toBeHidden()
  await expect(page.getByRole('button', { name: 'Instructor station', exact: true })).toBeFocused()
  await page.getByRole('button', { name: 'Instructor station', exact: true }).click()
  await expect(ios.getByLabel('Recording name')).toHaveValue('Keep this draft')
  await ios.getByRole('button', { name: 'More instructor room', exact: true }).click()
  expect((await ios.boundingBox())!.height).toBeGreaterThanOrEqual(500)
  await expect(ios.getByLabel('Recording name')).toHaveValue('Keep this draft')
  await ios.getByRole('button', { name: 'Close instructor station', exact: true }).click()
  await page.screenshot({ path: testInfo.outputPath('cockpit.png'), fullPage: true })
  await page.getByRole('button', { name: 'Engineering view', exact: true }).click()
  await expect(page.getByLabel('Recording name')).toHaveValue('Keep this draft')
  await expect(page.getByRole('button', { name: 'Stop recording', exact: true })).toBeEnabled()
})

test('cockpit CDU focus stays on its physical side and never selects aircraft guidance or captures instructor typing', async ({ page }) => {
  await open(page)
  await page.getByRole('button', { name: 'Cockpit view', exact: true }).click()
  const right = page.locator('.fmsBenchCduStation[data-side="2"]')
  const left = page.locator('.fmsBenchCduStation[data-side="1"]')
  await right.locator('.fmsCdu').focus()
  await page.keyboard.type('stays')
  await expect(right.locator('.fmsCduScreen')).toHaveAttribute('aria-label', /STAYS\s*$/)
  await expect(left.locator('.fmsCduScreen')).not.toHaveAttribute('aria-label', /STAYS\s*$/)
  await expect(right).toHaveAttribute('data-active', 'true')
  await expect(page.getByLabel('FMS guidance source')).toHaveValue('1')
  await page.getByRole('button', { name: 'Instructor station', exact: true }).click()
  await tab(page, 'Conditions')
  await page.getByLabel('Alert message to raise').fill('DRAFT')
  await expect(right.locator('.fmsCduScreen')).toHaveAttribute('aria-label', /STAYS\s*$/)
  await page.keyboard.press('Escape')
  await right.locator('.fmsCdu').focus()
  await page.keyboard.down('Backspace')
  await page.getByRole('button', { name: 'Instructor station', exact: true }).click()
  await page.keyboard.up('Backspace')
  await page.waitForTimeout(1200)
  await expect(right.locator('.fmsCduScreen')).toHaveAttribute('aria-label', /STAYS\s*$/)
  await expect(page.getByLabel('Alert message to raise')).toHaveValue('DRAFT')
})

test('the cockpit preserves faceplate and display proportions and stacks below its readable width floor', async ({ page }, testInfo) => {
  test.setTimeout(120_000)
  // The published faceplate geometry is the independent key-face oracle, not the overlay's CSS height (#1444).
  const geometry = JSON.parse(await readFile(new URL('../public/fms-cdu/layout.json', import.meta.url), 'utf8')) as {
    image: { w: number; h: number }; keys: Array<{ id: string; x: number; y: number; w: number; h: number }>
  }
  const centredKeys = async () => {
    const faults = await page.locator('.fmsCdu').evaluateAll((panels, layout) => panels.flatMap(panel => {
      const plate = panel.getBoundingClientRect()
      if (!plate.width || !plate.height) return []
      return [...panel.querySelectorAll<HTMLElement>('.fmsCduKey')].flatMap(key => {
        const physical = layout.keys.find(item => item.id === key.dataset.key)!
        const face = { x: plate.x + physical.x / layout.image.w * plate.width,
          y: plate.y + physical.y / layout.image.h * plate.height,
          w: physical.w / layout.image.w * plate.width, h: physical.h / layout.image.h * plate.height }
        const box = key.getBoundingClientRect(), legend = key.querySelector('.legend')?.getBoundingClientRect()
        const errors: string[] = []
        if (Math.abs(box.height - face.h) > 1 || Math.abs(box.width - face.w) > 1) errors.push(`${key.dataset.key}: button exceeds physical face`)
        if (legend && (legend.left < face.x - 1 || legend.right > face.x + face.w + 1
          || legend.top < face.y - 1 || legend.bottom > face.y + face.h + 1)) errors.push(`${key.dataset.key}: legend exceeds physical face`)
        if (legend && (Math.abs(legend.x + legend.width / 2 - face.x - face.w / 2) > 1
          || Math.abs(legend.y + legend.height / 2 - face.y - face.h / 2) > 1)) errors.push(`${key.dataset.key}: legend is not centred`)
        for (const line of key.querySelectorAll('.legend > span')) {
          const ink = line.getBoundingClientRect()
          if (ink.left < face.x - 1 || ink.right > face.x + face.w + 1
            || Math.abs(ink.x + ink.width / 2 - face.x - face.w / 2) > 1) errors.push(`${key.dataset.key}: legend line is not centred within its face`)
        }
        return errors
      })
    }), geometry)
    expect(faults).toEqual([])
  }
  await page.setViewportSize({ width: 1440, height: 900 })
  await open(page)
  // This isolated bench omits the workspace shell. Load its real density tokens so the product's
  // 40px button minimum is present; otherwise a faceplate regression can pass only in the fixture.
  await page.addStyleTag({ url: '/src/Density.css' })
  await centredKeys()
  const variation = page.getByRole('combobox', { name: 'Hardware variation' })
  const variations = await variation.locator('option').evaluateAll(options => options.map(option => (option as HTMLOptionElement).value))
  for (const value of variations) {
    await variation.selectOption(value)
    await centredKeys()
  }
  await variation.selectOption(variations[0])
  await page.screenshot({ path: testInfo.outputPath('centred-engineering-keys.png'), fullPage: true })
  await page.getByRole('button', { name: 'Cockpit view', exact: true }).click()
  const boxes = async () => {
    const cdu = await page.locator('.fmsBenchCduStation .fmsCdu').first().boundingBox()
    const pfd = await page.locator('.efisPfd').boundingBox()
    const nd = await page.locator('.efisNd').boundingBox()
    return { cdu: cdu!, pfd: pfd!, nd: nd! }
  }
  const wide = await boxes()
  await centredKeys()
  await page.evaluate(() => { document.documentElement.dataset.density = 'compact' })
  await centredKeys()
  await page.evaluate(() => { delete document.documentElement.dataset.density })
  await page.screenshot({ path: testInfo.outputPath('centred-cockpit-keys.png'), fullPage: true })
  expect(wide.cdu.width).toBeGreaterThanOrEqual(320)
  expect(wide.pfd.width).toBeGreaterThanOrEqual(280)
  expect(Math.abs(wide.cdu.height / wide.cdu.width - 1420 / 1216)).toBeLessThan(0.01)
  expect(wide.nd.x).toBeGreaterThan(wide.pfd.x)
  const pilotControls = await page.getByRole('form', { name: 'Vertical and speed selections' }).boundingBox()
  expect(pilotControls!.y + pilotControls!.height).toBeLessThanOrEqual(900)
  await page.getByRole('button', { name: 'Instructor station', exact: true }).click()
  await tab(page, 'Flight and setup')
  for (const value of variations) {
    await variation.selectOption(value)
    await centredKeys()
  }
  await variation.selectOption(variations[0])
  await tab(page, 'GPS sensors')
  const ios = await page.getByRole('region', { name: 'Instructor station', exact: true }).boundingBox()
  expect(ios!.y + ios!.height).toBeLessThanOrEqual(wide.cdu.y)
  await page.screenshot({ path: testInfo.outputPath('instructor-gps.png'), fullPage: true })
  await page.keyboard.press('Escape')
  await page.getByRole('button', { name: 'Show the view', exact: true }).click()
  const outsideScene = page.locator('.fmsBenchWindow .fmsOtw')
  // Read each natural footprint in one browser task: widget credits may initialize between IPC calls.
  const outsideFootprint = () => page.evaluate(() => {
    const rectangle = (selector: string) => {
      const element = document.querySelector(selector)
      if (!element) throw new Error(`Missing public geometry element: ${selector}`)
      const { x, y, width, height } = element.getBoundingClientRect()
      return { x, y, width, height }
    }
    return {
      header: rectangle('.fmsBenchWindow .fmsBenchMapHead'),
      scene: rectangle('.fmsBenchWindow .fmsOtw'),
      footer: rectangle('.fmsBenchWindow .fmsOtwCredits'),
      card: rectangle('.fmsBenchWindow[aria-label="Out-the-window view"]'),
      pilot: rectangle('form[aria-label="Vertical and speed selections"]'),
      scrollY: window.scrollY,
      cockpit: {
        cdu: rectangle('.fmsBenchCduStation .fmsCdu'),
        pfd: rectangle('.efisPfd'),
        nd: rectangle('.efisNd'),
      },
    }
  })
  const closedFootprint = await outsideFootprint()
  const { header: closedHeader, scene: closedScene, footer: closedFooter, card: closedCard, cockpit: closedCockpit } = closedFootprint
  expect(closedScene!.height).toBe(170)
  const clearOfInstructor = async (height: number) => {
    const { scene, footer, header, drawer, clippedLabels } = await page.locator('.fmsBenchWindow .fmsBenchMapHead').evaluate(head => {
      const rectangle = (selector: string) => {
        const element = head.ownerDocument.querySelector(selector)
        if (!element) throw new Error(`Missing public geometry element: ${selector}`)
        const { x, y, width, height } = element.getBoundingClientRect()
        return { x, y, width, height }
      }
      const headerBox = head.getBoundingClientRect()
      return {
        scene: rectangle('.fmsBenchWindow .fmsOtw'),
        footer: rectangle('.fmsBenchWindow .fmsOtwCredits'),
        header: { x: headerBox.x, y: headerBox.y, width: headerBox.width, height: headerBox.height },
        drawer: rectangle('[role="region"][aria-label="Instructor station"]'),
        clippedLabels: [...head.querySelectorAll('.fmsBenchModes label')].flatMap(label => {
          const box = label.getBoundingClientRect(), group = label.parentElement!.getBoundingClientRect()
          return box.left < group.left || box.right > group.right || box.top < group.top || box.bottom > group.bottom
            || box.left < headerBox.left || box.right > headerBox.right || box.top < headerBox.top || box.bottom > headerBox.bottom
            ? [label.textContent?.trim()] : []
        }),
      }
    })
    expect(scene!.width).toBeGreaterThan(0)
    expect(scene!.height).toBe(height)
    // Public occupied rectangles are independent of the reservation's CSS width and breakpoint.
    expect(scene!.x + scene!.width).toBeLessThanOrEqual(drawer!.x)
    expect(footer!.width).toBeGreaterThan(0)
    expect(footer!.x + footer!.width).toBeLessThanOrEqual(drawer!.x)
    expect(header!.width).toBeGreaterThan(0)
    expect(header!.x + header!.width).toBeLessThanOrEqual(drawer!.x)
    expect(clippedLabels).toEqual([])
  }
  await page.getByRole('button', { name: 'Instructor station', exact: true }).click()
  await clearOfInstructor(170)
  const openedFootprint = await outsideFootprint()
  const { cockpit: openedCockpit, header: openedHeader, footer: openedFooter, card: openedCard } = openedFootprint
  await testInfo.attach('instructor-toolbar-footprints', { body: JSON.stringify({ closedFootprint, openedFootprint }), contentType: 'application/json' })
  expect(openedCockpit.cdu.x).toBe(closedCockpit.cdu.x)
  expect(openedCockpit.cdu.width).toBe(closedCockpit.cdu.width)
  expect(openedCockpit.cdu.height).toBe(closedCockpit.cdu.height)
  // Only measured natural header/footer growth may move the unchanged cockpit down.
  const cardGrowth = openedCard!.height - closedCard!.height
  const headerGrowth = openedHeader!.height - closedHeader!.height
  const footerGrowth = openedFooter!.height - closedFooter!.height
  expect(cardGrowth).toBeGreaterThanOrEqual(-1)
  expect(Math.abs(cardGrowth - headerGrowth - footerGrowth)).toBeLessThanOrEqual(1)
  expect(Math.abs(openedCockpit.cdu.y - closedCockpit.cdu.y - cardGrowth)).toBeLessThanOrEqual(1)
  const ground = page.getByRole('radiogroup', { name: 'Window ground', exact: true })
  for (const [label, value] of [['Relief', 'relief'], ['Imagery', 'imagery']] as const) {
    await ground.getByText(label, { exact: true }).click()
    await expect(outsideScene).toHaveAttribute('data-ground', value)
    await clearOfInstructor(170)
  }
  await page.getByRole('button', { name: 'More instructor room', exact: true }).click()
  await clearOfInstructor(520)
  await page.getByRole('button', { name: 'Compact instructor', exact: true }).click()
  await clearOfInstructor(170)
  await page.keyboard.press('Escape')
  expect(await outsideScene.boundingBox()).toEqual(closedScene)
  await page.getByRole('button', { name: 'Hide the view', exact: true }).click()
  await page.setViewportSize({ width: 800, height: 900 })
  const narrow = await boxes()
  await centredKeys()
  expect(narrow.pfd.y).toBeGreaterThan(narrow.cdu.y)
  expect(narrow.cdu.width).toBeGreaterThanOrEqual(320)
  expect(narrow.pfd.width).toBeGreaterThanOrEqual(280)
  await page.setViewportSize({ width: 400, height: 900 })
  const phone = await boxes()
  await centredKeys()
  expect(phone.pfd.y).toBeGreaterThan(phone.cdu.y)
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(400)
  await page.screenshot({ path: testInfo.outputPath('narrow-cockpit.png'), fullPage: true })

  // This layout owner also protects the real low-speed PFD data once responsive fonts grow.
  // Cruise has no VX/VY or selected ground velocity, so its geometry misses these collisions.
  await page.setViewportSize({ width: 1440, height: 900 })
  await open(page)
  await page.addStyleTag({ url: '/src/Density.css' })
  const scenarios = page.getByRole('region', { name: 'Scenarios' })
  const groundSpeed = {
    id: 'pfd-readable-gspd', title: 'PFD selected ground velocity layout', objective: 'Separate actual, selected and height data',
    start: '87n-offshore-sar', maxSeconds: 240,
    steps: [
      { when: { kind: 'start' }, action: { kind: 'autopilot', hold: true, heading: 230, speed: 25 } },
      { when: { kind: 'time', seconds: 120 }, action: { kind: 'autopilot', hover: true } },
      { when: { kind: 'time', seconds: 160 }, action: { kind: 'autopilot', groundSpeed: 10 } },
      { when: { kind: 'time', seconds: 220 }, action: { kind: 'expectAfcs', collective: 'RHT', pitch: 'GSPD', roll: 'LVL' } },
    ],
  }
  await scenarios.getByLabel('Scenario file').setInputFiles({ name: 'pfd-readable-gspd.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify(groundSpeed)) })
  await page.getByLabel('Simulation rate').selectOption('64')
  await scenarios.getByRole('button', { name: 'Run the scenario' }).click()
  await expect(scenarios.getByRole('status').filter({ hasText: /^PASS/ })).toBeVisible()
  // A completed scenario stops the simulation itself.
  const pfd = page.locator('.efisPfd')
  await expect(pfd.getByTestId('pfd-selected-velocity')).toContainText('+10.0')
  await expect(pfd.getByTestId('pfd-hover-data')).toContainText('VX +10.0')
  const hoverGeometry = async () => pfd.evaluate(svg => {
    const actual = [...svg.querySelectorAll<SVGTextElement>('[data-testid="pfd-hover-data"] > text')]
    const selected = [...svg.querySelectorAll<SVGTextElement>('[data-testid="pfd-selected-velocity"] > text')]
    const data = [...actual, ...selected, svg.querySelector<SVGTextElement>('[data-testid="pfd-ra"]')!,
      svg.querySelector<SVGTextElement>('[data-testid="pfd-hover-height"]')!]
    const rectangles = data.map(node => ({ text: node.textContent, box: node.getBoundingClientRect().toJSON() }))
    const collisions = rectangles.flatMap((first, i) => rectangles.slice(i + 1).flatMap(second =>
      Math.min(first.box.right, second.box.right) > Math.max(first.box.left, second.box.left) &&
      Math.min(first.box.bottom, second.box.bottom) > Math.max(first.box.top, second.box.top)
        ? [`${first.text} overlaps ${second.text}`] : []))
    return { width: svg.getBoundingClientRect().width, rectangles, collisions }
  })
  for (const density of ['comfortable', 'compact']) {
    await page.evaluate(value => { document.documentElement.dataset.density = value }, density)
    for (const width of [1440, 960]) {
      await page.setViewportSize({ width, height: 900 })
      // The isolated bench has no workspace sidebar. Replay the native instrument footprints
      // recorded by the production owner, rather than accepting its wider fixture-only PFD.
      const instrumentWidth = density === 'comfortable' ? (width === 1440 ? 291 : 299) : (width === 1440 ? 302 : 317)
      await pfd.evaluate((svg, pixels) => { svg.style.width = `${pixels}px` }, instrumentWidth)
      await expect.poll(async () => (await hoverGeometry()).collisions, { message: `PFD hover data at ${width}px [${density}]` }).toEqual([])
      await testInfo.attach(`hover-layout-${density}-${width}`, { body: JSON.stringify(await hoverGeometry()), contentType: 'application/json' })
      await pfd.scrollIntoViewIfNeeded()
      await pfd.screenshot({ path: testInfo.outputPath(`hover-gspd-${density}-${width}.png`) })
    }
  }
})

test('the cockpit pilot selections remain keyboard reachable while the docked instructor is open', async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await open(page)
  await page.addStyleTag({ url: '/src/Density.css' })
  await page.getByRole('button', { name: 'Cockpit view', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Engineering view', exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Show the view', exact: true }).click()
  const outsideScene = page.locator('.fmsBenchWindow .fmsOtw')
  await expect(outsideScene).toBeVisible()
  await expect(outsideScene).toHaveAttribute('data-ground', 'imagery')
  await page.getByRole('button', { name: 'Instructor station', exact: true }).click()
  await expect(page.locator('.fmsBench .fmsStationSurfaceInstructor')
    .getByRole('region', { name: 'Instructor station', exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: 'More instructor room', exact: true })).toHaveAttribute('aria-pressed', 'false')
  expect((await outsideScene.boundingBox())!.height).toBe(170)
  const closedFootprint = { scrollY: await page.evaluate(() => window.scrollY) }
  // The open drawer may grow the page: verify normal scrolling and keyboard access, not an invented 900px fit.
  const pilotTargets = page.getByRole('form', { name: 'Vertical and speed selections' })
    .locator('input:enabled, select:enabled, button:enabled')
  const pilotTargetCount = await pilotTargets.count()
  expect(pilotTargetCount).toBeGreaterThan(0)
  const reachedTargets: Array<{ label: string | null; x: number; y: number; width: number; height: number; scrollY: number }> = []
  for (let index = 0; index < pilotTargetCount; index++) {
    const target = pilotTargets.nth(index)
    if (index === 0) await target.focus()
    else await page.keyboard.press('Tab')
    await expect(target).toBeFocused()
    const observation = await target.evaluate(element => {
      element.scrollIntoView({ block: 'nearest', inline: 'nearest', behavior: 'instant' })
      const { x, y, width, height } = element.getBoundingClientRect()
      return { label: element.getAttribute('aria-label') ?? (element as HTMLInputElement).labels?.[0]?.textContent?.trim() ?? element.textContent?.trim() ?? null,
        x, y, width, height, scrollY: window.scrollY, viewportHeight: window.innerHeight, viewportWidth: document.documentElement.clientWidth }
    })
    expect(observation.width).toBeGreaterThan(0)
    expect(observation.height).toBeGreaterThan(0)
    expect(observation.x).toBeGreaterThanOrEqual(0)
    expect(observation.x + observation.width).toBeLessThanOrEqual(observation.viewportWidth)
    expect(observation.y).toBeGreaterThanOrEqual(0)
    expect(observation.y + observation.height).toBeLessThanOrEqual(observation.viewportHeight)
    reachedTargets.push(observation)
  }
  const pilotTargetCountAfter = await pilotTargets.count()
  expect(pilotTargetCountAfter).toBe(pilotTargetCount)
  await testInfo.attach('pilot-target-cardinality', { body: JSON.stringify({ before: pilotTargetCount, after: pilotTargetCountAfter }), contentType: 'application/json' })
  await testInfo.attach('open-instructor-pilot-targets', { body: JSON.stringify(reachedTargets), contentType: 'application/json' })
  // Reachability inspection scrolls the page; restore the measured baseline before viewport-relative geometry checks.
  expect(await page.evaluate(scrollY => {
    window.scrollTo({ top: scrollY, behavior: 'instant' })
    return window.scrollY
  }, closedFootprint.scrollY)).toBe(closedFootprint.scrollY)
})

// Source annunciation owner: the public guidance selection must reach both displays, independently of CDU inspection.
test('cockpit PFD and ND name the guidance computer through FMS 1 to 2 to 1, independently of the inspected CDU', async ({ page }, testInfo) => {
  await open(page)
  await page.getByRole('button', { name: 'Cockpit view', exact: true }).click()
  await page.getByRole('button', { name: 'Instructor station', exact: true }).click()
  await tab(page, 'Flight and setup')
  const source = page.getByLabel('FMS guidance source', { exact: true })
  const inspected = page.getByLabel('CDU inspected', { exact: true })
  const efis = page.getByRole('region', { name: 'EFIS', exact: true })
  const expectSource = async (computer: 1 | 2) => {
    await expect(efis.getByTestId('nav-source')).toHaveText(`FMS${computer} TERM`)
    await expect(efis.getByTestId('nd-source')).toHaveText(new RegExp(`^FMS${computer} `))
  }
  await inspected.selectOption('2')
  await expect(source).toHaveValue('1')
  await expectSource(1)
  await source.selectOption('2')
  await expectSource(2)
  await efis.screenshot({ path: testInfo.outputPath('guidance-fms2.png') })
  await inspected.selectOption('1')
  await expect(source).toHaveValue('2')
  await expectSource(2)
  await source.selectOption('1')
  await expectSource(1)
  await efis.screenshot({ path: testInfo.outputPath('guidance-return-fms1.png') })
})
