import { createHash } from 'node:crypto'
import { readFileSync } from 'node:fs'
import { expect, logicTest as test } from './isolated-client-test'
import { aircraftData, type AircraftData } from '../src/fmsCdu/efis'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { bearingDeg, courseDeg, distanceNm, offset } from '../src/fmsCdu/fmsModel'
import { COPTER_PINS_CIFP_2609, COPTER_PINS_CIFP_2609_SHA256 } from '../src/fmsCdu/data/copterPinsCifp2609'
import {
  COPTER_PINS_SOURCE, FINAL_START_BEFORE_STAYS_NM, MISSION_87N_OFFSHORE_SAR, MISSION_87N_VARIANTS, MISSION_START_SOUTH_NM, setUp87nOffshoreSar, setUp87nRnav190Final,
} from '../src/fmsCdu/heliDemo'
import { HELICOPTER_PROFILE } from '../src/fmsCdu/profile'
import { ScenarioRunner, advanceTicks, runHeadless, scenarioProblems } from '../src/fmsCdu/scenario'
import { SCENARIO_LIBRARY } from '../src/fmsCdu/scenarioLibrary'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { memoryUserDatabaseStore } from '../src/fmsCdu/userDatabase'

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


// ---------------------------------------------------------------------------------------------- measured on the trace
// Astra's review of v1 (F6): the claims the scenario steps sample at single moments are measured here over their
// intervals, on the flown trace, tick by tick.

type Tick = {
  t: number; active: string | null; altitude: number; groundSpeed: number; track: number; crossTrack: number; ra: number | null; mrkM: number | null;
  /** Fuel on board and the flow it burns at (kg, kg/h); the procedure speed limit in force (KIAS) or null. */
  fuel: number; flow: number; procedureKt: number | null;
  /** The PFD's data (efis.ts aircraftData): what the display draws, not the truth behind it. */
  pfd: AircraftData;
}

/** The user database the observed runs store into, for this user and profile: read back after a run as a restart would. */
const MISSION_USER = { userId: 'mission.crew', profileId: HELICOPTER_PROFILE.id }

/** Runs a scenario tick by tick, recording each tick, and returns the trace with the final state. */
function observed(scenario: typeof MISSION_87N_OFFSHORE_SAR) {
  let now = Date.parse(scenario.startTime!)
  const store = memoryUserDatabaseStore()
  const fms = new ScriptedFms(() => new Date(now), { userDatabase: { store, scope: MISSION_USER } })
  const sim = new FlightSimulator(fms)
  const runner = new ScenarioRunner(scenario, fms, undefined, sim)
  const ticks: Tick[] = []
  for (let n = 1; !runner.finished; n++) {
    advanceTicks(1, ms => { now += ms }, sim, runner)
    const leg = fms.activeRoute.legs[0]
    const mrk = fms.coordinates('MRK')
    ticks.push({
      t: n * 0.25, active: leg?.kind === 'wpt' ? leg.ident : leg?.kind ?? null, altitude: fms.altitude, groundSpeed: fms.groundSpeed, track: fms.track,
      crossTrack: fms.crossTrack, ra: fms.radioHeight.status === 'NORMAL' ? fms.radioHeight.value : null, mrkM: mrk ? distanceNm(fms.truePosition, mrk) * 1852 : null,
      fuel: fms.fuelState.quantity, flow: fms.fuelState.flow, procedureKt: fms.procedureSpeed?.kt ?? null, pfd: aircraftData(fms, sim),
    })
  }
  const eventAt = (event: string, detail?: RegExp) => {
    const found = sim.modeEvents.find(e => e.event === event && (!detail || detail.test(e.detail)))
    return found ? (found.at.getTime() - Date.parse(scenario.startTime!)) / 1000 : null
  }
  /** What the scenario's expectLine step matching the pattern read on the CDU, and when (seconds from the start). */
  const lineRead = (pattern: RegExp) => {
    const index = scenario.steps.findIndex(step => step.action.kind === 'expectLine' && pattern.test(step.action.pattern))
    const result = runner.results[index]
    return index < 0 || result?.status !== 'pass' ? null : { text: result.actual!, at: result.at! }
  }
  return { fms, sim, runner, ticks, eventAt, store, lineRead }
}

let nominalRun: ReturnType<typeof observed> | null = null
const nominal = () => (nominalRun ??= observed(MISSION_87N_OFFSHORE_SAR))

test('the hover is held for two minutes from its capture, every tick: at MRK, 50 ft, stopped (plan §10 step 6)', () => {
  const { ticks, eventAt, runner } = nominal()
  expect(runner.outcome).toBe('passed')
  const capture = eventAt('HOV', /holding the target|captured at the target/)!
  const departure = eventAt('TU')!
  expect(capture).not.toBeNull()
  // The departure comes no sooner than two minutes after the capture.
  expect(departure - capture).toBeGreaterThanOrEqual(120)
  const hover = ticks.filter(k => k.t > capture && k.t <= capture + 120)
  expect(hover.length).toBe(480)
  // The whole two minutes: within the capture limits (50 m, 50 ft ± 5, 1 kt).
  for (const k of hover) {
    expect(k.mrkM!, `at ${k.t} s`).toBeLessThanOrEqual(50)
    expect(Math.abs(k.ra! - 50), `at ${k.t} s`).toBeLessThanOrEqual(5)
    expect(k.groundSpeed, `at ${k.t} s`).toBeLessThanOrEqual(1)
  }
  // Settled after 20 s: within 2 m and 0.5 kt for the remaining 100 s.
  for (const k of hover.filter(k => k.t > capture + 20)) {
    expect(k.mrkM!, `at ${k.t} s`).toBeLessThanOrEqual(2)
    expect(k.groundSpeed, `at ${k.t} s`).toBeLessThanOrEqual(0.5)
  }
})

test('the sighting is stored as user waypoint SIGHT at the mark on top, and a restarted FMS reads it back (plan §10 step 2, E5)', () => {
  const { fms, store, lineRead } = nominal()
  // NEW USER WPT from the mark on top: the ONTOP reference shown, then SIGHT STORED.
  expect(lineRead(/\^ONTOP/)).not.toBeNull()
  const mark = fms.markList[0]
  expect(mark.ident).toBe('MRK01')
  expect(fms.userWaypoints).toEqual([{ ident: 'SIGHT', position: mark.position, type: 'FIXED' }])
  // The hover was flown to that same mark.
  expect(fms.hover.active?.mark.position ?? fms.hover.mark?.position).toEqual(mark.position)
  // Persisted: a new FMS for the same user and profile, on the same store, has it.
  const restarted = new ScriptedFms(() => new Date(START), { userDatabase: { store, scope: MISSION_USER } })
  expect(restarted.userWaypoints).toEqual([{ ident: 'SIGHT', position: mark.position, type: 'FIXED' }])
  expect(restarted.coordinates('SIGHT')).toEqual(mark.position)
})

test('in the hover the PFD shows IAS dashes, RA 50 on the 50 ft datum, VX/VY about 0, the wind 230/20 and RHT | HOV | HOV (plan §10 step 5)', () => {
  const { ticks, eventAt } = nominal()
  const capture = eventAt('HOV', /holding the target|captured at the target/)!
  // Settled (20 s on) to the TU selection two minutes after the capture, which acts on its tick.
  const settled = ticks.filter(k => k.t > capture + 20 && k.t < capture + 120)
  expect(settled.length).toBe(399)
  for (const { t, pfd } of settled) {
    const heli = pfd.helicopter!
    expect(heli, `at ${t} s`).not.toBeNull()
    expect(pfd.ias, `IAS at ${t} s`).toBeNull()
    expect(heli.axes, `FMA at ${t} s`).toEqual({ collective: 'RHT', pitch: 'HOV', roll: 'HOV' })
    expect(heli.radioHeight.status, `RA at ${t} s`).toBe('NORMAL')
    expect(Math.abs(heli.radioHeight.value! - 50), `RA at ${t} s`).toBeLessThanOrEqual(2)
    expect(heli.hoverHeight, `datum at ${t} s`).toBe(50)
    expect(heli.hoverData, `hover data at ${t} s`).toBe(true)
    expect(heli.lowHeight, `caption at ${t} s`).toBeNull()
    expect(Math.hypot(heli.vx!, heli.vy!), `VX/VY at ${t} s`).toBeLessThanOrEqual(0.5)
    expect(pfd.wind, `wind at ${t} s`).toEqual({ direction: 230, speed: 20 })
    // Stationary in the 20 kt wind: the airspeed is the wind, the nose into it.
    expect(Math.abs(pfd.airspeed - 20), `TAS at ${t} s`).toBeLessThanOrEqual(1)
    expect(Math.abs(((pfd.heading - 230 + 540) % 360) - 180), `heading at ${t} s`).toBeLessThanOrEqual(5)
  }
})

test('over the two-minute hover the fuel falls at the flow, and the FUEL page ENDURANCE agrees with its quantity, reserve and flow (plan §10 step 6)', () => {
  const { ticks, eventAt, fms, lineRead } = nominal()
  const capture = eventAt('HOV', /holding the target|captured at the target/)!
  const window = ticks.filter(k => k.t >= capture && k.t <= capture + 120)
  expect(window.length).toBe(481)
  const flow = window[0].flow
  expect(flow).toBeGreaterThan(0)
  // Every tick burns the flow for a quarter of a second, stationary or not.
  for (let i = 1; i < window.length; i++) {
    expect(window[i].flow).toBe(flow)
    expect(window[i - 1].fuel - window[i].fuel, `burn at ${window[i].t} s`).toBeCloseTo((flow * 0.25) / 3600, 9)
  }
  expect(window[0].fuel - window.at(-1)!.fuel).toBeCloseTo((flow * 120) / 3600, 6)
  // The FUEL page read at the end of the hover: quantity and flow, then reserve and endurance.
  const quantityLine = lineRead(/KG\/H/)!, enduranceLine = lineRead(/\\\.\\dH/)!
  expect(quantityLine).not.toBeNull()
  expect(enduranceLine).not.toBeNull()
  const [, quantity, shownFlow] = /^(\d+)KG\s+(\d+)KG\/H$/.exec(quantityLine.text)!.map(Number)
  const [, reserve, endurance] = /^(\d+)KG\s+(\d+\.\d)H$/.exec(enduranceLine.text)!.map(Number)
  const read = ticks.find(k => Math.abs(k.t - quantityLine.at) < 1e-9)!
  expect(read.t).toBeGreaterThanOrEqual(capture + 120)
  expect(quantity).toBe(Math.round(read.fuel))
  expect(shownFlow).toBe(flow)
  expect(reserve).toBe(fms.fuelState.reserve)
  expect(endurance).toBeCloseTo((read.fuel - reserve) / flow, 1)
})

test('the missed approach at 70 KIAS until 2,000 ft, then 90: the procedure limit in force and the crew selection (plan §10 step 9, MA-SPD-90)', () => {
  const { ticks, eventAt } = nominal()
  const toga = eventAt('GO AROUND')!
  expect(toga).not.toBeNull()
  const from = ticks.findIndex(k => k.t > toga)
  // 2,000 as the air data reads it, to the foot (the truth settles onto the capture without reaching it exactly).
  const reached = ticks.findIndex((k, i) => i >= from && Math.round(k.altitude) >= 2000)
  expect(reached).toBeGreaterThan(from)
  // Below 2,000: the 70 kt limit is in force, and flown.
  for (const k of ticks.slice(from, reached)) {
    expect(k.procedureKt, `limit at ${k.t} s`).toBe(70)
    expect(k.pfd.ias!, `IAS at ${k.t} s`).toBeLessThanOrEqual(70.5)
  }
  // From 2,000: the limit released to the hold's 90 on the tick, and the crew's 90 selection reached.
  expect(ticks[reached].procedureKt).toBe(90)
  const at90 = ticks.findIndex((k, i) => i >= reached && k.pfd.ias !== null && k.pfd.ias >= 89.5)
  expect(at90).toBeGreaterThan(reached)
  expect(ticks[at90].t - ticks[reached].t).toBeLessThanOrEqual(60)
  for (const k of ticks.slice(reached, at90 + 4 * 60)) expect(k.pfd.ias!, `IAS at ${k.t} s`).toBeLessThanOrEqual(90.5)
})

test('the crew NEW HOLD at BEADS completes at least two whole circuits, counted by the hold, still under NAV (plan §10 step 10)', () => {
  const { fms, sim, eventAt } = nominal()
  const hold = fms.activeRoute.hold!
  expect(hold).toMatchObject({ fix: 'BEADS', exit: 'MANUAL', status: 'IN PROGRESS' })
  expect(hold.missed).toBeFalsy()
  // Whole racetracks flown after the entry: each ends at a fix passage the FMS sequenced.
  expect(hold.circuits ?? 0).toBeGreaterThanOrEqual(2)
  expect(sim.lateralMode).toBe('LNAV')
  // The missed-approach hold was left first: this one is the crew's new hold, not the missed one continued.
  expect(eventAt('HOLD EXITED', /^BEADS: 1 whole racetrack/)).not.toBeNull()
})

/** Height gained per NM over the ground from time t over the first NM, or to 1,990 ft if sooner. */
function gradientFrom(ticks: Tick[], t: number) {
  const start = ticks.findIndex(k => k.t >= t)
  const from = ticks[Math.max(0, start - 1)].altitude
  let nm = 0, climbed = 0
  for (const k of ticks.slice(start)) {
    if (nm >= 1 || k.altitude >= 1990) break
    nm += (k.groundSpeed * 0.25) / 3600
    climbed = k.altitude - from
  }
  return { gradient: climbed / nm, nm }
}

test('MA-GRAD from TOGA: the missed approach climbs at least 400 ft/NM over the first NM from the TOGA tick, ramp included', () => {
  // Named for what it measures: from the tick TOGA is pressed (0.1 NM before CRANN) over the first nautical mile flown
  // over the ground, or to the 2,000 ft capture if sooner. The GA vertical-acceleration ramp is inside it.
  const { ticks, eventAt } = nominal()
  const toga = eventAt('GO AROUND')!
  expect(toga).not.toBeNull()
  const { gradient, nm } = gradientFrom(ticks, toga)
  expect(nm).toBeGreaterThan(0.3)
  expect(gradient).toBeGreaterThanOrEqual(400)
})

test('MA-GRAD from the MAP: the missed approach climbs at least 400 ft/NM over the first NM after CRANN is passed (AIM 5-4-21)', () => {
  // Anchored to the MAP passage itself: the first tick CRANN is no longer the active leg.
  const { ticks } = nominal()
  const onFinal = ticks.findIndex(k => k.active === 'CRANN')
  const passage = ticks.findIndex((k, i) => i > onFinal && k.active !== 'CRANN')
  expect(onFinal).toBeGreaterThan(0)
  expect(passage).toBeGreaterThan(onFinal)
  const { gradient, nm } = gradientFrom(ticks, ticks[passage - 1].t)
  expect(nm).toBeGreaterThan(0.3)
  expect(gradient).toBeGreaterThanOrEqual(400)
})

test('variant (d): from TOGA to the MAP, every tick stays on the final to CRANN, climbing: no early turn (MA-EARLY, R2-03)', () => {
  const scenario = MISSION_87N_VARIANTS.find(v => v.id === '87n-d-early-toga')!
  const { ticks, eventAt, fms, runner } = observed(scenario)
  expect(runner.outcome).toBe('passed')
  const toga = eventAt('GO AROUND')!
  expect(toga).not.toBeNull()
  const final = courseDeg(fms.coordinates('STAYS')!, fms.coordinates('CRANN')!)
  const firstAfter = ticks.findIndex(k => k.t > toga)
  const passage = ticks.findIndex((k, i) => i >= firstAfter && k.active !== 'CRANN')
  const preMap = ticks.slice(firstAfter, passage)
  // About 1.5 NM at 70 KIAS into the wind: more than a minute of trace, all of it on CRANN.
  expect(preMap.length).toBeGreaterThan(4 * 60)
  let previous = -Infinity
  for (const k of preMap) {
    expect(k.active, `active at ${k.t} s`).toBe('CRANN')
    expect(Math.abs(k.crossTrack), `cross-track at ${k.t} s`).toBeLessThanOrEqual(0.1)
    expect(Math.abs(((k.track - final + 540) % 360) - 180), `track at ${k.t} s`).toBeLessThanOrEqual(10)
    // Never descending after the first 2 s of the go-around (the vertical-speed ramp).
    if (k.t > toga + 2) expect(k.altitude, `altitude at ${k.t} s`).toBeGreaterThanOrEqual(previous - 0.01)
    previous = k.altitude
  }
})

test('variant (f): DIRECT 87N at CRANN moves the prediction endpoint to SITE ARRIVAL 87N; no vertical approach guidance', () => {
  const scenario = MISSION_87N_VARIANTS.find(v => v.id === '87n-f-proceed-vfr')!
  // Before: the approach ends at its instrument end.
  const before = new ScriptedFms(() => new Date(START))
  setUp87nRnav190Final(before, new FlightSimulator(before))
  expect(before.profile().endpoint).toMatchObject({ kind: 'INSTRUMENT END', label: 'CRANN (MAP)' })
  const { fms, runner } = observed(scenario)
  expect(runner.outcome).toBe('passed')
  expect(fms.profile().endpoint).toMatchObject({ kind: 'SITE ARRIVAL', label: '87N' })
  expect(fms.approachVertical).toBe(false)
})

test('C.5a: the missed approach CA (190, to 439 ft) completes at once at the MAP when already above 439: DF BEADS next, no descent', () => {
  // At the MDA (560 ft), 0.3 NM before CRANN on the final, NAV engaged and the altitude held: the aircraft is already
  // above the CA's 439 ft ("at or above"), so the CA completes on the tick after the MAP, and BEADS follows.
  const unit = new ScriptedFms(() => new Date(START))
  const sim = new FlightSimulator(unit)
  expect(setUp87nRnav190Final(unit, sim)).toEqual({ ready: true })
  unit.sequence()
  const active = () => { const leg = unit.activeRoute.legs[0]; return leg?.kind === 'wpt' ? leg.ident : leg?.kind === 'cond' ? `(${leg.path})` : null }
  expect(active()).toBe('CRANN')
  const stays = unit.coordinates('STAYS')!, crann = unit.coordinates('CRANN')!
  const course = courseDeg(stays, crann)
  unit.placeAircraft({ position: offset(crann, course + 180, 0.3), track: course, altitude: 560 }, 'test: 0.3 NM before CRANN at the MDA')
  sim.engageAltitudeHold()
  const seen: (string | null)[] = []
  let lowest = Infinity
  for (let tick = 0; tick < 240 && active() !== 'BEADS'; tick += 1) {
    sim.step(0.25)
    seen.push(active())
    lowest = Math.min(lowest, unit.altitude)
  }
  expect(active()).toBe('BEADS')
  expect(sim.lateralMode).toBe('LNAV')
  // CRANN, then the CA for the one tick that passes the MAP, then BEADS: the CA is complete the first tick it is flown.
  const ca = seen.indexOf('(CA)')
  expect(ca).toBeGreaterThan(0)
  expect(seen.slice(0, ca).every(ident => ident === 'CRANN')).toBe(true)
  expect(seen.slice(ca)).toEqual(['(CA)', 'BEADS'])
  // No descent toward 439 ft.
  expect(lowest).toBeGreaterThan(550)
})
