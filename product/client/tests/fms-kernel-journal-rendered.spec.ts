import type { KernelRecord } from '../src/fmsCdu/kernel/kernel'
import type { Page, Locator } from '@playwright/test'
import { readFile } from 'node:fs/promises'
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
  await controls.getByRole('button', { name: 'Return all panels', exact: true }).click()
  if (popped) await expect.poll(() => instructor.isClosed()).toBe(true)
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
  expect(later.frame).toBe(paused ? 0 : 4)
  expect(later.journal).toEqual(after.journal)
  expect(later.control).toEqual(after.control)
  await expect(card.getByText(/^STOPPED by the operator:/)).toBeVisible()
  if (popped) {
    await page.getByRole('region', { name: 'Station arrangement', exact: true }).getByRole('button', { name: 'Return Instructor station', exact: true }).click()
    await expect.poll(() => instructor.isClosed()).toBe(true)
  }
})
