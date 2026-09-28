import { expect, logicTest as test } from './isolated-client-test'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { GpsPair, alertLimits, gpsInput, lowSatellites, modeLabel, overrideFor } from '../src/fmsCdu/gpsBench'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'

// The bench side of the CMA-5024 simulation (phase 4a): the adapter that feeds the two receivers from the simulated
// aircraft's TRUE state, the integrity limits the GPS card draws, and the override form wiring.
const setup = () => {
  let now = Date.UTC(2026, 8, 28, 14, 0, 0)
  const fms = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(fms)
  const fly = (seconds: number, each?: () => void) => { for (let t = 0; t < seconds; t += 1) { now += 1000; sim.step(1); each?.() } }
  return { fms, sim, fly }
}

test('the receivers are fed the aircraft truth: position, altitude, attitude, velocity and time; baro can be lost', () => {
  const { fms, sim, fly } = setup()
  fly(20)
  fms.setAircraft({ verticalSpeed: -1000, groundSpeed: 120 })
  const input = gpsInput(fms, sim)
  expect(input.position).toEqual(fms.truePosition)
  expect(input.altitude).toBe(fms.altitude)
  expect(input.baroAltitude).toBe(fms.altitude)
  expect(input.time).toBe(fms.now.getTime())
  expect(input).toMatchObject({ track: fms.track, groundSpeed: 120, verticalSpeed: -1000 })
  expect(input.attitude).toMatchObject({ bank: sim.bankAngle, heading: fms.track })
  // Nose down for the descent: atan((1000 / 60 ft/s) / (120 kt × 1.68781 ft/s)) = 4.70°.
  expect(input.attitude.pitch).toBeCloseTo(-4.70, 2)
  expect(gpsInput(fms, sim, { baroLost: true }).baroAltitude).toBeNull()
})

test('two receivers on one constellation, stepped from the bench, power up together and report slightly different fixes', () => {
  const { fms, sim, fly } = setup()
  const pair = new GpsPair()
  pair.step(fms, sim)
  expect(pair.receivers.map(rx => rx.mode)).toEqual(['SELF_TEST', 'SELF_TEST'])
  fly(90, () => pair.step(fms, sim))
  for (const rx of pair.receivers) expect(['NAV', 'SBAS_NAV']).toContain(rx.mode)
  const apart = pair.difference()!
  expect(apart).toBeGreaterThan(0)
  expect(apart).toBeLessThan(20)
  // Baro lost on GPS 2 only: its air data bus flag, not GPS 1's.
  pair.baroLost[1] = true
  pair.step(fms, sim)
  expect(pair.receivers[0].bus()!['355'].value!.buses.airData).toBe(false)
  expect(pair.receivers[1].bus()!['355'].value!.buses.airData).toBe(true)
})

test('the integrity limits follow the phase: 2 NM en route, 1 NM terminal, and on the approach its level', () => {
  expect(alertLimits('EN ROUTE', 'LPV')).toEqual({ halM: 3704, valM: null })
  expect(alertLimits('TERMINAL', 'LPV')).toEqual({ halM: 1852, valM: null })
  expect(alertLimits('APPROACH', 'LPV')).toEqual({ halM: 40, valM: 50 })
  expect(alertLimits('APPROACH', 'LNAV/VNAV')).toEqual({ halM: 556, valM: 50 })
  expect(alertLimits('APPROACH', 'LNAV')).toEqual({ halM: 556, valM: null })
  expect(alertLimits('APPROACH', 'NONE')).toEqual({ halM: 556, valM: null })
})

test('the bus monitor form maps onto receiver overrides, and a bias set on GPS 1 shows on its bus only', () => {
  expect(overrideFor('FORCE', 7)).toEqual({ kind: 'FORCE', value: 7 })
  expect(overrideFor('FORCE', 7, 'NCD')).toEqual({ kind: 'FORCE', value: 7, ssm: 'NCD' })
  expect(overrideFor('FREEZE', 0)).toEqual({ kind: 'FREEZE' })
  expect(overrideFor('BIAS', 5)).toEqual({ kind: 'BIAS', amount: 5 })
  expect(overrideFor('RAMP', 2)).toEqual({ kind: 'RAMP', perSecond: 2 })
  const { fms, sim, fly } = setup()
  const pair = new GpsPair()
  fly(60, () => pair.step(fms, sim))
  pair.receivers[0].override('076', overrideFor('BIAS', 500))
  pair.step(fms, sim)
  const [one, two] = pair.receivers
  expect(one.bus()!['076'].value! - one.rawBus()['076'].value!).toBe(500)
  expect(two.bus()!['076'].value).toBe(two.rawBus()['076'].value)
})

test('"mask low satellites" picks the GPS satellites below 15° and nothing else; modes read as the manual names them', () => {
  const { fms, sim, fly } = setup()
  const pair = new GpsPair()
  fly(60, () => pair.step(fms, sim))
  const bus = pair.receivers[0].bus()!
  const low = lowSatellites(bus)
  const gps = bus['060'].map(w => w.value!).filter(s => !s.sbas)
  expect(low.length).toBeGreaterThan(0)
  expect(low).toEqual(gps.filter(s => s.elevation < 15).map(s => s.prn))
  // Below 30° both geostationary satellites are too (about 15° and 23° here), and they are still left alone.
  const geos = bus['060'].map(w => w.value!).filter(s => s.sbas)
  expect(geos.length).toBe(2)
  expect(geos.every(s => s.elevation < 30)).toBe(true)
  expect(lowSatellites(bus, 30)).toEqual(gps.filter(s => s.elevation < 30).map(s => s.prn))
  expect(modeLabel('SBAS_PA')).toBe('SBAS PA')
  expect(modeLabel('ALT_AIDING')).toBe('ALT AIDING')
  expect(modeLabel('SELF_TEST')).toBe('SELF TEST')
})
