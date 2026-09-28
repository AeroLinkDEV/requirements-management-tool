import { expect, logicTest as test } from './isolated-client-test'
import { fmsOutputs } from '../src/fmsCdu/efis'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { bearingDeg, distanceNm } from '../src/fmsCdu/fmsModel'
import type { GpsBus, GpsReceiver } from '../src/fmsCdu/gps'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { screenText } from '../src/fmsCdu/screen'

// GPS phase 3b: GPS-driven approach guidance. The FMS builds the FAS data block of the selected RNAV approach and sends
// it to both receivers; the level the selected receiver reports (305) is the approach annunciated; once captured on
// final, the lateral and vertical guidance and the deviations shown are the receiver's 116/117 and its scaling; and a
// level with no vertical guidance drops the vertical: APPR LOST to a latched altitude hold (the D02/D03 contracts), the
// vertical deviation flagged, and NO APPR INTEGRITY. The receiver side (FAS CRC, 156, SBAS PA only inside the approach
// region, the deviations and scaling) is proved in fms-gps-sbas.spec.ts.
const START = Date.UTC(2026, 8, 27, 14, 0, 0)
const setup = (procedure = 'R24R') => {
  let now = START
  const unit = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(unit)
  const fly = (seconds: number, each?: () => boolean | void) => { for (let t = 0; t < seconds; t += 1) { now += 1000; sim.step(1); if (each?.()) return } }
  unit.selectProcedure('APPROACH', procedure)
  unit.press('EXEC')
  return { unit, sim, fly }
}
const receivers = (unit: ScriptedFms): readonly GpsReceiver[] => (unit as unknown as { gps?: readonly GpsReceiver[] }).gps ?? []
const bus = (unit: ScriptedFms, index = 0): GpsBus => receivers(unit)[index].bus()!
const selected = (unit: ScriptedFms): GpsBus => bus(unit, unit.gpsStatus.chosen ?? 0)
const active = (unit: ScriptedFms) => { const leg = unit.activeRoute.legs[0]; return leg?.kind === 'wpt' ? leg.ident : null }
const onFinal = (unit: ScriptedFms) => active(unit) === 'RW24R'
const recalled = (unit: ScriptedFms, text: string) => unit.recallList.some(message => message.text === text)
const threshold = { lat: 45.4790, lon: -73.7180 }
// The FAS path by hand: the runway at 118 ft, a 50 ft TCH (the RW24R leg's 168 ft), and the angle through FERDI at 1500 ft.
const ferdi = { lat: 45.5200, lon: -73.6313 }
const gpa = Math.atan((1500 - 168) / (distanceNm(threshold, ferdi) * 6076.12))
const pathAt = (toThresholdNm: number) => 168 + toThresholdNm * 6076.12 * Math.tan(gpa)

test('the FMS sends the selected RNAV approach\'s FAS block to both receivers, and none for an ILS (3b.1)', () => {
  const { unit, fly } = setup()
  fly(1)
  for (const index of [0, 1]) {
    // Selected, the block valid (its CRC checks and it is for the approach selected), and armed: far outside the region.
    expect(bus(unit, index)['156'].value, `GPS${index + 1}`).toMatchObject({ selected: true, available: true, crcInvalid: false, mismatch: false, incomplete: false, armed: true })
  }
  expect(distanceNm(unit.truePosition, threshold)).toBeGreaterThan(30)
  const ils = setup('I24R')
  ils.fly(1)
  for (const index of [0, 1]) expect(bus(ils.unit, index)['156'].value?.selected, `GPS${index + 1}`).toBe(false)
})

test('outside the approach region the GPS reports LNAV/VNAV, and that is the approach annunciated armed; inside it, LPV (3b.2)', () => {
  const { unit, sim, fly } = setup()
  unit.armApproach(true)
  fly(1)
  // SBAS NAV outside 30 NM: the level is LNAV/VNAV, not the LPV the classifier used to claim.
  expect(selected(unit)['305'].value).toMatchObject({ paActive: false, level: 'LNAV/VNAV' })
  expect(unit.approachType).toBe('LNAV/VNAV')
  expect(fmsOutputs(unit, sim).verticalArmed).toEqual(['LNAV/VNAV'])
  fly(3 * 3600, () => distanceNm(unit.truePosition, threshold) < 29)
  fly(2)
  expect(selected(unit)['156'].value?.armed).toBe(false)
  expect(selected(unit)['305'].value).toMatchObject({ paActive: true, level: 'LPV' })
  expect(unit.approachType).toBe('LPV')
  expect(fmsOutputs(unit, sim).verticalArmed).toEqual(['LPV'])
  expect(fmsOutputs(unit, sim).approach).toEqual({ type: 'LPV', state: 'ARMED' })
})

test('the VNAV page names the level, LNAV/VNAV shortened to L/VNAV so it fits beside the page number (3b.2, R19)', () => {
  const { unit, fly } = setup()
  fly(1)
  expect(unit.approachType).toBe('LNAV/VNAV')
  unit.press('VNAV')
  expect(screenText(unit.screen())[0]).toMatch(/^ACT VNAV 24R L\/VNAV\s+1\/3$/)
})

test('captured on final, the guidance and the deviations shown are the selected GPS\'s 116 and 117, on its scaling (3b.3)', () => {
  const { unit, sim, fly } = setup()
  unit.armApproach(true)
  fly(3 * 3600, () => sim.approachMode === 'CAPTURED')
  expect(sim.approachMode).toBe('CAPTURED')
  expect(unit.approachType).toBe('LPV')
  fly(20)
  const b = selected(unit)
  const out = fmsOutputs(unit, sim)
  expect(out.crossTrack.status).toBe('NORMAL')
  expect(out.crossTrack.value!).toBeCloseTo(b['116'].value! / 6076.12, 9)
  expect(out.verticalDeviation.value!).toBeCloseTo(b['117'].value!, 6)
  expect(out.verticalSource).toBe('APPR')
  expect(out.lateralFullScaleNm).toBeCloseTo(b.scale.value!.lateralFullScaleFt / 6076.12, 9)
  expect(out.verticalFullScaleFt).toBeCloseTo(b.scale.value!.verticalFullScaleFt, 9)
  expect(out.approach).toEqual({ type: 'LPV', state: 'CAPTURED' })
})

test('flying the GPS deviations, the aircraft stays on the FAS path down to the threshold (3b.3)', () => {
  const { unit, sim, fly } = setup()
  unit.armApproach(true)
  fly(3 * 3600, () => sim.approachMode === 'CAPTURED')
  // The demonstration FAF lies about 420 ft off the extended centreline (its leg is 236.0 degrees, the runway 237.0), so
  // the GPS guidance first brings the aircraft onto the FAS course; over the last 2 NM it must be established.
  let worstLateral = 0, worstVertical = 0
  fly(900, () => {
    const b = selected(unit)
    const toGo = b['201'].value
    if (toGo !== null && toGo < 0.3) return true
    worstVertical = Math.max(worstVertical, Math.abs(b['117'].value ?? Infinity))
    if (toGo !== null && toGo < 2) worstLateral = Math.max(worstLateral, Math.abs(b['116'].value ?? Infinity))
  })
  expect(sim.approachMode).toBe('CAPTURED')
  // The path the GPS measures from is the hand-derived one: the aircraft less its 117 deviation, within the GPS's own
  // vertical error, at the TCH-plus-angle height for the distance it reports (201).
  const b = selected(unit)
  expect(Math.abs(unit.altitude - b['117'].value! - pathAt(b['201'].value!))).toBeLessThan(20)
  // Established: within 100 ft laterally (the full scale there is a few hundred feet) and 30 ft of the path.
  expect(worstLateral).toBeLessThan(100)
  expect(worstVertical).toBeLessThan(30)
  // Near the threshold on a 3-degree-ish path: a few hundred feet above the runway (118 ft), not at the FAF altitude.
  expect(unit.altitude).toBeLessThan(400)
  expect(Math.abs(bearingDeg(threshold, unit.truePosition) - 57)).toBeLessThan(5)
})

test('an ionospheric storm after capture drops LPV to LNAV: APPR LOST to altitude hold, the vertical flagged, NO APPR INTEGRITY (3b.4)', () => {
  const { unit, sim, fly } = setup()
  unit.armApproach(true)
  fly(3 * 3600, () => sim.approachMode === 'CAPTURED' && unit.verticalSpeed < -300)
  expect(sim.approachMode).toBe('CAPTURED')
  expect(recalled(unit, 'NO APPR INTEGRITY')).toBe(false)
  for (const receiver of receivers(unit)) receiver.setSbas({ ionoStorm: 20 })
  fly(2)
  // VPL over the 50 m VAL: no vertical level; HPL still within 556 m, so LNAV, and 117 is a failure warning.
  expect(selected(unit)['305'].value?.level).toBe('LNAV')
  expect(selected(unit)['117'].ssm).toBe('FW')
  expect(unit.approachType).toBe('LNAV')
  expect(sim.approachMode).toBe('OFF')
  expect(sim.verticalMode).toBe('ALT HOLD')
  expect(sim.modeEvents.at(-1)).toMatchObject({ event: 'APPR LOST' })
  expect(recalled(unit, 'NO APPR INTEGRITY')).toBe(true)
  const out = fmsOutputs(unit, sim)
  expect(out.verticalDeviation.status).toBe('FAIL')
  // Laterally the GPS still guides: the leg is flown and the deviation shown.
  expect(out.crossTrack.status).toBe('NORMAL')
  // The storm passing does not re-capture: the approach was disarmed (D03).
  for (const receiver of receivers(unit)) receiver.setSbas({ ionoStorm: 1 })
  const held = sim.altitudeHoldReference!
  fly(20)
  expect(unit.approachType).toBe('LPV')
  expect(sim.approachMode).toBe('OFF')
  expect(Math.abs(unit.altitude - held)).toBeLessThan(40)
})

test('117 withdrawn after capture, the level still LPV, is loss of vertical guidance: APPR LOST and the vertical flagged (3b.4)', () => {
  const { unit, sim, fly } = setup()
  unit.armApproach(true)
  fly(3 * 3600, () => sim.approachMode === 'CAPTURED' && unit.verticalSpeed < -300)
  for (const receiver of receivers(unit)) receiver.override('117', { kind: 'FORCE', ssm: 'FW' })
  fly(2)
  expect(selected(unit)['305'].value?.level).toBe('LPV')
  expect(sim.approachMode).toBe('OFF')
  expect(sim.modeEvents.at(-1)).toMatchObject({ event: 'APPR LOST' })
  expect(fmsOutputs(unit, sim).verticalDeviation.status).toBe('FAIL')
})

test('with SBAS set do-not-use the level is LNAV: armed, nothing is annunciated vertically and the approach does not descend (3b.2, 3b.4)', () => {
  const { unit, sim, fly } = setup()
  for (const receiver of receivers(unit)) receiver.setSbas({ doNotUse: true })
  unit.armApproach(true)
  fly(3 * 3600, () => onFinal(unit))
  expect(onFinal(unit)).toBe(true)
  expect(selected(unit)['273'].value?.mode).toBe('NAV')
  expect(unit.approachType).toBe('LNAV')
  // LNAV has no vertical guidance: nothing is armed in the vertical column, though the approach is armed.
  expect(sim.approachMode).toBe('ARMED')
  expect(fmsOutputs(unit, sim).verticalArmed).toEqual([])
  const faf = unit.fafAltitudeCorrected
  let lowest = Infinity
  fly(300, () => { lowest = Math.min(lowest, unit.altitude); expect(sim.approachMode).not.toBe('CAPTURED'); return !onFinal(unit) })
  expect(lowest).toBeGreaterThan(faf - 60)
  expect(recalled(unit, 'NO APPR INTEGRITY')).toBe(false)
})

test('on final without GPS vertical guidance the vertical deviation is flagged, not drawn from the FMS\'s own path (3b.4)', () => {
  const { unit, sim, fly } = setup()
  for (const receiver of receivers(unit)) receiver.setSbas({ doNotUse: true })
  fly(3 * 3600, () => onFinal(unit))
  fly(5)
  const out = fmsOutputs(unit, sim)
  expect(out.verticalDeviation.status).toBe('FAIL')
  expect(out.verticalSource).toBeNull()
})

test('an ILS approach keeps its own capability and path: no FAS, ILS annunciated, captured on the FMS path (3b.2)', () => {
  const { unit, sim, fly } = setup('I24R')
  unit.armApproach(true)
  fly(1)
  expect(unit.approachType).toBe('ILS')
  expect(fmsOutputs(unit, sim).verticalArmed).toEqual(['ILS'])
  fly(3 * 3600, () => sim.approachMode === 'CAPTURED')
  expect(sim.approachMode).toBe('CAPTURED')
  expect(fmsOutputs(unit, sim).verticalDeviation.status).toBe('NORMAL')
})
