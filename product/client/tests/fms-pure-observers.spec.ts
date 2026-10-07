import { expect, logicTest as test } from './isolated-client-test'
import { DualFmsSystem } from '../src/fmsCdu/dualFms'
import { aircraftData, fmsOutputs } from '../src/fmsCdu/efis'
import { fmsGpsView } from '../src/fmsCdu/gpsBench'
import { stimulusFor } from '../src/fmsCdu/gpsStimulus'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { distanceNm, offset, type Leg } from '../src/fmsCdu/fmsModel'
import { CORE_PAGES } from '../src/fmsCdu/fmsPages'
import { DATALINK_PAGES } from '../src/fmsCdu/datalinkPages'
import { NAV_PAGES } from '../src/fmsCdu/navPages'
import { PLANNING_PAGES } from '../src/fmsCdu/planningPages'
import { profileById } from '../src/fmsCdu/profile'
import { RADIO_PAGES } from '../src/fmsCdu/radioPages'
import { ScenarioRunner, TICK_SECONDS, advanceTicks, runHeadless, scenarioStart, type Scenario } from '../src/fmsCdu/scenario'
import { SCENARIO_LIBRARY } from '../src/fmsCdu/scenarioLibrary'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { TACTICAL_PAGES } from '../src/fmsCdu/tacticalPages'
import { WMM2025_DATABASE } from '../src/fmsCdu/wmm2025'
import { SCRATCHPAD_LINE, screenText } from '../src/fmsCdu/screen'
import type { CduFunction } from '../src/fmsCdu/variants'

// #1518: reading the FMS (the CDU screen, the bus outputs, the guidance) must never change it. The live bench renders
// after every callback, so each case runs twice: once rendered the way the bench renders, once with no read at all.
// Both must give the bench's behaviour, so a headless run, a state digest or an analyzer view sees the same simulation.
const START = Date.UTC(2026, 8, 27, 14, 0, 0)
const ALL_PAGES = [CORE_PAGES, PLANNING_PAGES, NAV_PAGES, RADIO_PAGES, TACTICAL_PAGES, DATALINK_PAGES].flatMap(pages => Object.values(pages))
const RADIO_DEVICES = ['nav1', 'nav2', 'dme1', 'dme2', 'adf', 'adf2'] as const
/** A read that some states refuse (a device a profile lacks, say): purity is about what it changes, not what it returns. */
const attempt = (read: () => unknown) => { try { read() } catch { /* refused in this state */ } }

/** What the bench reads after each callback: the CDU screen and lamps, and the guidance computer's bus and air data. */
const render = (fms: ScriptedFms, sim: FlightSimulator) => {
  fms.screen(); fms.lamps(); fmsOutputs(fms, sim); aircraftData(fms, sim)
}

const single = () => {
  let now = START
  const fms = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(fms)
  return { fms, sim, advance: (ms: number) => { now += ms } }
}
const press = (fms: ScriptedFms, ...fns: CduFunction[]) => { for (const fn of fns) fms.press(fn) }
const legs = (count: number, from: ScriptedFms): Leg[] =>
  Array.from({ length: count }, (_, i) => ({ kind: 'wpt', ident: `PO${String(i + 1).padStart(2, '0')}`, position: offset(from.position, 0, 5 * (i + 1)) }))

// Owner: the CDU page index is settled by actions, not by drawing the screen. A LEGS view on page 3/3 of a modification
// that ERASE shrinks to the 2-page active route shows 2/2 on the bench; NEXT and the line selects then act on that page.
test('a LEGS route that shrinks under the view pages and line-selects from the page shown, with or without a render', () => {
  const drive = (rendered: boolean, then: CduFunction) => {
    const { fms, sim } = single()
    fms.replaceLegs(legs(7, fms)); fms.press('EXEC')
    fms.replaceLegs(legs(12, fms))
    press(fms, 'LEGS', 'NEXT', 'NEXT')
    if (rendered) render(fms, sim)
    fms.press('LSK6L') // ERASE: the view is back on the 7-leg active route, 2 pages.
    if (rendered) render(fms, sim)
    fms.press(then)
    const lines = screenText(fms.screen())
    return { title: lines[0], scratch: lines[SCRATCHPAD_LINE].trim() }
  }
  for (const then of ['NEXT', 'LSK1L'] as const) expect(drive(false, then), then).toEqual(drive(true, then))
  // ERASE left the view on page 2/2: NEXT wraps to 1/2, and LSK1L copies the sixth leg, the first one shown there.
  expect(drive(false, 'NEXT').title).toMatch(/^ACT RTE 1 LEGS\s+1\/2$/)
  expect(drive(false, 'LSK1L')).toMatchObject({ title: expect.stringMatching(/\s2\/2$/), scratch: 'PO06' })
})

// Owner: a moving waypoint's rendezvous is determined when the action that put it in the route ends (M300 11-37), so the
// 10-second recompute schedule runs from the EXEC, not from whenever the route was first drawn.
test('the rendezvous with a moving waypoint is determined at the EXEC, rendered or not, and keeps its 10 s schedule', () => {
  const drive = (rendered: boolean) => {
    const { fms, sim, advance } = single()
    Object.assign(fms.wind, { direction: 0, speed: 0 })
    fms.defineMoving('SHIP1', offset(fms.position, 30, 12), 90, 40)
    expect(fms.directTo('SHIP1')).toBeUndefined()
    const executed = fms.now.getTime()
    fms.press('EXEC')
    const computed: number[] = []
    for (let t = 0; t < 25; t += 1) {
      if (rendered) render(fms, sim)
      advance(1000); sim.step(1)
      const at = fms.rendezvousFor(fms.activeRoute, 0)!.computedAt
      if (computed.at(-1) !== at) computed.push(at)
    }
    return { schedule: computed.map(at => (at - executed) / 1000), position: fms.truePosition }
  }
  const headless = drive(false)
  expect(headless).toEqual(drive(true))
  expect(headless.schedule).toEqual([0, 10, 20])

  // On the dual bench a SYNC copies the plan into FMS 2 and then reconciles its navigation: FMS 2 determines its
  // rendezvous from where it stands once the whole exchange has ended, not part-way through it (CDU 2 on LEGS shows it).
  const sync = (rendered: boolean) => {
    let now = START
    const system = new DualFmsSystem(() => new Date(now))
    const [one, two] = system.computers
    const draw = () => { if (rendered) { render(one, system.simulator); two.screen(); two.lamps() } }
    const fly = (ticks: number) => { for (let i = 0; i < ticks; i += 1) { now += TICK_SECONDS * 1000; system.step(TICK_SECONDS); draw() } }
    system.setLinkAvailable(false); system.setLinkAvailable(true)
    fly(40)
    Object.assign(one.wind, { direction: 0, speed: 0 })
    one.defineMoving('SHIP1', offset(one.position, 30, 12), 90, 40)
    one.directTo('SHIP1'); one.press('EXEC'); draw()
    two.press('LEGS'); draw()
    fly(8)
    one.dualOperation!.requestMode('SYNC'); draw()
    one.dualOperation!.confirmMode(true); draw()
    const rendezvous = two.rendezvousFor(two.activeRoute, 0)!
    return { mode: system.mode, rendezvous, from: two.position, at: now }
  }
  const synced = sync(false)
  expect(synced).toEqual(sync(true))
  expect(synced).toMatchObject({ mode: 'SYNC', rendezvous: { computedAt: synced.at, condition: 1, achievable: true } })
  // Condition 1 solves from the present position: its distance is the distance from where FMS 2 now stands.
  expect(Math.abs(distanceNm(synced.from, synced.rendezvous.position!) - synced.rendezvous.distanceNm!)).toBeLessThan(1e-9)

  // A scenario action is one computation too: a procedure step (which changes the route without a notification)
  // determines the modification's rendezvous at its own tick, as the bench drawing after that callback did.
  const scenario: Scenario = {
    id: 'pure-observer-procedure', title: 'Procedure with a moving waypoint', objective: 'Rendezvous timing', maxSeconds: 6,
    steps: [
      { when: { kind: 'time', seconds: 2 }, action: { kind: 'procedure', procedure: 'APPROACH', ident: 'R24R' } },
      { when: { kind: 'after', seconds: 3 }, action: { kind: 'expectNoAlert', text: 'NONE' } },
    ],
  }
  const procedure = (rendered: boolean, run = scenario) => {
    let now = START
    const system = new DualFmsSystem(() => new Date(now))
    const one = system.computers[0]
    Object.assign(one.wind, { direction: 0, speed: 0 })
    one.defineMoving('SHIP1', offset(one.position, 30, 12), 90, 40); one.directTo('SHIP1'); one.press('EXEC')
    const runner = new ScenarioRunner(run, one, undefined, system.flights[0])
    while (!runner.finished) { advanceTicks(1, ms => { now += ms }, system, runner); if (rendered) render(one, system.simulator) }
    return { status: one.routeStatus, rendezvous: one.rendezvousFor(one.route, 0) }
  }
  const stepped = procedure(false)
  expect(stepped).toEqual(procedure(true))
  expect(stepped).toMatchObject({ status: 'MOD', rendezvous: { computedAt: START + 2000, condition: 4 } })

  // Declared boundary: each step is its own computation, even when two share a tick (one submission, one computation).
  // A wind step in the procedure's tick comes after the rendezvous is determined, so the solve is the procedure-only one.
  const windAfter: Scenario = { ...scenario, steps: [scenario.steps[0], { when: { kind: 'after', seconds: 0 }, action: { kind: 'wind', direction: 270, speed: 60 } }, scenario.steps[1]] }
  const sameTick = procedure(false, windAfter)
  expect(sameTick).toEqual(procedure(true, windAfter))
  expect(sameTick.rendezvous).toEqual(stepped.rendezvous)
})

// Owner: a settle that keeps starting computations is a programming fault, reported, never carried on in.
test('a settle that never comes to rest throws instead of leaving the FMS half settled', () => {
  const { fms } = single()
  let calls = 0
  fms.attachSettle(() => { calls += 1; fms.raiseAlert(`LOOP ${calls}`) })
  expect(() => fms.press('CLR')).toThrow(/did not come to rest/)
  expect(calls).toBe(4)
})

// Owner of the observer gate: a consumer of the flight's output port is an observer even though it is called from
// inside a step. Reading the bus outputs there, as the moving waypoint becomes the modification's first, keeps nothing.
test('an output-port consumer reading the outputs mid-step changes nothing the step determines', () => {
  const drive = (consumer: boolean) => {
    let now = START
    const fms = new ScriptedFms(() => new Date(now))
    let reads = 0
    const sim: FlightSimulator = new FlightSimulator(fms, consumer ? { write: () => { fmsOutputs(fms, sim); reads += 1 } } : undefined)
    Object.assign(fms.wind, { direction: 0, speed: 0 })
    const fixed = (i: number, nm: number): Leg => ({ kind: 'wpt', ident: `PO${String(i).padStart(2, '0')}`, position: offset(fms.position, 0, nm) })
    fms.replaceLegs([fixed(1, 0.6), fixed(2, 6), fixed(3, 12)]); fms.press('EXEC')
    fms.defineMoving('SHIP1', offset(fms.position, 30, 12), 90, 40)
    fms.replaceLegs([fixed(1, 0.6), { kind: 'wpt', ident: 'SHIP1' }, fixed(2, 6), fixed(3, 12)])
    for (let i = 0; i < 400 && fms.activeRoute.legs[0]?.kind === 'wpt' && (fms.activeRoute.legs[0] as { ident: string }).ident === 'PO01'; i += 1) {
      now += TICK_SECONDS * 1000; sim.step(TICK_SECONDS)
    }
    return { reads: reads > 0, at: now, first: fms.route.legs[0], rendezvous: fms.rendezvousFor(fms.route, 0) }
  }
  const quiet = drive(false), read = drive(true)
  expect(read.reads).toBe(true)
  expect({ ...read, reads: false }).toEqual(quiet)
  expect(quiet.rendezvous).toMatchObject({ condition: 4, computedAt: quiet.at })
})

// Owner: the laboratory FMS-failure reversion latches at the action that failed the FMS, paused or flying, whether or
// not anything reads the guidance before the next callback. Its readers are the bench, the map and the bus outputs.
test('an FMS failure latches its reversion at the toggle without a render, paused and flying, on the dual bench and headless', () => {
  const failure = (sim: FlightSimulator) => sim.modeEvents.filter(event => event.event === 'FMS FAILURE').map(event => ({ ...event, at: event.at.getTime() }))
  const dual = (rendered: boolean, flying: boolean) => {
    let now = START
    const system = new DualFmsSystem(() => new Date(now))
    const callback = () => {
      now += TICK_SECONDS * 1000
      if (flying) system.step(TICK_SECONDS); else system.tick()
      if (rendered) render(system.computers[system.guidanceSide - 1], system.simulator)
    }
    for (let i = 0; i < 4; i += 1) callback()
    const toggledAt = now
    system.computers[0].setCondition('fmsFail', true)
    if (rendered) render(system.computers[0], system.simulator)
    for (let i = 0; i < 4; i += 1) callback()
    return { toggledAt, failure: failure(system.flights[0]) }
  }
  for (const flying of [false, true]) {
    const headless = dual(false, flying)
    expect(headless, flying ? 'flying' : 'paused').toEqual(dual(true, flying))
    expect(headless.failure.map(event => event.at), flying ? 'flying' : 'paused').toEqual([headless.toggledAt])
  }

  // A scenario's condition step fails the FMS inside the runner's poll: headless, the latch is the bench's too.
  const scenario: Scenario = {
    id: 'pure-observer-fms-fail', title: 'FMS failure while flying', objective: 'Reversion latch timing', maxSeconds: 6,
    steps: [
      { when: { kind: 'time', seconds: 2 }, action: { kind: 'condition', condition: 'fmsFail', on: true } },
      { when: { kind: 'after', seconds: 2 }, action: { kind: 'expectLamp', lamp: 'FAIL', lit: true } },
    ],
  }
  const rendered = (() => {
    let now = START
    const fms = new ScriptedFms(() => new Date(now))
    const sim = new FlightSimulator(fms)
    const runner = new ScenarioRunner(scenario, fms, undefined, sim)
    while (!runner.finished) { advanceTicks(1, ms => { now += ms }, sim, runner); render(fms, sim) }
    return failure(sim)
  })()
  const headless = failure(runHeadless(scenario, START).sim)
  expect(headless).toEqual(rendered)
  expect(headless.map(event => event.at)).toEqual([START + 2000])

  // At 4x the bench runs four ticks per callback and draws once: the latch is still at the tick of the toggle, not at
  // the end of the callback, so a run's timeline does not depend on its rate (the scenario run contract).
  const midCallback: Scenario = { ...scenario, steps: [{ ...scenario.steps[0], when: { kind: 'time', seconds: 2.25 } }, scenario.steps[1]] }
  const paced = (rate: number) => {
    let now = START
    const system = new DualFmsSystem(() => new Date(now))
    const runner = new ScenarioRunner(midCallback, system.computers[0], undefined, system.flights[0])
    while (!runner.finished) { advanceTicks(rate, ms => { now += ms }, system, runner); render(system.computers[0], system.simulator) }
    return failure(system.flights[0])
  }
  const once = paced(1)
  expect(paced(4)).toEqual(once)
  expect(once.map(event => event.at)).toEqual([START + 2250])
})

// Owner of purity itself: whatever reads the system between computations (every getter, every page at every index, the
// bus outputs of either computer against either flight) leaves its whole state as it was. The snapshot walks data
// only, never an accessor, so taking it reads nothing. The memos it allows are each keyed by all of its inputs: the
// magnetic model's last field, and the navigation database's last nearby answers (an immutable database's answer per
// exact position and range, which fms-shared-reads.spec.ts proves is the computed answer).
test('reading the whole dual system between computations changes none of its state', () => {
  const snapshot = (root: unknown) => {
    const out = new Map<string, string>(), seen = new Map<object, string>()
    const walk = (value: unknown, path: string) => {
      if (typeof value === 'function') return
      if (value === null || typeof value !== 'object') { out.set(path, Object.is(value, -0) ? '-0' : String(value)); return }
      if (seen.has(value)) { out.set(path, `<ref ${seen.get(value)}>`); return }
      seen.set(value, path)
      if (value instanceof Date) { out.set(path, `D${value.getTime()}`); return }
      if (value instanceof Map) { out.set(`${path}#size`, String(value.size)); for (const [k, v] of value) walk(v, `${path}{${String(k)}}`); return }
      if (value instanceof Set) { out.set(`${path}#size`, String(value.size)); let i = 0; for (const v of value) walk(v, `${path}<${i++}>`); return }
      if (ArrayBuffer.isView(value)) { out.set(path, `buffer ${(value as Uint8Array).length}`); return }
      for (const key of Object.getOwnPropertyNames(value)) {
        const property = Object.getOwnPropertyDescriptor(value, key)!
        if ('value' in property) walk(property.value, `${path}.${key}`)
      }
    }
    walk(root, '$')
    return out
  }
  const getters = (object: object) => {
    const names = new Set<string>()
    for (let p: object | null = object; p && p !== Object.prototype; p = Object.getPrototypeOf(p))
      for (const key of Object.getOwnPropertyNames(p)) if (Object.getOwnPropertyDescriptor(p, key)!.get) names.add(key)
    return [...names]
  }
  const readEverything = (system: DualFmsSystem) => {
    const touch = (object: object) => { for (const name of getters(object)) { try { void (object as Record<string, unknown>)[name] } catch { /* some getters throw in some states */ } } }
    touch(system)
    system.computers.forEach((fms, side) => {
      touch(fms); if (fms.dualOperation) touch(fms.dualOperation)
      fms.screen(); fms.lamps(); fms.profile(); fms.rendezvous()
      for (const route of [fms.activeRoute, fms.route]) route.legs.forEach((leg, i) => { if (leg.kind === 'wpt') { fms.coordinates(leg.ident, route); fms.rendezvousFor(route, i) } })
      for (const page of ALL_PAGES) {
        const count = Math.max(1, page.pages(fms))
        for (let i = 0; i < Math.min(count, 12); i += 1) { try { page.render(fms, i) } catch { /* a page may not render in this state */ } }
      }
      touch(system.flights[side])
      for (const flight of system.flights) { fmsOutputs(fms, flight); aircraftData(fms, flight) }
      // The bench's tabs and cards: the GPS tab (its view, the receivers' buses, the stimulus record), the sensor
      // card's per-device radio reads, and the map's nearby navaids.
      const view = fmsGpsView(fms, system.computers[0]); attempt(() => view.difference())
      for (const receiver of fms.gps) { touch(receiver); attempt(() => receiver.bus()); attempt(() => receiver.rawBus()) }
      for (const index of [0, 1]) attempt(() => view.stimulus.state(index))
      for (const device of RADIO_DEVICES) {
        attempt(() => fms.radioReceiving(device)); attempt(() => fms.adfRelativeBearing(device as 'adf'))
        attempt(() => fms.navRadial(device as 'nav1')); attempt(() => fms.dmeSlantRangeNm(device as 'dme1'))
        attempt(() => fms.dmeReportedIdent(device as 'dme1')); attempt(() => fms.dmeStation(device as 'dme1'))
        attempt(() => fms.radioPort?.faults(device)); attempt(() => fms.radioPort?.dmeReceiving(device as 'dme1'))
      }
      attempt(() => fms.tacanStation()); attempt(() => fms.tacanBearingAndRange())
      attempt(() => fms.navdb.nearby(fms.position, 32))
    })
  }
  const changed: string[] = []
  const rest = (label: string, system: DualFmsSystem) => {
    // The GPS stimulus record is bench state the GPS tab reads, so it is in the snapshot too.
    const root = { system, stimulus: stimulusFor(system.computers[0]) }
    const before = snapshot(root)
    readEverything(system)
    const after = snapshot(root)
    for (const path of new Set([...before.keys(), ...after.keys()]))
      if (before.get(path) !== after.get(path) && !/\.magvar\.cached\b|\.db\.(nearbyMemo|allEntries)\b/.test(path)) changed.push(`${label} ${path}: ${before.get(path)} -> ${after.get(path)}`)
  }

  // The three mechanisms at their rest points: a moving waypoint entered and executed, a LEGS view, an FMS failure.
  let now = START
  const system = new DualFmsSystem(() => new Date(now))
  const [one] = system.computers
  one.defineMoving('SHIP1', offset(one.position, 30, 12), 90, 40); one.directTo('SHIP1'); rest('moving-mod', system)
  one.press('EXEC'); rest('moving-exec', system)
  for (let i = 0; i < 20; i += 1) { now += 250; system.step(0.25) }
  rest('moving-flying', system)
  one.replaceLegs(legs(12, one)); press(one, 'LEGS', 'NEXT', 'NEXT', 'LSK6L'); rest('legs-shrunk', system)
  one.setCondition('fmsFail', true); rest('fail-paused', system)
  now += 250; system.step(0.25); rest('fail-flying', system)
  one.setCondition('fmsFail', false); rest('recovered', system)
  now += 250; system.tick(); rest('recovered-tick', system)
  // Every action settles when it ends, so those rest points are settled already and cannot show a read that latches.
  // Changing a part of the state directly, past every action, leaves one unsettled: a LEGS view whose modification
  // shrinks under page 3/3, and a magnetic table that fails the FMS. Reading must leave both exactly as they are.
  one.replaceLegs(legs(12, one)); press(one, 'LEGS', 'NEXT', 'NEXT')
  one.route.legs.splice(7); rest('legs-unsettled', system)
  one.press('LSK6L')
  // A moving waypoint spliced into the active route: its rendezvous was never determined, and a read must not keep it.
  one.defineMoving('SHIP2', offset(one.position, 90, 20), 0, 30)
  one.activeRoute.legs.splice(1, 0, { kind: 'wpt', ident: 'SHIP2' }); rest('rendezvous-unsettled', system)
  one.activeRoute.legs.splice(1, 1)
  // Masking held while the integrity condition is on. No action can leave that state behind (the condition clears the
  // masking), so the condition is injected into the private condition set directly; drawing the GPS tab must not clear it.
  const stimulus = stimulusFor(one), injected = (one as unknown as { injected: Set<string> }).injected
  stimulus.toggleMasked(0, 7); injected.add('gpsIntegrity'); rest('masking-unsettled', system)
  injected.delete('gpsIntegrity'); stimulus.toggleMasked(0, 7)
  one.magvar.load({ ...WMM2025_DATABASE, coefficients: `${WMM2025_DATABASE.coefficients} ` }); rest('fail-unsettled', system)

  // And one short library scenario, read at a few of its ticks.
  const scenario = SCENARIO_LIBRARY.find(s => s.id === 'manual-rnp')!
  let at = scenarioStart(scenario) ?? START
  const run = new DualFmsSystem(() => new Date(at), { profile: profileById(scenario.profile) })
  const runner = new ScenarioRunner(scenario, run.computers[0], undefined, run.flights[0])
  for (let t = 0; !runner.finished && t < 400; t += 1) {
    advanceTicks(1, ms => { at += ms }, run, runner)
    if (t % 100 === 0) rest(`${scenario.id}@${t}`, run)
  }
  expect(changed.slice(0, 20)).toEqual([])
})

// Owner: the GPS tab's view only reads. The integrity condition takes the satellite selection, so the bench's masking
// is cleared when the condition comes on (and while it holds), not whenever the tab happens to be drawn.
test('the GPS integrity condition clears the bench masking whether or not the GPS tab is drawn', () => {
  const drive = (drawn: boolean) => {
    const { fms } = single()
    const stimulus = stimulusFor(fms)
    const prn = 7 // the masking record, whichever satellites are in view
    stimulus.toggleMasked(0, prn)
    expect(stimulus.state(0).masked).toEqual([prn])
    fms.setCondition('gpsIntegrity', true)
    if (drawn) fmsGpsView(fms)
    const held = stimulus.state(0).masked
    fms.setCondition('gpsIntegrity', false)
    if (drawn) fmsGpsView(fms)
    return { held, after: stimulus.state(0).masked }
  }
  const hidden = drive(false)
  expect(hidden).toEqual(drive(true))
  expect(hidden).toEqual({ held: [], after: [] })
})
