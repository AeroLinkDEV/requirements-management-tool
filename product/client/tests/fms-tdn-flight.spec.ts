import { expect, logicTest as test } from './isolated-client-test'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { distanceNm, offset, toLocal } from '../src/fmsCdu/fmsModel'
import { MISSION_87N_OFFSHORE_SAR, setUp87nOffshoreSar } from '../src/fmsCdu/heliDemo'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { planTransition, type TransitionPlan } from '../src/fmsCdu/transition'
import { departFromHover, planTransition as oraclePlan, tdhClosedLoop, type TransitionPlan as OraclePlan } from './support/tdnOracle'

// Astra's implementation review F3 (R3-01): the TD stage the autopilot actually flies, in the flight simulation's 0.25 s
// ticks, against the planner (transition.ts) and the independent oracle (tests/support/tdnOracle.ts), over the admitted
// start domain, including her 150 KIAS case and nonzero initial vertical speeds. At the planned end of TD the flown
// along-track distance, height and speed agree within 0.005 NM, 5 ft and 1 kt; the RHT capture annunciation comes
// before that arrival, and completion (5 ft, 50 fpm) is a third, separate event.

const WIND = 230
const START = Date.UTC(2026, 8, 29, 15, 0, 0)

type Case = { ias: number; height: number; vs: number; headwind: number }
type Sample = { t: number; along: number; height: number; ias: number; vs: number }

/** Flies TD from the given state over the declared sea and returns the flown trace from the engage tick, and the plans. */
function flyTd(c: Case) {
  let now = START
  const fms = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(fms)
  fms.declareSurface('offshore-87n')
  Object.assign(fms.wind, { direction: WIND, speed: c.headwind })
  const tick = () => { now += 250; sim.step(0.25) }
  // Level at the start height (or 250 ft beyond it, to arrive with the vertical speed), into the wind, at the IAS.
  const approach = c.vs === 0 ? 0 : -Math.sign(c.vs) * 250
  fms.placeAircraft({ position: { lat: 40.65, lon: -72.45 }, track: WIND, altitude: c.height + approach }, 'test: TD start')
  sim.selectHeading(WIND)
  sim.selectSpeed(c.ias)
  expect(sim.engageAltitudeHold()).toBe(true)
  for (let i = 0; i < 4 * 120; i++) tick()
  expect(Math.abs(sim.indicatedAirspeed - c.ias)).toBeLessThan(0.05)
  if (c.vs !== 0) {
    sim.selectAltitude(c.height + Math.sign(c.vs) * 2000)
    expect(sim.engageVerticalSpeed(c.vs)).toBe(true)
    for (let i = 0; i < 4 * 300 && (c.vs < 0 ? fms.altitude > c.height : fms.altitude < c.height); i++) tick()
    expect(Math.abs(fms.verticalSpeed - c.vs)).toBeLessThan(1)
  }
  const start = { ias: sim.indicatedAirspeed, radioHeight: fms.radioHeight.value!, verticalSpeed: fms.verticalSpeed, headwind: c.headwind, hoverHeight: 50 }
  const plan = planTransition(start)
  const oracle = oraclePlan({ iasKt: start.ias, raFt: start.radioHeight, vsFpm: start.verticalSpeed, headwindKt: start.headwind, hoverFt: 50 })
  if (plan.refused || oracle.refused) throw new Error('refused')
  expect(sim.engageTransitionDown()).toBe(true)
  const trace: Sample[] = [{ t: 0, along: 0, height: fms.radioHeight.value!, ias: sim.indicatedAirspeed, vs: fms.verticalSpeed }]
  let along = 0
  for (let i = 1; i <= 4 * (plan.td.seconds + 60); i++) {
    const before = fms.groundSpeed
    tick()
    along += ((before + fms.groundSpeed) / 2) * (0.25 / 3600)
    trace.push({ t: i * 0.25, along, height: fms.radioHeight.value!, ias: sim.indicatedAirspeed, vs: fms.verticalSpeed })
  }
  return { plan: plan as TransitionPlan, oracle: oracle as OraclePlan, trace, events: sim.modeEvents }
}

/** The trace at time t, interpolated between the two samples around it (removing the tick rounding). */
function at(trace: Sample[], t: number): Sample {
  const i = Math.min(trace.length - 2, Math.floor(t / 0.25))
  const a = trace[i], b = trace[i + 1], f = (t - a.t) / 0.25
  const mix = (x: number, y: number) => x + (y - x) * f
  return { t, along: mix(a.along, b.along), height: mix(a.height, b.height), ias: mix(a.ias, b.ias), vs: mix(a.vs, b.vs) }
}

const CASES: Case[] = [
  // Astra's F3 case: planner 2.321815 NM at 96.84 s, flight 2.338319 NM before the fix.
  { ias: 150, height: 1000, vs: 0, headwind: 20 },
  { ias: 100, height: 500, vs: 0, headwind: 20 },
  { ias: 100, height: 500, vs: 0, headwind: 0 },
  { ias: 120, height: 800, vs: -500, headwind: 20 },
  // Climbing into TD: it brakes, and descends to the gate height.
  { ias: 110, height: 300, vs: 500, headwind: 10 },
  { ias: 130, height: 1000, vs: -800, headwind: 0 },
  { ias: 90, height: 200, vs: 0, headwind: 20 },
]

for (const c of CASES) {
  test(`the flown TD agrees with the planner and the oracle at its end: ${c.ias} KIAS, ${c.height} ft, ${c.vs} fpm, ${c.headwind} kt headwind (F3)`, () => {
    const { plan, oracle, trace, events } = flyTd(c)
    const end = at(trace, plan.td.seconds)
    // Position: the along-track distance flown at the planned end of TD.
    expect(Math.abs(end.along - plan.td.distanceNm), 'flight vs planner, NM').toBeLessThanOrEqual(0.005)
    expect(Math.abs(end.along - oracle.td.distanceNm), 'flight vs oracle, NM').toBeLessThanOrEqual(0.005)
    // Height and speed at that boundary: the gate.
    expect(Math.abs(end.height - plan.td.gateHeight), 'height at the end of TD, ft').toBeLessThanOrEqual(5)
    expect(Math.abs(end.ias - 80), 'IAS at the end of TD, kt').toBeLessThanOrEqual(1)
    // Capture (the RHT annunciation, entering the 20 ft / 200 fpm band), completion (5 ft, 50 fpm) and arrival are three
    // events, each at the oracle's time for it within three 0.25 s ticks (the flight is discrete: its vertical speed changes by
    // 150 fpm a tick in the last feet, and an annunciation follows the tick that satisfies it), and the stage ends at arrival. With
    // braking at the acceleration limit the vertical speed falls below 200 fpm only in the last foot or so, so capture
    // and arrival come close together; the order is the oracle's, not assumed.
    const tdAt = events.find(e => e.event === 'TD')!.at.getTime()
    if (Math.abs(trace[0].height - plan.td.gateHeight) > 20) {
      const capture = events.find(e => e.event === 'RHT' && e.at.getTime() > tdAt)
      expect(capture, 'RHT captured').toBeDefined()
      expect(Math.abs((capture!.at.getTime() - tdAt) / 1000 - oracle.td.rht.captureS), 'capture time vs oracle, s').toBeLessThanOrEqual(0.75)
      const completion = trace.find(s => Math.abs(s.height - plan.td.gateHeight) <= 5 && Math.abs(s.vs) <= 50)
      expect(completion, 'completion (5 ft, 50 fpm)').toBeDefined()
      expect(Math.abs(completion!.t - oracle.td.rht.completionS), 'completion time vs oracle, s').toBeLessThanOrEqual(0.75)
      expect(Math.abs(oracle.td.rht.arrivalS - plan.td.seconds) <= 0.5 || oracle.td.ias.arrivalS > oracle.td.rht.arrivalS).toBe(true)
    }
  })
}

// ------------------------------------------------------------------------------ TD/H, TU-LAB and the whole transition
// R3-01 (4), (6) and (7) on the flown trace: the TD/H closed loop's bounds and its overshoot at MRK, the departure from a
// sideways drift, and the FMS transition from TDN checked against the planner and the oracle at every stage boundary.

/** At the gate (200 ft, 60 KIAS, still air), TD/H toward a target the given distance ahead; returns the trace. */
function flyTdhToward(remainingNm: number) {
  let now = START
  const fms = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(fms)
  fms.declareSurface('offshore-87n')
  Object.assign(fms.wind, { direction: WIND, speed: 0 })
  fms.placeAircraft({ position: { lat: 40.65, lon: -72.45 }, track: WIND, altitude: 200 }, 'test: at the gate')
  sim.selectHeading(WIND)
  sim.selectSpeed(60)
  expect(sim.engageAltitudeHold()).toBe(true)
  const tick = () => { now += 250; sim.step(0.25) }
  for (let i = 0; i < 4 * 120; i++) tick()
  const gs = fms.groundSpeed
  const target = offset(fms.position, WIND, remainingNm)
  expect(sim.engageTransitionDownToHover(target)).toBe(true)
  const trace: { t: number; gs: number; ahead: number }[] = []
  const ahead = () => { const e = toLocal(fms.truePosition, target); return e.x * Math.sin((WIND * Math.PI) / 180) + e.y * Math.cos((WIND * Math.PI) / 180) }
  for (let i = 1; i <= 4 * 240; i++) { tick(); trace.push({ t: i * 0.25, gs: fms.groundSpeed, ahead: ahead() }) }
  const hov = sim.modeEvents.find(e => e.event === 'HOV')!
  return { gs, trace, hov, fms, target }
}

test('TD/H closed loop on the flight: inside the bounds it stops at MRK; at 0.3 NM it saturates at 1.25 kt/s, passes MRK on the bound and recovers to it (R3-01 (4))', () => {
  // 0.672 NM at 60 kt needs 0.744 kt/s: arrives, and HOV holds the target.
  const exact = flyTdhToward(0.672)
  expect(tdhClosedLoop(exact.gs, 0.672)).toMatchObject({ saturated: 'none', outcome: 'arrived' })
  expect(exact.hov.detail).toBe('holding the target')
  const stopExact = exact.trace.find(k => k.gs <= 1)!
  expect(Math.abs(stopExact.ahead)).toBeLessThanOrEqual(0.005)
  // 0.3 NM needs 1.667 kt/s: flies the 1.25 bound, still on it once past MRK (never evaluated at d <= 0), and stops the
  // oracle's distance beyond.
  const over = flyTdhToward(0.3)
  const oracle = tdhClosedLoop(over.gs, 0.3)
  expect(oracle).toMatchObject({ saturated: 'upper', outcome: 'overshoot-recovery', hovCapturesAtStop: false })
  const stop = over.trace.findIndex(k => k.gs <= 1)
  for (let i = 1; i <= stop; i++) {
    const decel = (over.trace[i - 1].gs - over.trace[i].gs) / 0.25
    expect(Number.isFinite(decel), `at ${over.trace[i].t} s`).toBe(true)
    expect(decel, `deceleration at ${over.trace[i].t} s`).toBeLessThanOrEqual(1.25 + 1e-4)
  }
  expect(over.trace.some((k, i) => i < stop && k.ahead < 0)).toBe(true)
  if (!('stopOffsetNm' in oracle)) throw new Error('no closure')
  expect(Math.abs(-over.trace[stop].ahead - oracle.stopOffsetNm)).toBeLessThanOrEqual(0.005)
  expect(over.hov.detail).toMatch(/^recovering to the target, stopped \d+ m from it$/)
  // HOV brings it back to MRK.
  expect(distanceNm(over.fms.truePosition, over.target) * 1852).toBeLessThan(10)
})

test('TU-LAB from a 3 kt sideways drift starts from the true ground vector, and the lateral limit brings the drift to zero in the oracle time (R3-01 (6))', () => {
  let now = START
  const fms = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(fms)
  fms.declareSurface('offshore-87n')
  Object.assign(fms.wind, { direction: WIND, speed: 20 })
  fms.placeAircraft({ position: { lat: 40.65, lon: -72.45 }, track: WIND, altitude: 50 }, 'test: low over the sea')
  sim.selectHeading(WIND)
  sim.selectSpeed(25)
  expect(sim.engageAltitudeHold()).toBe(true)
  const tick = () => { now += 250; sim.step(0.25) }
  for (let i = 0; i < 4 * 120 && sim.tas > 21; i++) tick()
  expect(sim.engageHover()).toBe(true)
  for (let i = 0; i < 4 * 30; i++) tick()
  // Drift 3 kt to the right (toward 320): ATT holds the air velocity while the feedback is lost and the wind moves on;
  // with the feedback back the drift is measured, and ATT stays until the crew acts.
  fms.setCondition('gpsLost', true)
  for (let i = 0; i < 4 * 2; i++) tick()
  const rad = (d: number) => (d * Math.PI) / 180
  const toward = { north: 20 * Math.cos(rad(50)) + 3 * Math.cos(rad(320)), east: 20 * Math.sin(rad(50)) + 3 * Math.sin(rad(320)) }
  Object.assign(fms.wind, { direction: ((Math.atan2(-toward.east, -toward.north) * 180) / Math.PI + 360) % 360, speed: Math.hypot(toward.north, toward.east) })
  for (let i = 0; i < 4 * 20; i++) tick()
  fms.setCondition('gpsLost', false)
  for (let i = 0; i < 4 * 2; i++) tick()
  expect(sim.axisModes.pitch).toBe('ATT')
  const cross = () => { const h = rad(fms.heading), t = rad(fms.track); return fms.groundSpeed * (-Math.cos(t) * Math.sin(h) + Math.sin(t) * Math.cos(h)) }
  const oracle = departFromHover({
    raFt: fms.radioHeight.value, headingDeg: fms.heading,
    groundVelocity: { northKt: fms.groundSpeed * Math.cos(rad(fms.track)), eastKt: fms.groundSpeed * Math.sin(rad(fms.track)) },
    windVelocity: { northKt: toward.north, eastKt: toward.east },
  })
  if (oracle.refused) throw new Error(oracle.reason)
  expect(oracle.initialCrossGroundKt).toBeCloseTo(3, 1)
  expect(cross()).toBeCloseTo(3, 1)
  expect(sim.engageTransitionUp()).toBe(true)
  let zero: number | null = null
  for (let i = 1; i <= 4 * 10; i++) { tick(); if (zero === null && Math.abs(cross()) <= 0.05) zero = i * 0.25 }
  expect(zero).not.toBeNull()
  expect(Math.abs(zero! - oracle.crossGroundZeroS)).toBeLessThanOrEqual(0.5)
  expect(sim.axisModes.pitch).toBe('TU')
})

test('the FMS transition from TDN, flown: at the end of TD, the end of the gate segment and the end of TD/H the flight agrees with the planner and the oracle (R3-01 (7))', () => {
  let now = Date.parse(MISSION_87N_OFFSHORE_SAR.startTime!)
  const fms = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(fms)
  expect(setUp87nOffshoreSar(fms, sim)).toEqual({ ready: true })
  const tick = () => { now += 250; sim.step(0.25) }
  // One second in, as the mission's checkpoint variants: the sighting, ACTIVATE and EXEC.
  for (let i = 0; i < 4; i++) tick()
  for (const key of ['TACT', 'LSK1R', 'LSK4L', 'LSK6R', 'EXEC'] as const) fms.press(key)
  const mrk = fms.hover.active!.mark.position
  for (let i = 0; i < 4 * 1200 && fms.hover.request === 0; i++) tick()
  const atTdn = fms.hover.atTdn!
  expect(atTdn.decision?.engage).toBe(true)
  const plan = (atTdn.decision as { plan: TransitionPlan }).plan
  const start = atTdn.start!
  const oracle = oraclePlan({ iasKt: start.ias, raFt: start.radioHeight, vsFpm: start.verticalSpeed, headwindKt: start.headwind, hoverFt: start.hoverHeight, elevationFt: start.elevation })
  if (oracle.refused) throw new Error(oracle.reason)
  const requestAt = now
  const trace: { t: number; toMrk: number; height: number; gs: number; ias: number }[] = []
  for (let i = 1; i <= 4 * 400 && !sim.hoverCaptured; i++) {
    tick()
    trace.push({ t: i * 0.25, toMrk: distanceNm(fms.truePosition, mrk), height: fms.radioHeight.value!, gs: fms.groundSpeed, ias: sim.indicatedAirspeed })
  }
  const since = (event: string, detail: RegExp) => {
    const found = sim.modeEvents.find(e => e.event === event && detail.test(e.detail) && e.at.getTime() > requestAt)
    return found ? (found.at.getTime() - requestAt) / 1000 : null
  }
  // 1. The end of TD: from the tick TD engages (the one after the request), the planned TD distance flown at the planned
  // time (the trace interpolated between ticks), at the gate height and speed.
  const td = since('TD', /./)!
  const at = (t: number) => {
    const i = Math.min(trace.length - 1, Math.max(1, Math.ceil(t * 4) - 1)), a = trace[i - 1], b = trace[i], f = (t - a.t) / 0.25
    return { toMrk: a.toMrk + (b.toMrk - a.toMrk) * f, height: a.height + (b.height - a.height) * f, ias: a.ias + (b.ias - a.ias) * f }
  }
  const tdStart = at(td), tdEnd = at(td + plan.td.seconds)
  expect(Math.abs(tdStart.toMrk - tdEnd.toMrk - plan.td.distanceNm), 'TD distance vs planner, NM').toBeLessThanOrEqual(0.005)
  expect(Math.abs(tdStart.toMrk - tdEnd.toMrk - oracle.td.distanceNm), 'TD distance vs oracle, NM').toBeLessThanOrEqual(0.005)
  expect(Math.abs(tdEnd.height - plan.td.gateHeight)).toBeLessThanOrEqual(5)
  expect(Math.abs(tdEnd.ias - 80)).toBeLessThanOrEqual(1)
  // 2. The end of the gate segment: the deceleration starts the planned TD/H distance from MRK, at the gate ground speed.
  const gateEnd = since('TD/H', /gate segment ends/)
  expect(gateEnd).not.toBeNull()
  const atGate = trace[Math.round(gateEnd! * 4) - 1]
  expect(Math.abs(atGate.toMrk - plan.tdh.distanceNm), 'TD/H distance vs planner, NM').toBeLessThanOrEqual(0.005)
  expect(Math.abs(atGate.toMrk - oracle.tdh.distanceNm), 'TD/H distance vs oracle, NM').toBeLessThanOrEqual(0.005)
  expect(Math.abs(atGate.gs - oracle.gate.gsKt)).toBeLessThanOrEqual(1)
  expect(Math.abs(atGate.height - plan.td.gateHeight)).toBeLessThanOrEqual(5)
  // 3. The end of TD/H: stopped at MRK, at the hover height, in the planned time.
  const hov = since('HOV', /holding the target/)
  expect(hov).not.toBeNull()
  const atHov = trace[Math.round(hov! * 4) - 1]
  expect(atHov.toMrk * 1852).toBeLessThan(10)
  expect(atHov.gs).toBeLessThanOrEqual(1)
  expect(Math.abs(atHov.height - plan.tdh.hoverHeight)).toBeLessThanOrEqual(5)
  expect(Math.abs(hov! - gateEnd! - oracle.tdh.hovSpeedS), 'TD/H time vs oracle, s').toBeLessThanOrEqual(1)
})
