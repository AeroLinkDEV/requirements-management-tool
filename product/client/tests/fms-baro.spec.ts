import { expect, logicTest as test } from './isolated-client-test'
import { STANDARD_HPA, indicatedAltitudeFt, pressureAltitudeFt } from '../src/fmsCdu/baro'
import { aircraftData } from '../src/fmsCdu/efis'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { setUp87nOffshoreSar } from '../src/fmsCdu/heliDemo'
import { ScenarioRunner, advanceTicks, scenarioProblems, type Scenario } from '../src/fmsCdu/scenario'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'

// Plan rev 3 B1.1: the truth is the physical height; the barometric altitude is derived from it (plus an injectable
// error) and never written back; the crew's setting (QNH or STD) changes what is indicated. Neither the setting nor the
// error moves the aircraft directly or changes the radio height (rev 3.1 addendum: an error acts on the truth only
// through a controller that holds a barometric altitude).

const START = Date.UTC(2026, 8, 30, 15, 0, 0)

/** The 87N mission start (500 ft over the declared sea, ALT, NAV), stepped by quarter seconds. */
function offshore() {
  let now = START
  const unit = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(unit)
  expect(setUp87nOffshoreSar(unit, sim)).toEqual({ ready: true })
  const step = () => { now += 250; sim.step(0.25) }
  const fly = (seconds: number) => { for (let t = 0; t < seconds * 4; t++) step() }
  const moveClock = (ms: number) => { now += ms }
  return { unit, sim, step, fly, moveClock }
}
/** The same start, slowed and holding a hover: the collective on the radio height (RHT), no barometric reference. */
function hovering() {
  const run = offshore()
  run.sim.selectSpeed(20)
  for (let t = 0; t < 720 && run.sim.tas >= 26; t++) run.step()
  expect(run.sim.engageHover()).toBe(true)
  run.fly(30)
  expect(run.sim.axisModes).toMatchObject({ collective: 'RHT', pitch: 'HOV', roll: 'HOV' })
  return run
}

test('the ISA pressure altitude of a pressure matches the standard atmosphere table (ICAO Doc 7488)', () => {
  expect(pressureAltitudeFt(STANDARD_HPA)).toBe(0)
  // The ICAO standard atmosphere: 1000 hPa at 110.9 m, 850 hPa at 1457 m, 700 hPa at 3012 m.
  expect(pressureAltitudeFt(1000)).toBeCloseTo(110.9 / 0.3048, -0.3)
  expect(pressureAltitudeFt(850)).toBeCloseTo(1457 / 0.3048, -1)
  expect(pressureAltitudeFt(700)).toBeCloseTo(3012 / 0.3048, -1)
  // Above the standard pressure, below sea level.
  expect(pressureAltitudeFt(1030)).toBeLessThan(0)
})

test('the altimeter indicates the barometric altitude when set to the QNH, the pressure altitude at STD, and reads high when set too high', () => {
  expect(indicatedAltitudeFt(500, 1000, { kind: 'QNH', hPa: 1000 })).toBeCloseTo(500, 9)
  expect(indicatedAltitudeFt(500, 1000, { kind: 'STD' })).toBeCloseTo(500 + pressureAltitudeFt(1000), 9)
  // About 27 to 28 ft a hectopascal near sea level: set 10 hPa too high, it reads about 280 ft high.
  const high = indicatedAltitudeFt(500, 1000, { kind: 'QNH', hPa: 1010 }) - 500
  expect(high).toBeGreaterThan(260)
  expect(high).toBeLessThan(290)
  expect(indicatedAltitudeFt(500, 1000, { kind: 'QNH', hPa: 990 }) - 500).toBeLessThan(-260)
})

test('B1.1: the indicated altitude follows the crew\'s setting; the barometric altitude, the physical height and the radio height do not', () => {
  const { unit, sim } = offshore()
  expect(unit.baro).toEqual({ errorFt: 0, declaredQnhHpa: STANDARD_HPA, setting: { kind: 'QNH', hPa: STANDARD_HPA } })
  const physical = unit.physicalAltitude, baro = unit.altitude, ra = unit.radioHeight.value
  expect(baro).toBe(physical)
  expect(unit.indicatedAltitude).toBeCloseTo(physical, 9)
  expect(unit.declareQnh(1000, 'test: a low')).toBe(true)
  // The altimeter still set to 1013: it reads high by the ISA height of 1000 hPa above 1013.25.
  expect(unit.indicatedAltitude - baro).toBeCloseTo(pressureAltitudeFt(1000), 6)
  expect(unit.setBaroSetting({ kind: 'QNH', hPa: 1000 })).toBe(true)
  expect(unit.indicatedAltitude).toBeCloseTo(baro, 9)
  expect(unit.setBaroSetting({ kind: 'STD' })).toBe(true)
  expect(unit.indicatedAltitude).toBeCloseTo(baro + pressureAltitudeFt(1000), 9)
  expect([unit.physicalAltitude, unit.altitude, unit.radioHeight.value]).toEqual([physical, baro, ra])
  // The PFD's altitude is the indicated one, with the setting written beside it; the view's eye is the physical height.
  const air = aircraftData(unit, sim)
  expect(air).toMatchObject({ altitude: unit.indicatedAltitude, physicalAltitude: physical, baroSetting: 'STD' })
  unit.setBaroSetting({ kind: 'QNH', hPa: 1000 })
  expect(aircraftData(unit, sim).baroSetting).toBe('QNH 1000')
})

test('B1.1: changing the setting in flight never moves the aircraft: step for step the same as a flight without it', () => {
  const left = offshore(), right = offshore()
  right.unit.declareQnh(995, 'test')
  const settings = [{ kind: 'STD' as const }, { kind: 'QNH' as const, hPa: 980 }, { kind: 'QNH' as const, hPa: 1040 }, { kind: 'QNH' as const, hPa: 995 }]
  // Level in ALT, then a climb in VS to a preselected altitude, the setting changed every 20 s throughout.
  for (const run of [left, right]) { run.sim.selectAltitude(900); run.sim.engageVerticalSpeed(500) }
  for (let t = 0; t < 4 * 240; t++) {
    if (t % 80 === 0) right.unit.setBaroSetting(settings[(t / 80) % settings.length])
    left.step(); right.step()
    expect(right.unit.physicalAltitude).toBe(left.unit.physicalAltitude)
    expect(right.unit.radioHeight).toEqual(left.unit.radioHeight)
    expect(right.unit.truePosition).toEqual(left.unit.truePosition)
  }
  expect(left.sim.axisModes.collective).toBe('ALT')
  expect(Math.round(right.unit.altitude)).toBe(900)
})

test('B1.1: an injected baro error changes nothing physical at once, nor ever while no controller holds a barometric altitude (RHT on the radio height)', () => {
  const left = hovering(), right = hovering()
  expect(right.unit.physicalAltitude).toBe(left.unit.physicalAltitude)
  expect(right.unit.setBaroError(400, 'test: static source error')).toBe(true)
  // At once: the altimeter reads 400 ft more; the truth and the radio height are untouched.
  expect(right.unit.altitude).toBe(left.unit.altitude + 400)
  expect(right.unit.physicalAltitude).toBe(left.unit.physicalAltitude)
  expect(right.unit.radioHeight).toEqual(left.unit.radioHeight)
  // A minute in RHT: the hover holds the radio height, so the error never reaches the truth.
  for (let t = 0; t < 4 * 60; t++) {
    left.step(); right.step()
    expect(right.unit.physicalAltitude).toBe(left.unit.physicalAltitude)
    expect(right.unit.radioHeight).toEqual(left.unit.radioHeight)
  }
  expect(right.unit.altitude - right.unit.physicalAltitude).toBe(400)
  // The GPS measures the antenna's true height (its altitude word, label 076), not what the erroneous altimeter reads.
  const gpsAltitude = (right.unit.gps[0].bus()!['076'] as { value: number }).value
  expect(Math.abs(gpsAltitude - right.unit.physicalAltitude)).toBeLessThan(60)
})

test('B1.1: in ALT the autopilot holds the barometric altitude, so an error moves the aircraft through the controller; the radio height shows it', () => {
  const { unit, sim, fly } = offshore()
  expect(sim.axisModes.collective).toBe('ALT')
  const held = unit.altitude, physical = unit.physicalAltitude, ra = unit.radioHeight.value!
  expect(unit.setBaroError(120, 'test: static source error')).toBe(true)
  // The hold closes on it with a 30 s time constant (2 fpm a foot): four minutes leaves well under a foot.
  // Still over the declared sea after 90 s: the radio height has fallen exactly as the physical height has.
  fly(90)
  expect(unit.radioHeight.status).toBe('NORMAL')
  expect(ra - unit.radioHeight.value!).toBeCloseTo(physical - unit.physicalAltitude, 9)
  expect(physical - unit.physicalAltitude).toBeGreaterThan(100)
  fly(150)
  // The altimeter is back on the held altitude, and the aircraft is 120 ft lower.
  expect(unit.altitude).toBeCloseTo(held, 0)
  expect(unit.physicalAltitude).toBeCloseTo(physical - 120, 0)
  // The air-data word the procedure-speed release reads is the barometric altitude, error included.
  expect(unit.validBaroAltitude).toBe(Math.round(unit.altitude))
})

test('B1.1: a setting or an error out of range is refused and changes nothing; each engineering change is logged with its reason', () => {
  const { unit } = offshore()
  const before = unit.baro
  expect(unit.setBaroSetting({ kind: 'QNH', hPa: 800 })).toBe(false)
  expect(unit.setBaroSetting({ kind: 'QNH', hPa: Number.NaN })).toBe(false)
  expect(unit.setBaroError(2500, 'test')).toBe(false)
  expect(unit.declareQnh(1200, 'test')).toBe(false)
  expect(unit.baro).toEqual(before)
  expect(unit.engineeringLog.filter(e => /BARO|QNH/.test(e.action))).toEqual([])
  unit.setBaroError(-150, 'test: a leak')
  unit.declareQnh(1002, 'test: the forecast')
  expect(unit.engineeringLog.slice(-2).map(e => [e.action, e.detail])).toEqual([
    ['BARO ERROR', 'test: a leak: -150 FT'],
    ['DECLARE QNH', 'test: the forecast: 1002 HPA (the altimeter is set to QNH 1013)'],
  ])
})

test('B1.1: a scenario sets the altimeter, injects an error and declares the QNH; a bad baro step is refused at admission', () => {
  const { unit, sim, moveClock } = offshore()
  const scenarioOf = (steps: unknown[]): Scenario => ({ id: 'baro', title: 'Baro', objective: '', maxSeconds: 30, steps: steps as Scenario['steps'] })
  const scenario = scenarioOf([
    { when: { kind: 'start' }, action: { kind: 'baro', declaredQnh: 1005, errorFt: 60, setting: 'STD' } },
    { when: { kind: 'after', seconds: 1 }, action: { kind: 'baro', setting: 1005 } },
  ])
  expect(scenarioProblems(scenario)).toEqual([])
  const runner = new ScenarioRunner(scenario, unit, undefined, sim)
  advanceTicks(40, moveClock, sim, runner)
  expect(runner.finished).toBe(true)
  expect(unit.baro).toEqual({ errorFt: 60, declaredQnhHpa: 1005, setting: { kind: 'QNH', hPa: 1005 } })
  expect(unit.indicatedAltitude).toBeCloseTo(unit.physicalAltitude + 60, 6)
  for (const [action, problem] of [
    [{ kind: 'baro' }, /needs a setting/],
    [{ kind: 'baro', setting: 1200 }, /setting is STD or a QNH/],
    [{ kind: 'baro', setting: 'QNE' }, /setting is STD or a QNH/],
    [{ kind: 'baro', errorFt: 5000 }, /errorFt/],
    [{ kind: 'baro', declaredQnh: 'low' }, /declaredQnh/],
  ] as const) {
    expect(scenarioProblems(scenarioOf([{ when: { kind: 'start' }, action }])).join('; ')).toMatch(problem)
  }
})

test('B1.1: a scenario\'s "below" trigger and aircraft checks read the physical height; its "above" trigger reads the altimeter', () => {
  const scenarioOf = (steps: unknown[]): Scenario => ({ id: 'baro-frames', title: 'Frames', objective: '', maxSeconds: 120, steps: steps as Scenario['steps'] })
  const run = (steps: unknown[]) => {
    const { unit, sim, moveClock } = offshore()
    const runner = new ScenarioRunner(scenarioOf(steps), unit, undefined, sim)
    advanceTicks(4 * 120, moveClock, sim, runner)
    return runner.results.map(result => result.status)
  }
  // The altimeter reads 300 ft high: the aircraft's physical height is still 500 ft, and a check of it passes.
  expect(run([
    { when: { kind: 'start' }, action: { kind: 'baro', errorFt: 300 } },
    { when: { kind: 'start' }, action: { kind: 'expectAircraft', altitude: 500 } },
  ])[1]).toBe('pass')
  // ALT then flies the aircraft down 300 ft to put the altimeter back on 500: the physical height passes 400 ft, the
  // altimeter never reads below 500.
  expect(run([
    { when: { kind: 'start' }, action: { kind: 'baro', errorFt: 300 } },
    { when: { kind: 'below', feet: 400 }, action: { kind: 'expectAircraft', maxAltitude: 400 } },
  ])[1]).toBe('pass')
  // A low declared with the altimeter left on 1013: it reads about 275 ft high, so "above 700" is reached at once.
  expect(run([
    { when: { kind: 'start' }, action: { kind: 'baro', declaredQnh: 1003 } },
    { when: { kind: 'above', feet: 700 }, action: { kind: 'expectAircraft', maxAltitude: 520 } },
  ])[1]).toBe('pass')
})

test('B1.1: with an error the displays split: the PFD indicates the erroneous altitude, the view\'s eye stays at the physical height', () => {
  const { unit, sim } = offshore()
  expect(unit.setBaroError(-2500, 'test')).toBe(false)
  expect(unit.setBaroError(-250, 'test')).toBe(true)
  const air = aircraftData(unit, sim)
  expect(air.physicalAltitude).toBe(unit.physicalAltitude)
  expect(air.altitude).toBeCloseTo(unit.physicalAltitude - 250, 9)
})

test('B1.1: the air data the FMS and the receivers are given carries the error; the radio height beside it does not', () => {
  const left = hovering(), right = hovering()
  right.unit.setBaroError(400, 'test')
  left.fly(2); right.fly(2)
  const frame = right.unit.navigationInputs!, control = left.unit.navigationInputs!
  expect(frame.air!.value!.altitudeFt).toBe(right.unit.altitude)
  expect(frame.radioHeight!.value).toBe(control.radioHeight!.value)
  // Three satellites and baro: the receiver aids its altitude with the air data, so its fix follows the altimeter's
  // error (no redundancy to see it), 400 ft above the true height.
  const receiver = right.unit.gps[0]
  const bus = () => receiver.bus()!
  const used = () => (bus()['060'] as { value: { prn: number; used: boolean } | null }[]).filter(w => w.value?.used).map(w => w.value!.prn)
  receiver.deselect(used().slice(3))
  right.fly(4)
  expect(receiver.mode).toBe('ALT_AIDING')
  expect((bus()['076'] as { value: number }).value - right.unit.physicalAltitude).toBeGreaterThan(300)
})
