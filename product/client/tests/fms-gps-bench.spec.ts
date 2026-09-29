import { expect, logicTest as test } from './isolated-client-test'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { alertLimits, fmsGpsView, lowSatellites, modeLabel, overrideFor } from '../src/fmsCdu/gpsBench'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'

// The bench side of the CMA-5024 simulation, joined to the FMS: the FMS owns and feeds GPS1 and GPS2, the GPS sensors
// tab reads them through a view, and every bench change is followed by the FMS re-reading them.
const setup = () => {
  let now = Date.UTC(2026, 8, 28, 14, 0, 0)
  const fms = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(fms)
  const fly = (seconds: number, each?: () => boolean | void) => { for (let t = 0; t < seconds; t += 1) { now += 1000; sim.step(1); if (each?.()) return } }
  return { fms, sim, fly }
}
const visible = (fms: ScriptedFms, index: number) => fms.gps[index].bus()!['273'].value!.visible

test('the FMS feeds its receivers the aircraft attitude: a steep bank hides satellites on the high wing', () => {
  const { fms } = setup()
  fms.gpsUpdated()
  const level = visible(fms, 0)
  fms.setAircraft({ bank: 60 })
  fms.gpsUpdated()
  expect(visible(fms, 0)).toBeLessThan(level)
  fms.setAircraft({ bank: 0 })
  fms.gpsUpdated()
  expect(visible(fms, 0)).toBe(level)
})

test('the flight simulation reports its bank and flight-path pitch to the FMS', () => {
  const { fms, sim, fly } = setup()
  // Fly until the aircraft is banked in a turn and climbing or descending.
  fly(3 * 3600, () => Math.abs(sim.bankAngle) > 5 && Math.abs(fms.verticalSpeed) > 100)
  expect(Math.abs(sim.bankAngle)).toBeGreaterThan(5)
  expect(fms.attitude.bank).toBe(sim.bankAngle)
  // Flight-path pitch: atan of the vertical speed (ft/s) over the ground speed (1 kt = 1.68781 ft/s).
  const pitch = (Math.atan(fms.verticalSpeed / 60 / (fms.groundSpeed * 1.68781)) * 180) / Math.PI
  expect(fms.attitude.pitch).not.toBe(0)
  expect(fms.attitude.pitch).toBeCloseTo(pitch, 9)
})

test('baro altitude can be lost on one receiver: its air data flag, not the other one\'s', () => {
  const { fms } = setup()
  const view = fmsGpsView(fms)
  view.setBaroLost(1, true)
  expect(fms.gps[0].bus()!['355'].value!.buses.airData).toBe(false)
  expect(fms.gps[1].bus()!['355'].value!.buses.airData).toBe(true)
  view.setBaroLost(1, false)
  expect(fms.gps[1].bus()!['355'].value!.buses.airData).toBe(false)
})

test('the view reads the FMS receivers: their position difference, and a fault reaches the FMS choice at once', () => {
  const { fms, fly } = setup()
  fly(30)
  const view = fmsGpsView(fms)
  expect(view.receivers).toBe(fms.gps)
  const apart = view.difference()!
  expect(apart).toBeGreaterThan(0)
  expect(apart).toBeLessThan(20)
  expect(fms.gpsStatus.chosen).toBe(0)
  view.receivers[0].injectFault('RECEIVER', true)
  view.updated()
  // No new step of time: the FMS re-read the buses and moved to GPS2.
  expect(fms.gpsStatus.chosen).toBe(1)
  expect(view.difference()).toBeNull()
  expect(fms.navSourceLog[0].source).toBe('GPS2')
  // And the other way round: without GPS 2 there is no difference either.
  view.receivers[0].injectFault('RECEIVER', false)
  view.receivers[1].injectFault('RECEIVER', true)
  fly(20)
  expect(fms.gpsStatus.chosen).toBe(0)
  expect(view.difference()).toBeNull()
})

test('the view selects the receiver the FMS navigates on, and says when the integrity condition holds the satellites', () => {
  const { fms } = setup()
  const view = fmsGpsView(fms)
  expect(view.choice).toBe('AUTO')
  view.select('GPS2')
  expect(fms.gpsReceiverChoice).toBe('GPS2')
  expect(fms.gpsStatus.chosen).toBe(1)
  expect(fmsGpsView(fms).choice).toBe('GPS2')
  view.select('OFF')
  expect(fmsGpsView(fms).choice).toBe('OFF')
  expect(fms.gpsStatus.chosen).toBeNull()
  view.select('AUTO')
  expect(fmsGpsView(fms).integrityHeld).toBe(false)
  fms.setCondition('gpsIntegrity', true)
  expect(fmsGpsView(fms).integrityHeld).toBe(true)
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
  const { fms, fly } = setup()
  fly(10)
  const [one, two] = fmsGpsView(fms).receivers
  one.override('076', overrideFor('BIAS', 500))
  fly(1)
  expect(one.bus()!['076'].value! - one.rawBus()['076'].value!).toBe(500)
  expect(two.bus()!['076'].value).toBe(two.rawBus()['076'].value)
})

test('"mask low satellites" picks the GPS satellites below 15° and nothing else; modes read as the manual names them', () => {
  const { fms, fly } = setup()
  fly(10)
  const bus = fms.gps[0].bus()!
  const low = lowSatellites(bus)
  const gps = bus['060'].map(w => w.value!).filter(s => !s.sbas)
  expect(low.length).toBeGreaterThan(0)
  expect(low).toEqual(gps.filter(s => s.elevation < 15).map(s => s.prn))
  // Below 30° both geostationary satellites are too, and they are still left alone.
  const geos = bus['060'].map(w => w.value!).filter(s => s.sbas)
  expect(geos.length).toBe(2)
  expect(geos.every(s => s.elevation < 30)).toBe(true)
  expect(lowSatellites(bus, 30)).toEqual(gps.filter(s => s.elevation < 30).map(s => s.prn))
  expect(modeLabel('SBAS_PA')).toBe('SBAS PA')
  expect(modeLabel('ALT_AIDING')).toBe('ALT AIDING')
  expect(modeLabel('SELF_TEST')).toBe('SELF TEST')
})
