import { readFileSync } from 'node:fs'
import { expect, logicTest as test } from './isolated-client-test'
import { fmsOutputs } from '../src/fmsCdu/efis'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { offset } from '../src/fmsCdu/fmsModel'
import type { GpsBus, GpsReceiver } from '../src/fmsCdu/gps'
import { approachAuthority } from '../src/fmsCdu/gpsSensors'
import { setUpKbtvRnav15 } from '../src/fmsCdu/kbtvDemo'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import type { CduFunction } from '../src/fmsCdu/variants'

// An LNAV-only RNAV approach (no FAS data block: the 87N COPTER RNAV 190 point-in-space approach, whose only minimum is
// LNAV) is flown on the FMS's lateral guidance and annunciated LNAV, with the selected receiver usable at the approach
// HAL (HIL at most 0.3 NM). 156 (the selected approach), 116 and 117 belong to FAS approaches only (AC 20-138, TSO-C146
// practice): an LNAV-only approach selects nothing in the receiver, and never waits on 156 to be flown.
const COPTER = readFileSync('tests/fixtures/cifp/copter-pins-2609.pc', 'latin1')
const START = Date.UTC(2026, 8, 29, 14, 0, 0)
const typeText = (unit: ScriptedFms, text: string) => { for (const ch of text) unit.press(`CHAR_${ch}` as CduFunction) }
const receivers = (unit: ScriptedFms): readonly GpsReceiver[] => (unit as unknown as { gps?: readonly GpsReceiver[] }).gps ?? []
const bus = (unit: ScriptedFms, index = 0): GpsBus => receivers(unit)[index].bus()!
const recalled = (unit: ScriptedFms, text: string) => unit.recallList.some(message => message.text === text)

/** On the 87N R190 approach (HTO transition), 2 NM before STAYS on the final course, in the approach phase. */
function onR190() {
  let now = START
  const unit = new ScriptedFms(() => new Date(now))
  expect(unit.loadArinc424(COPTER, 'copter-pins-2609.pc')).toMatchObject({ loaded: 'CIFP2609' })
  unit.swapCycles()
  unit.press('RTE')
  typeText(unit, '87N')
  unit.press('LSK1R')
  unit.press('EXEC')
  unit.selectProcedure('APPROACH', 'R190', 'HTO')
  unit.press('EXEC')
  const stays = unit.coordinates('STAYS')!, tidue = unit.coordinates('TIDUE')!
  const course = (Math.atan2(stays.lon - tidue.lon, stays.lat - tidue.lat) * 180) / Math.PI
  unit.placeAircraft({ position: offset(stays, course + 180, 2), track: (course + 360) % 360, altitude: 1700 }, 'test: on the R190 final')
  expect(unit.directTo('STAYS')).toBeUndefined()
  unit.press('EXEC')
  const sim = new FlightSimulator(unit)
  const fly = (seconds: number) => { for (let t = 0; t < seconds; t += 1) { now += 1000; sim.step(1) } }
  fly(2)
  expect(unit.flightPhase).toBe('APPROACH')
  return { unit, sim, fly }
}

test('R190 (LNAV only, no FAS) annunciates LNAV in the approach phase with a usable receiver, and raises nothing', () => {
  const { unit, sim, fly } = onR190()
  // Nothing is selected in the receivers: 156, 116 and 117 are FAS-approach words.
  for (const index of [0, 1]) expect(bus(unit, index)['156'].value?.selected, `GPS${index + 1}`).toBe(false)
  expect(unit.gpsApproachAuthority).toEqual({ annunciation: 'LNAV', lateral: false, vertical: false, reason: 'NO FAS: LNAV ONLY' })
  expect(unit.approachType).toBe('LNAV')
  // Flown on the FMS's lateral guidance down the final, with no approach integrity alert.
  fly(60)
  expect(unit.flightPhase).toBe('APPROACH')
  expect(unit.approachType).toBe('LNAV')
  expect(sim.lateralMode).toBe('LNAV')
  expect(fmsOutputs(unit, sim).lateralSource).toBe('ROUTE')
  expect(recalled(unit, 'NO APPR INTEGRITY')).toBe(false)
})

test('R190: NO APPR INTEGRITY only when the selected receiver is unusable, as with a HIL above the 0.3 NM approach HAL', () => {
  // Within the HAL: still LNAV.
  const within = onR190()
  for (const rx of receivers(within.unit)) rx.override('130', { kind: 'FORCE', value: 0.29, ssm: 'NORMAL' })
  within.fly(2)
  expect(within.unit.approachType).toBe('LNAV')
  expect(recalled(within.unit, 'NO APPR INTEGRITY')).toBe(false)
  // Above it on both receivers: the approach may not be flown on GPS.
  const above = onR190()
  for (const rx of receivers(above.unit)) rx.override('130', { kind: 'FORCE', value: 0.31, ssm: 'NORMAL' })
  above.fly(2)
  expect(above.unit.approachType).toBe('NO APPR')
  expect(recalled(above.unit, 'NO APPR INTEGRITY')).toBe(true)
  // A receiver fault on both: the same.
  const failed = onR190()
  for (const rx of receivers(failed.unit)) rx.injectFault('RECEIVER', true)
  failed.fly(2)
  expect(failed.unit.approachType).toBe('NO APPR')
  expect(recalled(failed.unit, 'NO APPR INTEGRITY')).toBe(true)
  // The rule itself (approachAuthority): an LNAV-only approach on a receiver that may not be navigated on is NO APPR, with
  // the veto; the FMS never chooses such a receiver, so this is the contract, not a reachable FMS state.
  const assessment = within.unit.gpsStatus.assessed[0]
  expect(approachAuthority(bus(within.unit), assessment, false)).toMatchObject({ annunciation: 'LNAV' })
  expect(approachAuthority(bus(within.unit), { ...assessment, usable: false, detail: 'HIL 0.31 ABOVE HAL' }, false))
    .toEqual({ annunciation: 'NO APPR', lateral: false, vertical: false, reason: 'GPS HIL 0.31 ABOVE HAL' })
  expect(approachAuthority(null, null, false)).toMatchObject({ annunciation: 'NO APPR', reason: 'NO GPS SELECTED' })
})

test('KBTV R15 (a FAS approach) still needs its selected approach: 156 not selected is NO APPR, as before', () => {
  let now = START
  const unit = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(unit)
  expect(setUpKbtvRnav15(unit, sim)).toEqual({ ready: true })
  now += 1000
  sim.step(1)
  for (const index of [0, 1]) expect(bus(unit, index)['156'].value?.selected, `GPS${index + 1}`).toBe(true)
  expect(unit.gpsApproachAuthority.reason).not.toBe('NO FAS: LNAV ONLY')
  expect(['LPV', 'LNAV/VNAV']).toContain(unit.approachType)
  for (const rx of receivers(unit)) rx.overrideStatus('156', { selected: false })
  now += 1000
  sim.step(1)
  expect(unit.gpsApproachAuthority).toMatchObject({ annunciation: 'NO APPR', reason: '156 NOT SELECTED' })
})
