import { expect, logicTest as test } from './isolated-client-test'
import type { ActionSource, KernelAction } from '../src/fmsCdu/kernel/actions'
import { FmsKernel, type KernelEvent, type KernelRecord } from '../src/fmsCdu/kernel/kernel'
import { dualComposition, singleComposition, type LegacyPlant } from '../src/fmsCdu/kernel/legacyPlantAdapter'
import { stimulusFor } from '../src/fmsCdu/gpsStimulus'
import { ScenarioRunner, type Scenario, type ScenarioStep } from '../src/fmsCdu/scenario'
import { WMM2025_DATABASE } from '../src/fmsCdu/wmm2025'
import { CanonicalWriter, frameBytes, frameDigest } from './fixtures/fms-kernel-digest'

// #1517 I1b: the submission boundary (#1502 D5 7.1-7.4, D1 3.8a). submit() returns a receipt and nothing else; the
// outcome comes on the event stream; the journal records each executed action at its instant with its resolved payload;
// the ACTION rule orders pending submissions, the translated v1 steps and checks, and the submissions made at the
// resting point; control-plane operations carry controlSeq and a replay position and never take a submissionSeq.
// Every expectation is read off the rule or the D5 7.5 paper timeline (Δ = 0.25 s = 2,500,000 units), never off the
// kernel's own output; the live-input equivalence uses the frozen encoder from outside.

const START = Date.UTC(2026, 8, 27, 14, 0, 0)
const F = (j: number) => j * 2_500_000
const ui = (id: string): ActionSource => ({ kind: 'ui', id, surface: 'main' })
const ios = (id: string): ActionSource => ({ kind: 'ios', id, surface: 'main' })
const scenario = (steps: ScenarioStep[], maxSeconds = 10): Scenario => ({ id: 'submission', title: 'Submission', objective: '', maxSeconds, steps })
const alert = (text: string): KernelAction => ({ kind: 'ios.alert', unit: 1, text })
const flown = (units: LegacyPlant['units']) => ({ position: units.computers[0].truePosition, track: units.computers[0].track })

/** Replays an exported record on a fresh kernel: every native action and control record in recordOrdinal order, each at its instant. */
function replay(record: KernelRecord, kernel: FmsKernel, options: { onInstantClose?: () => void } = {}) {
  const entries = [...record.journal.filter(entry => entry.origin === 'native'), ...record.control].sort((a, b) => a.recordOrdinal - b.recordOrdinal)
  for (const entry of entries) {
    const at = 'frame' in entry ? entry.frame : entry.instant / F(1)
    while (kernel.frame < at) kernel.advance(1, options)
    if ('submissionSeq' in entry && 'origin' in entry) kernel.submit(entry.payload as KernelAction, entry.source)
    else if (entry.kind === 'halt' || entry.kind === 'resume' || entry.kind === 'reset') kernel.control({ kind: entry.kind })
    else if (entry.kind === 'rate') kernel.control({ kind: 'rate', rate: entry.rate })
  }
  while (kernel.frame < record.frame) kernel.advance(1, options)
}

// Owner: submit() is synchronous and returns only a receipt; the outcome is published on the kernel's event stream as
// {submissionSeq, frame, outcome}, and the journal records the action with its outcome (a refusal is journaled too).
test('submit returns a receipt with no outcome; the outcome arrives as an event and is journaled, refusals included', () => {
  const { plant, system } = dualComposition(START)
  const kernel = new FmsKernel(plant)
  const events: KernelEvent[] = []
  kernel.subscribe(event => events.push(event))
  kernel.advance(4)
  const accepted = kernel.submit(alert('RECEIPT'), ios('alerts.library'))
  expect(accepted).toEqual({ submissionSeq: 1 })
  expect(events).toEqual([{ kind: 'outcome', submissionSeq: 1, frame: 4, outcome: { status: 'accepted' }, payload: alert('RECEIPT') }])
  expect(system.computers[0].recallList.map(message => message.text)).toContain('RECEIPT')
  // The demonstration route has no missed approach: the FMS refuses the TOGA, the AFCS go-around still engages (no
  // behaviour fix in I1), and the journal records the refusal.
  const selected = system.simulator.selectedAltitude
  const refused = kernel.submit({ kind: 'afcs.select', selection: { select: 'toga' } }, ui('afcs.toga'))
  expect(refused).toEqual({ submissionSeq: 2 })
  expect(events.at(-1)).toMatchObject({ kind: 'outcome', submissionSeq: 2, frame: 4, outcome: { status: 'refused' } })
  expect(system.simulator.selectedAltitude).not.toBe(selected)
  expect(kernel.journal.map(record => [record.submissionSeq, record.frame, record.phase, record.orderInPhase, record.origin, record.kind, record.target, record.outcome.status]))
    .toEqual([[1, 4, 'ACTION', 0, 'native', 'ios.alert', 'fms1', 'accepted'], [2, 4, 'ACTION', 1, 'native', 'afcs.select', 'afcs', 'refused']])
  expect(kernel.journal[1].source).toEqual(ui('afcs.toga'))
})

// Owner: a toggle resolves when it executes, and the journal records the value it set (APPR, the cross-talk link, the
// GPS tab's toggles and masking), as does a crew heading entry (TRUE) and a "guidance" target (the side).
test('toggles, a heading entry and the guidance target are journaled as resolved, never as "toggle"', () => {
  const { plant, system } = dualComposition(START)
  const kernel = new FmsKernel(plant)
  const [one] = system.computers
  const payloads = () => kernel.journal.map(record => record.payload)
  kernel.submit({ kind: 'ios.link', available: 'toggle' }, ios('dual.link'))
  expect(system.linked).toBe(false)
  kernel.submit({ kind: 'ios.link', available: 'toggle' }, ios('dual.link'))
  expect(system.linked).toBe(true)
  kernel.submit({ kind: 'afcs.select', selection: { select: 'approach', on: 'toggle' } }, ui('afcs.approach'))
  const armed = one.approachArmed
  kernel.submit({ kind: 'f14.gps', receiver: 1, op: { op: 'toggle', field: 'receiver' } }, { kind: 'f14', id: 'gps1.receiver', surface: 'main' })
  kernel.submit({ kind: 'f14.gps', receiver: 1, op: { op: 'toggleMask', prn: 7 } }, { kind: 'f14', id: 'gps1.mask', surface: 'main' })
  kernel.submit({ kind: 'f14.gps', receiver: 1, op: { op: 'toggleMask', prn: 9 } }, { kind: 'f14', id: 'gps1.mask', surface: 'main' })
  kernel.submit({ kind: 'f14.gps', receiver: 2, op: { op: 'toggleOutage', geo: 131 } }, { kind: 'f14', id: 'gps2.outage', surface: 'main' })
  kernel.submit({ kind: 'f14.gps', receiver: 2, op: { op: 'toggle', field: 'doNotUse' } }, { kind: 'f14', id: 'gps2.doNotUse', surface: 'main' })
  const entry = one.angleFromEntry(90)
  kernel.submit({ kind: 'afcs.select', selection: { select: 'heading', entry: 90 } }, ui('afcs.heading'))
  kernel.submit({ kind: 'afcs.sourceSelect', side: 2 }, ui('flight.guidanceSource'))
  kernel.submit({ kind: 'ios.jump', unit: 'guidance', op: 'overrideDiscontinuity' }, ios('flight.overrideDiscontinuity'))
  expect(payloads()).toEqual([
    { kind: 'ios.link', available: false },
    { kind: 'ios.link', available: true },
    { kind: 'afcs.select', selection: { select: 'approach', on: true } },
    { kind: 'f14.gps', receiver: 1, op: { op: 'fault', fault: 'RECEIVER', on: true } },
    { kind: 'f14.gps', receiver: 1, op: { op: 'mask', prns: [7] } },
    { kind: 'f14.gps', receiver: 1, op: { op: 'mask', prns: [7, 9] } },
    { kind: 'f14.gps', receiver: 2, op: { op: 'sbas', outage: [131] } },
    { kind: 'f14.gps', receiver: 2, op: { op: 'sbas', doNotUse: true } },
    { kind: 'afcs.select', selection: { select: 'heading', heading: entry } },
    { kind: 'afcs.sourceSelect', side: 2 },
    { kind: 'ios.jump', unit: 2, op: 'overrideDiscontinuity' },
  ])
  // The resolved values are the ones the units now hold.
  expect(armed).toBe(true)
  expect(stimulusFor(one).state(0)).toMatchObject({ receiver: true, masked: [7, 9] })
  expect(stimulusFor(one).state(1)).toMatchObject({ outage: [131], doNotUse: true })
  expect(entry).not.toBe(90)
  expect(JSON.stringify(kernel.journal)).not.toContain('toggle')
  expect(JSON.stringify(kernel.journal)).not.toContain('"guidance"')
})

/** A run whose check at 0.5 s sees an alert raised by a submission: the ACTION rule decides whether it does. */
const ruleRun = () => {
  const composition = singleComposition(START)
  const runner = new ScenarioRunner(scenario([
    { when: { kind: 'time', seconds: 0.25 }, action: { kind: 'alert', text: 'STEP' } },
    { when: { kind: 'after', seconds: 0 }, action: { kind: 'expectAlert', text: 'PENDING' } },
  ]), composition.fms, undefined, composition.sim)
  return { composition, runner, kernel: new FmsKernel(composition.plant, runner) }
}

// Owner: the ACTION rule's order (D1 3.8a), and its closure. An input made once F_0's ACTION has closed (as the instant
// closes, before its INTEGRATE) is pending: F_0's INTEGRATE does not fly it, and it executes at F_1 before the poll, so
// the step and check due at F_1 come after it; an input made at R_1 comes after them. submissionSeq is allocated at
// submit() for live input and at translation for v1 steps.
test('ACTION orders pending input, then the translated steps and checks, then input at the resting point; it never reopens', () => {
  const drive = (input: boolean) => {
    const { composition, runner, kernel } = ruleRun()
    let submitted = false
    kernel.advance(1, {
      onInstantClose: () => {
        if (!input || submitted) return
        submitted = true
        kernel.submit(alert('PENDING'), ios('closed'))
        kernel.submit({ kind: 'afcs.select', selection: { select: 'heading', heading: (composition.fms.track + 90) % 360 } }, ui('closed'))
      },
    })
    if (input) kernel.submit(alert('REST'), ios('rest'))
    return { kernel, runner, flown: flown(composition.plant.units) }
  }
  const withInput = drive(true), control = drive(false)
  // F_0's INTEGRATE flew without the heading selection: it was not executed in a closed ACTION.
  expect(withInput.flown).toEqual(control.flown)
  expect(withInput.kernel.journal.map(record => [record.frame, record.orderInPhase, record.submissionSeq, record.origin, record.kind]))
    .toEqual([
      [1, 0, 1, 'native', 'ios.alert'],
      [1, 1, 2, 'native', 'afcs.select'],
      [1, 2, 3, 'v1-translated', 'scenario.step'],
      [1, 3, 4, 'v1-translated', 'scenario.check'],
      [1, 4, 5, 'native', 'ios.alert'],
    ])
  // The check at F_1 saw the pending input; without it, it failed.
  expect(withInput.runner.results[1]).toMatchObject({ status: 'pass', at: 0.25 })
  expect(control.runner.results[1]).toMatchObject({ status: 'fail' })
  expect(withInput.kernel.journal[3].source).toEqual({ kind: 'scenario', id: `${withInput.runner.runId} step 2`, surface: 'main' })
})

// Owner: an operator pause (a halt during a run) is an open ACTION: input made there executes in it at once, journaled
// at that instant, and nothing scheduled runs because of it: no poll (the step due later stays pending), no INTEGRATE
// (the clock and the aircraft stay put). Halted, advance() runs nothing. Resume continues from that cursor, and the next
// INTEGRATE flies what was selected (against a run with no input).
test('input during an operator pause executes in the open ACTION and triggers no poll or INTEGRATE', () => {
  const drive = (input: boolean) => {
    const composition = singleComposition(START)
    const runner = new ScenarioRunner(scenario([{ when: { kind: 'time', seconds: 1 }, action: { kind: 'alert', text: 'DUE' } }]), composition.fms, undefined, composition.sim)
    const kernel = new FmsKernel(composition.plant, runner)
    kernel.advance(2)
    kernel.control({ kind: 'halt' })
    const clock = kernel.unitClockMs, before = flown(composition.plant.units)
    if (input) {
      kernel.submit({ kind: 'afcs.select', selection: { select: 'heading', heading: (composition.fms.track + 90) % 360 } }, ui('afcs.heading'))
      kernel.submit(alert('PAUSED'), ios('alerts.other'))
      expect(kernel.journal.map(record => [record.frame, record.kind])).toEqual([[2, 'afcs.select'], [2, 'ios.alert']])
      expect(composition.fms.recallList.map(message => message.text)).toContain('PAUSED')
    }
    expect(runner.results[0]).toEqual({ status: 'pending' })
    expect(kernel.unitClockMs).toBe(clock)
    expect(flown(composition.plant.units)).toEqual(before)
    expect(kernel.advance(5)).toBe(0)
    expect(kernel.now).toBe(F(2))
    expect(kernel.unitClockMs).toBe(clock)
    kernel.control({ kind: 'resume' })
    kernel.advance(1)
    expect(kernel.now).toBe(F(3))
    return flown(composition.plant.units)
  }
  expect(drive(true)).not.toEqual(drive(false))
})

// Owner: the control plane (D1 3.8a). A control record carries controlSeq and its replay position (instant, cursor,
// recordOrdinal) and never allocates a submissionSeq; recordOrdinal is the one ordinal shared with the journal, which is
// what places a control record among the actions. A failed INTEGRATE writes an execution-error record.
test('control records carry controlSeq and a replay position, take no submissionSeq, and share recordOrdinal with the journal', () => {
  const composition = singleComposition(START)
  const step = composition.sim.step.bind(composition.sim)
  let steps = 0
  composition.sim.step = dt => { steps += 1; if (steps === 3) throw new Error('guidance source switch'); step(dt) }
  const runner = new ScenarioRunner(scenario([{ when: { kind: 'time', seconds: 0.25 }, action: { kind: 'alert', text: 'ONE' } }, { when: { kind: 'time', seconds: 10 }, action: { kind: 'alert', text: 'LATE' } }]), composition.fms, undefined, composition.sim)
  const kernel = new FmsKernel(composition.plant, runner)
  kernel.submit(alert('A'), ios('a'))
  kernel.control({ kind: 'rate', rate: 4 })
  kernel.submit(alert('B'), ios('b'))
  kernel.advance(1)
  kernel.control({ kind: 'halt' })
  kernel.control({ kind: 'resume' })
  expect(() => kernel.advance(4)).toThrow('guidance source switch')
  kernel.control({ kind: 'rate', rate: 1 })
  kernel.submit(alert('C'), ios('c'))
  expect(kernel.journal.map(record => [record.recordOrdinal, record.submissionSeq, record.frame, record.kind]))
    .toEqual([[1, 1, 0, 'ios.alert'], [3, 2, 0, 'ios.alert'], [4, 3, 1, 'scenario.step'], [9, 4, 3, 'ios.alert']])
  expect(kernel.controlRecords).toEqual([
    { recordOrdinal: 2, controlSeq: 1, instant: F(0), cursor: { phase: 'ACTION', boundary: 'rest' }, kind: 'rate', rate: 4 },
    { recordOrdinal: 5, controlSeq: 2, instant: F(1), cursor: { phase: 'ACTION', boundary: 'rest' }, kind: 'halt' },
    { recordOrdinal: 6, controlSeq: 3, instant: F(1), cursor: { phase: 'ACTION', boundary: 'rest' }, kind: 'resume' },
    { recordOrdinal: 7, controlSeq: 4, instant: F(2), cursor: { phase: 'INTEGRATE', boundary: 'during' }, kind: 'execution-error', message: 'guidance source switch' },
    { recordOrdinal: 8, controlSeq: 5, instant: F(3), cursor: { phase: 'ACTION', boundary: 'faulted' }, kind: 'rate', rate: 1 },
  ])
  expect(kernel.record().counters).toEqual({ submissionSeq: 5, controlSeq: 6, recordOrdinal: 10 })
})

// Owner: the live queue is deferred (D5 7.1, item 4), so the kernel keeps an explicit, empty, versioned queue state; it
// stays empty through a pause and survives the exported record's round trip, with the counters.
test('the live queue state is explicit, versioned and empty, and round-trips through the exported record', () => {
  const composition = singleComposition(START)
  const runner = new ScenarioRunner(scenario([{ when: { kind: 'time', seconds: 5 }, action: { kind: 'alert', text: 'LATER' } }]), composition.fms, undefined, composition.sim)
  const kernel = new FmsKernel(composition.plant, runner)
  kernel.advance(2)
  kernel.control({ kind: 'halt' })
  kernel.submit(alert('HALTED'), ios('a'))
  expect(kernel.liveQueue).toEqual({ v: 1, entries: [] })
  const record = kernel.record()
  expect(record.liveQueue).toEqual({ v: 1, entries: [] })
  expect(JSON.parse(JSON.stringify(record))).toEqual(record)
  expect(record).toMatchObject({ v: 1, frame: 2, counters: { submissionSeq: 2, controlSeq: 2, recordOrdinal: 3 } })
})

// Owner: Stop is a journaled action (scenario.stop): it stops the run at the resting point, and replaying the journal on
// a fresh kernel with the same run reproduces the stopped run exactly.
test('scenario.stop is journaled and its replay reproduces the stopped run', () => {
  const steps: ScenarioStep[] = [
    { when: { kind: 'time', seconds: 0.5 }, action: { kind: 'alert', text: 'EARLY' } },
    { when: { kind: 'time', seconds: 3 }, action: { kind: 'alert', text: 'AFTER STOP' } },
    { when: { kind: 'after', seconds: 0 }, action: { kind: 'expectAlert', text: 'AFTER STOP' } },
  ]
  const start = () => {
    const composition = singleComposition(START)
    const runner = new ScenarioRunner(scenario(steps), composition.fms, undefined, composition.sim)
    return { composition, runner, kernel: new FmsKernel(composition.plant, runner) }
  }
  const live = start()
  live.kernel.advance(6)
  live.kernel.submit({ kind: 'scenario.stop', runId: live.runner.runId }, ui('scenarios.stop'))
  live.kernel.advance(4)
  expect(live.runner.outcome).toBe('stopped')
  expect(live.runner.endedAfter).toBe(1.5)
  expect(live.kernel.journal.find(record => record.kind === 'scenario.stop')).toMatchObject({ frame: 6, origin: 'native', outcome: { status: 'accepted' }, source: ui('scenarios.stop') })
  // Stopping a run that has ended is refused, and journaled.
  live.kernel.submit({ kind: 'scenario.stop', runId: live.runner.runId }, ui('scenarios.stop'))
  expect(live.kernel.journal.at(-1)!.outcome).toEqual({ status: 'refused', reason: 'the run has already ended' })
  const replayed = start()
  replay(live.kernel.record(), replayed.kernel)
  expect({ outcome: replayed.runner.outcome, endedAfter: replayed.runner.endedAfter, results: replayed.runner.results })
    .toEqual({ outcome: live.runner.outcome, endedAfter: live.runner.endedAfter, results: live.runner.results })
  expect(replayed.kernel.journal).toEqual(live.kernel.journal)
})

/**
 * A scripted bench session on the dual composition: the bench's controls at the resting points, as direct unit calls
 * (what the bench did before I1b) or as the actions it now submits. Both use the kernel's journaled flight freeze.
 */
function session(mode: 'direct' | 'submit', record?: KernelRecord) {
  const { system, plant } = dualComposition(START)
  const kernel = new FmsKernel(plant)
  const frames: string[] = []
  const writer = new CanonicalWriter()
  const onInstantClose = () => { writer.length = 0; frames.push(frameDigest(frameBytes({ computers: system.computers, flights: system.flights }, writer))) }
  if (record) { replay(record, kernel, { onInstantClose }); return { frames, kernel } }
  const [one, two] = system.computers
  const act = (direct: () => void, action: KernelAction, source: ActionSource) => { if (mode === 'direct') direct(); else kernel.submit(action, source) }
  const freeze = (on: boolean) => { if (kernel.flightFreeze !== on) kernel.submit({ kind: 'ios.flightFreeze', on }, ui('flight.flyPause')) }
  const fly = (frames: number) => { freeze(false); for (let i = 0; i < frames / 4; i += 1) kernel.advance(4, { onInstantClose }) }
  const hold = (frames: number) => { freeze(true); for (let i = 0; i < frames; i += 1) kernel.advance(1, { onInstantClose }) }
  const cdu = (side: 1 | 2): ActionSource => ({ kind: 'cdu', id: `CDU ${side}`, surface: 'main', side })
  const f14 = (id: string): ActionSource => ({ kind: 'f14', id, surface: 'main' })
  fly(40)
  act(() => one.press('LEGS', { held: false }), { kind: 'cdu.key', side: 1, fn: 'LEGS', held: false }, cdu(1))
  act(() => two.press('PROG', { held: false }), { kind: 'cdu.key', side: 2, fn: 'PROG', held: false }, cdu(2))
  act(() => one.press('CLR', { held: true }), { kind: 'cdu.key', side: 1, fn: 'CLR', held: true }, cdu(1))
  fly(8)
  act(() => system.simulator.selectHeading(120), { kind: 'afcs.select', selection: { select: 'heading', heading: 120 } }, ui('afcs.heading')); fly(20)
  act(() => system.simulator.armLnav(), { kind: 'afcs.select', selection: { select: 'lnav' } }, ui('afcs.lnav')); fly(20)
  act(() => system.simulator.engageVerticalSpeed(-500), { kind: 'afcs.select', selection: { select: 'verticalSpeed', fpm: -500 } }, ui('afcs.vs')); fly(20)
  act(() => system.simulator.engageAltitudeHold(), { kind: 'afcs.select', selection: { select: 'altitudeHold' } }, ui('afcs.alt'))
  act(() => system.simulator.selectSpeed(100), { kind: 'afcs.select', selection: { select: 'speed', knots: 100 } }, ui('afcs.spd')); fly(20)
  act(() => system.selectGuidance(2), { kind: 'afcs.sourceSelect', side: 2 }, ui('flight.guidanceSource')); fly(20)
  act(() => system.selectGuidance(1), { kind: 'afcs.sourceSelect', side: 1 }, ui('flight.guidanceSource')); fly(8)
  act(() => system.setLinkAvailable(!system.linked), { kind: 'ios.link', available: 'toggle' }, ios('dual.link')); fly(8)
  act(() => system.setLinkAvailable(!system.linked), { kind: 'ios.link', available: 'toggle' }, ios('dual.link')); fly(8)
  act(() => one.setCondition('gpsLost', true), { kind: 'ios.condition', unit: 1, condition: 'gpsLost', on: true }, ios('conditions.gpsLost')); fly(20)
  act(() => one.setCondition('gpsLost', false), { kind: 'ios.condition', unit: 1, condition: 'gpsLost', on: false }, ios('conditions.gpsLost')); fly(8)
  act(() => stimulusFor(one).apply(0, { op: 'jam', db: 30 }), { kind: 'f14.gps', receiver: 1, op: { op: 'jam', db: 30 } }, f14('gps1.jamming')); fly(20)
  act(() => stimulusFor(one).apply(0, { op: 'fault', fault: 'RECEIVER', on: !stimulusFor(one).state(0).receiver }), { kind: 'f14.gps', receiver: 1, op: { op: 'toggle', field: 'receiver' } }, f14('gps1.receiver')); fly(8)
  act(() => stimulusFor(one).apply(0, { op: 'jam', db: 0 }), { kind: 'f14.gps', receiver: 1, op: { op: 'jam', db: 0 } }, f14('gps1.jamming')); fly(8)
  act(() => one.setBaroError(80, 'bench'), { kind: 'ios.atmosphere', baroErrorFt: 80, reason: 'bench' }, ios('baro.error'))
  act(() => one.setBaroSetting({ kind: 'QNH', hPa: 1002 }), { kind: 'fms.baroSetting', unit: 1, setting: { kind: 'QNH', hPa: 1002 } }, ui('baro.setting')); fly(8)
  act(() => Object.assign(one.wind, { direction: 200, speed: 35 }), { kind: 'ios.wind', direction: 200, speed: 35 }, ios('wind')); fly(20)
  hold(12)
  act(() => one.setCondition('fmsFail', true), { kind: 'ios.condition', unit: 1, condition: 'fmsFail', on: true }, ios('conditions.fmsFail')); hold(8); fly(8)
  act(() => one.setCondition('fmsFail', false), { kind: 'ios.condition', unit: 1, condition: 'fmsFail', on: false }, ios('conditions.fmsFail')); fly(8)
  act(() => one.sequence(), { kind: 'ios.jump', unit: 'guidance', op: 'sequence' }, ios('flight.jump')); fly(20)
  act(() => { one.goAround(); system.simulator.engageGoAround() }, { kind: 'afcs.select', selection: { select: 'toga' } }, ui('afcs.toga')); fly(8)
  act(() => one.raiseAlert('UNABLE RNP'), { kind: 'ios.alert', unit: 1, text: 'UNABLE RNP' }, ios('alerts.other'))
  act(() => one.loadMagvar(WMM2025_DATABASE), { kind: 'config.magvar', unit: 1, op: 'restoreWmm2025' }, ios('navdata.restoreWmm2025')); fly(8)
  act(() => system.rms.injectFailure('com1', true), { kind: 'f14.radio', device: 'com1', feedback: 'FAILED' }, f14('dual.com1Feedback'))
  act(() => system.rms.rejectNext('nav2'), { kind: 'f14.radio', device: 'nav2', rejectNext: true }, f14('dual.rejectNext'))
  act(() => one.open('POS'), { kind: 'cdu.open', side: 1, page: 'POS' }, ios('navdata.preflight.POS')); fly(8)
  act(() => two.powerOff(), { kind: 'ios.unitPower', unit: 2, mode: 'OFF', onGround: false }, ios('navdata.powerOff')); fly(8)
  act(() => two.powerOn('WARM', false), { kind: 'ios.unitPower', unit: 2, mode: 'WARM', onGround: false }, ios('navdata.warmStart')); fly(40)
  return { frames, kernel }
}

// Owner: routing the bench's controls through submit() changes no state (#1517: "I1b changes routing, not behaviour").
// The same session made with the direct calls the bench made before, made through submit(), and replayed from the
// exported journal on a fresh kernel gives the same frame digest at every instant, by the frozen encoder.
test('a session made with direct calls, through submit(), and replayed from its journal is identical at every frame', () => {
  test.setTimeout(3 * 60_000)
  const direct = session('direct'), submitted = session('submit')
  const record = submitted.kernel.record()
  const replayed = session('submit', record)
  expect(direct.frames.length).toBe(submitted.kernel.frame)
  expect(submitted.frames).toEqual(direct.frames)
  expect(replayed.frames).toEqual(direct.frames)
  // Every row reached the journal, resolved, and the replay journaled the same records again.
  expect(record.journal.filter(entry => entry.kind !== 'ios.flightFreeze')).toHaveLength(31)
  expect(replayed.kernel.journal).toEqual(submitted.kernel.journal)
})
