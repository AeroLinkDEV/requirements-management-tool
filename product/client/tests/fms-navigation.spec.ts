import { expect, logicTest as test } from './isolated-client-test'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { distanceNm } from '../src/fmsCdu/fmsModel'
import { RNP_DEFAULTS, radioRange, selectSources } from '../src/fmsCdu/navigation'
import { LAB_AIRLINE_VNAV_PROFILE } from '../src/fmsCdu/profile'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { SCRATCHPAD_LINE, screenText } from '../src/fmsCdu/screen'
import type { CduFunction } from '../src/fmsCdu/variants'

// Navigation sensors and RNP, per ICAO Doc 9613 and airline FMS practice (the FMS test bench research roadmap): the
// GPS > DME/DME > VOR/DME > inertial priority with automatic reversion, ANP from the sources, RNP by phase with its
// time to alert, dead-reckoning drift and the position shift when a sensor returns, NAV OPTIONS, and the approach.
const setup = () => {
  let now = Date.UTC(2026, 8, 27, 14, 0, 0)
  const unit = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(unit)
  const fly = (seconds: number, each?: () => boolean | void) => {
    for (let t = 0; t < seconds; t += 1) {
      now += 1000
      sim.step(1)
      if (each?.()) return t
    }
    return seconds
  }
  return { unit, sim, fly }
}
const press = (unit: ScriptedFms, ...fns: CduFunction[]) => { for (const fn of fns) unit.press(fn) }
const typeText = (unit: ScriptedFms, text: string) => {
  for (const ch of text) unit.press(ch === '.' ? 'DOT' : ch === '/' ? 'SLASH' : `CHAR_${ch}`)
}
const enter = (unit: ScriptedFms, text: string, lsk: CduFunction) => { typeText(unit, text); unit.press(lsk) }
const lines = (unit: ScriptedFms) => screenText(unit.screen())
const scratch = (unit: ScriptedFms) => lines(unit)[SCRATCHPAD_LINE].trimEnd()
const recalled = (unit: ScriptedFms, text: string) => unit.recallList.some(message => message.text === text)
const active = (unit: ScriptedFms) => { const leg = unit.activeRoute.legs[0]; return leg?.kind === 'wpt' ? leg.ident : leg?.kind === 'cond' ? `(${leg.path})` : null }

test('sources are chosen in the airline order, and DME/DME needs two stations crossing at a usable angle', () => {
  const unit = new ScriptedFms()
  const entries = unit.navdb.nearby(unit.coordinates('RDG')!, 160)
  const at = unit.coordinates('RDG')!
  const all = { gpsAvailable: true, gpsIntegrity: true, dmeAvailable: true, inhibited: [] }
  expect(selectSources(entries, at, 4500, all).mode).toBe('GPS')
  const dmeDme = selectSources(entries, at, 4500, { ...all, gpsAvailable: false })
  expect(dmeDme.mode).toBe('DME/DME')
  // HWK, south of the airway, crosses well with either YOW or YUL; the FMS takes the pair nearest 90 degrees.
  expect(dmeDme.dmes.map(d => d.ident)).toContain('HWK')
  expect(dmeDme.baseAnp).toBeLessThan(0.3)
  // Without HWK the only DMEs in range are nearly in line with the aircraft: no fix, so VOR/DME.
  expect(selectSources(entries, at, 4500, { ...all, gpsAvailable: false, inhibited: ['HWK'] }).mode).toBe('VOR/DME')
  expect(selectSources(entries, at, 4500, { ...all, gpsAvailable: false, dmeAvailable: false }).mode).toBe('DR')
  // Radio line of sight grows with altitude.
  expect(radioRange(3000)).toBeCloseTo(67, 0)
  expect(radioRange(40000)).toBe(160)
})

test('GPS loss reverts to radio updating automatically as stations come into range', () => {
  const { unit, fly } = setup()
  unit.setCondition('gpsLost', true)
  expect(unit.navState.mode).toBe('VOR/DME')
  const modes = new Set<string>()
  fly(3600, () => { modes.add(unit.navState.mode); return active(unit) === 'TOLGU' })
  expect(modes.has('DME/DME')).toBe(true)
  expect(modes.has('DR')).toBe(false)
})

test('in dead reckoning the FMS position drifts from the aircraft, ANP grows with it, and POS lights', () => {
  const { unit, fly } = setup()
  // DME first, then GPS: the aircraft goes straight from a GPS fix to dead reckoning.
  unit.setCondition('dmeOutage', true)
  unit.setCondition('gpsLost', true)
  expect(unit.navState.mode).toBe('DR')
  expect(unit.lamps().has('POS')).toBe(true)
  fly(30 * 60)
  // Two NM an hour of inertial drift: about a mile after half an hour, and ANP bounds it.
  const error = distanceNm(unit.truePosition, unit.position)
  expect(error).toBeGreaterThan(0.9)
  expect(error).toBeLessThan(1.1)
  expect(unit.navState.anp).toBeGreaterThan(error)
})

test('when GPS returns after dead reckoning the position jumps back, and the FMS reports POSITION SHIFT', () => {
  const { unit, fly } = setup()
  // DME first, then GPS: the aircraft goes straight from a GPS fix to dead reckoning.
  unit.setCondition('dmeOutage', true)
  unit.setCondition('gpsLost', true)
  fly(30 * 60)
  expect(recalled(unit, 'POSITION SHIFT')).toBe(false)
  unit.setCondition('gpsLost', false)
  expect(recalled(unit, 'POSITION SHIFT')).toBe(true)
  expect(distanceNm(unit.truePosition, unit.position)).toBeLessThan(0.05)
  expect(unit.lamps().has('POS')).toBe(false)
})

test('the aircraft flies the FMS position, so in dead reckoning it really is off the route', () => {
  const { unit, fly } = setup()
  // DME first, then GPS: the aircraft goes straight from a GPS fix to dead reckoning.
  unit.setCondition('dmeOutage', true)
  unit.setCondition('gpsLost', true)
  let offAtRdg = 0
  fly(3600, () => {
    if (active(unit) === 'TOLGU') { offAtRdg = distanceNm(unit.truePosition, unit.coordinates('RDG')!); return true }
  })
  // Sequenced where the FMS believed RDG was: really a few tenths of a mile away.
  expect(offAtRdg).toBeGreaterThan(0.3)
})

test('RNP defaults by phase: terminal near the airports, en route between them, approach on the approach', () => {
  const unit = new ScriptedFms()
  expect(unit.flightPhase).toBe('TERMINAL')
  expect(unit.requiredRnp).toBe(RNP_DEFAULTS.TERMINAL.rnp)
  unit.sequence()
  unit.sequence()
  unit.sequence()
  expect(unit.flightPhase).toBe('EN ROUTE')
  press(unit, 'PROG')
  expect(lines(unit)[9]).toMatch(/RNP\/ANP EN ROUTE/)
  // ANP in GPS mode is the receiver's HFOM with a 0.02 NM floor (GPS phase 3a); navigating on SBAS, HFOM is a few thousandths.
  expect(lines(unit)[10]).toMatch(/^2\.00\/0\.02NM/)
  unit.selectProcedure('APPROACH', 'R24R')
  unit.press('EXEC')
  unit.sequence()
  expect(unit.flightPhase).toBe('APPROACH')
  expect(unit.requiredRnp).toBe(0.3)
})

test('ANP above RNP raises CHECK ANP only after the time to alert for the phase', () => {
  const { unit, fly } = setup()
  press(unit, 'PROG')
  // 0.01 NM is below the 0.02 NM ANP floor, so it is below ANP whatever the satellite geometry (GPS phase 3a).
  enter(unit, '.01', 'LSK5L')
  expect(lines(unit)[10]).toMatch(/^0\.01\/0\.02NM/)
  expect(lines(unit)[9]).toMatch(/MANUAL/)
  expect(unit.lamps().has('RNP')).toBe(true)
  // Terminal phase: 60 seconds.
  fly(50)
  expect(recalled(unit, 'CHECK ANP')).toBe(false)
  fly(15)
  expect(recalled(unit, 'CHECK ANP')).toBe(true)
  press(unit, 'CLR', 'CLR', 'LSK5L')
  expect(unit.requiredRnp).toBe(1)
  expect(unit.lamps().has('RNP')).toBe(false)
})

test('a manual RNP larger than the phase default asks the crew to VERIFY RNP VALUE', () => {
  const unit = new ScriptedFms()
  press(unit, 'PROG')
  enter(unit, '3.0', 'LSK5L')
  expect(scratch(unit)).toBe('VERIFY RNP VALUE')
  expect(unit.requiredRnp).toBe(3)
  press(unit, 'CLR', 'CLR', 'CLR', 'CLR')
  enter(unit, 'X', 'LSK5L')
  expect(scratch(unit)).toBe('INVALID ENTRY')
})

test('loss of GPS integrity: GPS POS UNCERTAIN, larger ANP, and no RNAV approach guidance', () => {
  const unit = new ScriptedFms()
  unit.selectProcedure('APPROACH', 'R24R')
  unit.press('EXEC')
  // The receivers report the approach selected (156) once the FMS has sent its FAS block, at the next navigation update;
  // until then it is not selected, and an unselected approach may not be flown (the GPS review's GPS-06).
  expect(unit.approachType).toBe('NO APPR')
  unit.updateNavigation(0)
  // The level the GPS reports (305, GPS phase 3b): outside the 30 NM approach region SBAS NAV supports LNAV/VNAV; LPV
  // comes only inside it, in SBAS PA.
  expect(unit.approachType).toBe('LNAV/VNAV')
  unit.setCondition('gpsIntegrity', true)
  expect(scratch(unit)).toBe('GPS POS UNCERTAIN')
  // Neither receiver can be used (GPS phase 3a): the FMS navigates on the radios, with their larger ANP.
  expect(unit.navState.mode).not.toBe('GPS')
  expect(unit.navState.anp).toBeGreaterThan(0.3)
  expect(unit.approachType).toBe('NO APPR')
  for (let i = 0; i < 3; i += 1) unit.sequence()
  unit.updateNavigation(0)
  expect(unit.flightPhase).toBe('APPROACH')
  expect(recalled(unit, 'NO APPR INTEGRITY')).toBe(true)
  press(unit, 'INIT_REF', 'NEXT', 'LSK5R')
  // The condition leaves each receiver five satellites (one degree of freedom: detection without exclusion).
  expect(lines(unit)[6]).toMatch(/5 SAT NO RAIM$/)
})

test('NAV OPTIONS inhibits a navaid from updating, and GPS can be selected out', () => {
  const unit = new ScriptedFms()
  press(unit, 'INIT_REF', 'NEXT', 'LSK5R')
  expect(lines(unit)[0]).toMatch(/^NAV STATUS/)
  expect(lines(unit)[2]).toMatch(/^GPS/)
  unit.press('LSK6R')
  expect(lines(unit)[0]).toMatch(/^NAV OPTIONS/)
  // GPS NAV steps AUTO, GPS1, GPS2, OFF (GPS phase 3a): the third press selects GPS out.
  press(unit, 'LSK3L', 'LSK3L', 'LSK3L')
  expect(unit.gpsNavSelected).toBe(false)
  expect(unit.navState.mode).toBe('VOR/DME')
  expect(recalled(unit, 'GPS NAV LOST')).toBe(true)
  unit.press('CLR')
  enter(unit, 'YOW', 'LSK1L')
  // YOW excluded, the FMS updates from HWK; both excluded, nothing is left to update from.
  expect(unit.navState.vor).toBe('HWK')
  enter(unit, 'HWK', 'LSK1R')
  expect(unit.navState.mode).toBe('DR')
  expect(unit.lamps().has('POS')).toBe(true)
  press(unit, 'LSK6L')
  expect(lines(unit)[10]).toMatch(/^YOW HWK/)
  press(unit, 'LSK6R', 'CLR', 'LSK1L', 'CLR', 'LSK1L')
  expect(unit.inhibitedNavaids).toEqual([])
  expect(unit.navState.mode).toBe('VOR/DME')
})

test('the NPA annunciator follows a non-precision approach, not an ILS', () => {
  const unit = new ScriptedFms()
  unit.selectProcedure('APPROACH', 'R24R')
  unit.press('EXEC')
  // Jump through MUN, RDG, TOLGU, DEMEL, ALNIT and ULIDA to the FAF.
  for (let i = 0; i < 6; i += 1) unit.sequence()
  expect(active(unit)).toBe('FERDI')
  expect(unit.lamps().has('NPA')).toBe(true)
  const ils = new ScriptedFms()
  ils.selectProcedure('APPROACH', 'I24R')
  ils.press('EXEC')
  for (let i = 0; i < 6; i += 1) ils.sequence()
  expect(active(ils)).toBe('FERDI')
  expect(ils.lamps().has('NPA')).toBe(false)
})

test('ARM APPROACH is asked for within 2 NM of the final approach fix when the approach is not armed', () => {
  const { unit, fly } = setup()
  unit.selectProcedure('APPROACH', 'R24R')
  unit.press('EXEC')
  fly(3 * 3600, () => active(unit) === 'RW24R' || recalled(unit, 'ARM APPROACH'))
  expect(recalled(unit, 'ARM APPROACH')).toBe(true)
  expect(distanceNm(unit.position, unit.coordinates('FERDI')!)).toBeLessThanOrEqual(2.05)

  const armed = setup()
  armed.unit.selectProcedure('APPROACH', 'R24R')
  armed.unit.press('EXEC')
  armed.unit.armApproach()
  armed.fly(3 * 3600, () => active(armed.unit) === 'RW24R')
  expect(recalled(armed.unit, 'ARM APPROACH')).toBe(false)
})

test('TOGA before the MAP (helicopter): guidance continues to the MAP, which then sequences the missed approach; its hold armed (M300 7-16, R2-03)', () => {
  const unit = new ScriptedFms()
  unit.selectProcedure('APPROACH', 'R24R')
  unit.press('EXEC')
  for (let i = 0; i < 6; i += 1) unit.sequence()
  expect(active(unit)).toBe('FERDI')
  unit.armApproach(true)
  expect(unit.goAround()).toBe(true)
  // The lateral path is kept: still to FERDI, then the runway (the MAP); the approach is disarmed.
  expect(active(unit)).toBe('FERDI')
  expect(unit.approachArmed).toBe(false)
  expect(unit.activeRoute.hold).toMatchObject({ fix: 'UL502', status: 'ARMED' })
  while (active(unit) !== 'RW24R') unit.sequence()
  unit.sequence()
  expect(active(unit)).toBe('(CA)')
  expect(unit.goAround()).toBe(false)
})

test('TOGA on the approach (laboratory airline profile) drops the rest of it and flies the missed approach, its hold armed', () => {
  const unit = new ScriptedFms(undefined, { profile: LAB_AIRLINE_VNAV_PROFILE })
  unit.selectProcedure('APPROACH', 'R24R')
  unit.press('EXEC')
  for (let i = 0; i < 6; i += 1) unit.sequence()
  expect(active(unit)).toBe('FERDI')
  expect(unit.goAround()).toBe(true)
  expect(active(unit)).toBe('(CA)')
  expect(unit.activeRoute.hold).toMatchObject({ fix: 'UL502', status: 'ARMED' })
  expect(unit.goAround()).toBe(false)
})
