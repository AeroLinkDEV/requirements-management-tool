import { expect, logicTest as test } from './isolated-client-test'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { distanceNm } from '../src/fmsCdu/fmsModel'
import type { GpsBus } from '../src/fmsCdu/gps'
import {
  AutoSelection, ELIGIBILITY_DWELL_S, TRANSFER_JUMP_FRACTION, TRANSFER_SCALE_TOLERANCE, approachWords, assessReceiver, type SelectionInput,
} from '../src/fmsCdu/gpsSensors'
import { stimulusFor, type GpsOp } from '../src/fmsCdu/gpsStimulus'
import { setUpKbtvRnav15 } from '../src/fmsCdu/kbtvDemo'
import { runHeadless } from '../src/fmsCdu/scenario'
import { SCENARIO_LIBRARY } from '../src/fmsCdu/scenarioLibrary'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { screenText } from '../src/fmsCdu/screen'

// Approach-aware AUTO receiver selection (gpsSensors.ts AutoSelection): the AeroLink simulator policy decided on
// 29 September, cases 1 to 6, no switch back, no automatic recapture, and no approach on an unqualified change of
// source. Flown on the real KBTV RNAV (GPS) RWY 15 (FAA CIFP 2609, a published FAS), from its start state.
const START = Date.UTC(2026, 8, 27, 14, 0, 0)

const setup = () => {
  let now = START
  const fms = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(fms)
  expect(setUpKbtvRnav15(fms)).toEqual({ ready: true })
  const tick = () => { now += 250; sim.step(0.25) }
  const fly = (seconds: number) => { for (let i = 0; i < seconds * 4; i += 1) tick() }
  const until = (what: string, done: () => boolean, seconds = 900) => {
    for (let i = 0; i < seconds * 4 && !done(); i += 1) tick()
    expect(done(), what).toBe(true)
  }
  const gps = (receiver: 1 | 2, op: GpsOp) => expect(stimulusFor(fms).apply(receiver - 1, op)).toBe(true)
  const toThreshold = () => distanceNm(fms.truePosition, fms.coordinates('RW15')!)
  const alerts = () => fms.recallList.map(message => message.text)
  const lost = () => sim.modeEvents.filter(event => event.event === 'APPR LOST').length
  const capture = () => until('captured on LPV', () => sim.approachMode === 'CAPTURED' && fms.approachType === 'LPV')
  return { fms, sim, fly, until, gps, toThreshold, alerts, lost, capture, now: () => now }
}
type Bench = ReturnType<typeof setup>

/** Captured on LPV and flown to 3 NM from the threshold, on GPS1, with nothing wrong. */
const onFinal = (bench: Bench = setup()) => {
  bench.capture()
  bench.until('3 NM out', () => bench.toThreshold() <= 3)
  expect(bench.fms.gpsStatus.chosen).toBe(0)
  return bench
}

const expectApproachContinuesOn = (bench: Bench, receiver: 0 | 1, seconds = 10) => {
  for (let i = 0; i < seconds; i += 1) {
    bench.fly(1)
    expect(bench.fms.gpsStatus.chosen).toBe(receiver)
    expect(bench.sim.approachMode).toBe('CAPTURED')
    expect(bench.sim.verticalMode).toBe('APPR')
    expect(bench.fms.approachType).toBe('LPV')
  }
  expect(bench.lost()).toBe(0)
  expect(bench.alerts()).not.toContain('NO APPR INTEGRITY')
}

const expectApproachLost = (bench: Bench) => {
  bench.fly(2)
  expect(bench.sim.approachMode).toBe('OFF')
  expect(bench.sim.verticalMode).toBe('ALT HOLD')
  expect(bench.lost()).toBe(1)
  expect(bench.alerts()).toContain('NO APPR INTEGRITY')
}

test('case 1: both receivers support the approach, so the current one is kept, even when it is GPS 2', () => {
  const bench = setup()
  // GPS 2 becomes the current receiver (GPS 1 failed), then GPS 1 recovers before the approach: both are eligible.
  bench.gps(1, { op: 'fault', fault: 'RECEIVER', on: true })
  expect(bench.fms.gpsStatus.chosen).toBe(1)
  bench.fly(5)
  bench.gps(1, { op: 'fault', fault: 'RECEIVER', on: false })
  bench.capture()
  bench.until('1 NM out', () => bench.toThreshold() <= 1)
  expect(bench.fms.gpsStatus.chosen).toBe(1)
  expect(bench.fms.navSourceLog.map(entry => entry.source)).toEqual(['GPS2', 'GPS1'])
  expect(bench.alerts().filter(text => text.startsWith('APPR ON'))).toEqual([])
})

test('case 1, en route: a recovered GPS 1 does not take the navigation back from GPS 2', () => {
  let now = START
  const fms = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(fms)
  const stimulus = stimulusFor(fms)
  stimulus.apply(0, { op: 'fault', fault: 'RECEIVER', on: true })
  for (let i = 0; i < 20; i += 1) { now += 250; sim.step(0.25) }
  stimulus.apply(0, { op: 'fault', fault: 'RECEIVER', on: false })
  for (let i = 0; i < 120; i += 1) { now += 250; sim.step(0.25) }
  expect(fms.gpsStatus.assessed[0].usable).toBe(true)
  expect(fms.gpsStatus.chosen).toBe(1)
  expect(fms.navSourceLog[0].source).toBe('GPS2')
})

test('case 2: before capture, GPS 1 offers only LNAV and GPS 2 supports LPV, so GPS 2 is selected once established', () => {
  const bench = setup()
  // One second in: GPS 2 has been eligible (fresh LPV guidance for the approach) for well under the dwell.
  bench.fly(1)
  expect(bench.sim.approachMode).toBe('ARMED')
  bench.gps(1, { op: 'sbas', doNotUse: true })
  // Its eligibility has not yet held for the dwell: GPS 1 stays, and the refusal says why.
  expect(bench.fms.gpsStatus.chosen).toBe(0)
  expect(bench.fms.gpsApproachSource.refused).toBe('GPS2 NOT YET ESTABLISHED')
  bench.until('GPS 2 selected', () => bench.fms.gpsStatus.chosen === 1, ELIGIBILITY_DWELL_S + 1)
  expect(bench.alerts()).toContain('APPR ON GPS2')
  expect(bench.fms.navSourceLog[0].source).toBe('GPS2')
  bench.capture()
  expect(bench.fms.gpsStatus.chosen).toBe(1)
  expect(bench.lost()).toBe(0)
})

test('case 2, late: GPS 2 established long after GPS 1 lost LPV is still selected before capture (no guidance being flown to jump from)', () => {
  const bench = setup()
  bench.fly(1)
  // GPS 2 stale for 12 s, GPS 1 without SBAS: nobody can take the approach, and GPS 1's LPV guidance is 12 s behind.
  bench.gps(2, { op: 'override', label: '117', kind: 'FREEZE' })
  bench.gps(1, { op: 'sbas', doNotUse: true })
  bench.fly(12)
  expect(bench.fms.gpsStatus.chosen).toBe(0)
  bench.gps(2, { op: 'clearOverride', label: '117' })
  bench.until('GPS 2 selected', () => bench.fms.gpsStatus.chosen === 1, ELIGIBILITY_DWELL_S + 2)
  expect(bench.sim.approachMode).toBe('ARMED')
  expect(bench.fms.gpsApproachSource).toEqual({ qualified: true, refused: '' })
  bench.capture()
  expect(bench.fms.gpsStatus.chosen).toBe(1)
})

test('case 3: LPV captured, GPS 1 loses SBAS, and the approach transfers to GPS 2 without a jump or a loss', () => {
  const bench = onFinal()
  const before = bench.fms.gpsApproach!
  bench.gps(1, { op: 'sbas', doNotUse: true })
  // On the same update: the source, the annunciation, the log, the guidance and the approach agree.
  expect(bench.fms.gpsStatus.chosen).toBe(1)
  expect(bench.fms.navSourceLog[0]).toMatchObject({ source: 'GPS2', at: new Date(bench.now()) })
  expect(bench.alerts()).toContain('APPR ON GPS2')
  expect(bench.fms.gpsApproachAuthority).toMatchObject({ annunciation: 'LPV', lateral: true, vertical: true })
  const after = bench.fms.gpsApproach!
  expect(Math.abs(after.verticalFt! - before.verticalFt!)).toBeLessThanOrEqual(TRANSFER_JUMP_FRACTION * after.scale!.verticalFullScaleFt)
  expect(Math.abs(after.lateralFt! - before.lateralFt!)).toBeLessThanOrEqual(TRANSFER_JUMP_FRACTION * after.scale!.lateralFullScaleFt)
  expectApproachContinuesOn(bench, 1)
})

test('case 3: GPS 1 losing its lateral guidance (116) is taken over the same way', () => {
  const bench = onFinal()
  bench.fms.gps[0].override('116', { kind: 'FORCE', ssm: 'FW' })
  bench.fms.gpsUpdated()
  expect(bench.fms.gpsStatus.chosen).toBe(1)
  expectApproachContinuesOn(bench, 1)
})

const rejected: [string, GpsOp | ((bench: Bench) => void), string][] = [
  ['its approach identity mismatched (156)', { op: 'status', label: '156', patch: { mismatch: true } }, ''],
  ['its vertical deviation invalid (117 NCD)', { op: 'override', label: '117', kind: 'FORCE', amount: 0, ssm: 'NCD' }, ''],
  ['its vertical deviation stale (117 frozen)', { op: 'override', label: '117', kind: 'FREEZE' }, ''],
  ['its vertical guidance 60 ft off the one flown', { op: 'override', label: '117', kind: 'BIAS', amount: 60 }, 'GPS2 VERTICAL JUMP'],
  ['its lateral guidance 300 ft off the one flown', { op: 'override', label: '116', kind: 'BIAS', amount: 300 }, 'GPS2 LATERAL JUMP'],
  ['its position 300 m from GPS 1 (a disagreement)', { op: 'spoof', northM: 300, driftEastMps: 0 }, 'GPS1/GPS2 DISAGREE'],
]
for (const [what, stimulus, reason] of rejected) {
  test(`case 4: GPS 2 still reports LPV but ${what}: no transfer, and the approach is lost`, () => {
    const bench = onFinal()
    if (typeof stimulus === 'function') stimulus(bench)
    else bench.gps(2, stimulus)
    bench.fly(3)
    expect(bench.fms.gps[1].bus()!['305'].value!.level).toBe('LPV')
    bench.gps(1, { op: 'sbas', doNotUse: true })
    expect(bench.fms.gpsStatus.chosen).toBe(0)
    expect(bench.fms.gpsApproachSource.refused).toBe(reason)
    expectApproachLost(bench)
    expect(bench.alerts()).not.toContain('APPR ON GPS2')
  })
}

test('case 5: neither receiver can continue LPV: the current one is kept, annunciated, and the approach is lost', () => {
  const bench = onFinal()
  bench.gps(2, { op: 'sbas', doNotUse: true })
  bench.gps(1, { op: 'sbas', doNotUse: true })
  expect(bench.fms.gpsStatus.chosen).toBe(0)
  expect(bench.fms.gpsApproachSource).toEqual({ qualified: true, refused: '' })
  expectApproachLost(bench)
  expect(bench.fms.approachType).toBe('LNAV')
})

for (const receiver of [1, 2] as const) {
  test(`case 6: GPS${receiver} chosen by hand keeps the manual contract: no transfer when it loses SBAS`, () => {
    const bench = setup()
    bench.fms.selectGpsReceiver(`GPS${receiver}`)
    bench.capture()
    bench.until('3 NM out', () => bench.toThreshold() <= 3)
    bench.gps(receiver, { op: 'sbas', doNotUse: true })
    bench.fly(5)
    expect(bench.fms.gpsStatus.chosen).toBe(receiver - 1)
    expect(bench.fms.gpsStatus.assessed[2 - receiver].usable).toBe(true)
    expectApproachLost(bench)
    expect(bench.alerts().filter(text => text.startsWith('APPR ON'))).toEqual([])
  })
}

test('no switch back: GPS 1 recovering after a transfer does not take the approach back', () => {
  const bench = onFinal()
  bench.gps(1, { op: 'sbas', doNotUse: true })
  expect(bench.fms.gpsStatus.chosen).toBe(1)
  bench.fly(3)
  bench.gps(1, { op: 'sbas', doNotUse: false })
  bench.fly(5)
  expect(bench.fms.gps[0].bus()!['305'].value!.level).toBe('LPV')
  expectApproachContinuesOn(bench, 1)
  expect(bench.alerts()).not.toContain('APPR ON GPS1')
  expect(bench.fms.navSourceLog.map(entry => entry.source).slice(0, 2)).toEqual(['GPS2', 'GPS1'])
})

test('no automatic recapture: once the approach is lost, SBAS returning on both receivers does not resume it', () => {
  const bench = onFinal()
  bench.gps(2, { op: 'sbas', doNotUse: true })
  bench.gps(1, { op: 'sbas', doNotUse: true })
  expectApproachLost(bench)
  bench.gps(1, { op: 'sbas', doNotUse: false })
  bench.gps(2, { op: 'sbas', doNotUse: false })
  for (let i = 0; i < 20; i += 1) {
    bench.fly(1)
    expect(bench.sim.approachMode).toBe('OFF')
    expect(bench.sim.verticalMode).toBe('ALT HOLD')
  }
  expect(bench.fms.approachType).toBe('LPV')
  expect(bench.lost()).toBe(1)
})

test('an unqualified change of source during the approach is not flown: GPS 1 fails and GPS 2 is stale', () => {
  const bench = onFinal()
  bench.gps(2, { op: 'override', label: '117', kind: 'FREEZE' })
  bench.fly(3)
  bench.gps(1, { op: 'fault', fault: 'RECEIVER', on: true })
  // GPS 2 is navigated on for position (it is usable), but the approach may not be flown on it.
  expect(bench.fms.gpsStatus.chosen).toBe(1)
  expect(bench.fms.gpsApproachSource.qualified).toBe(false)
  expect(bench.fms.gpsApproachAuthority).toMatchObject({ annunciation: 'NO APPR', reason: 'GPS SOURCE CHANGE NOT QUALIFIED' })
  expectApproachLost(bench)
  expect(bench.alerts()).not.toContain('APPR ON GPS2')
})

test('library: SBAS lost on GPS 1 on final, and LPV continues on GPS 2', () => {
  const scenario = SCENARIO_LIBRARY.find(entry => entry.id === 'kbtv-rnav15-sbas-lost-gps1')!
  const { runner } = runHeadless(scenario)
  expect(runner.results.filter(result => result.status !== 'done' && result.status !== 'pass')).toEqual([])
  expect(runner.outcome).toBe('passed')
})

// ------------------------------------------------------------------ the thresholds, on the selection alone

type Change = { dwell?: number; verticalFt?: number; lateralFt?: number; scale?: number; sentCrc?: number; gps1Unusable?: boolean; gps2Level?: 'LNAV' }

/** Two real buses from the KBTV final, then synthetic updates on them: moving, the words changing, GPS 1 flown first. */
const selectionOn = (bench: Bench) => {
  const base = bench.fms.gps.map(receiver => structuredClone(receiver.bus()!)) as GpsBus[]
  const crc = bench.fms.executedFas!.fas.crc
  const run = (change: Change, selection = new AutoSelection()) => {
    const step = (k: number, time: number, gps1Level: 'LPV' | 'LNAV', last: boolean) => {
      const buses = base.map((bus, i) => {
        const next = structuredClone(bus)
        next['116'].value = bus['116'].value! + k * 0.01 + (last && i === 1 ? change.lateralFt ?? 0 : 0)
        next['117'].value = bus['117'].value! + k * 0.01 + (last && i === 1 ? change.verticalFt ?? 0 : 0)
        next['201'].value = bus['201'].value! - k * 0.001
        if (i === 0) next['305'].value = { ...next['305'].value!, level: gps1Level }
        if (last && i === 1 && change.scale) next.scale.value = { ...next.scale.value!, verticalFullScaleFt: next.scale.value!.verticalFullScaleFt * change.scale }
        if (last && i === 1 && change.gps2Level) next['305'].value = { ...next['305'].value!, level: change.gps2Level }
        if (last && i === 0 && change.gps1Unusable) next['273'].value = { ...next['273'].value!, mode: 'FAULT' }
        return next
      })
      const input: SelectionInput = {
        choice: 'AUTO', selected: true, assessed: buses.map(bus => assessReceiver(bus, 1)), buses, time,
        position: { lat: 44 + k * 0.001, lon: -73 }, executedCrc: crc, sentCrc: change.sentCrc ?? crc, approachArmed: true,
      }
      return selection.choose(input)
    }
    const dwell = (change.dwell ?? ELIGIBILITY_DWELL_S) * 1000
    let result = step(0, 0, 'LPV', false)
    for (let k = 1; k <= 8; k += 1) result = step(k, k * 250, 'LPV', false)
    expect(result.chosen).toBe(0)
    // GPS 2 eligible since the second update (the first only takes a freshness snapshot): 250 ms.
    return step(9, 250 + dwell, 'LNAV', true)
  }
  return { base, crc, run }
}

test('the transfer thresholds: the dwell, the jump fraction of full scale, and the scaling tolerance, at their boundaries', () => {
  const { base, run } = selectionOn(onFinal())
  expect(run({})).toMatchObject({ chosen: 1, transferred: true, refused: '' })
  expect(run({ dwell: ELIGIBILITY_DWELL_S - 0.25 })).toMatchObject({ chosen: 0, refused: 'GPS2 NOT YET ESTABLISHED' })
  const scale = approachWords(base[1]).scale!
  const flownVertical = approachWords(base[0]).verticalFt! + 8 * 0.01, gps2Vertical = approachWords(base[1]).verticalFt! + 9 * 0.01
  const verticalLimit = TRANSFER_JUMP_FRACTION * scale.verticalFullScaleFt
  const toVertical = (offset: number) => flownVertical + offset - gps2Vertical
  expect(run({ verticalFt: toVertical(verticalLimit - 0.5) })).toMatchObject({ chosen: 1, refused: '' })
  expect(run({ verticalFt: toVertical(verticalLimit + 0.5) })).toMatchObject({ chosen: 0, refused: 'GPS2 VERTICAL JUMP' })
  expect(run({ verticalFt: toVertical(-verticalLimit - 0.5) })).toMatchObject({ chosen: 0, refused: 'GPS2 VERTICAL JUMP' })
  const flownLateral = approachWords(base[0]).lateralFt! + 8 * 0.01, gps2Lateral = approachWords(base[1]).lateralFt! + 9 * 0.01
  const lateralLimit = TRANSFER_JUMP_FRACTION * scale.lateralFullScaleFt
  expect(run({ lateralFt: flownLateral + lateralLimit - 1 - gps2Lateral })).toMatchObject({ chosen: 1, refused: '' })
  expect(run({ lateralFt: flownLateral + lateralLimit + 1 - gps2Lateral })).toMatchObject({ chosen: 0, refused: 'GPS2 LATERAL JUMP' })
  expect(run({ scale: 1 + TRANSFER_SCALE_TOLERANCE - 0.01 })).toMatchObject({ chosen: 1, refused: '' })
  expect(run({ scale: 1 + TRANSFER_SCALE_TOLERANCE + 0.01 })).toMatchObject({ chosen: 0, refused: 'GPS2 SCALING DIFFERS' })
})

test('eligibility needs the executed approach sent, and an unqualified change disqualifies only that approach', () => {
  const { crc, run } = selectionOn(onFinal())
  // The FAS the FMS sent is not the executed one: GPS 2 is not eligible, so it never takes the approach over.
  expect(run({ sentCrc: crc ^ 1 })).toMatchObject({ chosen: 0, transferred: false })
  // GPS 1 fails while guiding, and GPS 2 cannot fly the approach: navigated on for position, not qualified for it.
  const selection = new AutoSelection()
  expect(run({ gps1Unusable: true, gps2Level: 'LNAV' }, selection)).toMatchObject({ chosen: 1, qualified: false })
  const bench = onFinal()
  const buses = bench.fms.gps.map(receiver => receiver.bus())
  const input = (executedCrc: number): SelectionInput => ({
    choice: 'AUTO', selected: true, assessed: buses.map(bus => assessReceiver(bus, 1)), buses, time: 10_000,
    position: { lat: 44, lon: -73 }, executedCrc, sentCrc: executedCrc, approachArmed: true,
  })
  expect(selection.choose(input(crc)).qualified).toBe(false)
  // A different approach executed: qualified again.
  expect(selection.choose(input(crc ^ 1)).qualified).toBe(true)
})

// ------------------------------------------------------------------ retention in every phase (Astra option A), presentation, log and alerts

/** The demonstration route en route, stepped in quarter seconds, with the GPS stimulus and the CDU. */
const enRoute = () => {
  let now = START
  const fms = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(fms)
  const fly = (seconds: number) => { for (let i = 0; i < seconds * 4; i += 1) { now += 250; sim.step(0.25) } }
  const gps = (receiver: 1 | 2, op: GpsOp) => expect(stimulusFor(fms).apply(receiver - 1, op)).toBe(true)
  const alerts = () => fms.recallList.map(message => message.text)
  /** A receiver's fault cleared: it restarts (self test, initialization, acquisition) before it is usable again. */
  const recover = (receiver: 1 | 2) => {
    for (let s = 0; s < 300 && !fms.gpsStatus.assessed[receiver - 1].usable; s += 1) fly(1)
    expect(fms.gpsStatus.assessed[receiver - 1].usable, `GPS${receiver} usable again`).toBe(true)
  }
  const log = () => (fms.gpsSelectionLog ?? []).map(event => (event.kind === 'TRANSFER' ? `TRANSFER ${event.from}>${event.to} ${event.reason}` : `${event.kind} ${event.receiver} ${event.reason}`))
  fly(1)
  return { fms, sim, fly, gps, alerts, log, recover }
}
type EnRoute = ReturnType<typeof enRoute>
/** GPS 1 failed and recovered: GPS 2 is the current receiver, GPS 1 available. */
const onGps2 = (bench: EnRoute = enRoute()) => {
  bench.gps(1, { op: 'fault', fault: 'RECEIVER', on: true })
  bench.fly(5)
  bench.gps(1, { op: 'fault', fault: 'RECEIVER', on: false })
  bench.recover(1)
  bench.fly(5)
  return bench
}
const navOptions = (fms: ScriptedFms) => {
  for (const key of ['INIT_REF', 'NEXT', 'LSK5R', 'LSK6R'] as const) fms.press(key)
  const lines = screenText(fms.screen())
  expect(lines[0]).toMatch(/^NAV OPTIONS/)
  return { lines, cells: fms.screen() }
}

test('the initial choice is GPS1, and it is neither a transfer nor annunciated', () => {
  const { fms, alerts, log } = enRoute()
  expect(fms.gpsStatus.chosen).toBe(0)
  expect(log()).toEqual([])
  expect(alerts()).toEqual([])
})

test('a recovery is logged as its own event, not a transfer, and moves nothing', () => {
  const bench = enRoute()
  bench.gps(1, { op: 'fault', fault: 'RECEIVER', on: true })
  bench.fly(5)
  bench.gps(1, { op: 'fault', fault: 'RECEIVER', on: false })
  bench.recover(1)
  bench.fly(5)
  expect(bench.fms.gpsStatus.chosen).toBe(1)
  expect(bench.log()).toEqual([
    'RECOVERED GPS1 USABLE AGAIN',
    'TRANSFER GPS1>GPS2 GPS1 NOT USABLE: 273 MODE FAULT',
    'LOST GPS1 273 MODE FAULT',
  ])
})

test('no alert for keeping a healthy GPS2 after GPS1 recovers; the failure itself was annunciated', () => {
  const bench = enRoute()
  bench.gps(1, { op: 'fault', fault: 'RECEIVER', on: true })
  bench.fly(5)
  expect(bench.alerts()).toEqual(['GPS1 NOT USABLE'])
  bench.gps(1, { op: 'fault', fault: 'RECEIVER', on: false })
  bench.recover(1)
  bench.fly(30)
  expect(bench.fms.gpsStatus.chosen).toBe(1)
  expect(bench.alerts()).toEqual(['GPS1 NOT USABLE'])
})

test('a later GPS2 failure hands the source back to GPS1: retention is not a lock', () => {
  const bench = onGps2()
  bench.gps(2, { op: 'fault', fault: 'STOP_TRANSMITTING', on: true })
  expect(bench.fms.gpsStatus.chosen).toBe(0)
  expect(bench.log()[0]).toBe('TRANSFER GPS2>GPS1 GPS2 NOT USABLE: NOT TRANSMITTING')
  expect(bench.alerts()).toContain('GPS2 NOT USABLE')
  expect(bench.fms.navSourceLog[0].source).toBe('GPS1')
})

test('both lost, then GPS2 back first: GPS2 is taken (no current receiver), and GPS1 back later does not take over', () => {
  const bench = enRoute()
  bench.gps(1, { op: 'fault', fault: 'STOP_TRANSMITTING', on: true })
  bench.gps(2, { op: 'fault', fault: 'STOP_TRANSMITTING', on: true })
  expect(bench.fms.gpsStatus.chosen).toBeNull()
  bench.gps(2, { op: 'fault', fault: 'STOP_TRANSMITTING', on: false })
  bench.recover(2)
  expect(bench.fms.gpsStatus.chosen).toBe(1)
  expect(bench.log().slice(0, 2)).toEqual(['TRANSFER NONE>GPS2 NO CURRENT RECEIVER', 'RECOVERED GPS2 USABLE AGAIN'])
  bench.gps(1, { op: 'fault', fault: 'STOP_TRANSMITTING', on: false })
  bench.recover(1)
  bench.fly(10)
  expect(bench.fms.gpsStatus.chosen).toBe(1)
})

test('equally suitable is not equally accurate: a worse but adequate HFOM and HIL on GPS2 do not move the source', () => {
  const bench = onGps2()
  // Worse than GPS1's by far, and still within the alert limit of every phase flown here.
  bench.gps(2, { op: 'override', label: '247', kind: 'FORCE', amount: 0.25 })
  bench.gps(2, { op: 'override', label: '130', kind: 'FORCE', amount: 0.28 })
  bench.fly(10)
  expect(bench.fms.gpsStatus.assessed[1]).toMatchObject({ usable: true, hil: 0.28 })
  expect(bench.fms.gpsStatus.assessed[1].hil!).toBeGreaterThan(5 * bench.fms.gpsStatus.assessed[0].hil!)
  expect(bench.fms.gpsStatus.chosen).toBe(1)
})

test('a flight phase change never resets the source to GPS1, and an approach both receivers support stays on GPS2', () => {
  const bench = onGps2()
  const { fms } = bench
  fms.selectProcedure('APPROACH', 'R24R')
  fms.press('EXEC')
  const phases = new Set([fms.flightPhase])
  for (let s = 0; s < 3000 && fms.flightPhase !== 'APPROACH'; s += 1) {
    bench.fly(1)
    phases.add(fms.flightPhase)
    expect(fms.gpsStatus.chosen, `${fms.flightPhase} at ${s} s`).toBe(1)
  }
  expect(phases).toEqual(new Set(['EN ROUTE', 'TERMINAL', 'APPROACH']))
  bench.fly(30)
  expect(fms.gpsStatus.chosen).toBe(1)
  expect(bench.log().filter(line => line.startsWith('TRANSFER'))).toHaveLength(1)
})

test('the CDU shows the current source on NAV OPTIONS, the other receiver available or failed, and the AUTO note', () => {
  const bench = enRoute()
  let { lines } = navOptions(bench.fms)
  expect(lines[7]).toMatch(/^ FMS SOURCE\s+OTHER $/)
  expect(lines[8]).toMatch(/^AUTO GPS1\s+GPS2 STBY$/)
  expect(lines[9].trimEnd()).toBe('AUTO KEEPS SUITABLE RCVR')
  expect(lines[10].trimEnd()).toBe('GPS1 INITIAL IF EQUAL')
  for (const line of lines) expect(line.length).toBeLessThanOrEqual(24)
  bench.gps(1, { op: 'fault', fault: 'RECEIVER', on: true })
  ;({ lines } = navOptions(bench.fms))
  expect(lines[8]).toMatch(/^AUTO GPS2\s+GPS1 FAIL$/)
  bench.gps(1, { op: 'fault', fault: 'RECEIVER', on: false })
  bench.recover(1)
  ;({ lines } = navOptions(bench.fms))
  expect(lines[8]).toMatch(/^AUTO GPS2\s+GPS1 STBY$/)
  // A manual choice that is not usable shows as failed, in amber, and is never replaced by the other receiver.
  bench.fms.selectGpsReceiver('GPS1')
  bench.gps(1, { op: 'fault', fault: 'RECEIVER', on: true })
  const shown = navOptions(bench.fms)
  expect(shown.lines[8]).toMatch(/^GPS1 FAIL\s+GPS2 STBY$/)
  expect(shown.cells[8][0].color).toBe('amber')
  expect(bench.fms.gpsStatus.chosen).toBeNull()
  expect(bench.alerts()).toEqual(expect.arrayContaining(['GPS1 NOT USABLE', 'GPS NAV LOST']))
  // AUTO with neither receiver usable: no source, said so in amber.
  bench.fms.selectGpsReceiver('AUTO')
  bench.gps(2, { op: 'fault', fault: 'RECEIVER', on: true })
  const none = navOptions(bench.fms)
  expect(none.lines[8]).toMatch(/^AUTO NONE\s+GPS1 FAIL$/)
  expect(none.cells[8][0].color).toBe('amber')
})

test('a transfer never suppresses the failure: GPS1 failing on the approach is annunciated as well as the transfer', () => {
  const bench = onFinal()
  bench.gps(1, { op: 'fault', fault: 'RECEIVER', on: true })
  expect(bench.fms.gpsStatus.chosen).toBe(1)
  expect(bench.alerts()).toEqual(expect.arrayContaining(['APPR ON GPS2', 'GPS1 NOT USABLE']))
  expect(bench.fms.gpsSelectionLog?.[0]).toMatchObject({ kind: 'TRANSFER', from: 'GPS1', to: 'GPS2', reason: 'GPS1 NOT USABLE: 273 MODE FAULT' })
  expectApproachContinuesOn(bench, 1)
})

test('a transfer never suppresses an integrity alert: GPS1 over its alert limit raises GPS POS UNCERTAIN while GPS2 is used', () => {
  const bench = enRoute()
  bench.gps(1, { op: 'override', label: '130', kind: 'FORCE', amount: 9 })
  expect(bench.fms.gpsStatus.chosen).toBe(1)
  expect(bench.alerts()).toEqual(expect.arrayContaining(['GPS POS UNCERTAIN', 'GPS1 NOT USABLE']))
  expect(bench.log()[0]).toBe('TRANSFER GPS1>GPS2 GPS1 NOT USABLE: HIL 9.00 > HAL 1.00')
})

test('a crew change from a failed manual receiver to AUTO is logged as the crew\'s, not annunciated as an approach transfer', () => {
  const bench = setup()
  bench.fms.selectGpsReceiver('GPS1')
  bench.fly(1)
  // Before capture, the approach armed: GPS 1, chosen by hand, fails; GPS 2 stays eligible for the approach.
  bench.gps(1, { op: 'fault', fault: 'RECEIVER', on: true })
  bench.fly(3)
  expect(bench.fms.gpsStatus.chosen).toBeNull()
  expect(bench.sim.approachMode).toBe('ARMED')
  bench.fms.selectGpsReceiver('AUTO')
  expect(bench.fms.gpsStatus.chosen).toBe(1)
  expect(bench.alerts()).not.toContain('APPR ON GPS2')
  expect(bench.fms.gpsSelectionLog[0]).toMatchObject({ kind: 'TRANSFER', from: 'NONE', to: 'GPS2', reason: 'GPS NAV AUTO' })
})
