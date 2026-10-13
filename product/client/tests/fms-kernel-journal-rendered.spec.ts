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
  const iosMain = page.getByRole('region', { name: 'Instructor station', exact: true })
  await iosMain.getByRole('tab', { name: 'Flight and setup', exact: true }).click()
  await iosMain.getByRole('button', { name: 'Jump to next waypoint', exact: true }).click()
  await iosMain.getByRole('tab', { name: 'Scenarios', exact: true }).click()
  const downloadTwo = page.waitForEvent('download')
  await iosMain.getByRole('button', { name: 'Export journal', exact: true }).click()
  const second = JSON.parse(await readFile((await (await downloadTwo).path())!, 'utf8'))
  expect(jumps(second).map((entry: { source: { surface: string } }) => entry.source.surface)).toEqual(['main', 'popout', 'main'])
  await controls.getByRole('button', { name: 'Return all panels', exact: true }).click()
  await expect.poll(() => cockpit.isClosed() && outside.isClosed()).toBe(true)
})
