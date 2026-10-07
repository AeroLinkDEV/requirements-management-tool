import { expect, logicTest as test } from './isolated-client-test'
import { DualFmsSystem } from '../src/fmsCdu/dualFms'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'

const START = Date.UTC(2026, 9, 7, 12)
const HERE = { lat: 45, lon: -63 }
const word = (unit: ScriptedFms) => {
  const sample = unit.navigationInputs?.apirs
  expect(sample?.status).toBe('NORMAL')
  if (!sample?.value) throw new Error('APIRS sample unavailable')
  return sample.value
}
const place = (unit: ScriptedFms, heading: number) => {
  unit.wind.speed = 0
  unit.placeAircraft({ position: HERE, track: heading, altitude: 3000 }, 'APIRS regression start')
}
const configure = (flight: FlightSimulator, heading: number) => {
  flight.selectHeading(heading)
  flight.selectSpeed(80)
}
const single = (heading = 0) => {
  let now = START
  const unit = new ScriptedFms(() => new Date(now))
  place(unit, heading)
  const flight = new FlightSimulator(unit)
  configure(flight, heading)
  return { unit, flight, advance: (dt: number) => { now += dt * 1000; flight.step(dt) }, rewind: () => { now = START; unit.refreshSensorInput() } }
}

// The established coordinated-flight model decelerates at 2 kt/s. Constant wind has no acceleration.
// This oracle is independent of the producer's previous-sample implementation and uses actual flight controls.
for (const heading of [0, 90]) for (const dt of [0.25, 1]) {
  test(`native APIRS includes real ${heading === 0 ? 'north' : 'east'} deceleration at ${dt}s cadence`, () => {
    const { unit, advance } = single(heading)
    const bias = word(unit)
    for (let i = 0; i < 4; i++) {
      advance(dt)
      const measured = word(unit)
      expect(measured.northMs2 - bias.northMs2, 'true north acceleration survives same-epoch sampling').toBeCloseTo(heading === 0 ? -1.028888 : 0, 6)
      expect(measured.eastMs2 - bias.eastMs2, 'true east acceleration survives same-epoch sampling').toBeCloseTo(heading === 90 ? -1.028888 : 0, 6)
    }
  })
}

for (const side of [1, 2] as const) {
  test(`the shared native APIRS retains its acceleration when FMS ${side} guides and paused samples repeat`, () => {
    let now = START
    const system = new DualFmsSystem(() => new Date(now))
    system.computers.forEach(unit => place(unit, 0))
    system.flights.forEach(flight => flight.adoptAircraftMotion())
    system.selectGuidance(side)
    configure(system.simulator, 0)
    const bias = word(system.computers[0])
    for (let i = 0; i < 4; i++) {
      now += 250; system.step(0.25)
      const measured = word(system.computers[0])
      expect(measured.northMs2 - bias.northMs2).toBeCloseTo(-1.028888, 6)
      expect(word(system.computers[1])).toEqual(measured)
      system.tick(); system.tick()
      expect(word(system.computers[0]), 'zero-dt refresh retains the measured interval').toEqual(measured)
      expect(word(system.computers[1])).toEqual(measured)
    }
  })
}

test('initial and same-epoch native samples contain finite bias without inventing acceleration', () => {
  const { unit } = single()
  const bias = word(unit)
  expect(Number.isFinite(bias.northMs2) && Number.isFinite(bias.eastMs2)).toBe(true)
  for (let i = 0; i < 4; i++) { unit.refreshSensorInput(); expect(word(unit)).toEqual(bias) }
  unit.setApirsFaultBias(0.3, -0.2); unit.refreshSensorInput()
  expect(word(unit).northMs2 - bias.northMs2).toBeCloseTo(0.3, 10)
  expect(word(unit).eastMs2 - bias.eastMs2).toBeCloseTo(-0.2, 10)
})

test('a reset simulation clock establishes a fresh velocity baseline without a fabricated impulse', () => {
  const { unit, advance, rewind } = single()
  const bias = word(unit)
  advance(1)
  rewind()
  expect(word(unit)).toEqual(bias)
  unit.refreshSensorInput()
  expect(word(unit)).toEqual(bias)
})
