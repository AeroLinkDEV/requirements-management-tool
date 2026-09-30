import { expect, logicTest as test } from './isolated-client-test'
import { aircraftData } from '../src/fmsCdu/efis'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { offset } from '../src/fmsCdu/fmsModel'
import { setUp87nOffshoreSar } from '../src/fmsCdu/heliDemo'
import { HELICOPTER_PROFILE } from '../src/fmsCdu/profile'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { screenText } from '../src/fmsCdu/screen'

// The helicopter displays' open rows of the v1 ledger: the FMA's armed (white) and degraded (amber) modes per axis,
// published from the autopilot (plan rev 2 B3.4, B4.1); the low-speed flag the ND's ground-velocity vector follows
// (B4.5); and PROGRESS with a hover procedure, TDN and MRK and the XTK blanking (D-T T8; M300 A-124, A-127, A-129). The
// drawing itself is checked rendered (fms-efis-displays-rendered.spec.ts).

const START = Date.UTC(2026, 8, 29, 15, 0, 0)
const BOX_SECONDS = HELICOPTER_PROFILE.parameters.fmaCaptureBox.value

/** The 87N mission start (500 ft over the sea, ALT, LNAV armed), stepped by quarter seconds. */
function offshore() {
  let now = START
  const unit = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(unit)
  expect(setUp87nOffshoreSar(unit, sim)).toEqual({ ready: true })
  const fly = (seconds: number, done?: () => boolean) => {
    for (let t = 0; t < seconds * 4; t += 1) { now += 250; sim.step(0.25); if (done?.()) return true }
    return false
  }
  const heli = () => aircraftData(unit, sim).helicopter!
  return { unit, sim, fly, heli }
}
/** The sighting at once over the aircraft (MARK ON TOP), ACTIVATE and EXEC, as fms-hover-join.spec.ts flies it. */
function activatedOverMark() {
  const run = offshore()
  run.unit.placeAircraft({ position: run.unit.truePosition, track: 230, altitude: 500 }, 'test: over the sighting')
  run.fly(1)
  for (const key of ['TACT', 'LSK1R', 'LSK4L', 'LSK6R', 'EXEC'] as const) run.unit.press(key)
  expect(run.unit.hover.status).toBe('ACT')
  return run
}
/** Slowed below the coordinated-flight speed and holding a hover (HOV on pitch and roll, RHT on the collective). */
function hovering() {
  const run = offshore()
  run.sim.selectSpeed(20)
  expect(run.fly(180, () => run.sim.tas < 26)).toBe(true)
  expect(run.sim.engageHover()).toBe(true)
  run.fly(20)
  expect(run.sim.axisModes).toMatchObject({ collective: 'RHT', pitch: 'HOV', roll: 'HOV' })
  return run
}

test('B3.4, B4.1: NAV is armed on the roll axis while heading holds, and leaves the armed list once it captures', () => {
  // On the hover procedure's join under NAV: nothing armed.
  const { unit, sim, fly, heli } = activatedOverMark()
  fly(2)
  expect(heli().axes.roll).toBe('NAV')
  expect(heli().armed.roll).toEqual([])
  // Off onto a heading 30° from the track (so the join stays within reach), then NAV armed to rejoin it.
  sim.selectHeading(Math.round(unit.track + 30) % 360)
  fly(10)
  expect(heli().axes.roll).toBe('HDG')
  expect(heli().armed.roll).toEqual([])
  sim.armLnav()
  expect(heli().axes.roll).toBe('HDG')
  expect(heli().armed.roll).toEqual(['NAV'])
  expect(fly(600, () => heli().axes.roll === 'NAV')).toBe(true)
  expect(heli().armed.roll).toEqual([])
})

test('B3.4, B4.1: the altitude capture is armed on the collective while VS climbs toward the selected altitude', () => {
  const { sim, fly, heli } = offshore()
  expect(heli().armed.collective).toEqual([])
  sim.selectAltitude(1000)
  expect(sim.engageVerticalSpeed(500)).toBe(true)
  fly(0.25)
  expect(heli().axes.collective).toBe('VS')
  expect(heli().armed.collective).toEqual(['ALT'])
  expect(fly(180, () => heli().axes.collective === 'ALT')).toBe(true)
  expect(heli().armed.collective).toEqual([])
})

test('B3.4, B4.1: TD/H is armed on pitch and roll while TD takes the aircraft down to the gate, then engaged', () => {
  const { fly, heli } = activatedOverMark()
  expect(heli().armed).toMatchObject({ pitch: [], roll: [] })
  // The join under NAV to TDN, then TD (the transition's deceleration on pitch) with TD/H to follow.
  expect(fly(1200, () => heli().axes.pitch === 'TD')).toBe(true)
  expect(heli().armed).toMatchObject({ pitch: ['TD/H'], roll: ['TD/H'] })
  expect(fly(600, () => heli().axes.pitch === 'TD/H')).toBe(true)
  expect(heli().armed).toMatchObject({ pitch: [], roll: [] })
})

test('B3.5, B4.1: hover feedback lost shows HOV degraded on pitch and roll beside ATT, for the capture-box time', () => {
  const { unit, fly, heli } = hovering()
  expect(heli().degraded).toEqual({ collective: [], pitch: [], roll: [] })
  unit.setCondition('gpsIntegrity', true)
  fly(1)
  expect(heli().axes).toMatchObject({ pitch: 'ATT', roll: 'ATT' })
  expect(heli().degraded).toEqual({ collective: [], pitch: ['HOV'], roll: ['HOV'] })
  fly(BOX_SECONDS - 2)
  expect(heli().degraded.pitch).toEqual(['HOV'])
  fly(3)
  expect(heli().degraded).toEqual({ collective: [], pitch: [], roll: [] })
})

test('B3.5, B4.1: hover feedback lost in TD/H shows TD/H degraded on pitch and roll, not HOV', () => {
  const { unit, fly, heli } = activatedOverMark()
  expect(fly(1800, () => heli().axes.pitch === 'TD/H')).toBe(true)
  unit.setCondition('gpsIntegrity', true)
  fly(1)
  expect(heli().axes).toMatchObject({ pitch: 'ATT', roll: 'ATT' })
  expect(heli().degraded).toMatchObject({ pitch: ['TD/H'], roll: ['TD/H'] })
})

test('B3.4, B4.1: the altitude capture is no longer armed once a hover takes the collective from VS', () => {
  const { sim, fly, heli } = offshore()
  sim.selectAltitude(3000)
  sim.selectSpeed(20)
  expect(sim.engageVerticalSpeed(100)).toBe(true)
  expect(fly(180, () => sim.tas < 26)).toBe(true)
  expect(heli().axes.collective).toBe('VS')
  expect(heli().armed.collective).toEqual(['ALT'])
  expect(sim.engageHover()).toBe(true)
  // At once, before the next step has updated the vertical mode.
  expect(heli().armed.collective).toEqual([])
  fly(1)
  expect(heli().axes.collective).not.toBe('VS')
  expect(heli().armed.collective).toEqual([])
})

test('B3.5, B4.1: hover feedback lost in GSPD shows GSPD degraded on pitch and LVL on roll, as the FMA showed them', () => {
  const { unit, sim, fly, heli } = hovering()
  expect(sim.engageGroundSpeed(5)).toBe(true)
  fly(5)
  expect(heli().axes).toMatchObject({ pitch: 'GSPD', roll: 'LVL' })
  unit.setCondition('gpsIntegrity', true)
  fly(1)
  expect(heli().axes).toMatchObject({ pitch: 'ATT', roll: 'ATT' })
  expect(heli().degraded).toMatchObject({ pitch: ['GSPD'], roll: ['LVL'] })
})

test('B3.5, B4.1: the radio height lost in the hover shows RHT degraded on the collective beside ALT', () => {
  const { unit, fly, heli } = hovering()
  unit.setCondition('raFail', true)
  fly(1)
  expect(heli().axes.collective).toBe('ALT')
  expect(heli().degraded.collective).toEqual(['RHT'])
  expect(heli().degraded.pitch).toEqual([])
})

test('B3.5, B4.1: an FMS failure with NAV engaged shows NAV degraded on the roll axis beside HDG', () => {
  const { unit, sim, fly, heli } = offshore()
  expect(fly(60, () => heli().axes.roll === 'NAV')).toBe(true)
  unit.setCondition('fmsFail', true)
  fly(1)
  expect(sim.axisModes.roll).toBe('HDG')
  expect(heli().degraded.roll).toEqual(['NAV'])
})

test('B3.5, B4.1: an FMS failure already on a heading takes nothing away: no degraded mode', () => {
  const { unit, sim, fly, heli } = offshore()
  sim.selectHeading(230)
  fly(1)
  unit.setCondition('fmsFail', true)
  fly(1)
  expect(sim.modeEvents.some(e => e.event === 'FMS FAILURE')).toBe(true)
  expect(heli().degraded).toEqual({ collective: [], pitch: [], roll: [] })
})

test('B3.5, B4.1: feedback lost in a go-around from the hover shows LVL degraded on the roll axis beside ATT', () => {
  const { unit, sim, fly, heli } = hovering()
  expect(sim.engageGoAround()).toBe(true)
  fly(5)
  expect(heli().axes.roll).toBe('LVL')
  // GA climbs to the preselected altitude, which it captures: ALT armed on the collective.
  expect(heli().axes.collective).toBe('GA')
  expect(heli().armed.collective).toEqual(['ALT'])
  expect(heli().degraded.roll).toEqual([])
  unit.setCondition('gpsIntegrity', true)
  fly(1)
  expect(heli().axes.roll).toBe('ATT')
  expect(heli().degraded).toEqual({ collective: [], pitch: [], roll: ['LVL'] })
})

test('B4.5: the low-speed flag follows the autopilot\'s low-speed regime, not the airspeed alone', () => {
  const { sim: slowing, fly, heli } = offshore()
  expect(heli().lowSpeed).toBe(false)
  // Slowed below the coordinated-flight speed with no hover engaged: still the coordinated-flight displays.
  slowing.selectSpeed(20)
  expect(fly(180, () => slowing.tas < 26)).toBe(true)
  expect(slowing.inLowSpeedRegime).toBe(false)
  expect(heli().lowSpeed).toBe(false)
  const { sim, heli: hovered } = hovering()
  expect(sim.inLowSpeedRegime).toBe(true)
  expect(hovered().lowSpeed).toBe(true)
})

/** PROGRESS 1/4 as the crew sees it. */
const progress = (unit: ScriptedFms) => { unit.open('PROG'); return screenText(unit.screen()) }

test('T8: with a hover procedure in the route PROGRESS shows TDN and MRK, each with its distance to go and ETA (M300 A-124, A-129)', () => {
  expect(progress(offshore().unit).join('\n')).not.toMatch(/\bTDN\b/)
  const { unit, fly } = activatedOverMark()
  fly(2)
  const lines = progress(unit)
  // The joining point is active; the page still lists the procedure's TDN then MRK.
  expect(unit.activeRoute.legs[0]).toMatchObject({ kind: 'wpt', ident: 'JN' })
  // Outbound on the join the aircraft is not closing, so no ETA yet (the page's usual dashes).
  expect(lines[2]).toMatch(/^TDN\s+\d+\.\dNM ----\.-$/)
  expect(lines[4]).toMatch(/^MRK\s+\d+\.\dNM ----\.-$/)
  expect(lines.join('\n')).not.toMatch(/^JN\b/m)
  // Neither is the active waypoint yet: green, not inverse.
  expect(unit.screen()[2][0]).toMatchObject({ ch: 'T', color: 'green', inverse: false })
  const dtg = (line: string) => Number(/(\d+\.\d)NM/.exec(line)![1])
  const legs = unit.legGeometry(unit.activeRoute)
  const along = (index: number) => legs.slice(0, index + 1).reduce((sum, leg) => sum + (leg?.distance ?? 0), 0)
  expect(dtg(lines[2])).toBeCloseTo(along(1), 1)
  expect(dtg(lines[4])).toBeCloseTo(along(2), 1)
  // TDN is the planned transition distance before MRK.
  expect(dtg(lines[4]) - dtg(lines[2])).toBeCloseTo(unit.hover.dtra!, 0)
  // Turned back toward TDN: the ETAs appear, MRK's the later.
  expect(fly(600, () => unit.closureSpeed > 30)).toBe(true)
  const closing = progress(unit)
  expect(closing[2]).toMatch(/^TDN\s+\d+\.\dNM \d{4}\.\dZ$/)
  expect(closing[4]).toMatch(/^MRK\s+\d+\.\dNM \d{4}\.\dZ$/)
  const minutes = (line: string) => { const [, h, m] = /(\d\d)(\d\d\.\d)Z$/.exec(line)!; return Number(h) * 60 + Number(m) }
  expect(minutes(closing[4])).toBeGreaterThan(minutes(closing[2]))
  // JN sequenced: TDN is the active waypoint, magenta and inverse.
  expect(fly(900, () => (unit.activeRoute.legs[0] as { ident?: string }).ident === 'TDN')).toBe(true)
  progress(unit)
  expect(unit.screen()[2][0]).toMatchObject({ ch: 'T', color: 'magenta', inverse: true })
  expect(unit.screen()[4][0]).toMatchObject({ ch: 'M', color: 'green', inverse: false })
})

test('T8: in the hover procedure XTK is blanked on a CF leg only when more than 0.2 NM off it and more than 20° off its course (M300 A-127)', () => {
  const { unit } = activatedOverMark()
  expect(unit.activeRoute.legs[0]).toMatchObject({ ident: 'JN', path: 'CF' })
  // The page's rule on the FMS's own cross-track and track error, set here as the lateral guidance would leave them.
  const xtk = (crossTrack: number, trackError: number) => { unit.setAircraft({ crossTrack, trackError }); return progress(unit)[8] }
  expect(xtk(0, 0).trim()).toBe('R000°/R0.00NM')
  expect(xtk(0.5, 10).trim()).toBe('R010°/R0.50NM')
  expect(xtk(-0.5, -19.9).trim()).toBe('L020°/L0.50NM')
  expect(xtk(0.1, 60).trim()).toBe('R060°/R0.10NM')
  expect(xtk(0.2, 60).trim()).toBe('R060°/R0.20NM')
  expect(xtk(0.5, 20).trim()).toBe('R020°/R0.50NM')
  // Both beyond: blanked, left or right; the track error stays.
  expect(xtk(0.5, 60).trimStart()).toBe('R060°/       ')
  expect(xtk(-0.21, -20.1).trimStart()).toBe('L020°/       ')
  expect(xtk(3, 170).trimStart()).toBe('R170°/       ')
})

test('T8: the XTK is shown on a CF leg outside a hover procedure; past TDN, PROGRESS is back to its usual lines', () => {
  // A departure's published course into OW511 (GATIN2): a CF leg, no hover procedure.
  const plain = new ScriptedFms(() => new Date(START))
  plain.setAircraft({ altitude: 400, track: 71 })
  plain.selectProcedure('SID', 'GATIN2', 'RDG')
  plain.press('EXEC')
  while (plain.activeRoute.legs[0] && !(plain.activeRoute.legs[0].kind === 'wpt' && plain.activeRoute.legs[0].ident === 'OW511')) plain.sequence()
  expect(plain.activeRoute.legs[0]).toMatchObject({ ident: 'OW511', path: 'CF' })
  plain.setAircraft({ crossTrack: 0.5, trackError: 60 })
  expect(progress(plain)[8].trim()).toBe('R060°/R0.50NM')
  // TDN sequenced, MRK active: the TO line is MRK and the XTK is shown.
  const { unit } = activatedOverMark()
  while (unit.activeRoute.legs[0]?.kind === 'wpt' && (unit.activeRoute.legs[0] as { ident: string }).ident !== 'MRK') unit.sequence()
  unit.setAircraft({ crossTrack: 0.5, trackError: 60 })
  const lines = progress(unit)
  expect(lines[2]).toMatch(/^MRK\s/)
  expect(lines.join('\n')).not.toMatch(/^TDN\b/m)
  expect(lines[8].trim()).toBe('R060°/R0.50NM')
})
