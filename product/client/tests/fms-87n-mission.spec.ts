import { createHash } from 'node:crypto'
import { readFileSync } from 'node:fs'
import { expect, logicTest as test } from './isolated-client-test'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { bearingDeg, distanceNm, offset } from '../src/fmsCdu/fmsModel'
import { COPTER_PINS_CIFP_2609, COPTER_PINS_CIFP_2609_SHA256 } from '../src/fmsCdu/data/copterPinsCifp2609'
import {
  COPTER_PINS_SOURCE, FINAL_START_BEFORE_STAYS_NM, MISSION_87N_OFFSHORE_SAR, MISSION_87N_VARIANTS, MISSION_START_SOUTH_NM, setUp87nOffshoreSar, setUp87nRnav190Final,
} from '../src/fmsCdu/heliDemo'
import { ScenarioRunner, advanceTicks, runHeadless, scenarioProblems } from '../src/fmsCdu/scenario'
import { SCENARIO_LIBRARY } from '../src/fmsCdu/scenarioLibrary'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'

// The helicopter acceptance mission (helicopter-first plan §10, "87N offshore SAR"): its bundled real data, its start
// state (synthetic, checked against its admission state), and the nominal run as a library scenario, flown headless on
// the bench's tick contract.
const FIXTURE = readFileSync('tests/fixtures/cifp/copter-pins-2609.pc', 'latin1')
const START = Date.parse(MISSION_87N_OFFSHORE_SAR.startTime!)

test('the bundled Copter PinS data is the CIFP fixture, byte for byte, and its recorded SHA-256', () => {
  expect(COPTER_PINS_CIFP_2609).toBe(FIXTURE)
  expect(createHash('sha256').update(COPTER_PINS_CIFP_2609, 'latin1').digest('hex')).toBe(COPTER_PINS_CIFP_2609_SHA256)
})

test('the 87N start state: the data active, the sea declared, the mission wind, 10 NM south at 500 ft with a valid radio height, the SAR datum set', () => {
  const unit = new ScriptedFms(() => new Date(START))
  const sim = new FlightSimulator(unit)
  expect(setUp87nOffshoreSar(unit, sim)).toEqual({ ready: true })
  expect(unit.activeCycle.source).toBe(COPTER_PINS_SOURCE)
  expect(unit.surface.id).toBe('offshore-87n')
  expect(unit.wind).toMatchObject({ direction: 230, speed: 20 })
  expect(unit.activeRoute.dest).toBe('87N')
  const heliport = unit.coordinates('87N')!
  expect(distanceNm(unit.truePosition, heliport)).toBeCloseTo(MISSION_START_SOUTH_NM, 6)
  expect(bearingDeg(heliport, unit.truePosition)).toBeCloseTo(180, 3)
  expect(unit.altitude).toBe(500)
  expect(unit.radioHeight).toMatchObject({ status: 'NORMAL', value: 500 })
  expect(unit.sar.refId).toMatch(/^DTM\d\d$/)
  expect(sim.axisModes.collective).toBe('ALT')
  // Refused without the flight simulation: the crew's selections need the autopilot.
  expect(setUp87nOffshoreSar(new ScriptedFms(() => new Date(START)))).toMatchObject({ refused: expect.stringContaining('flight simulation') })
})

test('the 87N mission is a valid library scenario on the helicopter profile', () => {
  expect(SCENARIO_LIBRARY).toContain(MISSION_87N_OFFSHORE_SAR)
  expect(scenarioProblems(MISSION_87N_OFFSHORE_SAR)).toEqual([])
})

test('the 87N offshore SAR mission, nominal run: search, mark, hover at the mark, TU-LAB, the RNAV 190 via HTO, the missed approach and the BEADS holds (plan §10)', () => {
  const { runner, fms, sim } = runHeadless(MISSION_87N_OFFSHORE_SAR)
  const failures = runner.results.map((result, i) => ({ step: i + 1, ...result })).filter(result => result.status !== 'pass' && result.status !== 'done')
  expect(failures).toEqual([])
  expect(runner.outcome).toBe('passed')
  // The HF course reversal at TIDUE and the missed-approach hold at BEADS were both flown; GPS stayed usable throughout
  // (a climb out of the hover no longer tips the antennas to the horizon).
  expect(fms.recallList.map(message => message.text)).not.toContain('GPS NAV LOST')
  expect(fms.recallList.map(message => message.text)).toContain('END OF ROUTE')
  // The HF at TIDUE left where its entry ended (ONCE), the go-around at CRANN, the BEADS hold left after one racetrack
  // (MISSED-HOLD): in that order, each once.
  const events = sim.modeEvents.filter(e => e.event === 'HOLD EXITED' || e.event === 'GO AROUND')
  expect(events.map(e => e.event)).toEqual(['HOLD EXITED', 'GO AROUND', 'HOLD EXITED'])
  expect(events[0].detail).toBe('TIDUE: 0 whole racetracks after the entry (EXIT TYPE ONCE)')
  expect(events[2].detail).toBe('BEADS: 1 whole racetrack after the entry (EXIT TYPE MANUAL, missed approach)')
})

test('executing a hover procedure interrupts a search pattern in progress: the pattern stops steering (Stage D)', () => {
  let now = START
  const unit = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(unit)
  setUp87nOffshoreSar(unit, sim)
  for (const key of ['TACT', 'LSK4L', 'LSK6R', 'EXEC'] as const) unit.press(key)
  const fly = (seconds: number) => { for (let t = 0; t < seconds * 4; t++) { now += 250; sim.step(0.25) } }
  fly(300)
  expect(unit.sar.status).toBe('IN PROGRESS')
  for (const key of ['TACT', 'LSK1R', 'LSK4L', 'LSK6R', 'EXEC'] as const) unit.press(key)
  expect(unit.hover.status).toBe('ACT')
  expect(unit.sar.active).toBeNull()
  expect(unit.activeRoute.legs.some(leg => leg.kind === 'wpt' && leg.qualifier === '/S')).toBe(false)
  fly(1)
  expect(sim.sarPath).toBeNull()
})

test('climbing out of a hover the modelled pitch stays within its limit, and both receivers stay usable (air-relative pitch)', () => {
  let now = START
  const unit = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(unit)
  setUp87nOffshoreSar(unit, sim)
  const ticks = (seconds: number, each?: () => void) => { for (let t = 0; t < seconds * 4; t++) { now += 250; sim.step(0.25); each?.() } }
  unit.placeAircraft({ position: offset(unit.truePosition, 0, 0.1), track: 230, altitude: 60 }, 'test: low over the sea')
  // Hold 60 ft (the start state held 500), so the departure below climbs to its 200 ft.
  expect(sim.engageAltitudeHold()).toBe(true)
  sim.selectSpeed(20)
  ticks(120)
  expect(sim.engageHover()).toBe(true)
  ticks(30)
  expect(unit.radioHeight.value!).toBeLessThan(80)
  expect(sim.engageTransitionUp()).toBe(true)
  let steepest = 0
  ticks(30, () => {
    steepest = Math.max(steepest, Math.abs(unit.attitude.pitch))
    // Pitch is the air-relative flight-path angle over at least 30 kt, within 20 degrees (flight.ts).
    const expected = Math.max(-20, Math.min(20, (Math.atan(unit.verticalSpeed / 60 / (Math.max(sim.tas, 30) * 1.68781)) * 180) / Math.PI))
    expect(unit.attitude.pitch).toBeCloseTo(expected, 9)
  })
  // The climb out of the hover (about 500 fpm at 20 to 40 kt) is a few degrees nose-up, well inside the limit.
  expect(steepest).toBeGreaterThan(3)
  expect(steepest).toBeLessThan(15)
  expect(unit.recallList.map(message => message.text)).not.toContain('GPS1 NOT USABLE')
  expect(unit.recallList.map(message => message.text)).not.toContain('GPS2 NOT USABLE')
})

test('the RNAV 190 final start state: 3 NM before STAYS on the final course at 1,700 ft, DIRECT STAYS, NAV and the approach armed', () => {
  const unit = new ScriptedFms(() => new Date(START))
  const sim = new FlightSimulator(unit)
  expect(setUp87nRnav190Final(unit, sim)).toEqual({ ready: true })
  const legs = unit.activeRoute.legs.map(leg => (leg.kind === 'wpt' ? leg.ident : leg.kind === 'cond' ? `(${leg.path})` : '(disco)'))
  expect(legs.slice(0, 4)).toEqual(['STAYS', 'CRANN', '(CA)', 'BEADS'])
  expect(distanceNm(unit.truePosition, unit.coordinates('STAYS')!)).toBeCloseTo(FINAL_START_BEFORE_STAYS_NM, 6)
  expect(unit.altitude).toBe(1700)
  expect(unit.approachArmed).toBe(true)
  expect(sim.lnavIsArmed || sim.lateralMode === 'LNAV').toBe(true)
})

for (const variant of MISSION_87N_VARIANTS) {
  test(`the 87N mission checkpoint variant ${variant.title.replace(/^87N mission variant /, '')}`, () => {
    expect(scenarioProblems(variant)).toEqual([])
    expect(SCENARIO_LIBRARY).toContain(variant)
    const { runner } = runHeadless(variant)
    const failures = runner.results.map((result, i) => ({ step: i + 1, ...result })).filter(result => result.status !== 'pass' && result.status !== 'done')
    expect(failures).toEqual([])
    expect(runner.outcome).toBe('passed')
  })
}

test('MA-GRAD: the missed approach at CRANN climbs at least 400 ft/NM over the ground, ramp included, measured on the flown trace (AIM 5-4-21)', () => {
  // The nominal run, observed tick by tick. The declared interval: from the tick TOGA is pressed at CRANN over the first
  // nautical mile flown over the ground (or to the 2,000 ft capture, if sooner). The GA vertical-acceleration ramp is
  // inside the interval, not excused (rev 3.1 addendum).
  let now = START
  const fms = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(fms)
  const runner = new ScenarioRunner(MISSION_87N_OFFSHORE_SAR, fms, undefined, sim)
  let from: number | null = null, flownNm = 0, climbed = 0
  while (!runner.finished) {
    const before = fms.altitude
    advanceTicks(1, ms => { now += ms }, sim, runner)
    if (from === null && sim.modeEvents.some(e => e.event === 'GO AROUND')) from = before
    if (from !== null && flownNm < 1 && fms.altitude < 1990) {
      flownNm += (fms.groundSpeed * 0.25) / 3600
      climbed = fms.altitude - from
    }
  }
  expect(from).not.toBeNull()
  expect(flownNm).toBeGreaterThan(0.3)
  const gradient = climbed / flownNm
  expect(gradient).toBeGreaterThanOrEqual(400)
})
