import { expect, logicTest as test } from './isolated-client-test'
import { FmsKernel } from '../src/fmsCdu/kernel/kernel'
import { dualComposition } from '../src/fmsCdu/kernel/legacyPlantAdapter'
import { distanceNm } from '../src/fmsCdu/fmsModel'
import { MISSION_87N_OFFSHORE_SAR } from '../src/fmsCdu/heliDemo'
import { START_STATES } from '../src/fmsCdu/kbtvDemo'
import { LATER_SBAS_PROFILE, type AircraftProfile } from '../src/fmsCdu/profile'
import { TICK_SECONDS } from '../src/fmsCdu/scenario'

// #1538 owner: M300 3-24 synchronizes GPS approach-integrity availability; 3-25 uses the best system sensor.
// Local receiver degradation must not give the offside computer a different approach phase or disarm the aircraft's
// panel approach switch. fms-dual-approach-phase covers healthy inputs, not these asymmetric receiver failures.
function setup(start: '87n-rnav190-final' | 'kbtv-rnav15', profile?: AircraftProfile) {
  const now = start === '87n-rnav190-final' ? Date.parse(MISSION_87N_OFFSHORE_SAR.startTime!) : Date.UTC(2026, 8, 27, 14)
  const { system, plant } = dualComposition(now, { profile })
  const [one, two] = system.computers
  expect(one.compute(() => {
    const result = START_STATES[start].setUp(one, system.flights[0])
    one.dualOperation?.settingsChanged(); one.dualOperation?.finishEdit(true); two.observeAircraft(one)
    return result
  })).toEqual({ ready: true })
  const kernel = new FmsKernel(plant)
  return { system, one, two, tick: () => { kernel.advance(1) } }
}

// Independent external-word vectors: each actual frame changes117, while the two inputs stay exactly60FT
// apart. This avoids adding a bias to naturally unequal receiver errors, and supplies no selection/receipt law.
function movingJump(one: ReturnType<typeof setup>['one'], tick: () => void) {
  let sequence = 0, healthy = true
  const publish = () => {
    one.gps[0].override('117', { kind: 'FORCE', value: sequence / 1000, ssm: healthy ? 'NORMAL' : 'FW' })
    one.gps[1].override('117', { kind: 'FORCE', value: 60 + sequence / 1000, ssm: 'NORMAL' })
  }
  return {
    fly(frames: number) { for (let n = 0; n < frames; n++) { sequence++; publish(); tick() } },
    setHealthy(value: boolean) { healthy = value; publish() },
  }
}

// Public Sample.sequence identifies a producer frame. One crew input must publish one new frame, then both
// computers consume that frame; a duplicate native sample or an old FMS2 frame changes real sensor provenance.
for (const side of [1, 2] as const) for (const origin of [1, 2] as const)
  test(`one FMS${origin} zero-time input publishes one coherent sensor frame with FMS${side} guiding`, () => {
    const { system, one, two } = setup('87n-rnav190-final')
    system.selectGuidance(side)
    one.gpsUpdated()
    const before = one.navigationInputs!
    const time = one.now.getTime()
    const aircraft = system.computers.map(unit => structuredClone(unit.truePosition))
    system.computers[origin - 1].gpsUpdated()
    const published = one.navigationInputs!, consumed = two.navigationInputs!
    expect(published.air.sequence).toBe(before.air.sequence + 1)
    expect(consumed).toEqual(published)
    expect(published.air.at).toBe(time)
    expect(system.computers.map(unit => unit.now.getTime())).toEqual([time, time])
    expect(system.computers.map(unit => unit.truePosition)).toEqual(aircraft)
  })

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
  expect(mismatches).toEqual([])
  expect(system.navigationSide).toBe(degradedGps === 1 ? 2 : 1)
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
  one.selectGpsReceiver('GPS1'); two.selectGpsReceiver('GPS2')
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

// #1563: ordinary TERMINAL navigation uses a literal 1 NM limit; approach permission remains strictly below
// 0.3 NM, including when denial leaves the phase TERMINAL. Forced coherent words protect consumer authority,
// not a claim that the unmodified receiver emits this combination. Healthy controls keep other vetoes visible.
for (const [hil, usable, annunciation] of [[0.299, true, 'LPV'], [0.3, true, 'NO APPR'],
  [0.301, true, 'NO APPR'], [0.5, true, 'NO APPR'], [0.999, true, 'NO APPR'], [1.001, false, 'NO APPR']] as const)
  test(`SYNC approach integrity HIL ${hil} NM keeps fixed 0.3 NM separate from TERMINAL navigation`, () => {
    const { system, one, two } = setup('kbtv-rnav15', LATER_SBAS_PROFILE)
    one.selectGpsReceiver('GPS1'); two.selectGpsReceiver('GPS2')
    const at = one.now.getTime(), position = structuredClone(one.truePosition)
    for (const receiver of one.gps) {
      receiver.override('130', { kind: 'FORCE', value: hil, ssm: 'NORMAL' })
      receiver.overrideStatus('156', { selected: true, available: true, armed: false, parked: false,
        crcInvalid: false, mismatch: false, incomplete: false })
      receiver.overrideStatus('305', { level: 'LPV' })
      receiver.override('116', { kind: 'FORCE', value: 1, ssm: 'NORMAL' })
      receiver.override('117', { kind: 'FORCE', value: 1, ssm: 'NORMAL' })
    }
    one.gpsUpdated()
    expect(system.mode).toBe('SYNC')
    // The existing uncertain-GPS retention can still display GPS above the navigation limit; usability,
    // rather than inventing a DR reversion, independently distinguishes the literal 1-NM boundary.
    expect([one, two].map(unit => unit.gpsStatus.assessed[0].usable)).toEqual([usable, usable])
    expect([one, two].map(unit => unit.localFlightPhase)).toEqual(['TERMINAL', 'TERMINAL'])
    expect([one, two].map(unit => unit.gpsApproachAuthority.annunciation)).toEqual([annunciation, annunciation])
    expect(one.now.getTime()).toBe(at)
    expect(one.truePosition).toEqual(position)
  })

// #1563 primary owner: distinct, healthy words expose the interim onside-guidance limitation.
// The 1 FT vector is below the literal minimum 10% vertical-scale bound (4.9212598425 FT);
// the retained 60 FT row below is above the maximum bound (49.2125984252 FT).
for (const guidanceSide of [1, 2] as const) test(`FMS${guidanceSide} consumes qualified elected GPS words without changing either local choice`, () => {
  const run = (cue: number) => {
    const { system, one, two, tick } = setup('kbtv-rnav15', LATER_SBAS_PROFILE)
    one.selectGpsReceiver('GPS1'); two.selectGpsReceiver('GPS2')
    system.selectGuidance(guidanceSide)
    const guiding = system.computers[guidanceSide - 1]
    guiding.armApproach(true)
    for (let n = 0; n * TICK_SECONDS < 1000; n++) {
      tick()
      const runway = guiding.coordinates('RW15')
      if (system.simulator.approachMode === 'CAPTURED' && runway && distanceNm(guiding.position, runway) <= 3) break
    }
    expect(system.simulator.approachMode).toBe('CAPTURED')
    const electedGps = 2 - guidanceSide, localGps = guidanceSide - 1
    // Independent moving117 vectors establish eligibility without assuming identical natural receiver errors.
    for (let n = 1; n <= 12; n++) {
      one.gps.forEach((receiver, index) => receiver.override('117', {
        kind: 'FORCE', value: n / 1000 + (index === electedGps ? 1 : 0), ssm: 'NORMAL' }))
      tick()
    }
    const initial = { at: guiding.now.getTime(), position: structuredClone(guiding.truePosition), heading: guiding.heading,
      bank: system.simulator.bankAngle, vertical: guiding.verticalSpeed }
    one.gps[localGps].override('247', { kind: 'FORCE', value: 0.2, ssm: 'NORMAL' })
    one.gps[localGps].override('116', { kind: 'FORCE', value: -cue, ssm: 'NORMAL' })
    one.gps[localGps].override('117', { kind: 'FORCE', value: -1, ssm: 'NORMAL' })
    one.gps[electedGps].override('116', { kind: 'FORCE', value: cue, ssm: 'NORMAL' })
    one.gps[electedGps].override('117', { kind: 'FORCE', value: 1, ssm: 'NORMAL' })
    guiding.gpsUpdated()
    expect(system.navigationSide).toBe(3 - guidanceSide)
    expect([one.gpsReceiverChoice, two.gpsReceiverChoice]).toEqual(['GPS1', 'GPS2'])
    expect(guiding.now.getTime()).toBe(initial.at)
    expect(guiding.truePosition).toEqual(initial.position)
    const bus = one.navigationInputs!.gps[electedGps].value!
    expect(bus['116']).toEqual({ value: cue, ssm: 'NORMAL' })
    expect(bus['117']).toEqual({ value: 1, ssm: 'NORMAL' })
    expect(bus.scale.value!.lateralFullScaleFt).toBeGreaterThan(40)
    expect(bus.scale.value!.verticalFullScaleFt).toBeGreaterThanOrEqual(49.21259842519685)
    const bankBefore = system.simulator.bankAngle, verticalBefore = guiding.verticalSpeed
    tick()
    expect(system.simulator.approachMode).toBe('CAPTURED')
    expect(Math.abs(system.simulator.bankAngle - bankBefore), 'roll-rate clipping cannot hide the116 response')
      .toBeLessThan(LATER_SBAS_PROFILE.parameters.rollRate.value * TICK_SECONDS)
    const requested = -guiding.groundSpeed * 101.27 * Math.tan(Math.PI / 60) - 2
    expect(Math.abs(requested - verticalBefore), 'acceleration clipping cannot hide literal117=1FT')
      .toBeLessThan(LATER_SBAS_PROFILE.parameters.verticalAccel.value * TICK_SECONDS)
    expect(guiding.verticalSpeed).toBeCloseTo(requested, 6)
    return { initial, bankBefore, bankAfter: system.simulator.bankAngle, headingAfter: guiding.heading,
      crossTrackAfter: system.simulator.guidance.crossTrack }
  }
  const right = run(4), left = run(-4)
  // Same reached aircraft/input history, distinct independent cue signs. This cancels the identical baseline
  // heading/wind/roll response without pretending a late HDG manoeuvre remained captured or reached zero bank.
  expect(right.initial).toEqual(left.initial)
  expect(right.bankBefore).toBe(left.bankBefore)
  expect(right.bankAfter, 'right-positive elected116 commands more left bank than the opposite cue').toBeLessThan(left.bankAfter)
  expect(right.headingAfter).toBeLessThan(left.headingAfter)
  expect(right.crossTrackAfter).toBeCloseTo(4 / 6076.12, 8)
  expect(left.crossTrackAfter).toBeCloseTo(-4 / 6076.12, 8)
})

test('navigation election refuses the literal 60 FT vertical jump while current live guidance remains valid', () => {
  const { system, one, tick } = setup('kbtv-rnav15', LATER_SBAS_PROFILE)
  for (let n = 0; n * TICK_SECONDS < 1000; n++) {
    tick()
    const runway = one.coordinates('RW15')
    if (system.simulator.approachMode === 'CAPTURED' && runway && distanceNm(one.position, runway) <= 3) break
  }
  expect(system.simulator.approachMode).toBe('CAPTURED')
  expect(system.navigationSide).toBe(1)
  const words = movingJump(one, tick)
  words.fly(12)
  const before = one.gpsApproach!
  expect(before.verticalFt).not.toBeNull()
  expect(before.scale!.verticalFullScaleFt).toBeLessThanOrEqual(492.1259842519685)
  one.gps[0].override('247', { kind: 'FORCE', value: 0.2, ssm: 'NORMAL' })
  tick()
  const after = one.gpsApproach!
  expect(system.navigationSide, 'position election remains independent of approach acceptance').toBe(2)
  expect(one.navState.gpsSource).toBe(2)
  expect(Math.abs(after.verticalFt! - before.verticalFt!)).toBeLessThanOrEqual(0.1 * after.scale!.verticalFullScaleFt)
  expect(system.mode).toBe('SYNC')
  expect(system.simulator.approachMode).toBe('CAPTURED')
})

// The reference belongs to the aircraft, not the newly selected computer's local AUTO history.
test('guidance handover and offside selection cannot replace the recently consumed aircraft reference', () => {
  const { system, one, two, tick } = setup('kbtv-rnav15', LATER_SBAS_PROFILE)
  one.selectGpsReceiver('GPS1'); two.selectGpsReceiver('GPS2')
  for (let n = 0; n * TICK_SECONDS < 1000; n++) {
    tick()
    const runway = one.coordinates('RW15')
    if (system.simulator.approachMode === 'CAPTURED' && runway && distanceNm(one.position, runway) <= 3) break
  }
  expect(system.simulator.approachMode).toBe('CAPTURED')
  const words = movingJump(one, tick)
  words.fly(12)
  expect(two.gpsStatus.chosen).toBe(1)
  const at = one.utcTime.getTime(), position = structuredClone(one.truePosition)
  const fas = one.executedFas!.fas.crc
  expect(one.modify(route => { route.flightNo = 'MOD1563' })).toBe(true)
  one.gpsUpdated(); one.press('CLR'); one.eraseModification()
  expect(one.modify(route => { route.flightNo = 'EXEC1563' })).toBe(true)
  one.press('EXEC'); one.gpsUpdated(); two.gpsUpdated()
  // Existing SYNC EXEC copies the editor's settings. Restore the declared offside manual choice through crew input.
  expect([one.gpsReceiverChoice, two.gpsReceiverChoice]).toEqual(['GPS1', 'GPS1'])
  two.selectGpsReceiver('GPS2'); two.gpsUpdated()
  expect([one, two].map(unit => unit.executedFas!.fas.crc)).toEqual([fas, fas])
  system.selectGuidance(2) // real handover disarms; its guide/observation must not issue a receipt
  expect(system.simulator.approachMode).toBe('OFF')
  one.gps[0].override('247', { kind: 'FORCE', value: 0.2, ssm: 'NORMAL' })
  two.gpsUpdated()
  two.armApproach(true); two.gpsUpdated() // real crew re-arm, still before any physical frame
  const buses = one.navigationInputs!.gps.map(sample => sample.value!)
  expect(buses[1]['117'].value! - buses[0]['117'].value!).toBeCloseTo(60, 1)
  expect(buses[1].scale.value!.verticalFullScaleFt).toBeLessThanOrEqual(492.1259842519685)
  expect(system.navigationSide).toBe(2)
  // Local manual-selection diagnostics remain local; refusal is witnessed by retaining the current live GPS1 words.
  expect(two.gpsApproachAuthority.vertical).toBe(true)
  expect(two.gpsApproach!.verticalFt).toBe(buses[0]['117'].value)
  expect(one.utcTime.getTime()).toBe(at)
  expect(one.truePosition).toEqual(position)
  tick()
  expect(system.simulator.approachMode).toBe('CAPTURED')
})

// Public 117 loss creates real APPR LOST/ALT HOLD; no private latch or ADVISORY-only command builds this fixture.
for (const recoveredWhileHeld of [false, true]) test(`ALT HOLD ages the complete reference with full words recovered while held ${recoveredWhileHeld}`, () => {
  const { system, one, two, tick } = setup('kbtv-rnav15', LATER_SBAS_PROFILE)
  one.selectGpsReceiver('GPS1'); two.selectGpsReceiver('GPS2')
  for (let n = 0; n * TICK_SECONDS < 1000; n++) {
    tick()
    const runway = one.coordinates('RW15')
    if (system.simulator.approachMode === 'CAPTURED' && runway && distanceNm(one.position, runway) <= 3) break
  }
  expect(system.simulator.approachMode).toBe('CAPTURED')
  const words = movingJump(one, tick)
  words.fly(12)
  const lastFullTime = one.utcTime.getTime()
  const captures = system.simulator.modeEvents.filter(event => event.event === 'APPR CAPTURED').length
  words.setHealthy(false)
  one.gpsUpdated(); words.fly(1)
  expect(system.simulator.modeEvents.some(event => event.event === 'APPR LOST')).toBe(true)
  expect(system.simulator.verticalMode).toBe('ALT HOLD')
  expect(system.simulator.gpsLateralActive).toBe(true)
  words.fly(5)
  words.setHealthy(true); one.gpsUpdated()
  if (recoveredWhileHeld) words.fly(5)
  expect(one.utcTime.getTime() - lastFullTime).toBeGreaterThan(1000)
  expect(one.gpsApproachVertical).toBe(true)
  expect(system.simulator.approachMode).toBe('OFF')
  expect(system.simulator.verticalMode).toBe('ALT HOLD')
  expect(system.simulator.gpsLateralActive).toBe(true)
  one.gpsUpdated(); two.gpsUpdated()
  expect(system.simulator.modeEvents.filter(event => event.event === 'APPR CAPTURED')).toHaveLength(captures)
  one.gps[0].override('247', { kind: 'FORCE', value: 0.2, ssm: 'NORMAL' })
  one.gpsUpdated()
  one.armApproach(true); one.gpsUpdated()
  const buses = one.navigationInputs!.gps.map(sample => sample.value!)
  expect(buses[1]['117'].value! - buses[0]['117'].value!).toBeCloseTo(60, 1)
  expect(buses[1].scale.value!.verticalFullScaleFt).toBeLessThanOrEqual(492.1259842519685)
  expect(system.navigationSide).toBe(2)
  expect(one.gpsApproachAuthority.vertical).toBe(true)
  expect(one.gpsApproach!.verticalFt).toBe(buses[1]['117'].value)
  tick()
  expect(system.simulator.approachMode).toBe('CAPTURED')
})

// A healthy elected navigation receiver cannot mask loss of the receiver still flying the approach after a refused
// 116/117 transfer. Shared monitoring must follow the still-valid live adopted approach output.
for (const [guidanceSide, navigationSide] of [[1, 2], [2, 1], [1, 1]] as const) test(`real LPV loss alerts both computers with FMS${guidanceSide} guiding and FMS${navigationSide} navigation`, () => {
  const { system, one, two, tick } = setup('kbtv-rnav15', LATER_SBAS_PROFILE)
  one.selectGpsReceiver('GPS1'); two.selectGpsReceiver('GPS2')
  system.selectGuidance(guidanceSide)
  const guiding = system.computers[guidanceSide - 1]
  guiding.armApproach(true)
  for (let n = 0; n * TICK_SECONDS < 1000; n++) {
    tick()
    const runway = guiding.coordinates('RW15')
    if (system.simulator.approachMode === 'CAPTURED' && runway && distanceNm(guiding.position, runway) <= 3) break
  }
  expect(system.simulator.approachMode).toBe('CAPTURED')
  expect(system.navigationSide).toBe(1)
  const words = movingJump(one, tick)
  words.fly(12)
  if (navigationSide === 2) one.gps[0].override('247', { kind: 'FORCE', value: 0.2, ssm: 'NORMAL' })
  words.fly(1)
  expect(system.navigationSide).toBe(navigationSide)
  const buses = one.navigationInputs!.gps.map(sample => sample.value!)
  expect(buses[1]['117'].value! - buses[0]['117'].value!).toBeCloseTo(60, 1)
  // GPS1 is the declared actually flown source, including with FMS2 guiding; local selection is diagnostic.
  expect([one, two].map(unit => unit.gpsApproach!.verticalFt)).toEqual([buses[0]['117'].value, buses[0]['117'].value])
  expect([one, two].map(unit => unit.recallList.some(message => message.text === 'NO APPR INTEGRITY'))).toEqual([false, false])
  one.gps[0].setSbas({ doNotUse: true })
  one.gpsUpdated(); guiding.gpsUpdated()
  words.fly(1)
  expect(system.simulator.modeEvents.some(event => event.event === 'APPR LOST')).toBe(true)
  expect(system.simulator.verticalMode).toBe('ALT HOLD')
  expect([one, two].map(unit => unit.recallList.some(message => message.text === 'NO APPR INTEGRITY'))).toEqual([true, true])
  expect(system.mode).toBe('SYNC')
})

for (const guidanceSide of [1, 2] as const) test(`zero-time GPS input settles shared navigation while FMS${guidanceSide} guides`, () => {
  const { system, one, two, tick } = setup('87n-rnav190-final')
  system.selectGuidance(guidanceSide)
  system.computers[guidanceSide - 1].armApproach(true)
  for (let n = 0; n * TICK_SECONDS < 80; n++) tick()
  expect([one.localFlightPhase, two.localFlightPhase]).toEqual(['APPROACH', 'APPROACH'])
  const utc = one.utcTime.getTime()
  one.gps[1].override('130', { kind: 'FORCE', value: 0.5, ssm: 'NORMAL' })
  two.gpsUpdated()
  expect(two.utcTime.getTime()).toBe(utc)
  expect(system.navigationSide).toBe(1)
  expect([one.navState.gpsSource, two.navState.gpsSource]).toEqual([1, 1])
  expect([one.localFlightPhase, two.localFlightPhase]).toEqual(['APPROACH', 'APPROACH'])
  expect([one, two].map(unit => unit.recallList.some(message => message.text === 'NO APPR INTEGRITY'))).toEqual([false, false])
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

// Fixed approach HAL applies even when the navigation assessment permits the literal 2-NM EN ROUTE band.
for (const [hil, usable, annunciation] of [[0.299, true, 'LPV'], [0.3, true, 'NO APPR'],
  [0.5, true, 'NO APPR'], [1.001, true, 'NO APPR'], [1.999, true, 'NO APPR'], [2.001, false, 'NO APPR']] as const)
  test(`SYNC approach integrity HIL ${hil} NM keeps fixed 0.3 NM separate from EN ROUTE navigation`, () => {
    const { system, one, two } = setup('kbtv-rnav15', LATER_SBAS_PROFILE)
    one.selectGpsReceiver('GPS1'); two.selectGpsReceiver('GPS2')
    one.placeAircraft({ position: { lat: 43, lon: -71 }, altitude: 8000, track: 150 }, 'test: declared remote EN ROUTE input')
    two.observeAircraft(one)
    for (const receiver of one.gps) {
      receiver.override('130', { kind: 'FORCE', value: hil, ssm: 'NORMAL' })
      receiver.overrideStatus('156', { selected: true, available: true, armed: false, parked: false,
        crcInvalid: false, mismatch: false, incomplete: false })
      receiver.overrideStatus('305', { level: 'LPV' })
      receiver.override('116', { kind: 'FORCE', value: 1, ssm: 'NORMAL' })
      receiver.override('117', { kind: 'FORCE', value: 1, ssm: 'NORMAL' })
    }
    const at = one.now.getTime(), position = structuredClone(one.truePosition)
    one.gpsUpdated()
    expect(system.mode).toBe('SYNC')
    expect([one, two].map(unit => unit.localFlightPhase)).toEqual(['EN ROUTE', 'EN ROUTE'])
    expect([one, two].map(unit => unit.gpsStatus.assessed[0].usable)).toEqual([usable, usable])
    expect([one, two].map(unit => unit.gpsApproachAuthority.annunciation)).toEqual([annunciation, annunciation])
    expect(one.now.getTime()).toBe(at)
    expect(one.truePosition).toEqual(position)
  })

test('system adoption waits 1.75 seconds then accepts at 2.00 seconds of actual fresh eligibility', () => {
  const { system, one, two, tick } = setup('kbtv-rnav15', LATER_SBAS_PROFILE)
  one.selectGpsReceiver('GPS1'); two.selectGpsReceiver('GPS2')
  let sequence = 0
  const frame = () => {
    sequence++
    one.gps.forEach((receiver, index) => receiver.override('117', {
      kind: 'FORCE', value: index + sequence / 1000, ssm: 'NORMAL' }))
    tick()
  }
  one.gps[1].override('117', { kind: 'FORCE', value: null, ssm: 'FW' }); tick()
  for (let n = 0; n < 16 && !two.navigationOutput.approach?.eligible; n++) frame()
  expect(two.navigationOutput.approach?.eligible).toBe(true)
  const since = one.now.getTime()
  expect(two.navigationOutput.approach?.eligibleSince).toBe(since)
  for (let n = 0; n < 7; n++) { frame(); two.gpsUpdated() }
  expect(one.now.getTime() - since).toBe(1750)
  one.gps[0].override('247', { kind: 'FORCE', value: 0.2, ssm: 'NORMAL' })
  const position = structuredClone(one.truePosition)
  for (let n = 0; n < 8; n++) two.gpsUpdated()
  expect(system.navigationSide).toBe(2)
  expect(one.now.getTime() - since).toBe(1750)
  expect(one.truePosition).toEqual(position)
  expect(one.gpsApproach!.verticalFt).toBe(one.navigationInputs!.gps[0].value!['117'].value)
  frame()
  expect(one.now.getTime() - since).toBe(2000)
  expect(one.gpsApproach!.verticalFt).toBe(one.navigationInputs!.gps[1].value!['117'].value)
  expect(one.approachArmed).toBe(true)
})

test('system transfer refuses a reached greater-than-5-percent scaling change with a matching positive control', () => {
  const run = (spoof: boolean) => {
    const { system, one, two, tick } = setup('kbtv-rnav15', LATER_SBAS_PROFILE)
    one.selectGpsReceiver('GPS1'); two.selectGpsReceiver('GPS2')
    for (let n = 0; n * TICK_SECONDS < 1000; n++) {
      tick()
      const runway = one.coordinates('RW15')
      if (system.simulator.approachMode === 'CAPTURED' && runway && distanceNm(one.position, runway) <= 3) break
    }
    expect(system.simulator.approachMode).toBe('CAPTURED')
    for (let n = 1; n <= 12; n++) {
      one.gps.forEach((receiver, index) => {
        receiver.override('116', { kind: 'FORCE', value: index + n / 1000, ssm: 'NORMAL' })
        receiver.override('117', { kind: 'FORCE', value: index + n / 1000, ssm: 'NORMAL' })
      })
      tick()
    }
    const oldScale = structuredClone(one.gpsApproach!.scale!), at = one.now.getTime(), position = structuredClone(one.truePosition)
    // Same declared600-m north spoof on both real receivers changes distance-dependent native scaling without
    // manufacturing a receiver disagreement. 201 overrides are not used: they occur after scaling computation.
    if (spoof) one.gps.forEach(receiver => receiver.setSpoof({ northM: 600, eastM: 0, driftNorthMps: 0, driftEastMps: 0 }))
    one.gps[0].override('247', { kind: 'FORCE', value: 0.2, ssm: 'NORMAL' })
    one.gpsUpdated()
    const buses = one.navigationInputs!.gps.map(sample => sample.value!)
    const nextScale = buses[1].scale.value!
    const scaleChange = Math.abs(nextScale.verticalFullScaleFt - oldScale.verticalFullScaleFt)
      / Math.max(nextScale.verticalFullScaleFt, oldScale.verticalFullScaleFt)
    if (spoof) expect(scaleChange).toBeGreaterThan(0.05)
    else expect(scaleChange).toBeLessThan(0.05)
    expect(two.navigationOutput.approach?.receiversDisagree).toBe(false)
    expect(two.navigationOutput.approach?.eligible).toBe(true)
    expect(system.navigationSide).toBe(2)
    expect(one.gpsApproach!.verticalFt).toBe(buses[spoof ? 0 : 1]['117'].value)
    expect(system.simulator.approachMode).toBe('CAPTURED')
    expect(one.now.getTime()).toBe(at)
    expect(one.truePosition).toEqual(position)
  }
  run(false); run(true)
})

// Current completed-output admission is a production peer-reader boundary, not a retained command or test callback.
test('a completed peer approach output is unavailable while its actual producer is off, testing or failed', () => {
  const { system, one, two, tick } = setup('kbtv-rnav15', LATER_SBAS_PROFILE)
  one.selectGpsReceiver('GPS1'); two.selectGpsReceiver('GPS2')
  expect(one.navigationOutput.approach?.computer).toBe(1)
  expect(two.navigationOutput.approach?.computer).toBe(2)
  expect(one.navigationOutput.approach?.receiver).toBe(0)
  expect(two.navigationOutput.approach?.receiver).toBe(1)
  const at = one.now.getTime(), position = structuredClone(one.truePosition)
  for (let n = 0; n < 5; n++) { void one.navigationOutput; void two.approachGuidanceOutput }
  expect(one.now.getTime()).toBe(at)
  expect(one.truePosition).toEqual(position)
  one.powerOff()
  expect(one.navigationOutput.approach).toBeNull()
  expect(one.approachGuidanceOutput).toBeNull()
  expect(system.mode).toBe('INDEPENDENT')
  one.powerOn('WARM', false)
  expect(one.powerState).toBe('TEST')
  expect(one.navigationOutput.approach).toBeNull()
  expect(one.approachGuidanceOutput).toBeNull()
  for (let n = 0; n < 240 && one.powerState !== 'ON'; n++) tick()
  expect(one.powerState).toBe('ON')
  one.gpsUpdated()
  expect(one.navigationOutput.approach?.computer).toBe(1)
  one.setCondition('fmsFail', true)
  expect(one.navigationOutput.approach).toBeNull()
  expect(one.approachGuidanceOutput).toBeNull()
  one.setCondition('fmsFail', false); one.gpsUpdated()
  expect(one.navigationOutput.approach?.computer).toBe(1)
})
