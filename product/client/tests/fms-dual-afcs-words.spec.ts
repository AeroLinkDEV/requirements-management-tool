import { expect, logicTest as test } from './isolated-client-test'
import { DualFmsSystem } from '../src/fmsCdu/dualFms'
import { stimulusFor } from '../src/fmsCdu/gpsStimulus'
import { setUp87nOffshoreSar } from '../src/fmsCdu/heliDemo'
import { SCRATCHPAD_LINE, screenText } from '../src/fmsCdu/screen'

// #1537: the one AFCS's status words (IAS, hover height, VX/VY) reach both computers, guiding or not, and independently
// of cross-talk (#1502 D3 endpoint 35, AFCS.status to FMS1, FMS2 and EFIS). Before the fix only the flying flight wrote
// them, so a computer that had never guided read IAS 0 at TDN, refused the transition below the gate speed (TDN NOT
// POSSIBLE, broadcast to both CDUs in SYNC) and showed dashes for the hover height and VX/VY on its HOVER page
// (M300 A-75, 11-21). The AFCS still consumes only the guiding computer's guidance (D3 endpoints 31-32).
// Owner: flight.ts adoptAfcsSelections and watchHover, driven through dualFms.ts. Every peer below has never guided:
// fms-guidance-source-switch.spec.ts owns what happens to a request across a guidance-source change.

const START = Date.UTC(2026, 8, 29, 15, 0, 0)
const scratch = (system: DualFmsSystem, index: number) => screenText(system.computers[index].screen())[SCRATCHPAD_LINE].trimEnd()

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
  return { system, one, two, fly, flyUntil }
}

type Link = 'SYNC' | 'INDEPENDENT' | 'LINK LOST'
/** The cross-talk state: SYNC as set up, INDEPENDENT by crew request, or the link itself lost (also INDEPENDENT). */
function link(system: DualFmsSystem, mode: Link) {
  if (mode === 'LINK LOST') system.setLinkAvailable(false)
  else if (mode === 'INDEPENDENT') {
    system.computers[0].dualOperation!.requestMode('INDEPENDENT')
    system.computers[0].dualOperation!.confirmMode(true)
  }
  expect(system.mode).toBe(mode === 'SYNC' ? 'SYNC' : 'INDEPENDENT')
}

for (const side of [1, 2] as const) for (const mode of ['SYNC', 'INDEPENDENT', 'LINK LOST'] as const)
  test(`FMS ${side} guides in ${mode}: the computer that has never guided receives the current AFCS sample`, () => {
    const { system, fly } = mission()
    system.selectGuidance(side)
    const selected = system.computers[side - 1], peer = system.computers[2 - side]
    expect(peer.afcs).toBeNull()
    link(system, mode)
    expect(system.simulator.selectHoverHeight(80)).toBe(true)
    fly(0.25)
    expect(selected.afcs).toMatchObject({ hoverHeight: 80 })
    expect(selected.afcs!.ias).toBeGreaterThan(80)
    expect(peer.afcs).toEqual(selected.afcs)
    fly(0.25)
    expect(peer.afcs).toEqual(selected.afcs)
    expect(peer.afcs!.hoverHeight).toBe(80)
  })

test('the HOVER page of the computer that has never guided shows the AFCS hover height and VX/VY, then dashes when the AFCS has none', () => {
  const { system, one, two, fly } = mission()
  // Due north in still air, with the measured north/east velocity forced to 3/4 kt: the AFCS's VX/VY is +3/+4.
  one.compute(() => {
    Object.assign(one.wind, { direction: 0, speed: 0 })
    one.modify(route => { route.legs = [] }); one.press('EXEC')
    one.placeAircraft({ position: { ...one.truePosition }, track: 0, altitude: 500 }, '1537 velocity vector')
    one.dualOperation!.settingsChanged(); two.observeAircraft(one)
  })
  system.simulator.selectHeading(0)
  expect(system.simulator.selectHoverHeight(80)).toBe(true)
  for (const receiver of [0, 1]) {
    stimulusFor(one).setOverride(receiver, '166', { kind: 'FORCE', amount: 3, ssm: 'NORMAL' })
    stimulusFor(one).setOverride(receiver, '174', { kind: 'FORCE', amount: 4, ssm: 'NORMAL' })
  }
  expect(two.afcs).toBeNull()
  fly(0.25)
  expect(one.heading).toBe(0)
  expect(one.afcs).toMatchObject({ hoverHeight: 80, vx: 3, vy: 4 })
  expect(two.afcs).toEqual(one.afcs)
  two.open('HOVER')
  let page = screenText(two.screen()).join('\n')
  expect(page).toMatch(/\d+FT\s+80FT/); expect(page).toContain('VX +3.0KT'); expect(page).toContain('VY +4.0KT')
  // The AFCS loses its velocity feedback (north velocity NCD): the peer shows the AFCS's missing words, not stale ones.
  for (const receiver of [0, 1]) stimulusFor(one).setOverride(receiver, '166', { kind: 'FORCE', amount: 3, ssm: 'NCD' })
  fly(0.25)
  expect(one.hoverFeedback).toBeNull()
  expect(two.afcs).toMatchObject({ hoverHeight: 80, vx: null, vy: null })
  page = screenText(two.screen()).join('\n')
  expect(page).toContain('VX ---.-KT'); expect(page).toContain('VY ---.-KT')
})

for (const [side, mode] of [[1, 'SYNC'], [2, 'SYNC'], [1, 'INDEPENDENT'], [1, 'LINK LOST']] as const)
  test(`FMS ${side} guides in ${mode}: the computer that has never guided accepts TDN at the gate speed, and never commands the AFCS`, () => {
    const { system, fly, flyUntil } = mission()
    system.selectGuidance(side)
    const selected = system.computers[side - 1], peer = system.computers[2 - side]
    const observing = system.flights[2 - side]
    expect(peer.afcs).toBeNull()
    // Independently declared crew selection: the 50-ft fallback must not satisfy this TDN consumer assertion.
    expect(system.simulator.selectHoverHeight(123)).toBe(true)
    expect(system.simulator.hoverHeight).toBe(123)
    fly(1)
    for (const key of ['TACT', 'LSK1R', 'LSK4L', 'LSK6R', 'EXEC'] as const) selected.press(key)
    expect(peer.hover.status).toBe('ACT')
    link(system, mode)
    const events = observing.modeEvents.length
    flyUntil(() => selected.hover.atTdn !== null && peer.hover.atTdn !== null, 400)
    expect(system.guidanceSide).toBe(side)
    // Both computers judge the one aircraft on the one AFCS's words: the IAS at the gate speed (not 0) and its hover height.
    expect(selected.hover.atTdn!.decision).toMatchObject({ engage: true })
    expect(peer.hover.refused, `refused: ${peer.hover.refusedReason}`).toBeNull()
    expect(peer.hover.atTdn!.decision).toMatchObject({ engage: true })
    expect(peer.hover.request).toBeGreaterThan(0)
    expect(peer.hover.atTdn!.start!.ias).toBeGreaterThanOrEqual(80)
    expect(Math.abs(peer.hover.atTdn!.start!.ias - selected.hover.atTdn!.start!.ias), 'IAS each computer judged at its TDN, kt').toBeLessThan(5)
    expect(peer.hover.atTdn!.start!.hoverHeight).toBe(123)
    // The AFCS flies the guiding computer's request; the peer's flight engages and logs nothing of its own.
    fly(0.25)
    expect(observing.modeEvents.slice(events).map(event => event.event)).toEqual([])
    expect(observing.transitionInProgress).toBeNull()
    expect(system.simulator.modeEvents.map(event => event.event)).toContain('TRANSITION REQUEST')
    // No TDN NOT POSSIBLE on either CDU: neither the peer's own nor the copy SYNC would broadcast (M300 3-24).
    for (const index of [0, 1]) {
      expect(system.computers[index].recallList.map(message => message.text)).not.toContain('TDN NOT POSSIBLE')
      expect(scratch(system, index)).not.toBe('TDN NOT POSSIBLE')
    }
  })
