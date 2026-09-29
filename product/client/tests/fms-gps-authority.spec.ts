import { expect, logicTest as test } from './isolated-client-test'
import { fmsOutputs } from '../src/fmsCdu/efis'
import { FlightSimulator } from '../src/fmsCdu/flight'
import type { GpsBus, GpsReceiver, Ssm } from '../src/fmsCdu/gps'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'

// Consumer authority (the GPS review of 56ae5b31, GPS-01, GPS-04 and GPS-06): what the FMS may do with the words a
// receiver transmits. One precedence table in gpsSensors decides, for each receiver, whether it may be navigated on and
// why not, and for the selected one whether the approach may be flown laterally and descended on. These tests construct
// typed, deliberately contradictory words with the receiver's overrides: they are forced-word stimuli, not claims that the
// unmodified receiver emits such combinations (its own behaviour is in fms-gps.spec.ts and fms-gps-sbas.spec.ts).
const START = Date.UTC(2026, 8, 27, 14, 0, 0)
const setup = () => {
  let now = START
  const unit = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(unit)
  const fly = (seconds: number, each?: () => boolean | void) => { for (let t = 0; t < seconds; t += 1) { now += 1000; sim.step(1); if (each?.()) return } }
  return { unit, sim, fly }
}
const receivers = (unit: ScriptedFms): readonly GpsReceiver[] => (unit as unknown as { gps?: readonly GpsReceiver[] }).gps ?? []
const gps1 = (unit: ScriptedFms) => receivers(unit)[0]
const bus = (unit: ScriptedFms, index = 0): GpsBus => receivers(unit)[index].bus()!
const recalled = (unit: ScriptedFms, text: string) => unit.recallList.some(message => message.text === text)
/** The FMS's judgement of GPS1, with the reason and its detail as the UI shows them. */
const judged = (unit: ScriptedFms) => unit.gpsStatus.assessed[0] as { usable: boolean; reason: string; detail?: string }

// ------------------------------------------------------------------ GPS-06 and GPS-04: may this receiver be navigated on

test('healthy control: GPS1 as transmitted is usable, and the FMS navigates on it', () => {
  const { unit } = setup()
  expect(judged(unit)).toMatchObject({ usable: true, reason: 'OK' })
  expect(unit.gpsStatus.chosen).toBe(0)
})

// Each row is one materially different veto class: the receiver says of itself that it cannot be trusted, while its
// position and HIL words stay Normal and plausible. Explicit receiver faults make it unusable (conservative default).
const RECEIVER_STATE: [string, (rx: GpsReceiver) => void, RegExp][] = [
  ['273 mode FAULT', rx => rx.overrideStatus('273', { mode: 'FAULT' }), /273.*FAULT/],
  ['273 integrity DETECTED', rx => rx.overrideStatus('273', { integrity: 'DETECTED' }), /273.*DETECTED/],
  ['355 unit fault', rx => rx.overrideStatus('355', { unit: true }), /355.*UNIT/],
  ['273 status word not Normal', rx => rx.override('273', { kind: 'FORCE', ssm: 'NCD' }), /273.*NCD/],
]
for (const [name, stimulus, detail] of RECEIVER_STATE) {
  test(`a receiver reporting its own fault is not navigated on: ${name}, position and HIL still Normal (GPS-06)`, () => {
    const { unit } = setup()
    stimulus(gps1(unit))
    unit.gpsUpdated()
    expect(bus(unit)['130'].ssm).toBe('NORMAL')
    expect(judged(unit).usable).toBe(false)
    expect(judged(unit).detail).toMatch(detail)
    expect(unit.gpsStatus.chosen).toBe(1)
  })
}

// Numeric domain: a Normal word is not a valid number. The receiver can still be made to transmit these; the consumer
// refuses them, naming the value. The assembled coarse + fine position is checked, not each fragment.
const BAD_VALUES: [string, (rx: GpsReceiver, b: GpsBus) => void, RegExp][] = [
  ['a negative HIL', rx => rx.override('130', { kind: 'FORCE', value: -1, ssm: 'NORMAL' }), /HIL/],
  ['a HIL that is not a number', rx => rx.override('130', { kind: 'FORCE', value: Number.NaN, ssm: 'NORMAL' }), /HIL/],
  ['an infinite HIL', rx => rx.override('130', { kind: 'FORCE', value: Number.POSITIVE_INFINITY, ssm: 'NORMAL' }), /HIL/],
  ['a negative HFOM', rx => rx.override('247', { kind: 'FORCE', value: -0.5, ssm: 'NORMAL' }), /HFOM/],
  ['a latitude of 1000 degrees', rx => rx.override('110', { kind: 'FORCE', value: 1000, ssm: 'NORMAL' }), /LAT/],
  ['a longitude of 1000 degrees', rx => rx.override('111', { kind: 'FORCE', value: 1000, ssm: 'NORMAL' }), /LON/],
  ['an infinite fine latitude', rx => rx.override('120', { kind: 'FORCE', value: Number.POSITIVE_INFINITY, ssm: 'NORMAL' }), /LAT/],
]
for (const [name, stimulus, detail] of BAD_VALUES) {
  test(`an impossible value on a Normal word is refused, and the FMS position is protected: ${name} (GPS-04)`, () => {
    const { unit } = setup()
    stimulus(gps1(unit), bus(unit))
    unit.gpsUpdated()
    expect(judged(unit).usable).toBe(false)
    expect(judged(unit).reason).toBe('BAD DATA')
    expect(judged(unit).detail).toMatch(detail)
    expect(unit.gpsStatus.chosen).toBe(1)
    expect(Math.abs(unit.position.lat)).toBeLessThanOrEqual(90)
    expect(Math.abs(unit.position.lon)).toBeLessThanOrEqual(180)
  })
}

test('boundary and spoof controls: HIL 0 and a plausible spoofed position are accepted; the domain check is not a spoof detector (GPS-04)', () => {
  const { unit } = setup()
  gps1(unit).override('130', { kind: 'FORCE', value: 0, ssm: 'NORMAL' })
  unit.gpsUpdated()
  expect(judged(unit)).toMatchObject({ usable: true, reason: 'OK' })
  gps1(unit).override('130', null)
  // 0.01 degrees north: consistent, in range, and wrong. Only the GPS1/GPS2 compare catches it (fms-gps-fms.spec.ts).
  gps1(unit).override('110', { kind: 'BIAS', amount: 0.01 })
  unit.gpsUpdated()
  expect(judged(unit)).toMatchObject({ usable: true, reason: 'OK' })
  expect(unit.gpsStatus.chosen).toBe(0)
})

// ------------------------------------------------------------------ GPS-06: may the approach be flown on this receiver

const captured = () => {
  const run = setup()
  run.unit.selectProcedure('APPROACH', 'R24R')
  run.unit.press('EXEC')
  run.unit.armApproach(true)
  run.fly(3 * 3600, () => run.sim.approachMode === 'CAPTURED' && run.unit.verticalSpeed < -300)
  expect(run.sim.approachMode).toBe('CAPTURED')
  expect(run.unit.approachType).toBe('LPV')
  return run
}

// The approach's identity and availability (156): each says the approach the receiver guides is not the valid one the
// FMS selected, while 305 still reports LPV and 116/117 stay Normal. Coupling is inhibited (conservative default).
const APPROACH_STATUS: [string, Record<string, boolean>, RegExp][] = [
  ['FAS CRC invalid', { crcInvalid: true, available: false }, /CRC/],
  ['FAS for another approach', { mismatch: true, available: false }, /MISMATCH/],
  ['FAS incomplete', { incomplete: true, available: false }, /INCOMPLETE/],
  ['no approach selected', { selected: false, available: false }, /NOT SELECTED/],
  ['the approach unavailable', { available: false }, /UNAVAILABLE/],
]
for (const [name, patch, reason] of APPROACH_STATUS) {
  test(`156 saying ${name} ends the captured approach, though 305 says LPV and 116/117 are Normal (GPS-06)`, () => {
    const { unit, sim, fly } = captured()
    for (const rx of receivers(unit)) rx.overrideStatus('156', patch)
    fly(1)
    expect(bus(unit)['305'].value?.level).toBe('LPV')
    expect(bus(unit)['117'].ssm).toBe('NORMAL')
    expect(unit.approachType).toBe('NO APPR')
    expect(unit.gpsApproachAuthority.reason).toMatch(reason)
    expect(sim.approachMode).toBe('OFF')
    expect(sim.verticalMode).toBe('ALT HOLD')
    expect(recalled(unit, 'NO APPR INTEGRITY')).toBe(true)
  })
}

test('a vetoed approach in the approach phase raises NO APPR INTEGRITY before any vertical guidance was had (GPS-06)', () => {
  const { unit } = setup()
  unit.selectProcedure('APPROACH', 'R24R')
  unit.press('EXEC')
  for (let i = 0; i < 3; i += 1) unit.sequence()
  unit.updateNavigation(0)
  expect(unit.flightPhase).toBe('APPROACH')
  // Outside the approach region: annunciated at its level, not yet guided, and nothing wrong.
  expect(unit.gpsApproachAuthority).toMatchObject({ lateral: false, vertical: false, reason: 'OUTSIDE APPROACH REGION' })
  expect(recalled(unit, 'NO APPR INTEGRITY')).toBe(false)
  for (const rx of receivers(unit)) rx.overrideStatus('156', { crcInvalid: true, available: false })
  unit.gpsUpdated()
  expect(unit.approachType).toBe('NO APPR')
  expect(recalled(unit, 'NO APPR INTEGRITY')).toBe(true)
})

// ------------------------------------------------------------------ GPS-01: lateral and vertical authority in flight

// Lateral lost (116 not Normal): the approach is lost, to the latched hold, and LNAV reverts to the route, recorded and
// shown as a change of source. Checked on the first step after the stimulus and on a following stable step.
for (const ssm of ['FW', 'NCD', 'FT'] as Ssm[]) {
  test(`116 withdrawn (${ssm}) after capture: APPR LOST, an announced reversion to route LNAV, never a silent substitute (GPS-01)`, () => {
    const { unit, sim, fly } = captured()
    expect(fmsOutputs(unit, sim).lateralSource).toBe('GPS')
    const selected = unit.gpsStatus.chosen!
    receivers(unit)[selected].override('116', { kind: 'FORCE', ssm })
    unit.gpsUpdated()
    for (const step of ['first', 'stable']) {
      fly(1)
      const out = fmsOutputs(unit, sim)
      expect(sim.approachMode, step).toBe('OFF')
      expect(sim.verticalMode, step).toBe('ALT HOLD')
      expect(out.lateralSource, step).toBe('ROUTE')
      expect(out.crossTrack.status, step).toBe('NORMAL')
      expect(unit.approachType, step).toBe('NO APPR')
    }
    const lost = sim.modeEvents.filter(event => event.event === 'APPR LOST')
    expect(lost).toHaveLength(1)
    expect(lost[0].detail).toMatch(new RegExp(`116 ${ssm}.*LNAV ON ROUTE`, 'i'))
    expect(recalled(unit, 'NO APPR INTEGRITY')).toBe(true)
  })
}

test('117 withdrawn with 116 still valid: the latched hold, and the lateral stays on GPS 116, annunciated LNAV (GPS-01)', () => {
  const { unit, sim, fly } = captured()
  const selected = unit.gpsStatus.chosen!
  // An unmistakable lateral stimulus: 500 ft right of course, Normal, on the receiver navigated on.
  receivers(unit)[selected].override('116', { kind: 'FORCE', value: 500, ssm: 'NORMAL' })
  unit.gpsUpdated()
  fly(1)
  expect(fmsOutputs(unit, sim).crossTrack.value!).toBeCloseTo(500 / 6076.12, 9)
  receivers(unit)[selected].override('117', { kind: 'FORCE', ssm: 'FW' })
  unit.gpsUpdated()
  for (const step of ['first', 'stable']) {
    fly(1)
    const out = fmsOutputs(unit, sim)
    expect(sim.approachMode, step).toBe('OFF')
    expect(sim.verticalMode, step).toBe('ALT HOLD')
    expect(out.verticalDeviation.status, step).toBe('FAIL')
    expect(out.lateralSource, step).toBe('GPS')
    expect(out.crossTrack.value!, step).toBeCloseTo(500 / 6076.12, 9)
    expect(sim.guidance.crossTrack, step).toBeCloseTo(500 / 6076.12, 9)
    expect(unit.approachType, step).toBe('LNAV')
  }
  // Steering the GPS deviation: 500 ft right of course, the aircraft is commanded left.
  expect(sim.guidance.bankCommand).toBeLessThan(0)
})
