import { expect, logicTest as test } from './isolated-client-test'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { planTransition, type TransitionPlan } from '../src/fmsCdu/transition'
import { planTransition as oraclePlan, type TransitionPlan as OraclePlan } from './support/tdnOracle'

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
