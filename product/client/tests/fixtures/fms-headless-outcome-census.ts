// The #1517 I1d outcome census: what moving runHeadless from the single composition to the dual one would change, at
// the base, before the switch (#1502 D4 6.3 classifies every difference it finds). It flies one scenario on both
// compositions in lockstep, frame by frame on the kernel, and compares what FMS 1 and its flight simulator show, the
// observable state the frozen encoder covers (fms-kernel-digest.ts), section by section.
//
// - single: runHeadless's composition (one computer, one flight simulator), driven exactly as runHeadless drives it.
// - dual: the bench's scenario composition (DualFmsSystem), the runner on FMS 1 and its flight, as the I1 census arm.
//
// It reads only, through the same side-effect-free reads the digest uses, and changes no product code. The driver
// (fms-headless-outcome-census-run.ts) runs it over the scenario library and over every runHeadless call in the specs.

import { DualFmsSystem } from '../../src/fmsCdu/dualFms'
import { aircraftData, fmsOutputs } from '../../src/fmsCdu/efis'
import type { FlightSimulator } from '../../src/fmsCdu/flight'
import { FmsKernel } from '../../src/fmsCdu/kernel/kernel'
import { dualComposition, singleComposition } from '../../src/fmsCdu/kernel/legacyPlantAdapter'
import { ACTIVE_PROFILE, profileById } from '../../src/fmsCdu/profile'
import { ScenarioRunner, TICK_SECONDS, scenarioStart, type RunContext, type Scenario, type StepResult } from '../../src/fmsCdu/scenario'
import { screenText } from '../../src/fmsCdu/screen'
import type { ScriptedFms } from '../../src/fmsCdu/scriptedFms'
import { CanonicalWriter, frameBytes, frameDigest, writeValue } from './fms-kernel-digest'

export const DEFAULT_START = Date.UTC(2026, 8, 27, 14, 0, 0)

export type Topology = 'single' | 'dual'
export type Composed = {
  readonly topology: Topology
  readonly fms: ScriptedFms
  readonly sim: FlightSimulator
  readonly runner: ScenarioRunner
  readonly kernel: FmsKernel
  readonly system: DualFmsSystem | null
}

/** runHeadless's composition and runner, line for line (scenario.ts). */
export function composeSingle(scenario: Scenario, start: number, context?: RunContext): Composed {
  const { fms, sim, plant, settleStart } = singleComposition(start, { profile: profileById(scenario.profile) })
  const runner = new ScenarioRunner(scenario, fms, context, sim, settleStart)
  return { topology: 'single', fms, sim, runner, kernel: new FmsKernel(plant, runner), system: null }
}

/** The dual composition runHeadless would switch to: the I1 census's dual arm, with runHeadless's context. */
export function composeDual(scenario: Scenario, start: number, context?: RunContext): Composed {
  const profile = profileById(scenario.profile) ?? ACTIVE_PROFILE
  const { system, plant, settleStart } = dualComposition(start, { profile, secondaryProfile: profile })
  const fms = system.computers[0], sim = system.flights[0]
  const runner = new ScenarioRunner(scenario, fms, context, sim, settleStart)
  return { topology: 'dual', fms, sim, runner, kernel: new FmsKernel(plant, runner), system }
}

/** runHeadless's loop bound. */
export const frameLimit = (scenario: Scenario) => Math.ceil((Number.isFinite(scenario.maxSeconds) ? scenario.maxSeconds : 0) / TICK_SECONDS) + 2

/** Flies a composition to its end as runHeadless does. */
export function flyToEnd(composed: Composed, scenario: Scenario) {
  const limit = frameLimit(scenario)
  for (let t = 0; !composed.runner.finished && t < limit; t += 1) composed.kernel.advance(1)
  return composed
}

// ------------------------------------------------------------------ observation

/** FMS 1 and its flight simulator, as the frozen encoder takes one computer. */
export const unitBytes = (c: Pick<Composed, 'fms' | 'sim'>) => frameBytes({ computers: [c.fms], flights: [c.sim] }, new CanonicalWriter())
export const unitDigest = (c: Pick<Composed, 'fms' | 'sim'>) => frameDigest(unitBytes(c))

const read = (f: () => unknown) => { try { return f() } catch (error) { return { threw: error instanceof Error ? error.message : String(error) } } }

const FLIGHT: Record<string, (sim: FlightSimulator) => unknown> = {
  guidance: sim => sim.guidance, lateralMode: sim => sim.lateralMode, lnavIsArmed: sim => sim.lnavIsArmed, verticalMode: sim => sim.verticalMode,
  approachMode: sim => sim.approachMode, axisModes: sim => sim.axisModes, axisArmed: sim => sim.axisArmed, selectedHeading: sim => sim.selectedHeading,
  headingHeld: sim => sim.headingHeld, selectedAltitude: sim => sim.selectedAltitude, selectedSpeed: sim => sim.selectedSpeed,
  verticalSpeedTarget: sim => sim.verticalSpeedTarget, altitudeHoldReference: sim => sim.altitudeHoldReference, gpsLateralActive: sim => sim.gpsLateralActive,
  verticalFlag: sim => sim.verticalFlag, holdProgress: sim => sim.holdProgress, hoverHeight: sim => sim.hoverHeight, hoverCaptured: sim => sim.hoverCaptured,
  lowHeightCaption: sim => sim.lowHeightCaption, inLowSpeedRegime: sim => sim.inLowSpeedRegime, transitionInProgress: sim => sim.transitionInProgress,
  bankAngle: sim => sim.bankAngle, tas: sim => sim.tas, modeEventCount: sim => sim.modeEvents.length, lastModeEvent: sim => sim.modeEvents.at(-1),
}

/** The encoder's frame split into named sections; bus outputs and air data per field. */
export function sections(c: Pick<Composed, 'fms' | 'sim'>): Map<string, unknown> {
  const { fms, sim } = c
  const out = new Map<string, unknown>()
  const outputs = read(() => fmsOutputs(fms, sim)) as Record<string, unknown>
  for (const key of Object.keys(outputs).sort()) out.set(`outputs.${key}`, outputs[key])
  const air = read(() => aircraftData(fms, sim)) as Record<string, unknown>
  for (const key of Object.keys(air).sort()) out.set(`airData.${key}`, air[key])
  const screen = fms.screen()
  out.set('screen.text', screenText(screen))
  out.set('screen.style', screen.map(row => row.map(cell => `${cell.color[0]}${cell.size[0]}${cell.inverse ? 'i' : ''}`).join('')))
  out.set('lamps', [...fms.lamps()])
  out.set('brightness', fms.brightness())
  const aircraft = (fms as unknown as { aircraft: Record<string, unknown> }).aircraft
  for (const key of Object.keys(aircraft).sort()) out.set(`aircraft.${key}`, aircraft[key])
  out.set('truePosition', fms.truePosition)
  out.set('physicalAltitude', fms.physicalAltitude)
  out.set('wind', { ...fms.wind })
  for (const [name, field] of Object.entries(FLIGHT)) out.set(`flight.${name}`, read(() => field(sim)))
  return out
}

const encode = (value: unknown) => { const w = new CanonicalWriter(); writeValue(w, value); return w.result() }
const sameBytes = (a: Uint8Array, b: Uint8Array) => {
  if (a.length !== b.length) return false
  for (let i = 0; i < a.length; i += 1) if (a[i] !== b[i]) return false
  return true
}
/** A short JSON view of a value, for the report. */
export const snippet = (value: unknown, max = 400) => {
  let text: string
  try { text = JSON.stringify(value, (_k, v) => (v instanceof Map ? [...v] : v instanceof Set ? [...v] : v)) ?? 'undefined' } catch { text = String(value) }
  return text.length > max ? `${text.slice(0, max)}...` : text
}

/** A readable end state: what a reader checks first when two outcomes differ. */
export function endSummary(c: Composed) {
  const { fms, sim, system } = c
  const leg = fms.activeRoute.legs[0]
  return {
    utc: fms.now.toISOString(), position: fms.truePosition, physicalAltitude: Math.round(fms.physicalAltitude * 10) / 10,
    groundSpeed: Math.round(fms.groundSpeed * 10) / 10, track: Math.round(fms.track * 10) / 10,
    navMode: read(() => fms.navState.mode), gpsSource: read(() => fms.navState.gpsSource), flightPhase: read(() => fms.localFlightPhase),
    activeLeg: leg ? `${leg.kind}${'ident' in leg ? ` ${(leg as { ident?: string }).ident}` : ''}` : null,
    approachType: fms.approachType, approachArmed: fms.approachArmed,
    lateralMode: read(() => sim.lateralMode), verticalMode: read(() => sim.verticalMode), approachMode: read(() => sim.approachMode), axisModes: read(() => sim.axisModes),
    lamps: [...fms.lamps()], screen: screenText(fms.screen()).map(line => line.trimEnd()),
    recall: fms.recallList.map(message => message.text),
    system: system ? { mode: system.mode, guidanceSide: system.guidanceSide, navigationSide: system.navigationSide, linked: system.linked } : null,
  }
}

export type RunRecord = {
  readonly topology: Topology
  readonly outcome: string
  readonly results: readonly StepResult[]
  readonly endedAfter: number | null
  readonly frames: number
  readonly end: string
  readonly endSummary: ReturnType<typeof endSummary>
}

export const record = (c: Composed, frames: number): RunRecord => ({
  topology: c.topology, outcome: c.runner.outcome, results: c.runner.results.map(result => ({ ...result })), endedAfter: c.runner.endedAfter,
  frames, end: unitDigest(c), endSummary: endSummary(c),
})

export type SectionDivergence = {
  frame: number; seconds: number; single: string; dual: string
  /** Divergent frames in all; the last one; how many separate runs of them, the longest, and the current run's length. */
  frames: number; lastFrame: number; episodes: number; longestEpisode: number; episode: number
}
export type Lockstep = {
  readonly single: RunRecord
  readonly dual: RunRecord
  readonly dualSystemEnd: string
  /** The first frame instant F_j (j counted from the start) whose FMS 1 observation differs, or null. */
  readonly firstFrame: number | null
  readonly divergentFrames: number
  readonly comparedFrames: number
  /** Per section: where it first differs, with both values there, and how often and how long it differs. */
  readonly sectionsFirst: Record<string, SectionDivergence>
  /** Sections that differ in the end state, with both values. */
  readonly endSections: Record<string, { single: string; dual: string }>
  /** Dual only: how long the computers stayed in SYNC, and the first drop. */
  readonly sync: { residency: number; firstDrop: { frame: number; seconds: number; fault: string | null } | null }
}

/**
 * Flies one scenario on both compositions in lockstep and compares FMS 1 at every frame instant as it closes. Each
 * composition runs exactly as it would alone: the kernel calls are the same, only interleaved.
 */
export function lockstep(scenario: Scenario, start = scenarioStart(scenario) ?? DEFAULT_START, context?: RunContext): Lockstep {
  const single = composeSingle(scenario, start, context)
  const dual = composeDual(scenario, start, context)
  const sectionsFirst: Record<string, SectionDivergence> = {}
  let firstFrame: number | null = null, divergent = 0, compared = 0, syncFrames = 0
  let firstDrop: Lockstep['sync']['firstDrop'] = null
  let frame = 0
  const compare = (a: Composed, b: Composed, at: number) => {
    compared += 1
    const ba = unitBytes(a), bb = unitBytes(b)
    if (sameBytes(ba, bb)) return
    divergent += 1
    firstFrame ??= at
    const sa = sections(a), sb = sections(b)
    for (const key of new Set([...sa.keys(), ...sb.keys()])) {
      if (sameBytes(encode(sa.get(key)), encode(sb.get(key)))) continue
      const seen = sectionsFirst[key]
      if (!seen) {
        sectionsFirst[key] = { frame: at, seconds: at * TICK_SECONDS, single: snippet(sa.get(key)), dual: snippet(sb.get(key)), frames: 1, lastFrame: at, episodes: 1, longestEpisode: 1, episode: 1 }
        continue
      }
      // Episodes: maximal runs of consecutive divergent frames. A one-frame shift of an event shows as short episodes.
      if (seen.lastFrame === at - 1) seen.episode += 1
      else { seen.episodes += 1; seen.episode = 1 }
      seen.longestEpisode = Math.max(seen.longestEpisode, seen.episode)
      seen.frames += 1; seen.lastFrame = at
    }
  }
  // F_0 after construction: the t = 0 poll has run.
  compare(single, dual, 0)
  const limit = frameLimit(scenario)
  let singleFrames = 0, dualFrames = 0
  for (let t = 0; t < limit && !(single.runner.finished && dual.runner.finished); t += 1) {
    if (!single.runner.finished) { single.kernel.advance(1); singleFrames += 1 }
    if (!dual.runner.finished) { dual.kernel.advance(1); dualFrames += 1 }
    frame = t + 1
    // Compare while both still fly the same instant; once one has ended, its state is its end state.
    if (singleFrames === frame && dualFrames === frame) compare(single, dual, frame)
    const system = dual.system!
    if (dualFrames === frame) {
      if (system.mode === 'SYNC') syncFrames += 1
      else if (!firstDrop) {
        // The fault log is newest first.
        firstDrop = { frame, seconds: frame * TICK_SECONDS, fault: system.computers[0].faultLog.find(fault => fault.text.startsWith('X-SIDE SYNC LOST'))?.text ?? null }
      }
    }
  }
  const sa = sections(single), sb = sections(dual)
  const endSections: Record<string, { single: string; dual: string }> = {}
  for (const key of new Set([...sa.keys(), ...sb.keys()])) {
    if (!sameBytes(encode(sa.get(key)), encode(sb.get(key)))) endSections[key] = { single: snippet(sa.get(key)), dual: snippet(sb.get(key)) }
  }
  return {
    single: record(single, singleFrames), dual: record(dual, dualFrames),
    // Both computers, as the I1 census digests a dual run: comparable with its golden end digests.
    dualSystemEnd: frameDigest(frameBytes({ computers: dual.system!.computers, flights: dual.system!.flights }, new CanonicalWriter())),
    firstFrame, divergentFrames: divergent, comparedFrames: compared,
    sectionsFirst, endSections, sync: { residency: dualFrames ? syncFrames / dualFrames : 1, firstDrop },
  }
}
