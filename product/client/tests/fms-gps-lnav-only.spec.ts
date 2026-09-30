import { readFileSync } from 'node:fs'
import { expect, logicTest as test } from './isolated-client-test'
import { fmsOutputs } from '../src/fmsCdu/efis'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { offset } from '../src/fmsCdu/fmsModel'
import type { GpsBus, GpsReceiver } from '../src/fmsCdu/gps'
import { parseArinc424 } from '../src/fmsCdu/arinc424'
import { approachAuthority, buildFas, fasRequirement } from '../src/fmsCdu/gpsSensors'
import { KBTV_SOURCE, setUpKbtvRnav15 } from '../src/fmsCdu/kbtvDemo'
import { LATER_SBAS_PROFILE } from '../src/fmsCdu/profile'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import type { CduFunction } from '../src/fmsCdu/variants'

// An LNAV-only RNAV approach (no FAS data block: the 87N COPTER RNAV 190 point-in-space approach, whose only minimum is
// LNAV) is flown on the FMS's lateral guidance and annunciated LNAV, with the selected receiver usable at the approach
// HAL (HIL at most 0.3 NM). 156 (the selected approach), 116 and 117 belong to FAS approaches only (AC 20-138, TSO-C146
// practice): an LNAV-only approach selects nothing in the receiver, and never waits on 156 to be flown.
const COPTER = readFileSync('tests/fixtures/cifp/copter-pins-2609.pc', 'latin1')
const KBTV = readFileSync('tests/fixtures/cifp/kbtv-2609.pc', 'latin1')
const START = Date.UTC(2026, 8, 29, 14, 0, 0)
const typeText = (unit: ScriptedFms, text: string) => { for (const ch of text) unit.press(`CHAR_${ch}` as CduFunction) }
const receivers = (unit: ScriptedFms): readonly GpsReceiver[] => (unit as unknown as { gps?: readonly GpsReceiver[] }).gps ?? []
const bus = (unit: ScriptedFms, index = 0): GpsBus => receivers(unit)[index].bus()!
const recalled = (unit: ScriptedFms, text: string) => unit.recallList.some(message => message.text === text)

/** On the 87N R190 approach (HTO transition), 2 NM before STAYS on the final course, in the approach phase. */
function onR190() {
  let now = START
  const unit = new ScriptedFms(() => new Date(now), { profile: LATER_SBAS_PROFILE })
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
  unit.armApproach()
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
  expect(approachAuthority(bus(within.unit), assessment, 'LNAV ONLY')).toMatchObject({ annunciation: 'LNAV' })
  expect(approachAuthority(bus(within.unit), { ...assessment, usable: false, detail: 'HIL 0.31 ABOVE HAL' }, 'LNAV ONLY'))
    .toEqual({ annunciation: 'NO APPR', lateral: false, vertical: false, reason: 'GPS HIL 0.31 ABOVE HAL' })
  expect(approachAuthority(null, null, 'LNAV ONLY')).toMatchObject({ annunciation: 'NO APPR', reason: 'NO GPS SELECTED' })
})

test('KBTV R15 (a FAS approach) still needs its selected approach: 156 not selected is NO APPR, as before', () => {
  let now = START
  const unit = new ScriptedFms(() => new Date(now), { profile: LATER_SBAS_PROFILE })
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

// ---------------------------------------------------------------------------------------------- intent, not absence (Q4)

/** A copy of the data with one line changed: `edit` returns the replacement for the first line `match` finds. */
const withLine = (data: string, match: (line: string) => boolean, edit: (line: string) => string) => {
  const lines = data.split('\n')
  const at = lines.findIndex(match)
  expect(at, 'the record to change is in the data').toBeGreaterThanOrEqual(0)
  lines[at] = edit(lines[at])
  return lines.join('\n')
}
/** 1-based ARINC columns a..b of a record replaced by `text` (same width). */
const setColumns = (line: string, a: number, b: number, text: string) => { expect(text.length).toBe(b - a + 1); return line.slice(0, a - 1) + text + line.slice(b) }

test('Q4: R190 is LNAV only because its data says so, not because it lacks a FAS', () => {
  const r190 = parseArinc424(COPTER).data.procedures.find(p => p.ident === 'R190')!
  expect(r190.lnavOnly).toEqual({ source: 'no path point record, and vertical angle 000 at the MAP (no vertical path published)' })
  expect(r190.publishedFas).toBeUndefined()
  expect(r190.fasInvalid).toBeUndefined()
  // An approach with a published FAS is not LNAV only.
  const r15 = parseArinc424(KBTV).data.procedures.find(p => p.ident === 'R15')!
  expect(r15.publishedFas).toBeDefined()
  expect(r15.lnavOnly).toBeUndefined()
})

test('Q4: a point-in-space approach that codes a vertical path but has no FAS data block is NO APPR, not LNAV', () => {
  // The same R190, with a 3.00 degree descent angle coded at the MAP (CRANN, columns 103-106): a vertical path is published,
  // so the approach needs a FAS data block, and the data has none to give it.
  const coded = withLine(COPTER, line => line.includes('K6FR190') && line.includes('030CRANN'), line => setColumns(line, 103, 106, '-300'))
  let now = START
  const unit = new ScriptedFms(() => new Date(now), { profile: LATER_SBAS_PROFILE })
  expect(unit.loadArinc424(coded, 'copter-pins-2609-vpa.pc')).toMatchObject({ loaded: 'CIFP2609' })
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
  for (let t = 0; t < 2; t += 1) { now += 1000; sim.step(1) }
  expect(unit.flightPhase).toBe('TERMINAL')
  expect(unit.lamps().has('NPA')).toBe(false)
  expect(unit.approachType).toBe('NO APPR')
  expect(unit.gpsApproachAuthority).toEqual({ annunciation: 'NO APPR', lateral: false, vertical: false, reason: 'FAS DATA MISSING' })
})

test('Q4: an unreadable published path point never becomes LNAV only, nor a derived FAS: NO APPR (FAS DATA INVALID)', () => {
  // KBTV R15's path point record with an impossible CRC (columns 116-123).
  const broken = withLine(KBTV, line => line.startsWith('SUSAP KBTVK6PR15   RW15 001'), line => setColumns(line, 116, 123, 'ZZZZZZZZ'))
  // The loader's own validation stands: a file with an impossible record is refused whole, so nothing changes.
  const unit = new ScriptedFms(() => new Date(START), { profile: LATER_SBAS_PROFILE })
  expect(unit.loadArinc424(broken, KBTV_SOURCE)).toEqual({ refused: expect.stringContaining('impossible path point CRC') })
  // Behind it, the approach read from such data keeps the fact: it needs a FAS and has none usable. No FAS is derived in
  // its place, and its requirement is FAS DATA INVALID, which is NO APPR on any receiver.
  const data = parseArinc424(broken).data
  const r15 = data.procedures.find(p => p.ident === 'R15')!
  expect(r15.publishedFas).toBeUndefined()
  expect(r15.fasInvalid).toBe('path point CRC')
  expect(r15.lnavOnly).toBeUndefined()
  const kbtv = data.entries.find(e => e.kind === 'airport' && e.ident === 'KBTV')
  const runway = kbtv && kbtv.kind === 'airport' ? kbtv.runways.find(r => r.ident === 'RW15') : undefined
  const foves = data.entries.find(e => e.ident === 'FOVES')?.position
  expect(runway).toBeDefined()
  expect(buildFas(r15, runway, 'KBTV', foves)).toBeNull()
  expect(fasRequirement(r15, null)).toBe('FAS DATA INVALID')
  // On a usable receiver, as the intact approach flies: NO APPR, with the reason.
  let now = START
  const intact = new ScriptedFms(() => new Date(now), { profile: LATER_SBAS_PROFILE })
  const sim = new FlightSimulator(intact)
  expect(setUpKbtvRnav15(intact, sim)).toEqual({ ready: true })
  now += 1000
  sim.step(1)
  expect(['LPV', 'LNAV/VNAV']).toContain(intact.approachType)
  const chosen = intact.gpsStatus.chosen!
  expect(approachAuthority(bus(intact, chosen), intact.gpsStatus.assessed[chosen], 'FAS DATA INVALID'))
    .toEqual({ annunciation: 'NO APPR', lateral: false, vertical: false, reason: 'FAS DATA INVALID' })
  expect(approachAuthority(bus(intact, chosen), intact.gpsStatus.assessed[chosen], 'FAS DATA MISSING')).toMatchObject({ annunciation: 'NO APPR', reason: 'FAS DATA MISSING' })
})
