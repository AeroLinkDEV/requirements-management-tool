// The #1517 I1-0 census: every run whose per-frame state the I1 kernel must leave bit-identical (#1502 D7 9.3). It drives
// each run the way the bench and runHeadless drive it, and observes it from outside with the frozen encoder
// (fms-kernel-digest.ts): the frame digest D_j is taken when instant F_j closes, immediately before its INTEGRATE.
//
// The runs: every library scenario, single (runHeadless's composition) and dual (the bench's scenario composition);
// the I0 workloads W2 and W3 (#1510; W1 and W4 are library scenarios); and scripted dual sessions with direct calls at
// the bench's resting points (D5 7.3 entry points, a moving-waypoint EXEC, a LEGS shrink then NEXT and a line select,
// and an FMS failure toggled while frozen and while flying). Each run is driven in callbacks of 1 or 4 frames, with
// or without the bench's reads after each callback.
//
// Only the arm adapter at the end of this file may differ between the arms of a comparison: it is how an arm composes
// a run and advances it. Everything else is frozen with the encoder.

import { DualFmsSystem } from '../../src/fmsCdu/dualFms'
import { aircraftData, fmsOutputs } from '../../src/fmsCdu/efis'
import { FlightSimulator } from '../../src/fmsCdu/flight'
import { offset, type Leg } from '../../src/fmsCdu/fmsModel'
import { stimulusFor } from '../../src/fmsCdu/gpsStimulus'
import { MISSION_87N_OFFSHORE_SAR } from '../../src/fmsCdu/heliDemo'
import { ACTIVE_PROFILE, profileById } from '../../src/fmsCdu/profile'
import { FmsKernel } from '../../src/fmsCdu/kernel/kernel'
import { dualComposition, singleComposition } from '../../src/fmsCdu/kernel/legacyPlantAdapter'
import { ScenarioRunner, scenarioStart, type Scenario, type StepResult } from '../../src/fmsCdu/scenario'
import { SCENARIO_LIBRARY } from '../../src/fmsCdu/scenarioLibrary'
import { ScriptedFms } from '../../src/fmsCdu/scriptedFms'
import { WMM2025_DATABASE } from '../../src/fmsCdu/wmm2025'
import { CanonicalWriter, Sha256, frameBytes, frameDigest, type DigestUnits } from './fms-kernel-digest'

export const CENSUS_START = Date.UTC(2026, 8, 27, 14, 0, 0)
const FRAME_MS = 250

/** I0 workload W3 (perf/protocol.json): the 87N sector search on both computers, flown for 1800 s whatever the runner. */
const W3: Scenario = {
  id: 'perf-w3-dual-sync', title: 'Dual-FMS SYNC: the 87N sector search flown on both computers', objective: 'Performance workload W3 (#1510)',
  maxSeconds: 1800, start: '87n-offshore-sar', startTime: '2026-09-29T15:00:00Z',
  steps: [
    { when: { kind: 'start' }, action: { kind: 'keys', keys: ['TACT', 'LSK4L', 'LSK6R', 'EXEC'] } },
    { when: { kind: 'start' }, action: { kind: 'expectLine', line: 0, pattern: 'ACT SECTOR SAR' } },
  ],
}

/** A scripted dual session: direct calls at the resting points between callbacks, as the bench's controls make them. */
export type SessionScript = (session: Session) => void
export type Session = {
  readonly system: DualFmsSystem
  /** Flies this many frames, in callbacks of the mode's rate. */
  fly(frames: number): void
  /** Freeze frames: the bench paused with no run (the aircraft stands still, the clock runs), one per callback. */
  freeze(frames: number): void
}

export type CensusRun =
  | { readonly id: string; readonly topology: 'single' | 'dual'; readonly kind: 'scenario'; readonly scenario: Scenario; readonly windowSeconds?: number }
  | { readonly id: string; readonly topology: 'dual'; readonly kind: 'session'; readonly script: SessionScript }

export type CensusMode = { readonly rate: 1 | 4; readonly rendered: boolean; readonly digest: boolean }
export const CENSUS_MODES: readonly CensusMode[] = [
  { rate: 1, rendered: false, digest: true }, { rate: 4, rendered: false, digest: true },
  { rate: 1, rendered: true, digest: true }, { rate: 4, rendered: true, digest: true },
]

export type CensusResult = {
  readonly id: string
  /** D_j for every frame instant closed, in order; then the digest of the state the run ended in. */
  readonly frames: readonly string[]
  readonly end: string
  /** SHA-256 over the canonical bytes of every frame and the end state, in order (frame digests on). */
  readonly sha256: string
  readonly bytes: number
  readonly outcome: string | null
  readonly results: readonly StepResult[] | null
  readonly endedAfter: number | null
}

const legs = (count: number, from: ScriptedFms): Leg[] =>
  Array.from({ length: count }, (_, i) => ({ kind: 'wpt', ident: `PO${String(i + 1).padStart(2, '0')}`, position: offset(from.position, 0, 5 * (i + 1)) }))

/** D5 7.3 entry points reachable by a direct call in I1a: keys on both CDUs (and a held CLR), the AFCS selections, the
 * guidance source, cross-talk, conditions (an FMS failure frozen and flying), GPS stimuli, baro, wind, Jump, magnetic
 * database, power, and freeze. */
const d5Rows: SessionScript = ({ system, fly, freeze }) => {
  const [one, two] = system.computers
  fly(40)
  one.press('LEGS'); two.press('PROG'); one.press('CLR', { held: true }); fly(8)
  system.simulator.selectHeading(120); fly(20)
  system.simulator.armLnav(); fly(20)
  system.simulator.engageVerticalSpeed(-500); fly(20)
  system.simulator.engageAltitudeHold(); system.simulator.selectSpeed(100); fly(20)
  system.selectGuidance(2); fly(20)
  system.selectGuidance(1); fly(8)
  system.setLinkAvailable(false); fly(8)
  system.setLinkAvailable(true); fly(8)
  one.setCondition('gpsLost', true); fly(20)
  one.setCondition('gpsLost', false); fly(8)
  stimulusFor(one).apply(0, { op: 'jam', db: 30 }); fly(20)
  stimulusFor(one).apply(0, { op: 'jam', db: 0 }); fly(8)
  one.setBaroError(80, 'census'); one.setBaroSetting({ kind: 'QNH', hPa: 1002 }); fly(8)
  Object.assign(one.wind, { direction: 200, speed: 35 }); fly(20)
  freeze(12)
  one.setCondition('fmsFail', true); freeze(8); fly(8)
  one.setCondition('fmsFail', false); fly(8)
  one.sequence(); fly(20)
  one.magvar.load(WMM2025_DATABASE); fly(8)
  two.powerOff(); fly(8)
  two.powerOn('WARM', false); fly(40)
}

const movingWaypointExec: SessionScript = ({ system, fly }) => {
  const [one] = system.computers
  fly(8)
  Object.assign(one.wind, { direction: 0, speed: 0 })
  one.defineMoving('SHIP1', offset(one.position, 30, 12), 90, 40)
  one.directTo('SHIP1'); fly(4)
  one.press('EXEC'); fly(160)
}

const legsShrink: SessionScript = ({ system, fly }) => {
  const [one] = system.computers
  one.replaceLegs(legs(7, one)); one.press('EXEC'); fly(4)
  one.replaceLegs(legs(12, one)); one.press('LEGS'); one.press('NEXT'); one.press('NEXT'); fly(4)
  one.press('LSK6L'); fly(4)
  one.press('NEXT'); fly(4)
  one.press('LSK1L'); fly(40)
}

const fmsFailToggles: SessionScript = ({ system, fly, freeze }) => {
  const [one] = system.computers
  fly(8); freeze(4)
  one.setCondition('fmsFail', true); freeze(8)
  one.setCondition('fmsFail', false); freeze(4); fly(8)
  one.setCondition('fmsFail', true); fly(8)
  one.setCondition('fmsFail', false); fly(40)
}

export const CENSUS_RUNS: readonly CensusRun[] = [
  ...SCENARIO_LIBRARY.flatMap(scenario => (['single', 'dual'] as const).map(topology => ({ id: `${scenario.id}/${topology}`, topology, kind: 'scenario' as const, scenario }))),
  ...(['single', 'dual'] as const).map(topology => ({ id: `W2 ${MISSION_87N_OFFSHORE_SAR.id}/${topology}`, topology, kind: 'scenario' as const, scenario: MISSION_87N_OFFSHORE_SAR })),
  { id: `W3 ${W3.id}/dual`, topology: 'dual', kind: 'scenario', scenario: W3, windowSeconds: 1800 },
  { id: 'session d5-rows/dual', topology: 'dual', kind: 'session', script: d5Rows },
  { id: 'session moving-waypoint-exec/dual', topology: 'dual', kind: 'session', script: movingWaypointExec },
  { id: 'session legs-shrink-next-lsk/dual', topology: 'dual', kind: 'session', script: legsShrink },
  { id: 'session fms-fail-frozen-and-flying/dual', topology: 'dual', kind: 'session', script: fmsFailToggles },
]

/** What the bench reads after each callback: the CDUs, and the guidance computer's bus outputs and air data. */
function benchReads(units: DigestUnits, guidance: { fms: ScriptedFms; sim: FlightSimulator }) {
  for (const fms of units.computers) { fms.screen(); fms.lamps() }
  fmsOutputs(guidance.fms, guidance.sim); aircraftData(guidance.fms, guidance.sim)
}

/** Runs one census run in one mode and returns its digests. */
export function runCensus(run: CensusRun, mode: CensusMode): CensusResult {
  const frames: string[] = []
  const sha = new Sha256()
  let bytes = 0
  const writer = new CanonicalWriter()
  let units: DigestUnits | null = null
  const take = (always = false) => {
    if ((!mode.digest && !always) || !units) return null
    writer.length = 0
    const frame = frameBytes(units, writer)
    sha.update(frame); bytes += frame.length
    return frameDigest(frame)
  }
  const onInstantClose = () => { const digest = take(); if (digest) frames.push(digest) }

  const scenario = run.kind === 'scenario' ? run.scenario : null
  const start = (scenario && scenarioStart(scenario)) ?? CENSUS_START
  const composed = run.topology === 'single' ? ARM.single(start, scenario!, onInstantClose) : ARM.dual(start, scenario, onInstantClose)
  units = composed.units
  const render = () => { if (mode.rendered) benchReads(composed.units, composed.guidance()) }

  if (run.kind === 'scenario') {
    const runner = composed.runner!
    if (run.windowSeconds !== undefined) {
      // A declared window flies on after the runner has finished, as the I0 harness does.
      const frameCount = Math.round(run.windowSeconds * 1000 / FRAME_MS)
      for (let flown = 0; flown < frameCount; flown += mode.rate) { composed.fly(Math.min(mode.rate, frameCount - flown)); render() }
    } else {
      // runHeadless's bound: the runner ends itself at maxSeconds; this only keeps a broken runner from looping.
      const limit = Math.ceil((Number.isFinite(scenario!.maxSeconds) ? scenario!.maxSeconds : 0) * 1000 / FRAME_MS) + 2
      for (let callbacks = 0; !runner.finished && callbacks < limit; callbacks += 1) { composed.fly(mode.rate); render() }
    }
  } else {
    const callbacks = (frames: number, each: () => void) => {
      if (frames % mode.rate) throw new Error(`a session moves in whole callbacks: ${frames} frames at ${mode.rate}x`)
      for (let i = 0; i < frames / mode.rate; i += 1) { each(); render() }
    }
    run.script({
      system: composed.system!,
      fly: frames => callbacks(frames, () => composed.fly(mode.rate)),
      // The bench's freeze is one frame per callback whatever the rate; a session freezes in single frames.
      freeze: frames => { for (let i = 0; i < frames; i += 1) { composed.freeze(); render() } },
    })
  }
  // The end state is digested in every mode, so a run without frame digests can be compared with one that has them.
  const end = take(true)!
  const runner = composed.runner
  return {
    id: run.id, frames, end, sha256: mode.digest ? sha.hex() : '', bytes,
    outcome: runner?.outcome ?? null, results: runner ? runner.results.map(result => ({ ...result })) : null, endedAfter: runner?.endedAfter ?? null,
  }
}

// ------------------------------------------------------------------ arm adapter (the only part an arm may change)

type Composed = {
  readonly units: DigestUnits
  readonly runner: ScenarioRunner | null
  readonly system: DualFmsSystem | null
  guidance(): { fms: ScriptedFms; sim: FlightSimulator }
  /** One bench callback while flying: `frames` frames, ending early on the frame where a run finishes. */
  fly(frames: number): void
  /** One freeze frame. */
  freeze(): void
}

type Arm = {
  single(start: number, scenario: Scenario, onInstantClose: () => void): Composed
  dual(start: number, scenario: Scenario | null, onInstantClose: () => void): Composed
}

/**
 * The I1a and I1b arm: the legacy plant adapter's compositions on the kernel. From I1b the flight freeze is kernel state,
 * set by a journaled ios.flightFreeze action at the resting point, as the bench's Fly/Pause button sets it.
 */
const ARM: Arm = {
  single(start, scenario, onInstantClose) {
    const { fms, sim, plant } = singleComposition(start, { profile: profileById(scenario.profile) })
    const runner = new ScenarioRunner(scenario, fms, undefined, sim)
    const kernel = new FmsKernel(plant, runner)
    return {
      units: { computers: [fms], flights: [sim] }, runner, system: null, guidance: () => ({ fms, sim }),
      fly: frames => { kernel.advance(frames, { onInstantClose }) },
      freeze: () => { throw new Error('the single composition has no freeze') },
    }
  },
  dual(start, scenario, onInstantClose) {
    const profile = profileById(scenario?.profile) ?? ACTIVE_PROFILE
    const { system, plant } = dualComposition(start, { profile, secondaryProfile: profile })
    const fms = system.computers[0]
    const runner = scenario ? new ScenarioRunner(scenario, fms, { variant: 'census', cycle: fms.activeCycle.id }, system.flights[0]) : null
    const kernel = new FmsKernel(plant, runner)
    const freezing = (on: boolean) => { if (kernel.flightFreeze !== on) kernel.submit({ kind: 'ios.flightFreeze', on }, { kind: 'ui', id: 'census', surface: 'main' }) }
    return {
      units: { computers: system.computers, flights: system.flights }, runner, system,
      guidance: () => ({ fms: system.computers[system.guidanceSide - 1], sim: system.simulator }),
      fly: frames => { freezing(false); kernel.advance(frames, { onInstantClose }) },
      freeze: () => { freezing(true); kernel.advance(1, { onInstantClose }) },
    }
  },
}
