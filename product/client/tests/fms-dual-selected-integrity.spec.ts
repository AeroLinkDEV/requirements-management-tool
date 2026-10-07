import { expect, logicTest as test } from './isolated-client-test'
import { DualFmsSystem } from '../src/fmsCdu/dualFms'
import { distanceNm } from '../src/fmsCdu/fmsModel'
import { MISSION_87N_OFFSHORE_SAR } from '../src/fmsCdu/heliDemo'
import { START_STATES } from '../src/fmsCdu/kbtvDemo'
import { LATER_SBAS_PROFILE, type AircraftProfile } from '../src/fmsCdu/profile'
import { TICK_SECONDS, advanceTicks } from '../src/fmsCdu/scenario'

// #1538 owner: M300 3-24 synchronizes GPS approach-integrity availability; 3-25 uses the best system sensor.
// Local receiver degradation must not give the offside computer a different approach phase or disarm the aircraft's
// panel approach switch. fms-dual-approach-phase covers healthy inputs, not these asymmetric receiver failures.
function setup(start: '87n-rnav190-final' | 'kbtv-rnav15', profile?: AircraftProfile) {
  let now = start === '87n-rnav190-final' ? Date.parse(MISSION_87N_OFFSHORE_SAR.startTime!) : Date.UTC(2026, 8, 27, 14)
  const system = new DualFmsSystem(() => new Date(now), { profile })
  const [one, two] = system.computers
  expect(one.compute(() => {
    const result = START_STATES[start].setUp(one, system.flights[0])
    one.dualOperation?.settingsChanged(); one.dualOperation?.finishEdit(true); two.observeAircraft(one)
    return result
  })).toEqual({ ready: true })
  return { system, one, two, tick: () => advanceTicks(1, ms => { now += ms }, system, null) }
}

for (const [guidanceSide, degradedGps] of [[1, 2], [2, 2], [2, 1]] as const) test(`87N stays in SYNC with FMS${guidanceSide} guiding when only GPS${degradedGps} HIL exceeds the approach limit`, () => {
  const { system, one, two, tick } = setup('87n-rnav190-final')
  system.selectGuidance(guidanceSide)
  system.computers[guidanceSide - 1].armApproach(true)
  one.gps[degradedGps - 1].override('130', { kind: 'FORCE', value: 0.5, ssm: 'NORMAL' })
  // A 0.2-NM HFOM on GPS1 puts GPS2 more than the manual's 100-m sensor hysteresis ahead. This row exercises the
  // system navigation selected from FMS2, separately from selecting FMS2 as the aircraft's guidance computer.
  if (degradedGps === 1) one.gps[0].override('247', { kind: 'FORCE', value: 0.2, ssm: 'NORMAL' })
  let approachAt: number | null = null
  const mismatches: unknown[] = []
  for (let n = 1; n * TICK_SECONDS <= 120; n++) {
    tick()
    if (system.computers[2 - degradedGps].localFlightPhase === 'APPROACH') approachAt ??= n * TICK_SECONDS
    if (one.localFlightPhase !== two.localFlightPhase || system.mode !== 'SYNC')
      mismatches.push({ at: n * TICK_SECONDS, one: one.localFlightPhase, two: two.localFlightPhase, mode: system.mode })
  }
  expect(approachAt, 'positive control: the healthy selected GPS reaches approach phase').not.toBeNull()
  expect(one.gps[degradedGps - 1].bus()!['130']).toEqual({ value: 0.5, ssm: 'NORMAL' })
  expect(system.navigationSide).toBe(degradedGps === 1 ? 2 : 1)
  expect(mismatches).toEqual([])
  expect([one.localFlightPhase, two.localFlightPhase]).toEqual(['APPROACH', 'APPROACH'])
})

test('KBTV LPV stays in SYNC with its shared arm when only GPS2 SBAS becomes do-not-use at 3 NM on final', () => {
  const { system, one, two, tick } = setup('kbtv-rnav15', LATER_SBAS_PROFILE)
  let failedAt: number | null = null
  const mismatches: unknown[] = []
  for (let n = 1; n * TICK_SECONDS <= 1000; n++) {
    tick()
    const runway = one.coordinates('RW15')
    if (failedAt === null && system.simulator.approachMode === 'CAPTURED' && runway && distanceNm(one.position, runway) <= 3) {
      expect(one.approachType).toBe('LPV')
      one.gps[1].setSbas({ doNotUse: true })
      failedAt = n * TICK_SECONDS
    }
    if (failedAt !== null && (!one.approachArmed || !two.approachArmed || one.localFlightPhase !== two.localFlightPhase || system.mode !== 'SYNC'))
      mismatches.push({ at: n * TICK_SECONDS, one: one.localFlightPhase, two: two.localFlightPhase, armed: two.approachArmed, mode: system.mode })
    if (failedAt !== null && n * TICK_SECONDS - failedAt > 45) break
  }
  expect(failedAt, 'positive control: LPV captures before degrading the offside receiver').not.toBeNull()
  expect(one.gps[1].bus()!['305'].value?.level).toBe('LNAV')
  expect(mismatches).toEqual([])
  expect(system.simulator.approachMode).toBe('CAPTURED')
  expect(system.flights[1].modeEvents.some(event => event.event === 'APPR LOST')).toBe(true)
  expect([one, two].map(unit => unit.recallList.filter(message => message.text === 'NO APPR INTEGRITY'))).toEqual([[], []])
  // Losing the selected receiver's SBAS as well still makes the driving flight disarm the aircraft switch.
  one.gps[0].setSbas({ doNotUse: true })
  for (let n = 0; n < 8; n++) tick()
  expect(system.simulator.approachMode).toBe('OFF')
  expect([one.approachArmed, two.approachArmed]).toEqual([false, false])
  expect(system.simulator.modeEvents.some(event => event.event === 'APPR LOST')).toBe(true)
})

for (const loss of ['vertical', 'lateral'] as const) test(`an independent observing flight can lose ${loss} LPV capability without disarming the aircraft switch`, () => {
  const { system, one, two, tick } = setup('kbtv-rnav15', LATER_SBAS_PROFILE)
  for (let n = 0; n * TICK_SECONDS <= 1000 && system.flights.some(flight => flight.approachMode !== 'CAPTURED'); n++) tick()
  expect(system.flights.map(flight => flight.approachMode)).toEqual(['CAPTURED', 'CAPTURED'])
  system.setLinkAvailable(false)
  if (loss === 'vertical') one.gps[1].setSbas({ doNotUse: true })
  else one.gps[1].override('116', { kind: 'FORCE', value: null, ssm: 'NCD' })
  for (let n = 0; n < 8; n++) tick()
  expect(system.mode).toBe('INDEPENDENT')
  expect(system.flights[1].modeEvents.some(event => event.event === 'APPR LOST')).toBe(true)
  expect(system.simulator.approachMode).toBe('CAPTURED')
  expect([one.approachArmed, two.approachArmed]).toEqual([true, true])
  expect(two.localFlightPhase, 'the observer must not clear the armed final approach phase').toBe('APPROACH')
})

// Navigation election is not an AFCS approach-source transfer. It must not substitute unqualified 116/117 words
// merely because the peer already uses that receiver. The single-FMS transfer owner cannot see dual ANP election.
for (const bias of [0, 60]) test(`navigation election preserves qualified approach words with GPS2 vertical bias ${bias} FT`, () => {
  const { system, one, tick } = setup('kbtv-rnav15', LATER_SBAS_PROFILE)
  for (let n = 0; n * TICK_SECONDS < 1000; n++) {
    tick()
    const runway = one.coordinates('RW15')
    if (system.simulator.approachMode === 'CAPTURED' && runway && distanceNm(one.position, runway) <= 3) break
  }
  expect(system.simulator.approachMode).toBe('CAPTURED')
  expect(system.navigationSide).toBe(1)
  one.gps[1].override('117', { kind: 'BIAS', amount: bias })
  for (let n = 0; n < 12; n++) tick()
  const before = one.gpsApproach!
  expect(before.verticalFt).not.toBeNull()
  one.gps[0].override('247', { kind: 'FORCE', value: 0.2, ssm: 'NORMAL' })
  tick()
  const after = one.gpsApproach!
  expect(system.navigationSide, 'positive control: the system elected the peer GPS solution').toBe(2)
  expect(one.navState.gpsSource).toBe(2)
  expect(Math.abs(after.verticalFt! - before.verticalFt!)).toBeLessThanOrEqual(0.1 * after.scale!.verticalFullScaleFt)
  expect(system.mode).toBe('SYNC')
  expect(system.simulator.approachMode).toBe('CAPTURED')
})

// Negative controls: independent computers still use their own receivers, and system-wide integrity loss still
// prevents entry. These assert the boundary of #1538 rather than repeating the single-FMS integrity-policy owner.
test('independent 87N computers retain their own integrity, and losing both receivers still prevents SYNC approach entry', () => {
  for (const independent of [true, false]) {
    const { system, one, two, tick } = setup('87n-rnav190-final')
    if (independent) system.setLinkAvailable(false)
    one.gps[1].override('130', { kind: 'FORCE', value: 0.5, ssm: 'NORMAL' })
    if (!independent) one.gps[0].override('130', { kind: 'FORCE', value: 0.5, ssm: 'NORMAL' })
    for (let n = 0; n * TICK_SECONDS < 100; n++) tick()
    expect([one.localFlightPhase, two.localFlightPhase]).toEqual(independent ? ['APPROACH', 'TERMINAL'] : ['TERMINAL', 'TERMINAL'])
    expect(system.mode).toBe(independent ? 'INDEPENDENT' : 'SYNC')
  }
})
