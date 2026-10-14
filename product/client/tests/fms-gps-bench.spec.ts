import { expect, logicTest as test } from './isolated-client-test'
import { FlightSimulator, trimPitch } from '../src/fmsCdu/flight'
import { LAB_AIRLINE_VNAV_PROFILE, type AircraftProfile } from '../src/fmsCdu/profile'
import { alertLimits, fmsGpsView, lowSatellites, modeLabel, overrideFor } from '../src/fmsCdu/gpsBench'
import { stimulusFor } from '../src/fmsCdu/gpsStimulus'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'

// The bench side of the CMA-5024 simulation, joined to the FMS: the FMS owns and feeds GPS1 and GPS2, the GPS sensors
// tab reads them through a view, and every bench change is followed by the FMS re-reading them.
const setup = (profile?: AircraftProfile) => {
  let now = Date.UTC(2026, 8, 28, 14, 0, 0)
  const fms = new ScriptedFms(() => new Date(now), { profile })
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

test('the flight simulation reports its modelled attitude to the FMS: the trim pitch for the airspeed and the bank of the turn, not the climb (B1.5)', () => {
  const { fms, sim, fly } = setup()
  // Climbing in VS (the crew's vertical mode under the helicopter profile), fly until also banked in a steady turn.
  sim.selectAltitude(9000)
  sim.engageVerticalSpeed(500)
  fly(3 * 3600, () => Math.abs(sim.bankAngle) > 5 && Math.abs(fms.verticalSpeed) > 400)
  fly(3)
  expect(Math.abs(sim.bankAngle)).toBeGreaterThan(5)
  // The bank of the coordinated turn, followed at the attitude rate; the pitch the trim for the airspeed: a helicopter
  // climbs on its collective, not by raising its nose.
  expect(Math.abs(fms.attitude.bank - sim.bankAngle)).toBeLessThan(1)
  expect(Math.abs(fms.attitude.pitch - trimPitch(sim.tas))).toBeLessThan(0.3)
  // The laboratory airline profile keeps the air-relative flight-path angle.
  const lab = setup(LAB_AIRLINE_VNAV_PROFILE)
  lab.fly(3 * 3600, () => Math.abs(lab.fms.verticalSpeed) > 100)
  const pitch = Math.max(-20, Math.min(20, (Math.atan(lab.fms.verticalSpeed / 60 / (Math.max(lab.sim.tas, 30) * 1.68781)) * 180) / Math.PI))
  expect(lab.fms.attitude.pitch).toBeCloseTo(pitch, 9)
  expect(lab.fms.attitude.bank).toBe(lab.sim.bankAngle)
})

test('baro altitude can be lost on one receiver: its air data flag, not the other one\'s', () => {
  const { fms } = setup()
  const stimulus = stimulusFor(fms)
  stimulus.apply(1, { op: 'baroLost', on: true })
  expect(fms.gps[0].bus()!['355'].value!.buses.airData).toBe(false)
  expect(fms.gps[1].bus()!['355'].value!.buses.airData).toBe(true)
  stimulus.apply(1, { op: 'baroLost', on: false })
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
  fms.gps[0].injectFault('RECEIVER', true)
  fms.gpsUpdated()
  // No new step of time: the FMS re-read the buses and moved to GPS2.
  expect(fms.gpsStatus.chosen).toBe(1)
  expect(view.difference()).toBeNull()
  expect(fms.navSourceLog[0].source).toBe('GPS2')
  // And the other way round: without GPS 2 there is no difference either.
  fms.gps[0].injectFault('RECEIVER', false)
  fms.gps[1].injectFault('RECEIVER', true)
  fly(20)
  expect(fms.gpsStatus.chosen).toBe(0)
  expect(view.difference()).toBeNull()
})

test('the view shows the receiver selection the FMS navigates on, and says when the integrity condition holds the satellites', () => {
  const { fms } = setup()
  const view = fmsGpsView(fms)
  expect(view.choice).toBe('AUTO')
  fms.selectGpsReceiver('GPS2')
  expect(fms.gpsReceiverChoice).toBe('GPS2')
  expect(fms.gpsStatus.chosen).toBe(1)
  expect(fmsGpsView(fms).choice).toBe('GPS2')
  fms.selectGpsReceiver('OFF')
  expect(fmsGpsView(fms).choice).toBe('OFF')
  expect(fms.gpsStatus.chosen).toBeNull()
  fms.selectGpsReceiver('AUTO')
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
