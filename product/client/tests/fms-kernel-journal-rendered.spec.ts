import type { KernelRecord } from '../src/fmsCdu/kernel/kernel'
import type { Page, Locator } from '@playwright/test'
import { readFile, writeFile } from 'node:fs/promises'
import { expect, renderedTest as test } from './isolated-client-test'

// Only the real component can detect a first-render TDZ or a CDU/peer key bypassing the submission boundary.
// The exported production journal supplies the evidence; no private unit or kernel seam is used.
test('bench initializes and both real CDU panels submit held keys to the exported journal', async ({ page }) => {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(`${error.name}: ${error.message}`))
  const epoch = new Date('2026-09-29T15:00:00Z')
  await page.clock.setFixedTime(epoch)
  await page.clock.pauseAt(epoch)
  await page.goto('/tests/fixtures/fms-cdu.html?terrain=off&imagery=off')
  await expect.poll(async () => errors.length > 0 || await page.locator('.fmsCdu').count() > 0).toBe(true)
  expect(errors, 'native first render must initialize the actual bench').toEqual([])
  await expect(page.locator('.fmsCdu')).toBeVisible()
  await page.getByRole('button', { name: 'Show peer CDU', exact: true }).click()
  const one = page.getByTestId('fms-cdu-inspected')
  const two = page.getByTestId('fms-cdu-peer')
  await one.locator('[data-key="RTE"]').click()
  await expect(page.getByTestId('fms-input-outcome')).toHaveText('Last executed input: accepted.')
  await two.locator('[data-key="RTE"]').click()
  const clr = two.locator('[data-key="CLR"]')
  const box = (await clr.boundingBox())!
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2)
  await page.mouse.down()
  await page.clock.runFor(1000)
  await page.mouse.up()
  // Play/pause must preserve the same composition and journal; an initializer dependency on playing loses these keys.
  await page.getByRole('button', { name: 'Fly', exact: true }).click()
  await page.getByRole('button', { name: 'Pause', exact: true }).click()
  // A refused FMS go-around remains a real AFCS selection and its outcome is read from the exported event journal.
  await page.getByRole('button', { name: 'TOGA', exact: true }).click()
  await expect(page.getByTestId('fms-input-outcome')).toHaveText('Last executed input: refused: the FMS refused the missed approach: none ahead, or it has failed.')
  const downloaded = page.waitForEvent('download')
  await page.getByRole('button', { name: 'Export journal', exact: true }).click()
  const download = await downloaded
  const record = JSON.parse(await readFile((await download.path())!, 'utf8'))
  const keys = record.journal.filter((entry: { kind: string }) => entry.kind === 'cdu.key')
  expect(keys.map((entry: { submissionSeq: number; payload: unknown; target: string; source: unknown; outcome: unknown }) =>
    [entry.submissionSeq, entry.payload, entry.target, entry.source, entry.outcome])).toEqual([
    [1, { kind: 'cdu.key', side: 1, fn: 'RTE', held: false }, 'fms1', { kind: 'cdu', id: 'CDU 1', surface: 'main', side: 1 }, { status: 'accepted' }],
    [2, { kind: 'cdu.key', side: 2, fn: 'RTE', held: false }, 'fms2', { kind: 'cdu', id: 'CDU 2', surface: 'main', side: 2 }, { status: 'accepted' }],
    [3, { kind: 'cdu.key', side: 2, fn: 'CLR', held: true }, 'fms2', { kind: 'cdu', id: 'CDU 2', surface: 'main', side: 2 }, { status: 'accepted' }],
  ])
  expect(record.journal.filter((entry: { kind: string }) => entry.kind === 'ios.flightFreeze').map((entry: { payload: unknown }) => entry.payload)).toEqual([
    { kind: 'ios.flightFreeze', on: false }, { kind: 'ios.flightFreeze', on: true },
  ])
  expect(record.journal.at(-1)).toMatchObject({ submissionSeq: 6, kind: 'afcs.select', target: 'afcs',
    payload: { kind: 'afcs.select', selection: { select: 'toga' } },
    outcome: { status: 'refused', reason: 'the FMS refused the missed approach: none ahead, or it has failed' },
  })
  expect(errors).toEqual([])
})

// Split windows are essential: both/neither popped out would mask a producer bound to the wrong surface.
test('Jump journal attribution follows the actual engineering or instructor window in split arrangements', async ({ page }) => {
  test.setTimeout(60_000)
  const epoch = new Date('2026-09-29T15:00:00Z')
  await page.clock.setFixedTime(epoch); await page.clock.pauseAt(epoch)
  await page.goto('/tests/fixtures/fms-cdu.html?terrain=off&imagery=off')
  await expect(page.locator('.fmsCdu')).toBeVisible()
  // The cockpit surface exposes Jump in Engineering view; Cockpit view deliberately puts it on the instructor.
  await page.getByRole('button', { name: 'Jump to next waypoint', exact: true }).click()
  await page.getByRole('button', { name: 'Cockpit view', exact: true }).click()
  await page.getByRole('button', { name: 'Instructor station', exact: true }).click()
  const disclosure = page.locator('.fmsStationDock > summary')
  await disclosure.click()
  const controls = page.getByRole('region', { name: 'Station arrangement', exact: true })
  await controls.getByLabel('Instructor apart').check()
  const instructorOpened = page.waitForEvent('popup')
  await controls.getByRole('button', { name: 'Open instructor station in a window', exact: true }).click()
  const instructor = await instructorOpened
  const iosChild = instructor.getByRole('region', { name: 'Instructor station', exact: true })
  await iosChild.getByRole('tab', { name: 'Flight and setup', exact: true }).click()
  // Instructor-popout negative control, while the cockpit surface remains on main.
  await iosChild.getByRole('button', { name: 'Jump to next waypoint', exact: true }).click()
  await iosChild.getByRole('tab', { name: 'Scenarios', exact: true }).click()
  const downloadOne = page.waitForEvent('download')
  await iosChild.getByRole('button', { name: 'Export journal', exact: true }).click()
  const first = JSON.parse(await readFile((await (await downloadOne).path())!, 'utf8'))
  const jumps = (record: typeof first) => record.journal.filter((entry: { kind: string }) => entry.kind === 'ios.jump')
  expect(jumps(first).map((entry: { source: unknown }) => entry.source)).toEqual([
    { kind: 'ios', id: 'flight.jump', surface: 'main' }, { kind: 'ios', id: 'flight.jump', surface: 'popout' },
  ])
  await controls.getByRole('button', { name: 'Return Instructor station', exact: true }).click()
  await expect.poll(() => instructor.isClosed()).toBe(true)
  // The station UI opens outside before cockpit in its three-screen arrangement; no API/seed is involved.
  await controls.getByLabel('Fixed station arrangement').selectOption('three')
  await controls.getByLabel('Instructor apart').uncheck()
  const outsideOpened = page.waitForEvent('popup')
  await controls.getByRole('button', { name: 'Open outside view in a window', exact: true }).click()
  const outside = await outsideOpened
  const cockpitOpened = page.waitForEvent('popup')
  await controls.getByRole('button', { name: 'Open cockpit in a window', exact: true }).click()
  const cockpit = await cockpitOpened
  // Both actual cockpit CDUs are in this window; a real held CLR must retain physical side2 and the producing surface.
  const two = cockpit.locator('.fmsBenchCduStation[data-side="2"]')
  await two.locator('[data-key="RTE"]').click()
  const box = (await two.locator('[data-key="CLR"]').boundingBox())!
  await cockpit.mouse.move(box.x + box.width / 2, box.y + box.height / 2)
  await cockpit.mouse.down(); await page.clock.runFor(1000); await cockpit.mouse.up()
  const iosMain = page.getByRole('region', { name: 'Instructor station', exact: true })
  await iosMain.getByRole('tab', { name: 'Flight and setup', exact: true }).click()
  await iosMain.getByRole('button', { name: 'Jump to next waypoint', exact: true }).click()
  await iosMain.getByRole('tab', { name: 'Scenarios', exact: true }).click()
  const downloadTwo = page.waitForEvent('download')
  await iosMain.getByRole('button', { name: 'Export journal', exact: true }).click()
  const second = JSON.parse(await readFile((await (await downloadTwo).path())!, 'utf8'))
  expect(second.journal.filter((entry: { kind: string }) => entry.kind === 'cdu.key').map((entry: { target: string; source: unknown; payload: unknown }) => [entry.target, entry.source, entry.payload])).toEqual([
    ['fms2', { kind: 'cdu', id: 'CDU 2', surface: 'popout', side: 2 }, { kind: 'cdu.key', side: 2, fn: 'RTE', held: false }],
    ['fms2', { kind: 'cdu', id: 'CDU 2', surface: 'popout', side: 2 }, { kind: 'cdu.key', side: 2, fn: 'CLR', held: true }],
  ])
  expect(jumps(second).map((entry: { source: { surface: string } }) => entry.source.surface)).toEqual(['main', 'popout', 'main'])
  await controls.getByRole('button', { name: 'Return all panels', exact: true }).click()
  await expect.poll(() => cockpit.isClosed() && outside.isClosed()).toBe(true)
})

// Owner: production UI routing. Model calculations remain owned below the UI; expectations here are literal D5 metadata.
const exportFrom = async (root: Page, surface: Page | Locator): Promise<KernelRecord> => {
  const downloaded = root.waitForEvent('download')
  await surface.getByRole('button', { name: 'Export journal', exact: true }).click()
  return JSON.parse(await readFile((await (await downloaded).path())!, 'utf8'))
}
const fixtureFile = (name: string, value: unknown) => ({ name, mimeType: 'application/json', buffer: Buffer.from(JSON.stringify(value)) })
// A file-backed attachment survives successful runs with the line reporter; body-only attachments need a retained trace.
const retainPublicJson = async (name: string, value: unknown) => {
  const path = test.info().outputPath(`${name}.json`)
  await writeFile(path, JSON.stringify(value, null, 2), 'utf8')
  await test.info().attach(name, { path, contentType: 'application/json' })
}

// These rows own D5 bridge metadata, not the GPS/radio model's numerical effects (their existing owners remain).
// A new operation can bypass or misaddress the shared adapter even when another operation on it is correct.
const openD5Instructor = async (page: Page, popped: boolean, options: { epoch?: Date; profile?: string } = {}) => {
  const epoch = options.epoch ?? new Date('2026-09-29T15:00:00Z')
  await page.clock.setFixedTime(epoch); await page.clock.pauseAt(epoch)
  await page.goto('/tests/fixtures/fms-cdu.html?terrain=off&imagery=off')
  await expect(page.locator('.fmsCdu')).toBeVisible()
  await page.getByRole('button', { name: 'Cockpit view', exact: true }).click()
  await page.getByRole('button', { name: 'Instructor station', exact: true }).click()
  if (options.profile) {
    const setup = page.getByRole('region', { name: 'Instructor station', exact: true })
    await setup.getByRole('tab', { name: 'Flight and setup', exact: true }).click()
    await setup.getByRole('combobox', { name: 'Aircraft profile', exact: true }).selectOption(options.profile)
  }
  let instructor = page
  if (popped) {
    await page.locator('.fmsStationDock > summary').click()
    const controls = page.getByRole('region', { name: 'Station arrangement', exact: true })
    await controls.getByLabel('Instructor apart').check()
    const opened = page.waitForEvent('popup')
    await controls.getByRole('button', { name: 'Open instructor station in a window', exact: true }).click()
    instructor = await opened
    await page.locator('.fmsStationDock > summary').click()
  }
  const ios = instructor.getByRole('region', { name: 'Instructor station', exact: true })
  await ios.getByRole('tab', { name: 'Flight and setup', exact: true }).click()
  await ios.getByLabel('CDU inspected', { exact: true }).selectOption('2')
  return { instructor, ios }
}

// AFCS controls belong to the cockpit surface. Keep the instructor on main so the wrong adapter cannot pass.
const openD5Cockpit = async (page: Page, popped: boolean) => {
  if (!popped) return { surface: page, close: async () => {} }
  await page.locator('.fmsStationDock > summary').click()
  const controls = page.getByRole('region', { name: 'Station arrangement', exact: true })
  await controls.getByLabel('Fixed station arrangement', { exact: true }).selectOption('three')
  await controls.getByLabel('Instructor apart', { exact: true }).uncheck()
  // The native station route opens the outside window before it admits the cockpit window.
  const outsideOpened = page.waitForEvent('popup')
  await controls.getByRole('button', { name: 'Open outside view in a window', exact: true }).click()
  const outside = await outsideOpened
  const cockpitOpened = page.waitForEvent('popup')
  await controls.getByRole('button', { name: 'Open cockpit in a window', exact: true }).click()
  const cockpit = await cockpitOpened
  await page.locator('.fmsStationDock > summary').click()
  return { surface: cockpit, close: async () => {
    await page.locator('.fmsStationDock > summary').click()
    await controls.getByRole('button', { name: 'Return all panels', exact: true }).click()
    await expect.poll(() => cockpit.isClosed() && outside.isClosed()).toBe(true)
  } }
}

// Observe the actual public input before the journal receives it; never read the expected context from the journal.
// This test instrumentation makes no model query and forwards the original submit exactly once, synchronously.
const observePinSInput = async (page: Page) => {
  await page.evaluate(async () => {
    const modulePath = '/src/fmsCdu/kernel/kernel.ts'
    const { FmsKernel } = await import(modulePath)
    const prototype = FmsKernel.prototype
    const descriptor = Object.getOwnPropertyDescriptor(prototype, 'submit')!
    if (typeof descriptor?.value !== 'function') throw new Error('Actual public submit boundary unavailable')
    const original = descriptor.value
    const state = { inputs: [] as { action: unknown; source: unknown }[], error: null as string | null, restore: () => {} }
    const host = window as typeof window & { __aerolinkI1bPinsInput?: typeof state }
    if (host.__aerolinkI1bPinsInput) throw new Error('Input observation already active')
    Object.defineProperty(prototype, 'submit', { ...descriptor, value: function (this: unknown, ...args: unknown[]) {
      const action = args[0] as { kind?: string; selection?: { select?: string } }
      if (action.kind === 'afcs.select' && action.selection?.select === 'pinsContinue') {
        try { state.inputs.push(structuredClone({ action: args[0], source: args[1] })) }
        catch (error) { state.error = String(error) }
      }
      return Reflect.apply(original, this, args)
    } })
    state.restore = () => { Object.defineProperty(prototype, 'submit', descriptor); delete host.__aerolinkI1bPinsInput }
    host.__aerolinkI1bPinsInput = state
  })
  return {
    read: () => page.evaluate(() => {
      const host = window as typeof window & { __aerolinkI1bPinsInput?: { inputs: { action: unknown; source: unknown }[]; error: string | null } }
      if (!host.__aerolinkI1bPinsInput) throw new Error('Input observation missing')
      return structuredClone({ inputs: host.__aerolinkI1bPinsInput.inputs, error: host.__aerolinkI1bPinsInput.error })
    }),
    restore: () => page.evaluate(() => {
      const host = window as typeof window & { __aerolinkI1bPinsInput?: { restore: () => void } }
      host.__aerolinkI1bPinsInput?.restore()
      return !host.__aerolinkI1bPinsInput
    }),
  }
}

// Conditional producer owner: the lab's failure/recovery path supplies a real hold; its UI has no ALT button.
// The failure law itself stays with fms-modes; these rows detect missing/wrong input receipts and window attribution.
for (const popped of [false, true]) test(`D5 eligible VNAV on cockpit ${popped ? 'popout' : 'main'} submits its literal action at the paused instant`, async ({ page }) => {
  const { ios } = await openD5Instructor(page, false)
  await expect(page.getByRole('button', { name: 'VNAV', exact: true })).toHaveCount(0)
  await ios.getByLabel('CDU inspected', { exact: true }).selectOption('1')
  await ios.getByRole('combobox', { name: 'Aircraft profile', exact: true }).selectOption('lab-airline-vnav')
  const cockpit = await openD5Cockpit(page, popped)
  const vnav = cockpit.surface.getByRole('button', { name: 'VNAV', exact: true })
  await expect(vnav).toBeDisabled() // healthy initial managed guidance has no altitude-hold latch
  await ios.getByRole('tab', { name: 'Scenarios', exact: true }).click()
  const before = await exportFrom(page, ios)
  expect(before.frame).toBe(0)
  expect(before.journal).toEqual([])
  expect(before.control).toEqual([])
  await ios.getByRole('tab', { name: 'Conditions', exact: true }).click()
  const failure = ios.getByRole('checkbox', { name: /^FMS failure/ })
  await failure.check()
  await expect(vnav).toBeDisabled()
  await failure.uncheck()
  await expect(vnav).toBeEnabled() // actual recovery keeps the hold until this explicit selection
  await vnav.click()
  await expect(vnav).toBeDisabled() // the native accepted selection releases that latch
  await ios.getByRole('tab', { name: 'Scenarios', exact: true }).click()
  await expect(ios.getByTestId('fms-input-outcome')).toHaveText('Last executed input: accepted.')
  const after = await exportFrom(page, ios)
  await retainPublicJson('vnav-public-journals', { before, after })
  expect(after.frame).toBe(0)
  expect(after.control).toEqual(before.control)
  expect(after.journal.map(entry => [entry.submissionSeq, entry.recordOrdinal, entry.frame, entry.phase, entry.orderInPhase, entry.origin, entry.kind, entry.target, entry.source, entry.payload, entry.outcome])).toEqual([
    [1, 1, 0, 'ACTION', 0, 'native', 'ios.condition', 'fms1', { kind: 'ios', id: 'conditions.fmsFail', surface: 'main' }, { kind: 'ios.condition', unit: 1, condition: 'fmsFail', on: true }, { status: 'accepted' }],
    [2, 2, 0, 'ACTION', 1, 'native', 'ios.condition', 'fms1', { kind: 'ios', id: 'conditions.fmsFail', surface: 'main' }, { kind: 'ios.condition', unit: 1, condition: 'fmsFail', on: false }, { status: 'accepted' }],
    [3, 3, 0, 'ACTION', 2, 'native', 'afcs.select', 'afcs', { kind: 'ui', id: 'afcs.vnav', surface: popped ? 'popout' : 'main' }, { kind: 'afcs.select', selection: { select: 'vnav' } }, { status: 'accepted' }],
  ])
  await cockpit.close()
})

// Published 87N Proceed VFR fixture from the existing PinS UI owner. No private route or chart declaration is used.
for (const popped of [false, true]) test(`D5 shipped PinS on cockpit ${popped ? 'popout' : 'main'} journals the crew continuation at its actual MAP`, async ({ page }) => {
  const { ios } = await openD5Instructor(page, false)
  await ios.getByRole('tab', { name: 'Scenarios', exact: true }).click()
  const card = ios.getByRole('region', { name: 'Scenarios', exact: true })
  const scenario = { id: 'd5-pins', title: 'D5 published PinS input', objective: 'Actual MAP continuation receipt', maxSeconds: 1,
    start: '87n-rnav190-final', startTime: '2026-09-29T15:00:00Z', steps: [{ when: { kind: 'start' }, action: { kind: 'expectActive', waypoint: 'STAYS' } }] }
  await card.getByLabel('Scenario file', { exact: true }).setInputFiles(fixtureFile('d5-pins.json', scenario))
  await card.getByRole('button', { name: 'Run the scenario', exact: true }).click()
  await expect(card.getByRole('status').filter({ hasText: /^PASS/ })).toBeVisible()
  const before = await exportFrom(page, ios)
  expect(before.frame).toBe(0)
  expect(before.control).toEqual([])
  expect(before.journal.map(entry => [entry.submissionSeq, entry.frame, entry.phase, entry.origin, entry.kind, entry.outcome.status])).toEqual([
    [1, 0, 'ACTION', 'v1-translated', 'ios.place', 'accepted'],
    [2, 0, 'ACTION', 'v1-translated', 'scenario.check', 'accepted'],
    [3, 0, 'ACTION', 'native', 'ios.flightFreeze', 'accepted'],
  ])
  const cockpit = await openD5Cockpit(page, popped)
  const continuation = cockpit.surface.getByRole('button', { name: 'Continue from MAP', exact: true })
  await expect(continuation).toBeDisabled()
  await expect(cockpit.surface.getByLabel('Landing area in sight', { exact: true })).toHaveCount(0)
  await ios.getByRole('tab', { name: 'Flight and setup', exact: true }).click()
  await ios.getByRole('button', { name: 'Jump to next waypoint', exact: true }).click()
  await ios.getByRole('button', { name: 'Jump to next waypoint', exact: true }).click()
  await expect(continuation).toBeDisabled() // at the MAP, the real chart still requires the crew's VFR declaration
  await cockpit.surface.getByLabel('Basic VFR conditions met', { exact: true }).check()
  await expect(continuation).toBeEnabled()
  const observation = await observePinSInput(page)
  let input: { action: unknown; source: unknown }[] = []
  try {
    await continuation.click()
    const captured = await observation.read()
    expect(captured.error).toBeNull()
    input = captured.inputs
    expect(input, 'the actual public submit must observe this one native continuation').toHaveLength(1)
  } finally {
    expect(await observation.restore(), 'restore the public prototype before further observations').toBe(true)
  }
  await expect(cockpit.surface.getByRole('region', { name: 'Flight', exact: true })).toContainText('crew flying the visual segment')
  await ios.getByRole('tab', { name: 'Scenarios', exact: true }).click()
  await expect(ios.getByTestId('fms-input-outcome')).toHaveText('Last executed input: accepted.')
  const after = await exportFrom(page, ios)
  await retainPublicJson('pins-public-input-and-journals', { input, before, after })
  expect(after.frame).toBe(0)
  expect(after.control).toEqual(before.control)
  // Includes the dynamic declaration context: expected preservation is bound before submit, independently of encoding.
  expect(after.journal.at(-1)!.payload).toEqual(input[0].action)
  expect(after.journal.at(-1)!.source).toEqual(input[0].source)
  // The literal crew/source/time contract remains independently asserted, rather than inferred from observed input.
  expect(after.journal.slice(before.journal.length)).toMatchObject([
    { submissionSeq: 4, recordOrdinal: 4, frame: 0, phase: 'ACTION', orderInPhase: 3, origin: 'native', kind: 'ios.jump', target: 'fms1', source: { kind: 'ios', id: 'flight.jump', surface: 'main' }, payload: { kind: 'ios.jump', unit: 1, op: 'sequence' }, outcome: { status: 'accepted' } },
    { submissionSeq: 5, recordOrdinal: 5, frame: 0, phase: 'ACTION', orderInPhase: 4, origin: 'native', kind: 'ios.jump', target: 'fms1', source: { kind: 'ios', id: 'flight.jump', surface: 'main' }, payload: { kind: 'ios.jump', unit: 1, op: 'sequence' }, outcome: { status: 'accepted' } },
    { submissionSeq: 6, recordOrdinal: 6, frame: 0, phase: 'ACTION', orderInPhase: 5, origin: 'native', kind: 'afcs.select', target: 'afcs', source: { kind: 'ui', id: 'afcs.pinsContinue', surface: popped ? 'popout' : 'main' }, payload: { kind: 'afcs.select', selection: { select: 'pinsContinue', declaration: { basicVfr: true, landingAreaVisible: false, publishedVisibility: false } } }, outcome: { status: 'accepted' } },
  ])
  await cockpit.close()
})

// POS INIT/SETUP laws remain owned by fms-navigation; this owner binds the actual entries to the native D5 stream.
for (const popped of [false, true]) test(`D5 RTC UTC date and SETUP local offset retain CDU1 cockpit ${popped ? 'popout' : 'main'} inputs`, async ({ page }) => {
  const { ios } = await openD5Instructor(page, false)
  await ios.getByLabel('CDU inspected', { exact: true }).selectOption('1')
  const cockpit = await openD5Cockpit(page, popped)
  const cdu = cockpit.surface.locator('.fmsBenchCduStation[data-side="1"]')
  const screen = cdu.locator('.fmsCduScreen')
  await ios.getByRole('tab', { name: 'Scenarios', exact: true }).click()
  const before = await exportFrom(page, ios)
  expect(before).toMatchObject({ frame: 0, journal: [], control: [] })
  const source = { kind: 'cdu', id: 'CDU 1', surface: popped ? 'popout' : 'main', side: 1 }
  const expected: Record<string, unknown>[] = []
  const press = async (physical: string, fn: string) => {
    await cdu.locator(`[data-key="${physical}"]`).click()
    expected.push({ kind: 'cdu.key', payload: { kind: 'cdu.key', side: 1, fn, held: false }, source, target: 'fms1', outcome: { status: 'accepted' } })
  }
  // Physical ids and expected functions are separately literal; no functionFor/encoder supplies this oracle.
  const utc = async () => {
    for (const [physical, fn] of [['1', 'CHAR_1'], ['2', 'CHAR_2'], ['1', 'CHAR_1'], ['5', 'CHAR_5'], ['3', 'CHAR_3'], ['0', 'CHAR_0']]) await press(physical, fn)
    await press('LSK5L', 'LSK5L')
  }
  await press('INIT_REF', 'INIT_REF'); await press('LSK2L', 'LSK2L')
  await expect(screen).toContainText('GPS UTC')
  await expect(screen).toContainText('15:00:00')
  await utc()
  await expect(screen).toContainText('NOT ALLOWED')
  await expect(screen).toContainText('15:00:00')
  const gpsRefusal = await exportFrom(page, ios)
  // Native press returns void: acceptance of LSK5L is not a claim that the GPS-protected RTC entry succeeded.
  expect(gpsRefusal.journal.at(-1)).toMatchObject({ payload: { fn: 'LSK5L' }, outcome: { status: 'accepted' } })
  for (let i = 0; i < 7; i++) await press('CLR', 'CLR') // dismiss the advisory, then remove the retained six digits
  await ios.getByRole('tab', { name: 'Conditions', exact: true }).click()
  await ios.getByRole('checkbox', { name: /^GPS lost sensor/ }).check()
  expected.push({ kind: 'ios.condition', payload: { kind: 'ios.condition', unit: 1, condition: 'gpsLost', on: true },
    source: { kind: 'ios', id: 'conditions.gpsLost', surface: 'main' }, target: 'fms1', outcome: { status: 'accepted' } })
  await expect(screen).toContainText('RTC UTC') // actual positive eligibility, not a forced private RTC offset
  await utc()
  await expect(screen).toContainText('12:15:30')
  for (const [physical, fn] of [['O', 'CHAR_O'], ['C', 'CHAR_C'], ['T', 'CHAR_T'], ['0', 'CHAR_0'], ['1', 'CHAR_1'], ['SLASH', 'SLASH'], ['2', 'CHAR_2'], ['6', 'CHAR_6']]) await press(physical, fn)
  await press('LSK5R', 'LSK5R')
  await expect(screen).toContainText('2026-10-01')
  await expect(screen).toContainText('12:15:30')
  await press('LSK6L', 'LSK6L')
  await expect(screen).toContainText('SETUP')
  await press('LSK2L', 'LSK2L')
  await expect(screen).toContainText('LOCAL')
  for (const [physical, fn] of [['5', 'CHAR_5'], ['DOT', 'DOT'], ['5', 'CHAR_5']]) await press(physical, fn)
  await press('LSK2R', 'LSK2R')
  await expect(screen).toContainText('+5.5HRS')
  await press('LSK6L', 'LSK6L')
  await expect(screen).toContainText('LOCAL')
  await expect(screen).toContainText('17:45:30')
  await ios.getByRole('checkbox', { name: /^GPS lost sensor/ }).uncheck()
  expected.push({ kind: 'ios.condition', payload: { kind: 'ios.condition', unit: 1, condition: 'gpsLost', on: false },
    source: { kind: 'ios', id: 'conditions.gpsLost', surface: 'main' }, target: 'fms1', outcome: { status: 'accepted' } })
  await expect(screen).toContainText('GPS UTC')
  await expect(screen).toContainText('2026-09-29')
  await expect(screen).toContainText('20:30:00') // restored world15:00 UTC plus the unchanged +5.5 local display offset
  await ios.getByRole('tab', { name: 'Scenarios', exact: true }).click()
  const after = await exportFrom(page, ios)
  await retainPublicJson('rtc-setup-public-journals', { before, gpsRefusal, after })
  expect(after.frame).toBe(0)
  expect(after.control).toEqual([])
  expect(after.liveQueue).toEqual({ v: 1, entries: [] })
  expect(after.counters).toEqual({ submissionSeq: 42, controlSeq: 1, recordOrdinal: 42 })
  expect(after.journal).toMatchObject(expected.map((entry, i) => ({ ...entry, submissionSeq: i + 1,
    recordOrdinal: i + 1, origin: 'native', frame: 0, phase: 'ACTION', orderInPhase: i })))
  expect(after.journal).toHaveLength(expected.length)
  await cockpit.close()
})

// Brightness and variation laws stay in fms-cdu-engine; this owner detects the non-CLR hold branch and resolved native keys.
for (const popped of [false, true]) test(`D5 BRT hold and hardware variation keys retain CDU2 cockpit ${popped ? 'popout' : 'main'} identity`, async ({ page }) => {
  const { ios } = await openD5Instructor(page, false)
  const cockpit = await openD5Cockpit(page, popped)
  const cdu = cockpit.surface.locator('.fmsBenchCduStation[data-side="2"]')
  const screen = cdu.locator('.fmsCduScreen')
  await ios.getByRole('tab', { name: 'Scenarios', exact: true }).click()
  const before = await exportFrom(page, ios)
  expect(before).toMatchObject({ frame: 0, journal: [], control: [] })
  const brt = cdu.locator('[data-key="F2_6"]')
  await expect(brt).toHaveAttribute('aria-label', 'BRT')
  const box = (await brt.boundingBox())!
  await cockpit.surface.mouse.move(box.x + box.width / 2, box.y + box.height / 2)
  await cockpit.surface.mouse.down()
  await page.clock.runFor(1000)
  await cockpit.surface.mouse.up() // BRT fires on down only; holding it does not produce a second/held:true key
  const afterHold = await exportFrom(page, ios)
  expect(afterHold.frame).toBe(4)
  expect(afterHold.journal).toHaveLength(1)
  await brt.click()
  await page.clock.runFor(6000) // existing idle-clock path; never alter the five-second BRT law/pacing to admit a fixture
  await brt.click()
  await ios.getByRole('tab', { name: 'Flight and setup', exact: true }).click()
  await ios.getByRole('combobox', { name: 'Hardware variation', exact: true }).selectOption('030/430')
  await expect(cdu.locator('[data-key="F2_2"]')).toHaveAttribute('aria-label', 'TPDR')
  await cdu.locator('[data-key="F2_2"]').click()
  await expect(screen).toContainText('RADIO')
  await expect(screen).toContainText('2/2')
  await ios.getByRole('combobox', { name: 'Hardware variation', exact: true }).selectOption('001/101/301/501')
  await expect(cdu.locator('[data-key="F2_2"]')).toHaveAttribute('aria-label', 'ANS')
  await expect(cdu.locator('[data-key="F2_3"]')).toHaveAttribute('aria-label', 'SQK IDT')
  await cdu.locator('[data-key="F2_2"]').click()
  await expect(screen).toContainText('GSM/SMS')
  await cdu.locator('[data-key="F2_3"]').click()
  await expect(screen).toContainText('TPDR IDENT')
  await ios.getByRole('tab', { name: 'Scenarios', exact: true }).click()
  const after = await exportFrom(page, ios)
  await retainPublicJson('brt-variation-public-journals', { before, afterHold, after })
  expect(after.frame).toBe(28)
  expect(after.control).toEqual([])
  expect(after.liveQueue).toEqual({ v: 1, entries: [] })
  expect(after.counters).toEqual({ submissionSeq: 7, controlSeq: 1, recordOrdinal: 7 })
  expect(after.journal).toMatchObject([
    ['BRT', 0, 0], ['BRT', 4, 0], ['BRT', 28, 0], ['TPDR', 28, 1], ['ANS', 28, 2], ['SQK_IDT', 28, 3],
  ].map(([fn, frame, orderInPhase], i) => ({ kind: 'cdu.key', payload: { kind: 'cdu.key', side: 2, fn, held: false },
    source: { kind: 'cdu', id: 'CDU 2', surface: popped ? 'popout' : 'main', side: 2 }, target: 'fms2', outcome: { status: 'accepted' },
    submissionSeq: i + 1, recordOrdinal: i + 1, origin: 'native', frame, phase: 'ACTION', orderInPhase })))
  expect(after.journal).toHaveLength(6)
  await cockpit.close()
})

for (const popped of [false, true]) test(`D5 GPS parameter branches retain literal receiver, values and instructor ${popped ? 'popout' : 'main'} source`, async ({ page }) => {
  const { instructor, ios } = await openD5Instructor(page, popped)
  await ios.getByRole('tab', { name: 'GPS sensors', exact: true }).click()
  const faults = ios.getByRole('region', { name: 'GPS 2 faults', exact: true })
  const ops: { id: string; op: unknown; outcome?: unknown }[] = []
  // Literal on/off vectors distinguish receiver2 from the inspected FMS2 and shared sensor owner FMS1.
  for (const [label, id, onOp, offOp] of [
    ['SBAS do not use', 'doNotUse', { op: 'sbas', doNotUse: true }, { op: 'sbas', doNotUse: false }],
    ['RF input fault', 'rfInput', { op: 'fault', fault: 'RF_INPUT', on: true }, { op: 'fault', fault: 'RF_INPUT', on: false }],
    ['Baro lost', 'baroLost', { op: 'baroLost', on: true }, { op: 'baroLost', on: false }],
    ['Stop transmitting', 'stopped', { op: 'fault', fault: 'STOP_TRANSMITTING', on: true }, { op: 'fault', fault: 'STOP_TRANSMITTING', on: false }],
  ] as const) {
    await faults.getByLabel(`GPS 2 ${label}`, { exact: true }).check()
    await faults.getByLabel(`GPS 2 ${label}`, { exact: true }).uncheck()
    ops.push({ id, op: onOp }, { id, op: offOp })
  }
  for (const geo of [131, 133]) {
    await faults.getByLabel(`GPS 2 GEO ${geo} outage`, { exact: true }).check()
    await faults.getByLabel(`GPS 2 GEO ${geo} outage`, { exact: true }).uncheck()
    ops.push({ id: 'outage', op: { op: 'sbas', outage: [geo] } }, { id: 'outage', op: { op: 'sbas', outage: [] } })
  }
  await faults.getByLabel('GPS 2 jamming', { exact: true }).focus()
  await faults.getByLabel('GPS 2 jamming', { exact: true }).press('End')
  await faults.getByLabel('GPS 2 jamming', { exact: true }).press('Home')
  ops.push({ id: 'jamming', op: { op: 'jam', db: 25 } }, { id: 'jamming', op: { op: 'jam', db: 0 } })
  await faults.getByLabel('GPS 2 ionospheric storm', { exact: true }).focus()
  await faults.getByLabel('GPS 2 ionospheric storm', { exact: true }).press('End')
  await faults.getByLabel('GPS 2 ionospheric storm', { exact: true }).press('Home')
  ops.push({ id: 'ionoStorm', op: { op: 'sbas', ionoStorm: 30 } }, { id: 'ionoStorm', op: { op: 'sbas', ionoStorm: 1 } })
  await faults.getByLabel('GPS 2 spoof north offset', { exact: true }).fill('-300')
  await faults.getByLabel('GPS 2 spoof east drift', { exact: true }).fill('2')
  await faults.getByRole('button', { name: 'Spoof', exact: true }).click()
  await faults.getByRole('button', { name: 'End', exact: true }).click()
  ops.push({ id: 'spoof', op: { op: 'spoof', northM: -300, driftEastMps: 2 } }, { id: 'spoof', op: { op: 'clearSpoof' } })
  await ios.getByText('GPS 2 bus monitor', { exact: true }).click()
  const monitor = ios.getByRole('table', { name: 'GPS 2 bus monitor', exact: true })
  // Independent declared numeric-label vector. No encoder/receiver output supplies expected label/value/SSM.
  for (const label of ['110', '120', '111', '121', '076', '370', '103', '112', '165', '166', '174', '101', '102', '130', '133', '247', '136', '116', '117', '201']) {
    const row = monitor.locator(`[data-label="${label}"]`)
    await row.getByLabel(`Override ${label}`, { exact: true }).selectOption('FORCE')
    await row.getByLabel(`Override ${label} amount`, { exact: true }).fill('1')
    await row.getByLabel(`Override ${label} status`, { exact: true }).selectOption('FW')
    await row.getByRole('button', { name: 'Set', exact: true }).click()
    await row.getByRole('button', { name: 'Clear', exact: true }).click()
    ops.push({ id: `override ${label}`, op: { op: 'override', label, kind: 'FORCE', amount: 1, ssm: 'FW' } }, { id: `override ${label}`, op: { op: 'clearOverride', label } })
  }
  const numeric = monitor.locator('[data-label="130"]')
  for (const kind of ['FREEZE', 'BIAS', 'RAMP']) {
    await numeric.getByLabel('Override 130', { exact: true }).selectOption(kind)
    if (kind !== 'FREEZE') await numeric.getByLabel('Override 130 amount', { exact: true }).fill('-2')
    await numeric.getByRole('button', { name: 'Set', exact: true }).click()
    await numeric.getByRole('button', { name: 'Clear', exact: true }).click()
    ops.push({ id: 'override 130', op: kind === 'FREEZE' ? { op: 'override', label: '130', kind: 'FREEZE' } : { op: 'override', label: '130', kind, amount: -2 } }, { id: 'override 130', op: { op: 'clearOverride', label: '130' } })
  }
  for (const ssm of ['NORMAL', 'NCD', 'FT', 'FW']) {
    await numeric.getByLabel('Override 130', { exact: true }).selectOption('FORCE')
    await numeric.getByLabel('Override 130 amount', { exact: true }).fill('1')
    await numeric.getByLabel('Override 130 status', { exact: true }).selectOption(ssm)
    await numeric.getByRole('button', { name: 'Set', exact: true }).click()
    await numeric.getByRole('button', { name: 'Clear', exact: true }).click()
    ops.push({ id: 'override 130', op: { op: 'override', label: '130', kind: 'FORCE', amount: 1, ssm } }, { id: 'override 130', op: { op: 'clearOverride', label: '130' } })
  }
  // All typed form branches, including a nested boolean and blank nullable string; validation science stays below UI.
  for (const [label, field, value, control, patch] of [
    ['273', 'used', '5', 'input', { used: 5 }], ['355', 'buses.dme', 'true', 'select', { buses: { dme: true } }],
    ['156', 'armed', 'true', 'select', { armed: true }], ['305', 'provider', 'D5-LITERAL', 'input', { provider: 'D5-LITERAL' }],
    ['305', 'provider', '', 'input', { provider: null }], ['305', 'level', 'LNAV', 'select', { level: 'LNAV' }],
  ] as const) {
    const row = monitor.locator(`[data-label="${label}"]`)
    await row.getByLabel(`Status field ${label}`, { exact: true }).selectOption(field)
    const input = row.getByLabel(`Status value ${label}`, { exact: true })
    if (control === 'select') await input.selectOption(value); else await input.fill(value)
    await row.getByRole('button', { name: 'Set', exact: true }).click()
    await row.getByRole('button', { name: 'Clear', exact: true }).click()
    ops.push({ id: `status ${label}`, op: { op: 'status', label, patch } }, { id: `status ${label}`, op: { op: 'clearStatus', label } })
  }
  // The native amount form has no max attribute: this admitted input reaches the receiver's actual refusal boundary.
  // The preceding BIAS -2 is its positive control; do not substitute disabled/missing-control failures for this refusal.
  await numeric.getByLabel('Override 130', { exact: true }).selectOption('BIAS')
  await numeric.getByLabel('Override 130 amount', { exact: true }).fill('1000000001')
  await numeric.getByRole('button', { name: 'Set', exact: true }).click()
  ops.push({ id: 'override 130', op: { op: 'override', label: '130', kind: 'BIAS', amount: 1000000001 }, outcome: { status: 'refused', reason: 'the receiver refused the operation' } })
  await ios.getByRole('tab', { name: 'Scenarios', exact: true }).click()
  // The execution-outcome display belongs to the actual instructor's Scenarios panel (main or popout).
  await expect(ios.getByTestId('fms-input-outcome')).toHaveText('Last executed input: refused: the receiver refused the operation.')
  const record = await exportFrom(page, ios)
  expect(record.journal.map(entry => [entry.kind, entry.target, entry.source, entry.payload, entry.outcome])).toEqual(ops.map(({ id, op, outcome }) => [
    'f14.gps', 'gps2', { kind: 'f14', id: `gps2.${id}`, surface: popped ? 'popout' : 'main' }, { kind: 'f14.gps', receiver: 2, op }, outcome ?? { status: 'accepted' },
  ]))
  if (popped) { await page.locator('.fmsStationDock > summary').click(); await page.getByRole('button', { name: 'Return all panels', exact: true }).click(); await expect.poll(() => instructor.isClosed()).toBe(true) }
})

for (const popped of [false, true]) test(`D5 sensor parameter branches retain shared versus inspected targets and instructor ${popped ? 'popout' : 'main'} source`, async ({ page }) => {
  const { instructor, ios } = await openD5Instructor(page, popped)
  await ios.getByRole('tab', { name: 'Conditions', exact: true }).click()
  const card = ios.getByRole('region', { name: 'Sensor fault laboratory', exact: true })
  const vectors: { unit: number; stimulus: Record<string, unknown> & { kind: string }; outcome: unknown }[] = []
  const accepted = (unit: number, stimulus: Record<string, unknown> & { kind: string }) => vectors.push({ unit, stimulus, outcome: { status: 'accepted' } })
  await card.getByLabel('Fault radio', { exact: true }).selectOption('nav2')
  await card.getByLabel('Radio receiver state', { exact: true }).selectOption('SILENT')
  await card.getByRole('button', { name: 'Apply receiver state', exact: true }).click()
  accepted(2, { kind: 'radioFault', device: 'nav2', receiver: 'SILENT' })
  await card.getByRole('button', { name: 'Apply control path', exact: true }).click()
  accepted(2, { kind: 'radioFault', device: 'nav2', controlPath: 'LOST' })
  await card.getByRole('button', { name: 'Apply measurement bus', exact: true }).click()
  accepted(2, { kind: 'radioFault', device: 'nav2', measurementBus: 'LOST' })
  await card.getByLabel('Fault radio', { exact: true }).selectOption('dme1')
  await expect(card.getByRole('button', { name: 'Apply control path', exact: true })).toBeDisabled()
  await card.getByLabel('Fault station ident', { exact: true }).fill('ZZZZ')
  await card.getByRole('button', { name: 'Apply ground stimulus', exact: true }).click()
  vectors.push({ unit: 2, stimulus: { kind: 'stationFault', ident: 'ZZZZ', component: 'DME', reply: false }, outcome: { status: 'refused', reason: 'DME ZZZZ requires one unambiguous compatible facility in the active database' } })
  // Published native F14 fixture has YOW VOR/DME and the five-character BOBTU NDB; no fabricated facilities.
  await card.getByLabel('Fault station ident', { exact: true }).fill('YOW')
  for (const [choice, stimulus] of [
    ['DME_NO_REPLY', { kind: 'stationFault', ident: 'YOW', component: 'DME', reply: false }],
    ['DME_REPLY', { kind: 'stationFault', ident: 'YOW', component: 'DME', reply: true }],
    ['OFF_AIR', { kind: 'stationOffAir', ident: 'YOW', off: true }], ['ON_AIR', { kind: 'stationOffAir', ident: 'YOW', off: false }],
  ] as const) {
    await card.getByLabel('Ground station stimulus', { exact: true }).selectOption(choice)
    await card.getByRole('button', { name: 'Apply ground stimulus', exact: true }).click(); accepted(2, stimulus)
  }
  await card.getByLabel('Ground station stimulus', { exact: true }).selectOption('DME_IDENT')
  for (const ident of ['BAD', '']) {
    await card.getByLabel('DME reported ident', { exact: true }).fill(ident)
    await card.getByRole('button', { name: 'Apply ground stimulus', exact: true }).click()
    accepted(2, { kind: 'stationFault', ident: 'YOW', component: 'DME', reportedIdent: ident === '' ? null : 'BAD' })
  }
  await card.getByLabel('Ground station stimulus', { exact: true }).selectOption('VOR_BIAS')
  await card.getByLabel('VOR radial bias degrees', { exact: true }).fill('10')
  await card.getByRole('button', { name: 'Apply ground stimulus', exact: true }).click()
  accepted(2, { kind: 'stationFault', ident: 'YOW', component: 'VOR', biasDeg: 10 })
  for (const [choice, offAir] of [['NDB_OFF', true], ['NDB_ON', false]] as const) {
    await card.getByLabel('Ground station stimulus', { exact: true }).selectOption(choice)
    await card.getByLabel('Fault station ident', { exact: true }).fill('BOBTU')
    await card.getByRole('button', { name: 'Apply ground stimulus', exact: true }).click()
    accepted(2, { kind: 'ndb', ident: 'BOBTU', offAir })
  }
  await card.getByLabel('Navigation heading valid', { exact: true }).uncheck()
  accepted(1, { kind: 'airInput', headingValid: false })
  await card.getByLabel('Navigation heading bias degrees', { exact: true }).fill('30')
  await card.getByRole('button', { name: 'Apply heading bias', exact: true }).click()
  accepted(1, { kind: 'airInput', headingBiasDeg: 30 })
  await card.getByLabel('Measured Doppler surface', { exact: true }).selectOption('SEA')
  await card.getByRole('button', { name: 'Apply Doppler surface', exact: true }).click()
  accepted(1, { kind: 'dvsInput', surface: 'SEA' })
  await card.getByLabel('Power interruption duration ms', { exact: true }).fill('51')
  await card.getByRole('button', { name: 'Interrupt KALMAN power', exact: true }).click()
  accepted(1, { kind: 'powerInterrupt', durationMs: 51 })
  await card.getByLabel('APIRS accelerometer bias north m/s²', { exact: true }).fill('0.3')
  await card.getByLabel('APIRS accelerometer bias east m/s²', { exact: true }).fill('-0.2')
  await card.getByRole('button', { name: 'Apply APIRS bias', exact: true }).click()
  accepted(1, { kind: 'apirsBias', northMs2: 0.3, eastMs2: -0.2 })
  for (const [button, mode] of [['GPS pair integrity only', 'INTEGRITY_ONLY'], ['GPS pair position gone', 'POSITION_GONE'], ['Restore GPS pair words', 'NORMAL']] as const) {
    await card.getByRole('button', { name: button, exact: true }).click()
    accepted(1, { kind: 'gpsPair', mode })
  }
  await ios.getByRole('tab', { name: 'Scenarios', exact: true }).click()
  const record = await exportFrom(page, ios)
  expect(record.journal.map(entry => [entry.kind, entry.target, entry.source, entry.payload, entry.outcome])).toEqual(vectors.map(({ unit, stimulus, outcome }) => [
    'f14.sensor', unit === 1 ? 'fms1' : 'fms2', { kind: 'f14', id: `sensor.${stimulus.kind}`, surface: popped ? 'popout' : 'main' }, { kind: 'f14.sensor', unit, stimulus }, outcome,
  ]))
  if (popped) { await page.locator('.fmsStationDock > summary').click(); await page.getByRole('button', { name: 'Return all panels', exact: true }).click(); await expect.poll(() => instructor.isClosed()).toBe(true) }
})

// HOV/TU are v1 scenario inputs, not native AFCS buttons. Their refused high-speed branch must not become a silent pass.
for (const popped of [false, true]) test(`D5 v1 HOV TU refusal stays a scenario-host input when instructor is ${popped ? 'popout' : 'main'}`, async ({ page }) => {
  const { instructor, ios } = await openD5Instructor(page, popped)
  await expect(page.getByRole('button', { name: 'HOV', exact: true })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'TU', exact: true })).toHaveCount(0)
  await ios.getByRole('tab', { name: 'Scenarios', exact: true }).click()
  const card = ios.getByRole('region', { name: 'Scenarios', exact: true })
  for (const [id, action, reason] of [
    ['d5-v1-hover', { kind: 'autopilot', hover: true }, 'HOV is not available: above the coordinated-flight speed, or no eligible hover feedback'],
    ['d5-v1-tu', { kind: 'autopilot', transitionUp: true }, 'TU is not available: not in a hover mode, too fast, or no valid radio height'],
  ] as const) {
    // Published 87N start:100 KIAS/500ft. ALT is a real accepted positive control; no forced hover/instant recovery.
    const scenario = { id, title: id, objective: 'Preserve native v1 refusal and sender', profile: 'cma9000-s300-heli-civil',
      start: '87n-offshore-sar', surface: 'offshore-87n', startTime: '2026-09-29T15:00:00.000Z', maxSeconds: 10,
      steps: [{ when: { kind: 'start' }, action: { kind: 'autopilot', hold: true } }, { when: { kind: 'start' }, action }] }
    await card.getByLabel('Scenario file', { exact: true }).setInputFiles(fixtureFile(`${id}.json`, scenario))
    await expect(card.getByText(`Loaded “${id}.json”.`, { exact: true })).toBeVisible()
    await card.getByRole('button', { name: 'Run the scenario', exact: true }).click()
    await expect(card.getByRole('status').filter({ hasText: /^EXECUTION ERROR:/ })).toBeVisible()
    const record = await exportFrom(page, ios)
    await retainPublicJson(`${id}-public-journal`, { suppliedScenario: scenario, record })
    const start = `${id}@2026-09-29T15:00:00.000Z`
    expect(record.frame).toBe(0)
    expect(record.control).toEqual([])
    expect(record.liveQueue).toEqual({ v: 1, entries: [] })
    expect(record.counters).toEqual({ submissionSeq: 6, controlSeq: 1, recordOrdinal: 6 })
    expect(record.journal).toMatchObject([
      { kind: 'config.surface', target: 'plant', payload: { kind: 'config.surface', surface: 'offshore-87n' }, outcome: { status: 'accepted' }, origin: 'v1-translated', source: { kind: 'scenario', id: `${start} start`, surface: 'main' } },
      { kind: 'ios.place', target: 'plant', payload: { kind: 'ios.place', startState: '87n-offshore-sar' }, outcome: { status: 'accepted' }, origin: 'v1-translated', source: { kind: 'scenario', id: `${start} start`, surface: 'main' } },
      { kind: 'scenario.step', target: 'scenario', payload: { kind: 'autopilot', hold: true }, outcome: { status: 'accepted' }, origin: 'v1-translated', source: { kind: 'scenario', id: `${start} step 1`, surface: 'main' } },
      { kind: 'scenario.step', target: 'scenario', payload: action, outcome: { status: 'refused', reason }, origin: 'v1-translated', source: { kind: 'scenario', id: `${start} step 2`, surface: 'main' } },
      { kind: 'ios.flightFreeze', target: 'plant', payload: { kind: 'ios.flightFreeze', on: true }, outcome: { status: 'accepted' }, origin: 'native', source: { kind: 'ui', id: 'flight.runEnded', surface: 'main' } },
    ].map((entry, i) => ({ ...entry, submissionSeq: i + 1, recordOrdinal: i + 1, frame: 0, phase: 'ACTION', orderInPhase: i })))
    expect(record.journal).toHaveLength(5)
    const downloaded = page.waitForEvent('download')
    await card.getByRole('button', { name: 'Download run report', exact: true }).click()
    const report = await readFile((await (await downloaded).path())!, 'utf8')
    const path = test.info().outputPath(`${id}-run.md`)
    await writeFile(path, report, 'utf8')
    await test.info().attach(`${id}-run`, { path, contentType: 'text/markdown' })
    expect(report).toMatch(/^\| 1 \|[^\n]*\| 0 s \| DONE \|/m)
    expect(report).toMatch(/^\| 2 \|[^\n]*\| 0 s \| ERROR \|/m)
    expect(report).toContain(reason)
  }
  if (popped) { await page.locator('.fmsStationDock > summary').click(); await page.getByRole('button', { name: 'Return all panels', exact: true }).click(); await expect.poll(() => instructor.isClosed()).toBe(true) }
})

// Published warm GPS1 PRN24 fixture; the logical GPS/FDE owners keep the sky and fault-response science.
for (const popped of [false, true]) test(`D5 mask satellite and selection producers retain resolved values on instructor ${popped ? 'popout' : 'main'}`, async ({ page }) => {
  const { instructor, ios } = await openD5Instructor(page, popped, { epoch: new Date('2026-09-27T14:00:00Z'), profile: 'cma9000-later-sbas-heli' })
  await ios.getByRole('tab', { name: 'Scenarios', exact: true }).click()
  const before = await exportFrom(page, ios)
  expect(before).toMatchObject({ frame: 0, journal: [], control: [] })
  await ios.getByRole('tab', { name: 'GPS sensors', exact: true }).click()
  const expected: Record<string, unknown>[] = []
  const surface = popped ? 'popout' : 'main'
  for (const choice of ['GPS1', 'OFF', 'AUTO']) {
    await ios.getByLabel('FMS GPS selection', { exact: true }).selectOption(choice)
    expected.push({ kind: 'fms.gpsSelect', target: 'fms2', payload: { kind: 'fms.gpsSelect', unit: 2, choice },
      source: { kind: 'ios', id: 'gps.selection', surface }, outcome: { status: 'accepted' } })
  }
  const faults = ios.getByRole('region', { name: 'GPS 1 faults', exact: true })
  const gps = (id: string, op: unknown) => expected.push({ kind: 'f14.gps', target: 'gps1', payload: { kind: 'f14.gps', receiver: 1, op },
    source: { kind: 'f14', id: `gps1.${id}`, surface }, outcome: { status: 'accepted' } })
  const low = faults.getByRole('button', { name: 'Mask low satellites (below 15°)', exact: true })
  await expect(low).toBeEnabled(); await expect(low).toHaveAttribute('aria-pressed', 'false')
  await low.click(); gps('maskLow', { op: 'maskLow', on: true })
  await expect(low).toHaveAttribute('aria-pressed', 'true') // positive low-satellite eligibility, not an assumed empty toggle
  await low.click(); gps('maskLow', { op: 'maskLow', on: false })
  await expect(low).toHaveAttribute('aria-pressed', 'false')
  const prn = faults.getByRole('group', { name: 'GPS 1 mask satellites', exact: true }).getByRole('button', { name: '24', exact: true })
  await expect(prn).toBeEnabled(); await expect(prn).toHaveAttribute('aria-pressed', 'false')
  await prn.click(); gps('mask', { op: 'mask', prns: [24] })
  await expect(prn).toHaveAttribute('aria-pressed', 'true')
  await prn.click(); gps('mask', { op: 'mask', prns: [] })
  await expect(prn).toHaveAttribute('aria-pressed', 'false')
  const satellite = faults.getByLabel('GPS 1 faulty satellite', { exact: true })
  const inject = faults.getByRole('button', { name: 'Inject', exact: true })
  const clear = faults.getByRole('button', { name: 'Clear', exact: true })
  await expect(inject).toBeDisabled(); await expect(clear).toBeDisabled()
  await expect(satellite.getByRole('option', { name: 'PRN 24', exact: true })).toHaveCount(1)
  await satellite.selectOption('24') // independently published tracked PRN; never select an observed/model-derived expected id
  for (const [fault, value] of [['RAMP', 5], ['STEP', 200]] as const) {
    await faults.getByLabel('GPS 1 range error kind', { exact: true }).selectOption(fault)
    await faults.getByLabel('GPS 1 range error amount', { exact: true }).fill(String(value))
    await expect(inject).toBeEnabled(); await inject.click()
    gps('satelliteFault', { op: 'satelliteFault', prn: 24, fault, value })
    await expect(clear).toBeEnabled(); await clear.click()
    gps('satelliteFault', { op: 'clearSatelliteFault' })
    await expect(clear).toBeDisabled()
  }
  await ios.getByRole('tab', { name: 'Conditions', exact: true }).click()
  await ios.getByRole('checkbox', { name: /^GPS integrity lost \(RAIM\) sensor/ }).check()
  expected.push({ kind: 'ios.condition', target: 'fms1', payload: { kind: 'ios.condition', unit: 1, condition: 'gpsIntegrity', on: true },
    source: { kind: 'ios', id: 'conditions.gpsIntegrity', surface }, outcome: { status: 'accepted' } })
  await ios.getByRole('tab', { name: 'GPS sensors', exact: true }).click()
  await expect(low).toBeDisabled()
  await ios.getByRole('tab', { name: 'Conditions', exact: true }).click()
  await ios.getByRole('checkbox', { name: /^GPS integrity lost \(RAIM\) sensor/ }).uncheck()
  expected.push({ kind: 'ios.condition', target: 'fms1', payload: { kind: 'ios.condition', unit: 1, condition: 'gpsIntegrity', on: false },
    source: { kind: 'ios', id: 'conditions.gpsIntegrity', surface }, outcome: { status: 'accepted' } })
  await ios.getByRole('tab', { name: 'GPS sensors', exact: true }).click()
  await expect(low).toBeEnabled()
  await ios.getByRole('tab', { name: 'Scenarios', exact: true }).click()
  const after = await exportFrom(page, ios)
  await retainPublicJson('mask-satellite-public-journals', { before, after })
  expect(after.frame).toBe(0)
  expect(after.control).toEqual([])
  expect(after.liveQueue).toEqual({ v: 1, entries: [] })
  expect(after.counters).toEqual({ submissionSeq: 14, controlSeq: 1, recordOrdinal: 14 })
  expect(after.journal).toMatchObject(expected.map((entry, i) => ({ ...entry, submissionSeq: i + 1,
    recordOrdinal: i + 1, origin: 'native', frame: 0, phase: 'ACTION', orderInPhase: i })))
  expect(after.journal).toHaveLength(13)
  if (popped) { await page.locator('.fmsStationDock > summary').click(); await page.getByRole('button', { name: 'Return all panels', exact: true }).click(); await expect.poll(() => instructor.isClosed()).toBe(true) }
})

for (const popped of [false, true]) test(`D5 actual instructor ${popped ? 'popout' : 'main'} producers export literal resolved actions including a valid discontinuity override`, async ({ page }) => {
  test.setTimeout(90_000)
  const epoch = new Date('2026-09-29T15:00:00Z')
  await page.clock.setFixedTime(epoch); await page.clock.pauseAt(epoch)
  await page.goto('/tests/fixtures/fms-cdu.html?terrain=off&imagery=off')
  const one = page.getByTestId('fms-cdu-inspected')
  await one.locator('[data-key="LEGS"]').click()
  for (const fn of ['CHAR_E', 'CHAR_L', 'CHAR_I', 'CHAR_B', 'CHAR_A', 'LSK1L', 'EXEC']) await one.locator(`[data-fn="${fn}"]`).click()
  await page.getByRole('button', { name: 'Jump to next waypoint', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Override discontinuity', exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Jump to next waypoint', exact: true }).click()
  await page.getByRole('button', { name: 'Cockpit view', exact: true }).click()
  await page.getByRole('button', { name: 'Instructor station', exact: true }).click()
  await page.locator('.fmsStationDock > summary').click()
  const controls = page.getByRole('region', { name: 'Station arrangement', exact: true })
  let instructor = page
  if (popped) {
    await controls.getByLabel('Instructor apart').check()
    const opened = page.waitForEvent('popup')
    await controls.getByRole('button', { name: 'Open instructor station in a window', exact: true }).click()
    instructor = await opened
  }
  // Station arrangement is presentation-only; close its sticky disclosure before using the underlying controls.
  await page.locator('.fmsStationDock > summary').click()
  const ios = instructor.getByRole('region', { name: 'Instructor station', exact: true })
  const tab = (name: string) => ios.getByRole('tab', { name, exact: true }).click()
  await tab('Flight and setup')
  await ios.getByRole('button', { name: 'Override discontinuity', exact: true }).click()
  await expect(ios.getByRole('button', { name: 'Override discontinuity', exact: true })).toHaveCount(0)
  await ios.getByLabel('CDU inspected', { exact: true }).selectOption('2')
  await page.getByLabel('FMS guidance source', { exact: true }).selectOption('2')
  await ios.getByLabel('CDU inspected', { exact: true }).selectOption('1')
  await ios.getByRole('button', { name: 'Jump to next waypoint', exact: true }).click()
  await ios.getByLabel('CDU inspected', { exact: true }).selectOption('2')
  // Jump already leaves the default flight in LNAV. Select the real basic mode first, with TRUE heading while failed.
  await tab('Conditions')
  await ios.getByRole('checkbox', { name: /^FMS failure/ }).check()
  await page.getByLabel('Selected heading', { exact: true }).fill('45')
  await page.getByRole('button', { name: 'HDG SEL', exact: true }).click()
  await ios.getByRole('checkbox', { name: /^FMS failure/ }).uncheck()
  await tab('Flight and setup')
  await page.getByRole('button', { name: 'LNAV', exact: true }).click()
  await page.getByLabel('Preselected altitude', { exact: true }).fill('4000')
  await page.getByRole('button', { name: 'SET', exact: true }).click()
  await page.getByLabel('Selected speed', { exact: true }).fill('100')
  await page.getByRole('button', { name: 'SET SPD', exact: true }).click()
  await page.getByRole('button', { name: 'TOGA', exact: true }).click()
  await page.getByRole('button', { name: 'VS', exact: true }).click()
  await page.getByRole('button', { name: 'ALT', exact: true }).click()
  await page.getByRole('button', { name: 'FTR', exact: true }).click()
  await page.getByLabel('Selected ground speed', { exact: true }).fill('5')
  await page.getByRole('button', { name: 'GSPD', exact: true }).click()
  await tab('Conditions')
  await ios.getByRole('checkbox', { name: /^GPS lost sensor/ }).check()
  await ios.getByRole('checkbox', { name: /^GPS lost sensor/ }).uncheck()
  await ios.getByLabel('Alert message to raise', { exact: true }).fill('D5 LITERAL')
  await ios.getByRole('button', { name: 'Raise alert', exact: true }).click()
  const baro = ios.getByRole('region', { name: 'Barometric altitude', exact: true })
  await baro.getByLabel('Altimeter setting (QNH, hPa)', { exact: true }).fill('1002')
  await baro.getByRole('button', { name: 'Set QNH', exact: true }).click()
  await baro.getByLabel('Declared QNH (hPa)', { exact: true }).fill('1000')
  await baro.getByRole('button', { name: 'Declare the QNH', exact: true }).click()
  await baro.getByLabel('Baro error (ft)', { exact: true }).fill('80')
  await baro.getByRole('button', { name: 'Inject the error', exact: true }).click()
  await ios.getByLabel('Navigation TAS valid', { exact: true }).uncheck()
  await tab('GPS sensors')
  await ios.getByLabel('FMS GPS selection', { exact: true }).selectOption('GPS2')
  await ios.getByLabel('GPS 2 Receiver fault', { exact: true }).check()
  await ios.getByLabel('GPS 2 Receiver fault', { exact: true }).uncheck()
  await tab('Dual FMS and radios')
  await ios.getByRole('button', { name: 'Fail cross-talk link', exact: true }).click()
  await ios.getByRole('button', { name: 'Restore cross-talk link', exact: true }).click()
  await ios.getByLabel('COM1 device feedback', { exact: true }).selectOption('failed')
  await ios.getByLabel('COM1 device feedback', { exact: true }).selectOption('normal')
  await ios.getByRole('button', { name: 'Reject its next tune', exact: true }).click()
  await tab('Nav data')
  await ios.getByRole('button', { name: 'FMS power off', exact: true }).click()
  await ios.getByRole('button', { name: 'Warm start FMS', exact: true }).click()
  // The accepted S300 laboratory boot duration is five seconds; admit preflight only after the real unit finishes.
  await page.clock.runFor(5000)
  await expect(ios.getByRole('button', { name: 'POS INIT', exact: true })).toBeEnabled()
  await ios.getByRole('button', { name: 'POS INIT', exact: true }).click()
  await ios.getByRole('button', { name: 'Restore WMM2025', exact: true }).click()
  const unsupported = {}
  await ios.getByLabel('Load magnetic model package', { exact: true }).setInputFiles(fixtureFile('unsupported.json', unsupported))
  await expect(ios.getByText('Refused: unsupported model package.', { exact: true })).toBeVisible()
  const user = { schema: 'aerolink.fms.user-database', version: 1, userId: 'fixture', profileId: 'cma9000-s300-heli-civil', exported: epoch.toISOString(), waypoints: [{ ident: 'D5FIX', type: 'FIXED', position: { lat: 45, lon: -73 } }], routes: [] }
  const text = JSON.stringify(user)
  await ios.getByLabel('User database file', { exact: true }).setInputFiles(fixtureFile('d5-user.json', user))
  await expect(ios.getByText('d5-user.json: 1 user waypoints and 0 user routes added.', { exact: true })).toBeVisible()
  const cifp = await readFile('tests/fixtures/cifp/kbtv-2609.pc')
  await ios.getByLabel('Airports to load', { exact: true }).fill('KBTV')
  await ios.getByLabel('ARINC 424 navigation data file', { exact: true }).setInputFiles({ name: 'kbtv.pc', mimeType: 'text/plain', buffer: cifp })
  await expect(ios.getByText(/kbtv.pc: .*records read/)).toBeVisible()
  await ios.getByRole('button', { name: /^Activate / }).click()
  await ios.getByRole('button', { name: 'Load the KBTV demonstration (FAA CIFP 2609)', exact: true }).click()
  await expect(ios.getByText(/^KBTV demonstration loaded and active:/)).toBeVisible()
  await tab('Scenarios')
  const record = await exportFrom(page, ios)
  const literal = (kind: string, sourceKind: string, id: string, target: string, payload: unknown, status = 'accepted', surface = popped ? 'popout' : 'main') => ({ kind, source: { kind: sourceKind, id, surface }, target, payload, status })
  expect(record.journal.slice(0, 8).map(entry => [entry.kind, entry.target, entry.source, entry.payload, entry.outcome])).toEqual(
    ['LEGS', 'CHAR_E', 'CHAR_L', 'CHAR_I', 'CHAR_B', 'CHAR_A', 'LSK1L', 'EXEC'].map(fn => ['cdu.key', 'fms1', { kind: 'cdu', id: 'CDU 1', surface: 'main', side: 1 }, { kind: 'cdu.key', side: 1, fn, held: false }, { status: 'accepted' }]))
  // Slice after the eight real keys; no child effect records are invented for a sensor parent action.
  expect(record.journal.slice(8).map(entry => ({ kind: entry.kind, source: entry.source, target: entry.target, payload: entry.payload, status: entry.outcome.status }))).toEqual([
    literal('ios.jump', 'ios', 'flight.jump', 'fms1', { kind: 'ios.jump', unit: 1, op: 'sequence' }, 'accepted', 'main'),
    literal('ios.jump', 'ios', 'flight.jump', 'fms1', { kind: 'ios.jump', unit: 1, op: 'sequence' }, 'refused', 'main'),
    literal('ios.jump', 'ios', 'flight.overrideDiscontinuity', 'fms1', { kind: 'ios.jump', unit: 1, op: 'overrideDiscontinuity' }),
    literal('afcs.sourceSelect', 'ui', 'flight.guidanceSource', 'afcs', { kind: 'afcs.sourceSelect', side: 2 }, 'accepted', 'main'),
    literal('ios.jump', 'ios', 'flight.jump', 'fms2', { kind: 'ios.jump', unit: 2, op: 'sequence' }),
    literal('ios.condition', 'ios', 'conditions.fmsFail', 'fms2', { kind: 'ios.condition', unit: 2, condition: 'fmsFail', on: true }),
    literal('afcs.select', 'ui', 'afcs.heading', 'afcs', { kind: 'afcs.select', selection: { select: 'heading', heading: 45 } }, 'accepted', 'main'),
    literal('ios.condition', 'ios', 'conditions.fmsFail', 'fms2', { kind: 'ios.condition', unit: 2, condition: 'fmsFail', on: false }),
    ...[{ select: 'lnav' }, { select: 'altitude', altitude: 4000 }, { select: 'speed', knots: 100 }].map(selection => literal('afcs.select', 'ui', `afcs.${selection.select}`, 'afcs', { kind: 'afcs.select', selection }, 'accepted', 'main')),
    literal('afcs.select', 'ui', 'afcs.toga', 'afcs', { kind: 'afcs.select', selection: { select: 'toga' } }, 'refused', 'main'),
    ...[{ select: 'verticalSpeed', fpm: -500 }, { select: 'altitudeHold' }, { select: 'forceTrimRelease' }].map(selection => literal('afcs.select', 'ui', `afcs.${selection.select}`, 'afcs', { kind: 'afcs.select', selection }, 'accepted', 'main')),
    literal('afcs.select', 'ui', 'afcs.groundSpeed', 'afcs', { kind: 'afcs.select', selection: { select: 'groundSpeed', knots: 5 } }, 'refused', 'main'),
    ...[true, false].map(on => literal('ios.condition', 'ios', 'conditions.gpsLost', 'fms1', { kind: 'ios.condition', unit: 1, condition: 'gpsLost', on })),
    literal('ios.alert', 'ios', 'alerts.other', 'fms2', { kind: 'ios.alert', unit: 2, text: 'D5 LITERAL' }),
    literal('fms.baroSetting', 'ui', 'baro.setting', 'fms2', { kind: 'fms.baroSetting', unit: 2, setting: { kind: 'QNH', hPa: 1002 } }),
    literal('ios.atmosphere', 'ios', 'baro.declaredQnh', 'plant', { kind: 'ios.atmosphere', declaredQnh: 1000, reason: 'bench' }),
    literal('ios.atmosphere', 'ios', 'baro.error', 'plant', { kind: 'ios.atmosphere', baroErrorFt: 80, reason: 'bench' }),
    literal('f14.sensor', 'f14', 'sensor.airInput', 'fms1', { kind: 'f14.sensor', unit: 1, stimulus: { kind: 'airInput', tasValid: false } }),
    literal('fms.gpsSelect', 'ios', 'gps.selection', 'fms2', { kind: 'fms.gpsSelect', unit: 2, choice: 'GPS2' }),
    ...[true, false].map(on => literal('f14.gps', 'f14', 'gps2.receiver', 'gps2', { kind: 'f14.gps', receiver: 2, op: { op: 'fault', fault: 'RECEIVER', on } })),
    ...[false, true].map(available => literal('ios.link', 'ios', 'dual.link', 'ios', { kind: 'ios.link', available })),
    literal('f14.radio', 'f14', 'dual.com1Feedback', 'rms', { kind: 'f14.radio', device: 'com1', feedback: 'FAILED' }),
    literal('f14.radio', 'f14', 'dual.com1Feedback', 'rms', { kind: 'f14.radio', device: 'com1', feedback: 'NORMAL' }),
    literal('f14.radio', 'f14', 'dual.rejectNext', 'rms', { kind: 'f14.radio', device: 'nav2', rejectNext: true }),
    ...[{ mode: 'OFF', id: 'powerOff' }, { mode: 'WARM', id: 'warmStart' }].map(({mode, id}) => literal('ios.unitPower', 'ios', `navdata.${id}`, 'fms2', { kind: 'ios.unitPower', unit: 2, mode, onGround: false })),
    { ...literal('cdu.open', 'ios', 'navdata.preflight.POS', 'fms2', { kind: 'cdu.open', side: 2, page: 'POS' }), source: { kind: 'ios', id: 'navdata.preflight.POS', side: 2, surface: popped ? 'popout' : 'main' } },
    literal('config.magvar', 'ios', 'navdata.restoreWmm2025', 'fms2', { kind: 'config.magvar', unit: 2, op: 'restoreWmm2025' }),
    literal('config.magvar', 'import', 'navdata.magvarPackage', 'fms2', { kind: 'config.magvar', unit: 2, op: 'load', package: unsupported }, 'refused'),
    literal('config.userdb', 'import', 'navdata.userDatabase', 'fms2', { kind: 'config.userdb', unit: 2, text }),
    literal('config.navdb', 'import', 'navdata.arinc424', 'fms2', { kind: 'config.navdb', unit: 2, op: 'loadArinc424', name: 'kbtv.pc', airports: ['KBTV'], text: cifp.toString('utf8') }),
    literal('config.navdb', 'ios', 'navdata.activate', 'fms2', { kind: 'config.navdb', unit: 2, op: 'activate' }),
    literal('config.navdb', 'ios', 'navdata.kbtv', 'fms2', { kind: 'config.navdb', unit: 2, op: 'loadKbtv' }),
  ])
  expect(record.journal[9].outcome).toEqual({ status: 'refused', reason: 'discontinuity' })
  expect(record.journal.find(entry => entry.kind === 'config.magvar' && entry.outcome.status === 'refused')?.outcome).toEqual({ status: 'refused', reason: 'unsupported model package' })
  await tab('Nav data')
  await ios.getByRole('button', { name: 'Set up 87N COPTER RNAV 190 final', exact: true }).click()
  await expect(ios.getByText(/^Set up: /)).toBeVisible()
  await tab('Scenarios')
  await page.getByRole('button', { name: 'APPR', exact: true }).click()
  await page.getByRole('button', { name: 'APPR', exact: true }).click()
  const placed = await exportFrom(page, ios)
  expect(placed.frame).toBe(0)
  expect(placed.journal).toHaveLength(3)
  expect(placed.journal.slice(1)).toMatchObject([
    { kind: 'afcs.select', target: 'afcs', source: { kind: 'ui', id: 'afcs.approach', surface: 'main' }, payload: { kind: 'afcs.select', selection: { select: 'approach', on: false } }, outcome: { status: 'accepted' } },
    { kind: 'afcs.select', target: 'afcs', source: { kind: 'ui', id: 'afcs.approach', surface: 'main' }, payload: { kind: 'afcs.select', selection: { select: 'approach', on: true } }, outcome: { status: 'accepted' } },
  ])
  expect(placed.journal[0]).toMatchObject({ frame: 0, kind: 'ios.place', target: 'plant', source: { kind: 'ios', id: 'navdata.startState', surface: popped ? 'popout' : 'main' }, payload: { kind: 'ios.place', startState: '87n-rnav190-final' }, outcome: { status: 'accepted' } })
  await tab('Nav data')
  await ios.getByLabel('On ground at power-up (bench input)').check()
  await ios.getByRole('button', { name: 'Cold start FMS', exact: true }).click()
  await tab('Scenarios')
  expect((await exportFrom(page, ios)).journal.at(-1)).toMatchObject({ kind: 'ios.unitPower', target: 'fms2', source: { kind: 'ios', id: 'navdata.coldStart', surface: popped ? 'popout' : 'main' }, payload: { kind: 'ios.unitPower', unit: 2, mode: 'COLD', onGround: true }, outcome: { status: 'accepted' } })
  await page.locator('.fmsStationDock > summary').click()
  const returnAll = controls.getByRole('button', { name: 'Return all panels', exact: true })
  if (popped) {
    await returnAll.click()
    await expect.poll(() => instructor.isClosed()).toBe(true)
  } else await expect(returnAll).toBeDisabled()
})

for (const popped of [false, true]) for (const paused of [false, true]) test(`real Stop ${paused ? 'paused' : 'running'} on instructor ${popped ? 'popout' : 'main'} executes at its retained instant and freezes without an extra frame`, async ({ page }) => {
  const epoch = new Date('2026-09-29T15:00:00Z')
  await page.clock.setFixedTime(epoch); await page.clock.pauseAt(epoch)
  await page.goto('/tests/fixtures/fms-cdu.html?terrain=off&imagery=off')
  await page.getByRole('button', { name: 'Cockpit view', exact: true }).click()
  await page.getByRole('button', { name: 'Instructor station', exact: true }).click()
  let instructor = page
  if (popped) {
    await page.locator('.fmsStationDock > summary').click()
    const controls = page.getByRole('region', { name: 'Station arrangement', exact: true })
    await controls.getByLabel('Instructor apart').check()
    const opened = page.waitForEvent('popup')
    await controls.getByRole('button', { name: 'Open instructor station in a window', exact: true }).click()
    instructor = await opened
  }
  const ios = instructor.getByRole('region', { name: 'Instructor station', exact: true })
  await ios.getByRole('tab', { name: 'Scenarios', exact: true }).click()
  const scenario = { id: 'd5-stop', title: 'D5 stop owner', objective: '', maxSeconds: 20, startTime: epoch.toISOString(), surface: 'offshore-87n', steps: [{ when: { kind: 'time', seconds: 0 }, action: { kind: 'wind', direction: 200, speed: 35 } }, { when: { kind: 'time', seconds: 0 }, action: { kind: 'alert', text: 'BEFORE STOP' } }, { when: { kind: 'time', seconds: 10 }, action: { kind: 'alert', text: 'MUST NOT EXECUTE' } }] }
  const card = ios.getByRole('region', { name: 'Scenarios', exact: true })
  await card.getByLabel('Scenario file', { exact: true }).setInputFiles(fixtureFile('stop.json', scenario))
  await expect(card.getByText('Loaded “stop.json”.', { exact: true })).toBeVisible()
  await card.getByRole('button', { name: 'Run the scenario', exact: true }).click()
  await expect(card.getByRole('button', { name: 'Stop the run', exact: true })).toBeVisible()
  if (paused) await page.getByRole('button', { name: 'Pause', exact: true }).click()
  const before = await exportFrom(page, ios)
  expect(before.frame).toBe(0)
  expect(before.journal.slice(0, 2)).toMatchObject([
    { frame: 0, origin: 'v1-translated', kind: 'config.surface', target: 'plant', source: { kind: 'scenario', id: 'd5-stop@2026-09-29T15:00:00.000Z start', surface: 'main' }, payload: { kind: 'config.surface', surface: 'offshore-87n' }, outcome: { status: 'accepted' } },
    { frame: 0, origin: 'v1-translated', kind: 'scenario.step', target: 'scenario', source: { kind: 'scenario', id: 'd5-stop@2026-09-29T15:00:00.000Z step 1', surface: 'main' }, payload: { kind: 'wind', direction: 200, speed: 35 }, outcome: { status: 'accepted' } },
  ])
  await card.getByRole('button', { name: 'Stop the run', exact: true }).click()
  await expect(card.getByText(/^STOPPED by the operator:/)).toBeVisible()
  const after = await exportFrom(page, ios)
  expect(after.frame).toBe(0)
  expect(after.journal.slice(before.journal.length)).toMatchObject([
    { frame: 0, kind: 'scenario.stop', target: 'scenario', source: { kind: 'ui', id: 'scenarios.stop', surface: popped ? 'popout' : 'main' }, payload: { kind: 'scenario.stop', runId: 'd5-stop@2026-09-29T15:00:00.000Z' }, outcome: { status: 'accepted' } },
    { frame: 0, kind: 'ios.flightFreeze', target: 'plant', source: { kind: 'ui', id: 'flight.runEnded', surface: popped ? 'popout' : 'main' }, payload: { kind: 'ios.flightFreeze', on: true }, outcome: { status: 'accepted' } },
  ])
  await expect(card.getByRole('button', { name: 'Stop the run', exact: true })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Fly', exact: true })).toBeVisible()
  const downloaded = page.waitForEvent('download')
  await card.getByRole('button', { name: 'Download run report', exact: true }).click()
  expect(await readFile((await (await downloaded).path())!, 'utf8')).toContain('ended after 0 s of simulated time')
  await page.clock.runFor(1000)
  const later = await exportFrom(page, ios)
  expect(later.frame).toBe(4)
  expect(later.journal).toEqual(after.journal)
  expect(later.control).toEqual(after.control)
  await expect(card.getByText(/^STOPPED by the operator:/)).toBeVisible()
  if (popped) {
    await page.getByRole('region', { name: 'Station arrangement', exact: true }).getByRole('button', { name: 'Return Instructor station', exact: true }).click()
    await expect.poll(() => instructor.isClosed()).toBe(true)
  }
})
