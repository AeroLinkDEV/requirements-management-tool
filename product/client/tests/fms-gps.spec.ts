import { expect, logicTest as test } from './isolated-client-test'
import { distanceNm } from '../src/fmsCdu/fmsModel'
import { Constellation } from '../src/fmsCdu/gnss'
import { GpsReceiver, type GpsBus, type GpsInput } from '../src/fmsCdu/gps'

// A simulated CMA-5024 GPS sensor (phase 1: the constellation and one receiver, no SBAS and no FMS wiring). Expected
// values come from the installation manual's behaviour as the design records it: 10 s of self-test, NAV with four
// satellites, altitude aiding with three and baro, FDE with enough redundancy, and the word status rules.
const T0 = Date.UTC(2026, 8, 28, 14, 0, 0)
const AT = { lat: 45.31, lon: -75.68 }
const input = (t: number, extra: Partial<GpsInput> = {}): GpsInput => ({
  time: T0 + t * 1000, position: AT, altitude: 3000, baroAltitude: 3000, track: 90, groundSpeed: 120, verticalSpeed: 0,
  attitude: { bank: 0, pitch: 0, heading: 90 }, ...extra,
})
// GPS only: these prove the receiver's own RAIM, FDE and modes; SBAS is proved in fms-gps-sbas.spec.ts.
const receiver = () => new GpsReceiver({ constellation: new Constellation(7), ttffSeconds: 45, sbas: false })
/** Steps once a second from `from` to `to` seconds after power-up, inclusive. */
const run = (rx: GpsReceiver, from: number, to: number, extra: Partial<GpsInput> = {}) => { for (let t = from; t <= to; t += 1) rx.step(input(t, extra)) }
const bus = (rx: GpsReceiver) => rx.bus()!
const used = (b: GpsBus) => b['060'].filter(w => w.value?.used).map(w => w.value!.prn)
const latitude = (b: GpsBus) => b['110'].value! + b['120'].value!
const longitude = (b: GpsBus) => b['111'].value! + b['121'].value!
const errorMetres = (b: GpsBus) => distanceNm(AT, { lat: latitude(b), lon: longitude(b) }) * 1852
/** A receiver in NAV, with only the given number of satellites left in use (the rest deselected). */
const navWith = (count: number) => {
  const rx = receiver()
  run(rx, 0, 60)
  const all = used(bus(rx))
  rx.deselect(all.slice(count))
  run(rx, 61, 62)
  return { rx, kept: all.slice(0, count), all }
}

test('from power-up: 10 s of self-test, initialization, acquisition, then NAV at the time to first fix', () => {
  const rx = receiver()
  const modes: Record<number, string> = {}
  for (let t = 0; t <= 50; t += 1) { rx.step(input(t)); modes[t] = rx.mode }
  expect(modes[0]).toBe('SELF_TEST')
  expect(modes[9]).toBe('SELF_TEST')
  expect(modes[10]).toBe('INITIALIZATION')
  expect(modes[11]).toBe('INITIALIZATION')
  expect(modes[12]).toBe('ACQUISITION')
  expect(modes[44]).toBe('ACQUISITION')
  expect(modes[45]).toBe('NAV')
  expect(bus(rx)['273'].value).toMatchObject({ mode: 'NAV' })
  expect(bus(rx)['273'].value!.used).toBeGreaterThanOrEqual(6)
})

test('the antenna mask tilts with the wings: a steep bank hides satellites on the high side', () => {
  const sky = new Constellation(7)
  const level = sky.sky(T0, AT, 3000, { bank: 0, pitch: 0, heading: 90 }).filter(s => s.visible).length
  const banked = sky.sky(T0, AT, 3000, { bank: 60, pitch: 0, heading: 90 }).filter(s => s.visible).length
  expect(level).toBeGreaterThanOrEqual(6)
  expect(banked).toBeLessThan(level)
  // The same satellites at the same time and place: the constellation is deterministic for its seed.
  expect(new Constellation(7).sky(T0, AT, 3000, { bank: 0, pitch: 0, heading: 90 })).toEqual(sky.sky(T0, AT, 3000, { bank: 0, pitch: 0, heading: 90 }))
})

test('masking satellites uses fewer, raises DOP and HPL, and steps NAV to altitude aiding to acquisition', () => {
  const rx = receiver()
  run(rx, 0, 60)
  const full = bus(rx)
  const all = used(full)
  expect(all.length).toBeGreaterThanOrEqual(7)

  rx.deselect(all.slice(5))
  run(rx, 61, 62)
  const five = bus(rx)
  expect(used(five)).toEqual(all.slice(0, 5))
  expect(rx.mode).toBe('NAV')
  expect(five['101'].value!).toBeGreaterThan(full['101'].value!)
  expect(five['130'].value!).toBeGreaterThan(full['130'].value!)

  // Four: a fix, but no redundancy for RAIM, so no integrity limit.
  rx.deselect(all.slice(4))
  run(rx, 63, 64)
  expect(rx.mode).toBe('NAV')
  expect(bus(rx)['130'].ssm).toBe('NCD')

  // Three and baro: altitude aiding.
  rx.deselect(all.slice(3))
  run(rx, 65, 66)
  expect(rx.mode).toBe('ALT_AIDING')
  expect(bus(rx)['110'].ssm).toBe('NORMAL')

  // Three without baro, then two: no fix.
  run(rx, 67, 68, { baroAltitude: null })
  expect(rx.mode).toBe('ACQUISITION')
  rx.deselect(all.slice(2))
  run(rx, 69, 70)
  expect(rx.mode).toBe('ACQUISITION')
  expect(bus(rx)['110'].ssm).toBe('NCD')

  // Back to all of them: NAV again at once, with no new time to first fix.
  rx.deselect([])
  run(rx, 71, 72)
  expect(rx.mode).toBe('NAV')
})

test('with enough redundancy FDE excludes a ramping satellite and navigation continues accurately', () => {
  const rx = receiver()
  run(rx, 0, 60)
  const all = used(bus(rx))
  expect(all.length).toBeGreaterThanOrEqual(6)
  const faulty = all[0]
  rx.satelliteFault(faulty, { kind: 'RAMP', metresPerSecond: 2 })
  // In the very step it is excluded, the satellite is no longer counted as used, in 060 or in 273.
  let t = 61
  for (; t <= 180 && !bus(rx)['060'].some(w => w.value!.excluded); t += 1) rx.step(input(t))
  expect(bus(rx)['060'].find(w => w.value!.prn === faulty)!.value).toMatchObject({ excluded: true, used: false })
  expect(bus(rx)['273'].value!.used).toBe(all.length - 1)
  run(rx, t, 180)
  const b = bus(rx)
  expect(b['060'].find(w => w.value!.prn === faulty)!.value).toMatchObject({ excluded: true, used: false })
  expect(used(b)).toEqual(all.slice(1))
  expect(rx.mode).toBe('NAV')
  expect(b['273'].value!.integrity).toBe('OK')
  expect(b['130'].ssm).toBe('NORMAL')
  // 240 m of range error on that satellite by now, yet the fix is within the nominal error.
  expect(errorMetres(b)).toBeLessThan(15)
})

test('without enough redundancy the ramp is detected but not excluded, and integrity is lost', () => {
  const { rx, kept } = navWith(5)
  rx.satelliteFault(kept[0], { kind: 'RAMP', metresPerSecond: 2 })
  run(rx, 63, 180)
  const b = bus(rx)
  expect(b['060'].some(w => w.value!.excluded)).toBe(false)
  expect(b['273'].value!.integrity).toBe('DETECTED')
  expect(b['130'].ssm).toBe('FW')
  expect(b['133'].ssm).toBe('FW')
})

test('a bias below the detection threshold is not detected: the fix moves while integrity still reads fine', () => {
  const control = receiver(), biased = receiver()
  run(control, 0, 60)
  run(biased, 0, 60)
  const faulty = used(bus(biased))[0]
  biased.satelliteFault(faulty, { kind: 'STEP', metres: 4 })
  run(control, 61, 90)
  run(biased, 61, 90)
  expect(bus(biased)['273'].value!.integrity).toBe('OK')
  expect(bus(biased)['060'].some(w => w.value!.excluded)).toBe(false)
  expect(bus(biased)['130'].ssm).toBe('NORMAL')
  const moved = distanceNm({ lat: latitude(bus(control)), lon: longitude(bus(control)) }, { lat: latitude(bus(biased)), lon: longitude(bus(biased)) }) * 1852
  expect(moved).toBeGreaterThan(0.5)
})

test('word status follows the mode: functional test, no computed data, normal, and failure warning', () => {
  const rx = receiver()
  rx.step(input(0))
  let b = bus(rx)
  expect([b['110'].ssm, b['130'].ssm, b['150'].ssm, b['273'].ssm, b['355'].ssm]).toEqual(['FT', 'FT', 'FT', 'NORMAL', 'NORMAL'])
  run(rx, 1, 20)
  b = bus(rx)
  expect(rx.mode).toBe('ACQUISITION')
  expect([b['110'].ssm, b['076'].ssm, b['130'].ssm, b['101'].ssm, b['273'].ssm]).toEqual(['NCD', 'NCD', 'NCD', 'NCD', 'NORMAL'])
  expect(b['110'].value).toBeNull()
  run(rx, 21, 60)
  b = bus(rx)
  for (const label of ['110', '120', '111', '121', '076', '370', '103', '112', '165', '166', '174', '101', '102', '130', '133', '247', '136', '150', '260', '273', '355'] as const)
    expect(b[label].ssm, label).toBe('NORMAL')
  // MSL altitude carries the fix's vertical error; the velocity words are the truth (Doppler noise is not modelled).
  expect(Math.abs(b['076'].value! - 3000)).toBeLessThan(50)
  expect(b['112'].value).toBe(120)
  expect(b['103'].value).toBe(90)
  expect(b['150'].value).toEqual({ hours: 14, minutes: 1, seconds: 0 })
  expect(b['260'].value).toEqual({ day: 28, month: 9, year: 2026 })
  rx.injectFault('RECEIVER', true)
  run(rx, 61, 61)
  b = bus(rx)
  expect([b['110'].ssm, b['076'].ssm, b['130'].ssm, b['150'].ssm, b['273'].ssm, b['355'].ssm]).toEqual(['FW', 'FW', 'FW', 'FW', 'NORMAL', 'NORMAL'])
})

test('an override forces, freezes, biases or ramps a word, and clearing it restores the receiver output', () => {
  const rx = receiver()
  run(rx, 0, 60)
  rx.override('110', { kind: 'FORCE', value: 10, ssm: 'NCD' })
  rx.override('076', { kind: 'BIAS', amount: 500 })
  rx.override('112', { kind: 'FREEZE' })
  rx.override('103', { kind: 'RAMP', perSecond: 1 })
  run(rx, 61, 61)
  expect(bus(rx)['110']).toEqual({ value: 10, ssm: 'NCD' })
  expect(bus(rx)['076']).toEqual({ value: rx.rawBus()['076'].value! + 500, ssm: 'NORMAL' })
  run(rx, 62, 71, { groundSpeed: 150 })
  expect(rx.rawBus()['112'].value).toBe(150)
  expect(bus(rx)['112']).toEqual({ value: 120, ssm: 'NORMAL' })
  // Ramping from the step it was set: 10 s later, 10 degrees on.
  expect(bus(rx)['103'].value).toBeCloseTo(100, 6)
  rx.override('110', null)
  rx.override('112', null)
  expect(bus(rx)['110']).toEqual(rx.rawBus()['110'])
  expect(bus(rx)['112']).toEqual({ value: 150, ssm: 'NORMAL' })
})

test('Fault mode: the fault discrete, the unit fault in 355, no tracking; RF input and baro faults; a silent bus', () => {
  const rx = receiver()
  run(rx, 0, 60)
  rx.injectFault('RECEIVER', true)
  run(rx, 61, 61)
  expect(rx.mode).toBe('FAULT')
  expect(rx.faultDiscrete).toBe(true)
  let b = bus(rx)
  expect(b['355'].value).toMatchObject({ unit: true, rfInput: false })
  expect(b['273'].value).toMatchObject({ mode: 'FAULT', used: 0, visible: 0 })
  expect(b['060']).toEqual([])
  // Clearing it restarts the unit from its self-test.
  rx.injectFault('RECEIVER', false)
  run(rx, 62, 62)
  expect(rx.mode).toBe('SELF_TEST')
  expect(rx.faultDiscrete).toBe(false)
  run(rx, 63, 80)
  expect(rx.mode).toBe('NAV')

  // An antenna or cable fault: no signal, an RF input fault, no fix; not a unit fault.
  rx.injectFault('RF_INPUT', true)
  run(rx, 81, 82)
  b = bus(rx)
  expect(rx.mode).toBe('ACQUISITION')
  expect(b['355'].value).toMatchObject({ unit: false, rfInput: true })
  expect(b['273'].value).toMatchObject({ used: 0 })
  expect(b['110'].ssm).toBe('NCD')
  rx.injectFault('RF_INPUT', false)

  // Baro altitude lost: the air data bus is flagged.
  run(rx, 83, 84, { baroAltitude: null })
  expect(bus(rx)['355'].value!.buses.airData).toBe(true)
  expect(bus(rx)['273'].value!.baroAiding).toBe(false)

  // A unit that cannot control its word status stops transmitting.
  rx.injectFault('STOP_TRANSMITTING', true)
  run(rx, 85, 85)
  expect(rx.bus()).toBeNull()
  rx.injectFault('STOP_TRANSMITTING', false)
  run(rx, 86, 86)
  expect(rx.bus()).not.toBeNull()
})

test('jamming lowers every satellite C/N0 by the given dB: enough of it loses the fix, and removing it recovers', () => {
  const rx = receiver()
  run(rx, 0, 60)
  const before = new Map(bus(rx)['060'].map(w => [w.value!.prn, w.value!.cn0]))
  rx.setJamming(8)
  run(rx, 61, 61)
  for (const w of bus(rx)['060']) expect(w.value!.cn0).toBeCloseTo(before.get(w.value!.prn)! - 8, 0)
  // At most 46 dB-Hz overhead; 20 dB of jamming puts every satellite under the 30 dB-Hz tracking threshold.
  rx.setJamming(20)
  run(rx, 62, 62)
  expect(rx.mode).toBe('ACQUISITION')
  expect(bus(rx)['273'].value!.used).toBe(0)
  rx.setJamming(0)
  run(rx, 63, 63)
  expect(rx.mode).toBe('NAV')
})

test('spoofing moves the reported position by a consistent offset or drift that the receiver reports as valid', () => {
  const control = receiver(), spoofed = receiver()
  run(control, 0, 60); run(spoofed, 0, 60)
  spoofed.setSpoof({ northM: 300, eastM: 0, driftNorthMps: 0, driftEastMps: 0 })
  run(control, 61, 61); run(spoofed, 61, 61)
  const north = (a: GpsBus, b: GpsBus) => (latitude(a) - latitude(b)) * 111_120
  expect(north(bus(spoofed), bus(control))).toBeCloseTo(300, 1)
  expect(bus(spoofed)['110'].ssm).toBe('NORMAL')
  expect(bus(spoofed)['273'].value!.integrity).toBe('OK')
  // A drift of 5 m/s east from the step it is set: 60 s later, 300 m east.
  spoofed.setSpoof({ northM: 0, eastM: 0, driftNorthMps: 0, driftEastMps: 5 })
  run(control, 62, 122); run(spoofed, 62, 122)
  const east = (longitude(bus(spoofed)) - longitude(bus(control))) * 111_120 * Math.cos((AT.lat * Math.PI) / 180)
  expect(east).toBeCloseTo(300, 0)
  spoofed.setSpoof(null)
  run(control, 123, 123); run(spoofed, 123, 123)
  expect(latitude(bus(spoofed))).toBe(latitude(bus(control)))
})
