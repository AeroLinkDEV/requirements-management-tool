import { createHash } from 'node:crypto'
import { expect, logicTest as test } from './isolated-client-test'
import { DualFmsSystem } from '../src/fmsCdu/dualFms'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { distanceNm, longitudeDelta, offset, type LatLon } from '../src/fmsCdu/fmsModel'
import { NavDatabase, type NavEntry } from '../src/fmsCdu/navData'
import { ACTIVE_PROFILE, profileById } from '../src/fmsCdu/profile'
import { ScenarioRunner, TICK_SECONDS, advanceTicks, scenarioStart, type Scenario } from '../src/fmsCdu/scenario'
import { SCENARIO_LIBRARY } from '../src/fmsCdu/scenarioLibrary'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'

// #1502 D10: the dual bench's hottest reads stopped copying. NavDatabase.nearby keeps its last few answers and computes
// each distance once; FMS 2 reads the sensor frame FMS 1 published, and the dual system compares the two local solutions
// in place. None of that may change what the simulation computes. These are performance changes with a value contract:
// a kept answer is the answer, and a shared object is a value nobody edits.
const START = Date.UTC(2026, 8, 27, 14, 0, 0)
const library = (id: string) => SCENARIO_LIBRARY.find(scenario => scenario.id === id)!
const identity = (entry: NavEntry) => `${entry.kind}:${entry.ident}@${entry.position.lat},${entry.position.lon}`

/** The nearby answer computed from nothing kept: every entry in the box, sorted by a distance computed in the comparator. */
const computedNearby = (entries: readonly NavEntry[], at: LatLon, nm: number) => entries
  .filter(({ position: p }) => Math.abs(p.lat - at.lat) * 60 < nm && Math.abs(longitudeDelta(at.lon, p.lon)) * 60 * Math.cos((at.lat * Math.PI) / 180) < nm)
  .sort((a, b) => distanceNm(at, a.position) - distanceNm(at, b.position))

const flyScenario = (scenario: Scenario, speed: number, topology: 'single' | 'dual', onStep: (state: unknown, computers: readonly ScriptedFms[]) => void) => {
  let now = scenarioStart(scenario) ?? START
  const clock = () => new Date(now), moveClock = (ms: number) => { now += ms }
  let sim: Pick<FlightSimulator, 'step'>, runner: ScenarioRunner, state: () => unknown, units: ScriptedFms[]
  if (topology === 'single') {
    const fms = new ScriptedFms(clock, { profile: profileById(scenario.profile) })
    const flight = new FlightSimulator(fms)
    sim = flight; runner = new ScenarioRunner(scenario, fms, undefined, flight); units = [fms]
    state = () => ({ fms, flight, results: runner.results, outcome: runner.outcome })
  } else {
    // The perf harness's dual composition: the runner flies computer 1's flight.
    const profile = profileById(scenario.profile) ?? ACTIVE_PROFILE
    const dual = new DualFmsSystem(clock, { profile, secondaryProfile: profile })
    sim = dual; runner = new ScenarioRunner(scenario, dual.computers[0], undefined, dual.flights[0]); units = [...dual.computers]
    state = () => ({ dual, results: runner.results, outcome: runner.outcome })
  }
  const limit = Math.ceil(scenario.maxSeconds / TICK_SECONDS) + 2
  for (let t = 0; t < limit && !runner.finished; t += speed) { advanceTicks(speed, moveClock, sim, runner); onStep(state(), units) }
  return { runner, computers: units }
}

// Owner of the nearby contract: the entries within range, nearest first, equal distances in database order, the same
// whether the answer is computed or kept. A kept answer keyed on less than (lat, lon, nm), or handed out without a copy
// a caller could edit, gives a different answer here.
test('nearby answers with the entries in range nearest first, the same whether it computes the answer or keeps it', () => {
  const fms = new ScriptedFms(() => new Date(START))
  const db = fms.navdb, entries = db.exportData().entries
  const here = fms.position
  // More questions than the database keeps answers for, asked three times round, so answers are kept, reused and
  // dropped. Each position shares its latitude or its longitude with another, and each is asked at three ranges.
  const positions: LatLon[] = [here, { lat: here.lat, lon: here.lon + 1.5 }, { lat: here.lat + 1.5, lon: here.lon },
    ...[70, 140, 210, 280].map((bearing, i) => offset(here, bearing, 10 + 30 * i)), { lat: -0, lon: 0 }]
  for (let round = 0; round < 3; round += 1)
    for (const at of positions)
      for (const nm of [160, 32, 5]) expect(db.nearby(at, nm).map(identity), `${at.lat},${at.lon} ${nm} NM`).toEqual(computedNearby(entries, at, nm).map(identity))
  // Kept answers are exact: 0.2 NM further south, an entry 4.9 NM north has left the 5 NM range.
  const edge = entries.find(entry => entry.kind === 'navaid')!, { lat, lon } = edge.position
  const inside = { lat: lat - 4.9 / 60, lon }, outside = { lat: lat - 5.1 / 60, lon }
  for (const at of [inside, outside, inside]) expect(db.nearby(at, 5).map(identity)).toEqual(computedNearby(entries, at, 5).map(identity))
  expect([inside, outside].map(at => db.nearby(at, 5).map(identity).includes(identity(edge)))).toEqual([true, false])
  expect(db.nearby(here, 32).length).toBeGreaterThan(0)
  expect(db.nearby(here, 32).length).toBeLessThan(db.nearby(here, 160).length)
  // A caller that edits its answer, computed or kept, edits its own copy; the answer holds the database's own entries.
  const computed = db.nearby(here, 5), kept = db.nearby(here, 5)
  expect(kept.length).toBeGreaterThan(0)
  computed.splice(0); kept.splice(0)
  const again = db.nearby(here, 5)
  expect(again.map(identity)).toEqual(computedNearby(entries, here, 5).map(identity))
  expect(again.every(entry => db.find(entry.ident).includes(entry))).toBe(true)
  // A merged database is another database: it answers with its own entries, not the one it was merged from.
  const moved = { ...again[0], ident: 'ZZMOVED', position: offset(here, 0, 1) } as NavEntry
  const merged = db.merge({ cycle: db.cycle, entries: [moved], airways: [], procedures: [], msa: [] })
  expect(merged.nearby(here, 32).map(identity)).toEqual(computedNearby([...entries, moved], here, 32).map(identity))
  expect(merged.nearby(here, 32).map(entry => entry.ident)).toContain('ZZMOVED')
})

/** Freezes a value all the way down, except the database's own entries (shared module data the database check covers). */
const deepFreeze = <T>(value: T): T => {
  if (value === null || typeof value !== 'object' || Object.isFrozen(value)) return value
  const record = value as Record<string, unknown>
  if (typeof record.ident === 'string' && ['fix', 'navaid', 'airport'].includes(record.kind as string)) return value
  Object.freeze(value)
  for (const key of Object.keys(value)) deepFreeze(record[key])
  return value
}

/**
 * Runs with FMS 1's published sensor frame (which FMS 2 reads in place) and each computer's local solution (which the
 * dual system compares in place) frozen as they are handed out, so a later edit by either computer, the dual system or
 * the flight model throws where it happens (the source modules are strict).
 */
const frozen = <T>(run: () => T): T => {
  const proto = ScriptedFms.prototype, { peekNavigationInputs, peekLocalNavigationSolution } = proto
  proto.peekNavigationInputs = function () { return deepFreeze(peekNavigationInputs.call(this)) }
  proto.peekLocalNavigationSolution = function () { return deepFreeze(peekLocalNavigationSolution.call(this)) }
  try { return run() } finally { Object.assign(proto, { peekNavigationInputs, peekLocalNavigationSolution }) }
}

/**
 * A value digest of the whole state: every own enumerable property, Maps, Sets and Dates by value, -0 and NaN distinct.
 * The navigation database is left out: it holds the nearby answers kept, which the copying reference never keeps.
 */
const digest = (root: unknown) => {
  const memo = new Map<object, string>(), stack = new Set<object>()
  const walk = (value: unknown): string => {
    if (value === null) return 'n'
    switch (typeof value) {
      case 'number': return `d${Object.is(value, -0) ? '-0' : String(value)}`
      case 'string': return `s${JSON.stringify(value)}`
      case 'boolean': return value ? 'T' : 'F'
      case 'undefined': return 'u'
      case 'bigint': return `b${value}`
      case 'function': case 'symbol': return ''
    }
    const object = value as object, known = memo.get(object)
    if (known !== undefined) return known
    if (object instanceof NavDatabase) return '<db>'
    if (stack.has(object)) return '^'
    stack.add(object)
    let text: string
    if (object instanceof Date) text = `D${object.getTime()}`
    else if (object instanceof Map) text = `M{${[...object].map(([k, v]) => `${walk(k)}=${walk(v)}`).join(',')}}`
    else if (object instanceof Set) text = `S{${[...object].map(walk).join(',')}}`
    else if (Array.isArray(object)) text = `[${object.map(walk).join(',')}]`
    else if (ArrayBuffer.isView(object)) text = `A${Array.from(object as unknown as ArrayLike<number>).join(',')}`
    else text = `{${object.constructor?.name ?? ''} ${Object.keys(object).map(key => `${key}:${walk((object as Record<string, unknown>)[key])}`).join(',')}}`
    stack.delete(object)
    if (text.length > 120) text = `#${createHash('sha1').update(text).digest('hex').slice(0, 20)}`
    memo.set(object, text)
    return text
  }
  return createHash('sha1').update(walk(root)).digest('hex').slice(0, 16)
}

/** The database's own entry objects in database order (exportData copies them), listed once per database. */
const listed = new WeakMap<NavDatabase, NavEntry[]>()
const ownEntries = (db: NavDatabase) => {
  let entries = listed.get(db)
  if (!entries) listed.set(db, entries = [...new Set(db.exportData().entries.map(entry => entry.ident))].flatMap(ident => db.find(ident)))
  return entries
}

/** Runs with the copying reads and the computed nearby put back, the way the bench read before #1502 D10. */
const copying = <T>(run: () => T): T => {
  const proto = ScriptedFms.prototype, { peekNavigationInputs, peekLocalNavigationSolution } = proto, { nearby } = NavDatabase.prototype
  proto.peekNavigationInputs = function () { return this.navigationInputs }
  proto.peekLocalNavigationSolution = function () { return this.localNavigationSolution }
  NavDatabase.prototype.nearby = function (at: LatLon, nm: number) { return computedNearby(ownEntries(this), at, nm) }
  try { return run() } finally {
    Object.assign(proto, { peekNavigationInputs, peekLocalNavigationSolution }); NavDatabase.prototype.nearby = nearby
  }
}

// Owner of the composition: per step, the whole state of a run with the shared reads and kept answers is the state of
// the same run that copies and recomputes, so the change is bit-identical. The shared run is also frozen: nothing edits
// a value it was handed, and the database the answers come from ends the run as it started. The digest sees what the
// freeze and the nearby test cannot, such as a kept answer reused for a nearby but different position the run flies
// through; the freeze names a writer that leaves no difference behind. A helicopter hover mission in single and dual at one and
// four ticks per step, and the dual fixed-wing approach the comparison 5 analysis measured (W1). The PR that added this
// recorded the same census over the whole library against main; a library-wide run here costs minutes per Fast run.
const CENSUS = [
  ...(['single', 'dual'] as const).flatMap(topology => [1, 4].map(speed => ({ id: '87n-c-hover-feedback-lost', topology, speed }))),
  { id: 'kbtv-rnav15-advisory', topology: 'dual' as const, speed: 4 },
]
for (const { id, topology, speed } of CENSUS)
  test(`${id} ${topology} at ${speed}x computes the same state at every step, sharing or copying`, () => {
    test.setTimeout(180_000)
    const record = () => {
      const steps: string[] = []
      let databases: string[] | null = null
      const exported = (units: readonly ScriptedFms[]) => units.map(unit => JSON.stringify(unit.navdb.exportData()))
      const { runner, computers } = flyScenario(library(id), speed, topology, (state, units) => {
        databases ??= exported(units)
        steps.push(digest(state))
      })
      return { steps, outcome: runner.outcome, databases, after: exported(computers) }
    }
    const reference = copying(record), shared = frozen(record)
    expect(shared.steps.length).toBe(reference.steps.length)
    expect(shared.steps.findIndex((step, i) => step !== reference.steps[i]), 'first step whose state differs').toBe(-1)
    expect(shared.outcome).toBe(reference.outcome)
    expect(shared.after).toEqual(shared.databases)
  })
