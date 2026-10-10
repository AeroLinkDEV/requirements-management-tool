import { expect, logicTest as test } from './isolated-client-test'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { LATER_SBAS_PROFILE } from '../src/fmsCdu/profile'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'

// #1506: the mode-event history is bounded. A crew re-selecting a target adds one event per selection; the simulator
// keeps the newest events, its retained state stops growing, the latest event stays exact, and the FMA's amber
// "recently lost" modes (axisDegraded, read by the EFIS every frame) cost the same however long the history is and
// survive the lost-mode event leaving the log. No clock moves while the selections are made, so every event is inside
// the amber window: the hardest case for both.
const SELECTIONS = 20_000

/** A flight with LNAV engaged that has just had an FMS failure, which took NAV off the roll axis (fms-modes.spec.ts). */
const failedOnLnav = () => {
  let now = Date.UTC(2026, 8, 27, 14, 0, 0)
  const unit = new ScriptedFms(() => new Date(now), { profile: LATER_SBAS_PROFILE })
  const sim = new FlightSimulator(unit)
  const fly = (seconds: number) => { for (let t = 0; t < seconds; t += 1) { now += 1000; sim.step(1) } }
  fly(30)
  expect(sim.lateralMode).toBe('LNAV')
  unit.setCondition('fmsFail', true)
  fly(1)
  expect(sim.modeEvents.at(-1)).toMatchObject({ event: 'FMS FAILURE', lost: [{ axis: 'roll', mode: 'NAV' }] })
  return { sim, fly }
}

/** Every value the simulator retains: each object reachable through data properties, and each array's length. */
const retained = (root: object) => {
  const seen = new Set<object>()
  let size = 0
  const walk = (value: unknown) => {
    if (value === null || typeof value !== 'object' || seen.has(value)) return
    seen.add(value); size += 1
    if (value instanceof Map) { size += value.size; for (const [k, v] of value) { walk(k); walk(v) } return }
    if (value instanceof Set) { size += value.size; for (const v of value) walk(v); return }
    for (const key of Object.getOwnPropertyNames(value)) {
      const property = Object.getOwnPropertyDescriptor(value, key)!
      if ('value' in property) walk(property.value)
    }
  }
  walk(root)
  return size
}

// Owner of the bound and its wrap: the history is a fixed-size window of the newest events, oldest first, after any
// number of appends. Retention that grows with the selections (the unbounded log, copied whole on each append) fails
// the first assertion; a window that loses order or the latest event fails the rest.
test('20,000 repeated selections keep a fixed-size history of the newest events, oldest first', () => {
  const { sim } = failedOnLnav()
  for (let i = 1; i <= SELECTIONS / 2; i += 1) sim.selectAltitude(i)
  const half = { retained: retained(sim), length: sim.modeEvents.length }
  for (let i = SELECTIONS / 2 + 1; i <= SELECTIONS; i += 1) sim.selectAltitude(i)
  expect(retained(sim), 'retained state after 20,000 selections against after 10,000').toBe(half.retained)

  const events = sim.modeEvents
  expect(events.length).toBe(half.length)
  expect(events.length).toBeLessThan(SELECTIONS / 2)
  // The window is the newest events, in the order they were made, ending with the last selection.
  expect(events.map(e => e.detail)).toEqual(Array.from({ length: events.length }, (_, i) => `${SELECTIONS - events.length + 1 + i} FT`))
  expect(events.every(e => e.event === 'ALT SELECTED')).toBe(true)
  expect(sim.latestModeEvent).toBe(events.at(-1))
  // A history read is a copy the next event does not change.
  sim.selectAltitude(1)
  expect(events.at(-1)?.detail).toBe(`${SELECTIONS} FT`)
  expect(sim.modeEvents.at(-1)?.detail).toBe('1 FT')
  expect(sim.modeEvents.length).toBe(half.length)
})

// Owner of the per-axis losses: the amber NAV comes from the roll axis's latest loss, not from the log, so it holds for
// its window after the failure event has left the log, and clears when the window has passed.
test('the roll axis keeps its latest loss amber after the failure event has wrapped out of the history', () => {
  const { sim, fly } = failedOnLnav()
  for (let i = 1; i <= SELECTIONS; i += 1) sim.selectAltitude(i)
  expect(sim.modeEvents.some(e => e.event === 'FMS FAILURE')).toBe(false)
  expect(sim.axisDegraded(10)).toEqual({ collective: [], pitch: [], roll: ['NAV'] })
  fly(11)
  expect(sim.axisDegraded(10)).toEqual({ collective: [], pitch: [], roll: [] })
})

// Owner of the render-path cost: axisDegraded reads the same amount at 20,000 events as at 100. It must compare each
// candidate's time with the window, so the times it reads count the work; the unbounded scan read one per event.
test('axisDegraded reads as much at 20,000 events as at 100, all inside its window', () => {
  const timesRead = (sim: FlightSimulator) => {
    const getTime = Date.prototype.getTime, valueOf = Date.prototype.valueOf
    let reads = 0
    Date.prototype.getTime = function (this: Date) { reads += 1; return getTime.call(this) }
    Date.prototype.valueOf = function (this: Date) { reads += 1; return valueOf.call(this) }
    try {
      const degraded = sim.axisDegraded(10)
      return { degraded, reads }
    } finally { Date.prototype.getTime = getTime; Date.prototype.valueOf = valueOf }
  }
  const { sim } = failedOnLnav()
  for (let i = sim.modeEvents.length; i < 100; i += 1) sim.selectAltitude(i)
  expect(sim.modeEvents.length).toBe(100)
  const short = timesRead(sim)
  for (let i = 1; i <= SELECTIONS; i += 1) sim.selectAltitude(i)
  const long = timesRead(sim)
  expect(short.degraded.roll).toEqual(['NAV'])
  expect(long).toEqual(short)
})
