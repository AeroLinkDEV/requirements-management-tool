import { expect, logicTest as test } from './isolated-client-test'
import { DualFmsSystem } from '../src/fmsCdu/dualFms'
import { setUp87nOffshoreSar } from '../src/fmsCdu/heliDemo'
import type { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { SCRATCHPAD_LINE, screenText } from '../src/fmsCdu/screen'

// #1537: the one AFCS's status words (IAS, hover height, VX/VY) reach both computers, guiding or not (#1502 D3 endpoint
// 35, AFCS.status to FMS1, FMS2 and EFIS). Before the fix only the flying flight wrote them, so in SYNC the computer
// that was not guiding read IAS 0 at TDN, refused the transition below the gate speed, broadcast TDN NOT POSSIBLE to
// both CDUs, and showed dashes for the hover height and VX/VY on its HOVER page (M300 A-75, 11-21).
// Owner: flight.ts adoptAfcsSelections (the unselected computer's view of the one AFCS), driven through dualFms.ts.
// The guidance-source change rules for a request raised by the unselected computer stay with
// fms-guidance-source-switch.spec.ts.

const START = Date.UTC(2026, 8, 29, 15, 0, 0)
const scratch = (unit: ScriptedFms) => screenText(unit.screen())[SCRATCHPAD_LINE].trimEnd()

/** The 87N offshore SAR start on a dual system in SYNC, FMS 1 guiding, set up and copied to FMS 2 as the bench does it. */
function mission() {
  let now = START
  const system = new DualFmsSystem(() => new Date(now))
  const [one, two] = system.computers
  one.compute(() => {
    expect(setUp87nOffshoreSar(one, system.flights[0])).toEqual({ ready: true })
    one.dualOperation?.settingsChanged(); one.dualOperation?.finishEdit(true); two.observeAircraft(one)
  })
  const fly = (seconds: number) => { for (let i = 0; i < seconds * 4; i++) { now += 250; system.step(0.25) } }
  const flyUntil = (done: () => boolean, limit: number) => {
    for (let i = 0; i < limit * 4 && !done(); i++) { now += 250; system.step(0.25) }
    expect(done(), 'condition reached in time').toBe(true)
  }
  expect(system.mode).toBe('SYNC')
  expect(system.guidanceSide).toBe(1)
  return { system, one, two, fly, flyUntil }
}

/** The hover procedure over the sighting, executed on FMS 1 and so on FMS 2 (SYNC), flown until both are past TDN. */
function pastTdn() {
  const setup = mission()
  const { system, one, two, fly, flyUntil } = setup
  fly(1)
  for (const key of ['TACT', 'LSK1R', 'LSK4L', 'LSK6R', 'EXEC'] as const) one.press(key)
  expect(two.hover.status).toBe('ACT')
  flyUntil(() => one.hover.atTdn !== null && two.hover.atTdn !== null, 400)
  expect(system.guidanceSide).toBe(1)
  return setup
}

test('in SYNC the computer that is not guiding accepts TDN at the gate speed, judged on the AFCS IAS and hover height', () => {
  const { system, one, two } = pastTdn()
  // FMS 1 guides and accepts; FMS 2, on the same aircraft and the same AFCS words, judges the same state the same way.
  expect(one.hover.atTdn!.decision).toMatchObject({ engage: true })
  expect(two.hover.refused, `FMS 2 refused: ${two.hover.refusedReason}`).toBeNull()
  expect(two.hover.atTdn!.decision).toMatchObject({ engage: true })
  expect(two.hover.request).toBeGreaterThan(0)
  // The IAS it judged is the AFCS's, at the gate speed (not 0), and the hover height is the AFCS's selection.
  expect(two.hover.atTdn!.start!.ias).toBeGreaterThan(30)
  expect(Math.abs(two.hover.atTdn!.start!.ias - one.hover.atTdn!.start!.ias), 'IAS seen by each computer at its TDN, kt').toBeLessThan(5)
  expect(two.hover.atTdn!.start!.hoverHeight).toBe(system.simulator.hoverHeight)
})

test('in SYNC no TDN NOT POSSIBLE from the computer that is not guiding reaches either CDU', () => {
  const { one, two } = pastTdn()
  // Neither FMS 2's own refusal nor the copy SYNC broadcasts to FMS 1 (M300 3-24, alert messages are synchronized).
  for (const unit of [one, two]) {
    expect(unit.recallList.map(message => message.text)).not.toContain('TDN NOT POSSIBLE')
    expect(scratch(unit)).not.toBe('TDN NOT POSSIBLE')
  }
})

test('in SYNC the HOVER page of the computer that is not guiding shows the AFCS hover height and its X/Y velocities', () => {
  const { system, one, two, fly, flyUntil } = mission()
  fly(1)
  system.simulator.selectSpeed(20)
  flyUntil(() => system.simulator.tas < 26, 120)
  expect(system.simulator.engageHover()).toBe(true)
  fly(30)
  expect(system.simulator.axisModes).toMatchObject({ pitch: 'HOV', roll: 'HOV' })
  expect(system.simulator.selectHoverHeight(80)).toBe(true)
  fly(1)
  const words = one.afcs!
  expect(words.vx).not.toBeNull()
  // Both computers receive the one AFCS's words, and the HOVER page shows them (M300 A-75 VELOCITIES 3R, 11-21 NOTE).
  expect(two.afcs).toEqual(words)
  two.open('HOVER')
  const page = screenText(two.screen()).join('\n')
  expect(page).toMatch(/\d+FT\s+80FT/)
  expect(page).toMatch(/VX [+-]\d+\.\dKT/)
  expect(page).toMatch(/VY [+-]\d+\.\dKT/)
  expect(page).not.toContain('---.-KT')
})

test('the request the computer that is not guiding now raises is never engaged or logged by its view of the one AFCS', () => {
  const { system, two, fly } = pastTdn()
  fly(120)
  // FMS 1's request is the one the AFCS flies; FMS 2 raised its own on the same words, and the AFCS consumes only the
  // guiding computer's guidance (#1502 D3 endpoints 31-32): FMS 2's flight logs no mode change and flies no transition.
  expect(two.hover.request).toBeGreaterThan(0)
  expect(system.flights[0].modeEvents.map(e => e.event)).toEqual(expect.arrayContaining(['TRANSITION REQUEST', 'TD', 'TD/H']))
  expect(system.flights[1].modeEvents).toEqual([])
  expect(system.flights[1].transitionInProgress).toBeNull()
})
