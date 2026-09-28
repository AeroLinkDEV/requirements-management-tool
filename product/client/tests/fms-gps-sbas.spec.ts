import { expect, logicTest as test } from './isolated-client-test'
import { offset, type LatLon } from '../src/fmsCdu/fmsModel'
import { Constellation } from '../src/fmsCdu/gnss'
import { GpsReceiver, crc32q, fasCrc, type FasDataBlock, type GpsBus, type GpsInput } from '../src/fmsCdu/gps'

// CMA-5024 phase 2: SBAS NAV and SBAS PA, the FAS data block from the FMS, and the approach guidance the GPS computes
// from it (116/117 rectilinear deviations, 201 distance to threshold) with the approach level it can support.
const T0 = Date.UTC(2026, 8, 28, 14, 0, 0)
// CYUL runway 24R's threshold (the landing threshold point, LTP) and a flight path alignment point 1.81 NM (11 000 ft)
// beyond it on the landing course, 237.
const LTP = { lat: 45.4790, lon: -73.7180 }
const FPAP = offset(LTP, 237, 11000 / 6076.12)
// The LTP is at 118 ft MSL; with the receiver's geoid separation of -32 m its ellipsoid height is 35.97 - 32 m.
const LTP_HAE_M = 118 * 0.3048 - 32
const GEOID_M = -32
const baseFas = (): Omit<FasDataBlock, 'crc'> => ({
  operationType: 0, sbasProvider: 0, airport: 'CYUL', runway: 24, designator: 'R', performance: 0, routeIndicator: ' ',
  referencePathSelector: 0, referencePathId: 'W24A', ltp: { ...LTP, heightM: LTP_HAE_M },
  fpapDelta: { lat: FPAP.lat - LTP.lat, lon: FPAP.lon - LTP.lon }, tchFt: 50, gpaDeg: 3, courseWidthM: 105, lengthOffsetM: 0,
  halM: 40, valM: 50,
})
const fas = (changes: Partial<Omit<FasDataBlock, 'crc'>> = {}): FasDataBlock => { const block = { ...baseFas(), ...changes }; return { ...block, crc: fasCrc(block) } }

/** On the 3° path, `nm` before the threshold: MSL altitude (ft) where the path is, from TCH 50 ft above the LTP. */
const pathMslFt = (nm: number) => (LTP_HAE_M + 50 * 0.3048 + nm * 1852 * Math.tan((3 * Math.PI) / 180) - GEOID_M) / 0.3048
const onFinal = (nm: number): LatLon => offset(LTP, 57, nm)

const input = (t: number, extra: Partial<GpsInput> = {}): GpsInput => ({
  time: T0 + t * 1000, position: onFinal(5), altitude: pathMslFt(5), baroAltitude: pathMslFt(5), track: 237, groundSpeed: 120,
  verticalSpeed: -630, attitude: { bank: 0, pitch: 0, heading: 237 }, ...extra,
})
const receiver = (sbas = true) => new GpsReceiver({ constellation: new Constellation(7), ttffSeconds: 45, sbas })
const run = (rx: GpsReceiver, from: number, to: number, extra: Partial<GpsInput> = {}) => { for (let t = from; t <= to; t += 1) rx.step(input(t, extra)) }
const bus = (rx: GpsReceiver) => rx.bus()!
const errorM = (b: GpsBus, at: LatLon) => Math.hypot((b['110'].value! + b['120'].value! - at.lat) * 111_120, (b['111'].value! + b['121'].value! - at.lon) * 111_120 * Math.cos((at.lat * Math.PI) / 180))

test('the FAS CRC is CRC-32Q: its published check value, and any changed field changes it', () => {
  expect(crc32q(new TextEncoder().encode('123456789'))).toBe(0x3010bf7f)
  expect(fasCrc(baseFas())).not.toBe(fasCrc({ ...baseFas(), gpaDeg: 3.1 }))
})

test('SBAS NAV once a geostationary satellite has been tracked long enough; NAV while it is out', () => {
  const rx = receiver()
  run(rx, 0, 44)
  expect(rx.mode).toBe('ACQUISITION')
  run(rx, 45, 45)
  // The two geostationary satellites were tracked from the end of initialization (12 s): corrections are in by 42 s.
  expect(rx.mode).toBe('SBAS_NAV')
  const geos = bus(rx)['060'].filter(w => w.value!.sbas).map(w => w.value!)
  expect(geos.map(g => g.prn)).toEqual([131, 133])
  expect(geos.every(g => g.tracked && g.cn0 > 30)).toBe(true)
  rx.setSbas({ outage: [131, 133] })
  run(rx, 46, 46)
  expect(rx.mode).toBe('NAV')
  // Back in view from 47 s: the corrections are in 30 s later.
  rx.setSbas({ outage: [] })
  run(rx, 47, 76)
  expect(rx.mode).toBe('NAV')
  run(rx, 77, 77)
  expect(rx.mode).toBe('SBAS_NAV')
  // One geostationary satellite is enough.
  rx.setSbas({ outage: [131] })
  run(rx, 78, 78)
  expect(rx.mode).toBe('SBAS_NAV')
})

test('SBAS corrections shrink the fix error, the figure of merit and the protection level', () => {
  const gps = receiver(false), sbas = receiver()
  let gpsWorst = 0, sbasWorst = 0
  for (let t = 0; t <= 600; t += 1) {
    gps.step(input(t)); sbas.step(input(t))
    if (t >= 60) { gpsWorst = Math.max(gpsWorst, errorM(bus(gps), onFinal(5))); sbasWorst = Math.max(sbasWorst, errorM(bus(sbas), onFinal(5))) }
  }
  expect(sbas.mode).toBe('SBAS_NAV')
  expect(sbasWorst).toBeLessThan(gpsWorst / 2)
  expect(bus(sbas)['247'].value!).toBeLessThan(bus(gps)['247'].value!)
  expect(bus(sbas)['130'].value!).toBeLessThan(bus(gps)['130'].value!)
})

test('SBAS "do not use" returns the receiver to NAV at once, on RAIM integrity', () => {
  const gps = receiver(false), rx = receiver()
  run(gps, 0, 60); run(rx, 0, 60)
  expect(rx.mode).toBe('SBAS_NAV')
  rx.setSbas({ doNotUse: true })
  run(gps, 61, 61); run(rx, 61, 61)
  expect(rx.mode).toBe('NAV')
  // The same satellites, the same errors, the same RAIM: the same protection level as a receiver without SBAS.
  expect(bus(rx)['130'].value).toBeCloseTo(bus(gps)['130'].value!, 9)
  expect(bus(rx)['305'].value).toMatchObject({ paActive: false })
})

test('label 156 reports the approach selection: available, CRC invalid, mismatch, incomplete and parked', () => {
  const rx = receiver()
  run(rx, 0, 60)
  expect(bus(rx)['156'].value).toMatchObject({ selected: false, available: false })
  rx.selectApproach({ id: 'W24A', fas: fas() })
  run(rx, 61, 61)
  expect(bus(rx)['156']).toEqual({ ssm: 'NORMAL', value: { armed: false, selected: true, available: true, crcInvalid: false, mismatch: false, incomplete: false, parked: false } })
  expect(rx.mode).toBe('SBAS_PA')

  // A field changed after the CRC was computed: refused.
  rx.selectApproach({ id: 'W24A', fas: { ...fas(), gpaDeg: 3.5 } })
  run(rx, 62, 62)
  expect(bus(rx)['156'].value).toMatchObject({ available: false, crcInvalid: true })
  expect(rx.mode).toBe('SBAS_NAV')
  expect(bus(rx)['116'].ssm).toBe('NCD')

  // The FMS selected one approach and sent another's block.
  rx.selectApproach({ id: 'W24B', fas: fas() })
  run(rx, 63, 63)
  expect(bus(rx)['156'].value).toMatchObject({ available: false, mismatch: true })

  // Selected, no block yet.
  rx.selectApproach({ id: 'W24A', fas: null })
  run(rx, 64, 64)
  expect(bus(rx)['156'].value).toMatchObject({ available: false, incomplete: true })

  // Parked: valid but not active, so no deviations.
  rx.selectApproach({ id: 'W24A', fas: fas(), parked: true })
  run(rx, 65, 65)
  expect(bus(rx)['156'].value).toMatchObject({ available: true, parked: true })
  expect(bus(rx)['116'].ssm).toBe('NCD')
  expect(bus(rx)['117'].ssm).toBe('NCD')
  expect(rx.mode).toBe('SBAS_NAV')
})

test('the GPS computes the rectilinear deviations and the distance to the threshold from the FAS and its own fix', () => {
  const rx = receiver()
  run(rx, 0, 60)
  expect(bus(rx)['116'].ssm).toBe('NCD')
  expect(bus(rx)['201'].ssm).toBe('NCD')
  rx.selectApproach({ id: 'W24A', fas: fas() })
  run(rx, 61, 70)
  let b = bus(rx)
  expect(rx.mode).toBe('SBAS_PA')
  expect(b['305'].value).toMatchObject({ paActive: true, provider: 'WAAS', level: 'LPV' })
  // On the centreline and on the path, 5 NM out: deviations within the SBAS fix error.
  expect(Math.abs(b['116'].value!)).toBeLessThan(15)
  expect(Math.abs(b['117'].value!)).toBeLessThan(15)
  expect(b['201'].value!).toBeCloseTo(5, 2)
  // 500 ft right of the centreline (right of the landing course 237 is toward 327), and 200 ft above the path.
  run(rx, 71, 72, { position: offset(onFinal(5), 327, 500 / 6076.12), altitude: pathMslFt(5) + 200 })
  b = bus(rx)
  expect(Math.abs(b['116'].value! - 500)).toBeLessThan(15)
  expect(Math.abs(b['117'].value! - 200)).toBeLessThan(15)
})

test('the approach level follows the protection levels: LPV, LNAV/VNAV, LNAV, or none', () => {
  const rx = receiver()
  run(rx, 0, 60)
  // A FAS alert limit tighter than the SBAS HPL: no LPV, but LNAV/VNAV (HPL within 556 m, VPL within 50 m).
  rx.selectApproach({ id: 'W24A', fas: fas({ halM: 2 }) })
  run(rx, 61, 62)
  expect(bus(rx)['305'].value).toMatchObject({ level: 'LNAV/VNAV' })
  expect(bus(rx)['117'].ssm).toBe('NORMAL')

  // An ionospheric storm inflates the vertical protection level past 50 m: lateral guidance only.
  rx.selectApproach({ id: 'W24A', fas: fas() })
  rx.setSbas({ ionoStorm: 20 })
  run(rx, 63, 64)
  expect(bus(rx)['133'].value! * 0.3048).toBeGreaterThan(50)
  expect(bus(rx)['305'].value).toMatchObject({ level: 'LNAV' })
  expect(bus(rx)['116'].ssm).toBe('NORMAL')
  expect(bus(rx)['117'].ssm).toBe('FW')

  // Storm over, SBAS lost: NAV on RAIM supports LNAV only.
  rx.setSbas({ ionoStorm: 1, doNotUse: true })
  run(rx, 65, 66)
  expect(rx.mode).toBe('NAV')
  expect(bus(rx)['305'].value).toMatchObject({ level: 'LNAV', paActive: false })
  expect(bus(rx)['117'].ssm).toBe('FW')

  // A satellite fault detected but not excluded: no guidance at all.
  rx.setSbas({ doNotUse: false })
  const all = bus(rx)['060'].filter(w => w.value!.used).map(w => w.value!.prn)
  rx.deselect(all.slice(5))
  rx.satelliteFault(all[0], { kind: 'RAMP', metresPerSecond: 2 })
  run(rx, 67, 200)
  expect(bus(rx)['273'].value!.integrity).toBe('DETECTED')
  expect(bus(rx)['305'].value).toMatchObject({ level: 'NONE' })
  expect(bus(rx)['116'].ssm).toBe('FW')
  expect(bus(rx)['117'].ssm).toBe('FW')
})

// Phase 3b groundwork: typed status overrides, the approach region, and the deviation scaling.
const at = (nm: number): Partial<GpsInput> => ({ position: onFinal(nm), altitude: pathMslFt(nm), baroAltitude: pathMslFt(nm) })

test('a status word can be forced field by field, typed and validated; an invalid patch is refused and changes nothing', () => {
  const rx = receiver()
  run(rx, 0, 60)
  const raw273 = rx.rawBus()['273'].value!
  expect(rx.overrideStatus('273', { mode: 'FAULT', used: 0 })).toBe(true)
  expect(bus(rx)['273']).toEqual({ ssm: 'NORMAL', value: { ...raw273, mode: 'FAULT', used: 0 } })
  expect(rx.rawBus()['273'].value!.mode).toBe('SBAS_NAV')
  // Wrong enum, wrong type, unknown field: refused whole.
  expect(rx.overrideStatus('273', { mode: 'BOGUS' } as never)).toBe(false)
  expect(rx.overrideStatus('273', { used: 'x' } as never)).toBe(false)
  expect(rx.overrideStatus('273', { used: 3, colour: 1 } as never)).toBe(false)
  expect(bus(rx)['273'].value).toMatchObject({ mode: 'FAULT', used: 0 })
  // Nested flags in 355, and the approach words.
  expect(rx.overrideStatus('355', { buses: { dme: true } })).toBe(true)
  expect(bus(rx)['355'].value).toEqual({ ...rx.rawBus()['355'].value!, buses: { ...rx.rawBus()['355'].value!.buses, dme: true } })
  expect(rx.overrideStatus('156', { crcInvalid: true })).toBe(true)
  expect(bus(rx)['156'].value!.crcInvalid).toBe(true)
  expect(rx.overrideStatus('305', { level: 'LPV' })).toBe(true)
  expect(rx.overrideStatus('305', { level: 'ILS' } as never)).toBe(false)
  expect(bus(rx)['305'].value!.level).toBe('LPV')
  rx.overrideStatus('273', null)
  expect(bus(rx)['273']).toEqual(rx.rawBus()['273'])
})

test('SBAS PA only inside the approach region: outside it the approach is armed in SBAS NAV, with no deviations', () => {
  const rx = receiver()
  run(rx, 0, 60, at(40))
  rx.selectApproach({ id: 'W24A', fas: fas() })
  run(rx, 61, 61, at(40))
  expect(rx.mode).toBe('SBAS_NAV')
  expect(bus(rx)['156'].value).toMatchObject({ available: true, armed: true })
  expect(bus(rx)['116'].ssm).toBe('NCD')
  // 30 NM from the threshold is the region's edge (a laboratory value).
  run(rx, 62, 62, at(29))
  expect(rx.mode).toBe('SBAS_PA')
  expect(bus(rx)['156'].value).toMatchObject({ available: true, armed: false })
  expect(bus(rx)['116'].ssm).toBe('NORMAL')
})

test('the deviation scaling: lateral full scale splays from the course width, vertical is a quarter of the path angle, bounded', () => {
  const rx = receiver()
  run(rx, 0, 60, at(5))
  rx.selectApproach({ id: 'W24A', fas: fas() })
  run(rx, 61, 61, at(5))
  // The GARP is 305 m beyond the FPAP: 3352.8 + 305 = 3657.8 m from the LTP. 5 NM out the aircraft is 12 917.8 m from
  // it, so the 105 m course width at the threshold has splayed to 105 × 12917.8 / 3657.8 = 370.8 m (1216.6 ft), an
  // angle of atan(105 / 3657.8) = 1.644°. Vertically, the path origin is TCH / tan GPA = 290.8 m beyond the LTP, so
  // 9550.8 m away; ±0.75° there is 125.0 m (410.2 ft).
  let scale = bus(rx).scale
  expect(scale.ssm).toBe('NORMAL')
  expect(scale.value!.lateralFullScaleFt).toBeCloseTo(1216.6, 0)
  expect(scale.value!.lateralAngleDeg).toBeCloseTo(1.644, 2)
  expect(scale.value!.verticalFullScaleFt).toBeCloseTo(410.2, 0)
  expect(scale.value!.verticalAngleDeg).toBeCloseTo(0.75, 6)
  // Close in, the vertical full scale stops at 15 m (49.2 ft); far out, at 150 m (492.1 ft).
  run(rx, 62, 62, at(0.3))
  expect(bus(rx).scale.value!.verticalFullScaleFt).toBeCloseTo(49.2, 0)
  run(rx, 63, 63, at(10))
  expect(bus(rx).scale.value!.verticalFullScaleFt).toBeCloseTo(492.1, 0)
  // No active approach, no scale.
  rx.selectApproach(null)
  run(rx, 64, 64, at(10))
  scale = bus(rx).scale
  expect(scale).toEqual({ value: null, ssm: 'NCD' })
})
