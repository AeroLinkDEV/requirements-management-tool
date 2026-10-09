import { expect, logicTest as test } from './isolated-client-test'
import { dualComposition, singleComposition } from '../src/fmsCdu/kernel/legacyPlantAdapter'
import { FmsKernel } from '../src/fmsCdu/kernel/kernel'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'

const START = Date.UTC(2026, 9, 7, 12)
const HERE = { lat: 45, lon: -63 }
const word = (unit: ScriptedFms) => {
  const sample = unit.navigationInputs?.apirs
  expect(sample?.status).toBe('NORMAL')
  if (!sample?.value) throw new Error('APIRS sample unavailable')
  return structuredClone(sample.value)
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
  const { fms: unit, sim: flight, plant } = singleComposition(START)
  place(unit, heading)
  configure(flight, heading)
  const kernel = new FmsKernel(plant)
  return { unit, flight, advance: (frames: number) => kernel.advance(frames) }
}
const dual = (side: 1 | 2) => {
  const { system, plant } = dualComposition(START)
  system.computers.forEach(unit => place(unit, 0))
  system.flights.forEach(flight => flight.adoptAircraftMotion())
  system.selectGuidance(side)
  configure(system.simulator, 0)
  return { system, kernel: new FmsKernel(plant) }
}

// The established coordinated-flight model decelerates at 2 kt/s. Constant wind has no acceleration.
// This oracle is independent of the producer's previous-sample implementation and uses actual flight controls.
for (const heading of [0, 90]) for (const frames of [1, 4]) {
  test(`native APIRS includes real ${heading === 0 ? 'north' : 'east'} deceleration with ${frames}-frame kernel batches`, () => {
    const { unit, advance } = single(heading)
    const bias = word(unit)
    for (let i = 0; i < 4; i++) {
      advance(frames)
      const measured = word(unit)
      expect(measured.northMs2 - bias.northMs2, 'true north acceleration survives same-epoch sampling').toBeCloseTo(heading === 0 ? -2 * 1852 / 3600 : 0, 5)
      expect(measured.eastMs2 - bias.eastMs2, 'true east acceleration survives same-epoch sampling').toBeCloseTo(heading === 90 ? -2 * 1852 / 3600 : 0, 5)
    }
  })
}

for (const side of [1, 2] as const) {
  test(`shared published APIRS retains acceleration when FMS${side} guides and same-frame refresh repeats (#1553 consumption separate)`, () => {
    const { system, kernel } = dual(side)
    const bias = word(system.computers[0])
    for (let i = 0; i < 4; i++) {
      kernel.advance(1)
      const measured = word(system.computers[0])
      expect(measured.northMs2 - bias.northMs2).toBeCloseTo(-2 * 1852 / 3600, 5)
      expect(word(system.computers[1])).toEqual(measured)
      const utc = system.computers[0].now.getTime()
      system.tick(); system.tick()
      expect(system.computers[0].now.getTime()).toBe(utc)
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
})

test('ending commanded deceleration returns the native word to bias at constant physical velocity', () => {
  const { unit, flight, advance } = single()
  const bias = word(unit)
  advance(4)
  expect(word(unit).northMs2 - bias.northMs2).toBeCloseTo(-2 * 1852 / 3600, 5)
  // S300 speed selection is IAS; let the existing 80 KIAS command reach steady flight rather than treating TAS as IAS.
  advance(240)
  const tas = flight.tas
  advance(4)
  expect(flight.tas).toBe(tas)
  expect(word(unit)).toEqual(bias)
})

// Defensive clock case only: production kernel clocks do not rewind. Two distinct intervals reach the branch;
// Exact physical 1852/3600 expectations elsewhere use precision 5 for the producer's truncated 0.514444 conversion.
test('defensive backwards-clock sampling inside a prior interval rebaselines without a fabricated impulse', () => {
  let now = START
  const unit = new ScriptedFms(() => new Date(now))
  place(unit, 0)
  const flight = new FlightSimulator(unit)
  configure(flight, 0)
  const bias = word(unit)
  now += 1000; flight.step(1)
  now += 1000; flight.step(1)
  now = START + 1500; unit.refreshSensorInput()
  expect(word(unit)).toEqual(bias)
  unit.refreshSensorInput()
  expect(word(unit)).toEqual(bias)
})

for (const side of [1, 2] as const) {
  test(`FMS${side} advancing-clock flight freeze reports only bias and resumes physical acceleration`, () => {
    const { system, kernel } = dual(side)
    const bias = word(system.computers[0])
    kernel.advance(4)
    expect(word(system.computers[0]).northMs2 - bias.northMs2).toBeCloseTo(-2 * 1852 / 3600, 5)
    const position = structuredClone(system.computers[0].truePosition)
    const utc = system.computers[0].now.getTime()
    kernel.advance(4, { flightFreeze: true })
    expect(system.computers[0].now.getTime()).toBe(utc + 1000)
    expect(system.computers[0].truePosition).toEqual(position)
    expect(word(system.computers[0])).toEqual(bias)
    expect(word(system.computers[1])).toEqual(bias)
    kernel.advance(1)
    expect(word(system.computers[0]).northMs2 - bias.northMs2).toBeCloseTo(-2 * 1852 / 3600, 5)
  })
}

test('single IOS reposition rebaselines immediate samples and retains acceleration on the next physical interval', () => {
  const { unit, flight, advance } = single()
  const bias = word(unit)
  advance(4)
  place(unit, 90); flight.adoptAircraftMotion(); configure(flight, 90)
  expect(word(unit)).toEqual(bias)
  unit.refreshSensorInput(); unit.refreshSensorInput()
  expect(word(unit)).toEqual(bias)
  advance(1)
  expect(word(unit).northMs2 - bias.northMs2).toBeCloseTo(0, 5)
  expect(word(unit).eastMs2 - bias.eastMs2).toBeCloseTo(-2 * 1852 / 3600, 5)
})

for (const side of [1, 2] as const) for (const freeze of [false, true]) {
  test(`shared IOS reposition while FMS${side} guides${freeze ? ' after advancing-clock freeze' : ''} reports bias then physical acceleration`, () => {
    const { system, kernel } = dual(side)
    const bias = word(system.computers[0])
    kernel.advance(4)
    if (freeze) kernel.advance(1, { flightFreeze: true })
    system.computers.forEach(unit => place(unit, 90))
    system.flights.forEach(flight => flight.adoptAircraftMotion())
    configure(system.simulator, 90)
    expect(word(system.computers[0])).toEqual(bias)
    expect(word(system.computers[1])).toEqual(bias)
    system.tick(); system.tick()
    expect(word(system.computers[0])).toEqual(bias)
    expect(word(system.computers[1])).toEqual(bias)
    kernel.advance(1)
    expect(word(system.computers[0]).northMs2 - bias.northMs2).toBeCloseTo(0, 5)
    expect(word(system.computers[0]).eastMs2 - bias.eastMs2).toBeCloseTo(-2 * 1852 / 3600, 5)
  })
}
