// Test-only session driver. Actions below are the existing primary owner's public-call session, not an outcome oracle.
import type { ActionSource, KernelAction } from '../../src/fmsCdu/kernel/actions'
import { FmsKernel, type KernelRecord } from '../../src/fmsCdu/kernel/kernel'
import { dualComposition } from '../../src/fmsCdu/kernel/legacyPlantAdapter'
import type { DualFmsSystem } from '../../src/fmsCdu/dualFms'
import { stimulusFor } from '../../src/fmsCdu/gpsStimulus'
import { WMM2025_DATABASE } from '../../src/fmsCdu/wmm2025'
import { CanonicalWriter, frameBytes, frameDigest } from './fms-kernel-digest'
const START = Date.UTC(2026, 8, 27, 14, 0, 0)
const F = (j: number) => j * 2_500_000
const ui = (id: string): ActionSource => ({ kind: 'ui', id, surface: 'main' })
const ios = (id: string): ActionSource => ({ kind: 'ios', id, surface: 'main' })
export type Reference = { system: DualFmsSystem; step: (frozen: boolean) => void; stimulus: typeof stimulusFor; wmm: typeof WMM2025_DATABASE }

export function replay(record: KernelRecord, kernel: FmsKernel, options: { onInstantClose?: () => void } = {}) {
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


export function session(mode: 'direct' | 'submit', record?: KernelRecord, reference?: Reference) {
  const { system, plant } = reference ? { system: reference.system, plant: null } : dualComposition(START)
  const kernel = plant ? new FmsKernel(plant) : null
  const frames: string[] = [], bytes: string[] = []
  const stimulus = reference?.stimulus ?? stimulusFor
  let frozen = false
  const writer = new CanonicalWriter()
  const hex = (value: Uint8Array) => Array.from(value, byte => byte.toString(16).padStart(2, '0')).join('')
  const observe = () => { writer.length = 0; return frameBytes({ computers: system.computers, flights: system.flights }, writer) }
  const onInstantClose = () => { const value = observe(); frames.push(frameDigest(value)); bytes.push(hex(value)) }
  if (record) { if (!kernel) throw new Error("journal replay needs the head kernel"); replay(record, kernel, { onInstantClose }); return { frames, bytes, end: hex(observe()), kernel } }
  const [one, two] = system.computers
  const act = (direct: () => void, action: KernelAction, source: ActionSource) => { if (mode === 'direct') direct(); else { if (!kernel) throw new Error('submit needs the head kernel'); kernel.submit(action, source) } }
  const freeze = (on: boolean) => { frozen = on; if (kernel && kernel.flightFreeze !== on) kernel.submit({ kind: 'ios.flightFreeze', on }, ui('flight.flyPause')) }
  const advance = (frames: number) => { if (kernel) kernel.advance(frames, { onInstantClose }); else for (let i = 0; i < frames; i += 1) { onInstantClose(); reference!.step(frozen) } }
  const fly = (frames: number) => { freeze(false); for (let i = 0; i < frames / 4; i += 1) advance(4) }
  const hold = (frames: number) => { freeze(true); for (let i = 0; i < frames; i += 1) advance(1) }
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
  act(() => stimulus(one).apply(0, { op: 'jam', db: 30 }), { kind: 'f14.gps', receiver: 1, op: { op: 'jam', db: 30 } }, f14('gps1.jamming')); fly(20)
  act(() => stimulus(one).apply(0, { op: 'fault', fault: 'RECEIVER', on: !stimulus(one).state(0).receiver }), { kind: 'f14.gps', receiver: 1, op: { op: 'toggle', field: 'receiver' } }, f14('gps1.receiver')); fly(8)
  act(() => stimulus(one).apply(0, { op: 'jam', db: 0 }), { kind: 'f14.gps', receiver: 1, op: { op: 'jam', db: 0 } }, f14('gps1.jamming')); fly(8)
  act(() => one.setBaroError(80, 'bench'), { kind: 'ios.atmosphere', baroErrorFt: 80, reason: 'bench' }, ios('baro.error'))
  act(() => one.setBaroSetting({ kind: 'QNH', hPa: 1002 }), { kind: 'fms.baroSetting', unit: 1, setting: { kind: 'QNH', hPa: 1002 } }, ui('baro.setting')); fly(8)
  act(() => Object.assign(one.wind, { direction: 200, speed: 35 }), { kind: 'ios.wind', direction: 200, speed: 35 }, ios('wind')); fly(20)
  hold(12)
  act(() => one.setCondition('fmsFail', true), { kind: 'ios.condition', unit: 1, condition: 'fmsFail', on: true }, ios('conditions.fmsFail')); hold(8); fly(8)
  act(() => one.setCondition('fmsFail', false), { kind: 'ios.condition', unit: 1, condition: 'fmsFail', on: false }, ios('conditions.fmsFail')); fly(8)
  act(() => one.sequence(), { kind: 'ios.jump', unit: 'guidance', op: 'sequence' }, ios('flight.jump')); fly(20)
  act(() => { one.goAround(); system.simulator.engageGoAround() }, { kind: 'afcs.select', selection: { select: 'toga' } }, ui('afcs.toga')); fly(8)
  act(() => one.raiseAlert('UNABLE RNP'), { kind: 'ios.alert', unit: 1, text: 'UNABLE RNP' }, ios('alerts.other'))
  act(() => one.loadMagvar(reference?.wmm ?? WMM2025_DATABASE), { kind: 'config.magvar', unit: 1, op: 'restoreWmm2025' }, ios('navdata.restoreWmm2025')); fly(8)
  act(() => system.rms.injectFailure('com1', true), { kind: 'f14.radio', device: 'com1', feedback: 'FAILED' }, f14('dual.com1Feedback'))
  act(() => system.rms.rejectNext('nav2'), { kind: 'f14.radio', device: 'nav2', rejectNext: true }, f14('dual.rejectNext'))
  act(() => one.open('POS'), { kind: 'cdu.open', side: 1, page: 'POS' }, ios('navdata.preflight.POS')); fly(8)
  act(() => two.powerOff(), { kind: 'ios.unitPower', unit: 2, mode: 'OFF', onGround: false }, ios('navdata.powerOff')); fly(8)
  act(() => two.powerOn('WARM', false), { kind: 'ios.unitPower', unit: 2, mode: 'WARM', onGround: false }, ios('navdata.warmStart')); fly(40)
  return { frames, bytes, end: hex(observe()), kernel }
}
