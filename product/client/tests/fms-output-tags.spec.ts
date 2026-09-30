import { expect, logicTest as test } from './isolated-client-test'
import { aircraftData, fmsOutputs } from '../src/fmsCdu/efis'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { setUp87nOffshoreSar } from '../src/fmsCdu/heliDemo'
import { setUpKbtvRnav15 } from '../src/fmsCdu/kbtvDemo'
import { AIRCRAFT_DATA_TAGS, FMS_OUTPUT_TAGS, HELICOPTER_DATA_TAGS, outputEngagement, type OutputTag } from '../src/fmsCdu/outputTags'
import { LAB_AIRLINE_VNAV_PROFILE } from '../src/fmsCdu/profile'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'

// Plan rev 3 A5: provenance, validity, selection and engagement kept distinct, for every output the displays take. The
// catalogues (outputTags.ts) are typed over every key, so an untagged output does not compile; these tests hold the
// catalogues to what the bus and the aircraft data actually carry, and the coupling derived from the engaged modes to
// the bus's own statement of it.

const START = Date.UTC(2026, 8, 30, 15, 0, 0)
function run(setUp: (fms: ScriptedFms, sim: FlightSimulator) => { ready: true } | { refused: string }, options: ConstructorParameters<typeof ScriptedFms>[1] = {}) {
  let now = START
  const unit = new ScriptedFms(() => new Date(now), options)
  const sim = new FlightSimulator(unit)
  expect(setUp(unit, sim)).toEqual({ ready: true })
  const step = () => { now += 250; sim.step(0.25) }
  const snapshot = () => { const outputs = fmsOutputs(unit, sim); return { outputs, air: aircraftData(unit, sim), engagement: outputEngagement(outputs, unit, sim) } }
  return { unit, sim, step, snapshot }
}
const keys = (value: object) => Object.keys(value).sort()
const isWord = (value: unknown) => typeof value === 'object' && value !== null && 'status' in value && ['NORMAL', 'NCD', 'FAIL'].includes((value as { status: string }).status)

/** What each tag promises of the value it describes. */
function honours(tag: OutputTag, value: unknown): boolean {
  if (tag.kind === 'data' || tag.kind === 'target') {
    if (tag.validity === 'word') return isWord(value) && ((value as { status: string }).status === 'NORMAL') === ((value as { value: unknown }).value !== null)
    // Not a word: its validity is not carried as a status, so it must not look like one.
    if (isWord(value)) return false
    if (tag.validity === 'always valid (laboratory)') return value !== null && value !== undefined
    return value !== undefined
  }
  return value !== undefined
}

test('A5: every output the displays take is tagged, and nothing is tagged that is not an output', () => {
  const heli = run(setUp87nOffshoreSar).snapshot()
  expect(keys(heli.outputs)).toEqual(keys(FMS_OUTPUT_TAGS))
  const { helicopter, ...air } = heli.air
  expect(keys(air)).toEqual(keys(AIRCRAFT_DATA_TAGS))
  expect(keys(helicopter!)).toEqual(keys(HELICOPTER_DATA_TAGS))
  // The failed FMS publishes the same set of words, all failed.
  const failed = run(setUp87nOffshoreSar)
  failed.unit.setCondition('fmsFail', true)
  failed.step()
  expect(keys(failed.snapshot().outputs)).toEqual(keys(FMS_OUTPUT_TAGS))
})

test('A5: each data word and target carries its validity as its tag says: a word with its status, a value that is null when missing, or always valid', () => {
  for (const snapshot of [run(setUp87nOffshoreSar).snapshot(), run(setUpKbtvRnav15).snapshot()]) {
    for (const [key, tag] of Object.entries(FMS_OUTPUT_TAGS)) expect(honours(tag, snapshot.outputs[key as keyof typeof snapshot.outputs]), key).toBe(true)
    const { helicopter, ...air } = snapshot.air
    for (const [key, tag] of Object.entries(AIRCRAFT_DATA_TAGS)) expect(honours(tag, air[key as keyof typeof air]), key).toBe(true)
    if (helicopter) for (const [key, tag] of Object.entries(HELICOPTER_DATA_TAGS)) expect(honours(tag, helicopter[key as keyof typeof helicopter]), key).toBe(true)
  }
  // Every data word names a provenance; every target who selects it.
  for (const tag of [...Object.values(FMS_OUTPUT_TAGS), ...Object.values(AIRCRAFT_DATA_TAGS), ...Object.values(HELICOPTER_DATA_TAGS)]) {
    if (tag.kind === 'data' || tag.kind === 'annunciation' || tag.kind === 'plan') expect(tag.provenance.length).toBeGreaterThan(5)
    if (tag.kind === 'target') expect(['crew', 'FMS', 'FMS or procedure']).toContain(tag.selectedBy)
  }
})

test('A5: selection: under the helicopter profile the crew selects altitude and speed and the FMS commands neither; under the airline profile the FMS does', () => {
  const heli = run(setUp87nOffshoreSar).snapshot()
  expect(heli.air.selectedAltitude).not.toBeNull()
  expect(heli.air.selectedSpeed).not.toBeNull()
  expect(heli.outputs.targetAltitude.status).toBe('NCD')
  expect(heli.outputs.targetSpeed.status).toBe('NCD')
  expect(heli.engagement).toMatchObject({ targetAltitude: 'no data', targetSpeed: 'no data' })
  // The laboratory airline profile: the FMS commands speed and altitude, the speed coupled to the FMS speed mode.
  const airline = run(() => ({ ready: true }), { profile: LAB_AIRLINE_VNAV_PROFILE })
  for (let i = 0; i < 8; i++) airline.step()
  const lab = airline.snapshot()
  expect(lab.air.selectedAltitude).toBeNull()
  expect(lab.outputs.targetSpeed.status).toBe('NORMAL')
  expect(lab.engagement.targetSpeed).toBe('coupled')
  expect(lab.outputs.targetAltitude.status).toBe('NORMAL')
  expect(lab.engagement.targetAltitude).toBe(lab.outputs.verticalMode?.startsWith('VNAV') ? 'coupled' : 'advisory')
})

test('A5: coupling follows the engaged mode: NAV consumes the roll command; on a heading the FMS publishes none', () => {
  const heli = run(setUp87nOffshoreSar)
  expect(heli.sim.axisModes.roll).toBe('NAV')
  expect(heli.snapshot().engagement.rollCommand).toBe('coupled')
  heli.sim.selectHeading(200)
  heli.step()
  const onHeading = heli.snapshot()
  expect(onHeading.outputs.rollCommand.status).toBe('NCD')
  expect(onHeading.engagement.rollCommand).toBe('no data')
})

test('A5: the FMS transition request is coupled while the autopilot flies it (TD, the gate segment, TD/H) and advisory otherwise', () => {
  const heli = run(setUp87nOffshoreSar)
  expect(heli.snapshot().engagement.transitionRequest).toBe('no data')
  heli.unit.placeAircraft({ position: heli.unit.truePosition, track: 230, altitude: 500 }, 'test: over the sighting')
  for (let i = 0; i < 4; i++) heli.step()
  for (const key of ['TACT', 'LSK1R', 'LSK4L', 'LSK6R', 'EXEC'] as const) heli.unit.press(key)
  const runs: { engagement: string; ticks: number; collective: string[] }[] = []
  const tdhRoll = new Set<string>()
  for (let t = 0; t < 4 * 1800 && !heli.sim.hoverCaptured; t++) {
    heli.step()
    const snap = heli.snapshot(), engagement = snap.engagement.transitionRequest, collective = heli.sim.axisModes.collective
    if (heli.sim.axisModes.roll === 'TD/H') tdhRoll.add(snap.engagement.rollCommand)
    const last = runs.at(-1)
    if (last?.engagement === engagement) { last.ticks++; if (!last.collective.includes(collective)) last.collective.push(collective) }
    else runs.push({ engagement, ticks: 1, collective: [collective] })
  }
  expect(heli.sim.hoverCaptured).toBe(true)
  // Published at TDN, taken up by the autopilot on its next step; flown through TD, the gate segment (RHT, TD/H to
  // follow) and TD/H; advisory once the hover is captured at MRK.
  expect(runs.map(r => r.engagement)).toEqual(['no data', 'advisory', 'coupled', 'advisory'])
  expect(runs[1].ticks).toBe(1)
  expect(runs[2].collective).toEqual(expect.arrayContaining(['TD', 'RHT']))
  // TD/H on the roll axis steers to MRK on the transition, not on label 121: the roll command is not coupled then.
  expect(tdhRoll.has('coupled')).toBe(false)
  expect(heli.sim.transitionInProgress).toBeNull()
})

test('A5: the vertical deviation\'s coupling, derived from the engaged mode, agrees with the bus at every step of the KBTV approach, advisory before capture and coupled after', () => {
  const kbtv = run(setUpKbtvRnav15)
  const seen = new Set<string>()
  let disagreements: string[] = []
  const check = (label: string, snapshot: ReturnType<typeof kbtv.snapshot>) => {
    const e = snapshot.engagement.verticalDeviation
    seen.add(e)
    if (e !== 'no data' && snapshot.outputs.verticalCoupled !== (e === 'coupled')) disagreements = [...disagreements, `${label}: ${e} but the bus says coupled=${snapshot.outputs.verticalCoupled}`]
  }
  for (let t = 0; t < 4 * 900 && kbtv.unit.activeRoute.legs[0]; t++) { kbtv.step(); check(`KBTV t=${t / 4}`, kbtv.snapshot()) }
  expect(disagreements).toEqual([])
  // The approach was both advisory (a path shown before capture) and coupled (APPR).
  expect([...seen]).toEqual(expect.arrayContaining(['advisory', 'coupled']))
})

test('A5: on the airline profile the vertical deviation is advisory in DES NOW and coupled once VNAV PTH flies the path, as the bus says', () => {
  let now = START
  const unit = new ScriptedFms(() => new Date(now), { profile: LAB_AIRLINE_VNAV_PROFILE })
  const sim = new FlightSimulator(unit)
  const fly = (seconds: number, until?: () => boolean) => { for (let t = 0; t < seconds; t++) { now += 1000; sim.step(1); if (until?.()) return true } return false }
  const active = () => { const leg = unit.activeRoute.legs[0]; return leg?.kind === 'wpt' ? leg.ident : null }
  // At 80 kt the path descends at about 420 fpm, so DES NOW's 1000 fpm is its own rate (as fms-vnav.spec.ts flies it).
  for (const key of ['VNAV', 'NEXT', 'CHAR_8', 'CHAR_0', 'LSK1R'] as const) unit.press(key)
  expect(fly(3 * 3600, () => active() === 'DEMEL')).toBe(true)
  for (const key of ['VNAV', 'NEXT', 'NEXT', 'LSK6L'] as const) unit.press(key)
  expect(unit.vnav.desNow).toBe(true)
  const byMode = new Map<string, Set<string>>()
  let disagreements: string[] = []
  fly(3600, () => {
    const outputs = fmsOutputs(unit, sim), e = outputEngagement(outputs, unit, sim).verticalDeviation
    const mode = String(outputs.verticalMode)
    byMode.set(mode, (byMode.get(mode) ?? new Set()).add(e))
    if (e !== 'no data' && outputs.verticalCoupled !== (e === 'coupled')) disagreements = [...disagreements, `${mode}: ${e}, bus coupled=${outputs.verticalCoupled}`]
    return !unit.vnav.desNow && mode === 'VNAV PTH'
  })
  fly(60, () => { const o = fmsOutputs(unit, sim); const e = outputEngagement(o, unit, sim).verticalDeviation; if (e !== 'no data' && o.verticalCoupled !== (e === 'coupled')) disagreements = [...disagreements, `after: ${e}`]; return false })
  expect(disagreements).toEqual([])
  // In DES NOW the deviation is shown (or not yet computed) but never flown.
  expect(byMode.get('DES NOW')).toContain('advisory')
  expect(byMode.get('DES NOW')).not.toContain('coupled')
  expect(byMode.get('VNAV PTH')).toContain('coupled')
})

test('A5: an FMS output that is not published is no data, whatever mode would consume it', () => {
  let now = START
  const unit = new ScriptedFms(() => new Date(now), { profile: LAB_AIRLINE_VNAV_PROFILE })
  const sim = new FlightSimulator(unit)
  for (let i = 0; i < 8; i++) { now += 250; sim.step(0.25) }
  expect(outputEngagement(fmsOutputs(unit, sim), unit, sim).targetSpeed).toBe('coupled')
  unit.setCondition('fmsFail', true)
  now += 250; sim.step(0.25)
  const outputs = fmsOutputs(unit, sim)
  expect(outputs.targetSpeed.status).toBe('FAIL')
  expect(outputEngagement(outputs, unit, sim)).toEqual({ rollCommand: 'no data', verticalDeviation: 'no data', targetAltitude: 'no data', targetSpeed: 'no data', transitionRequest: 'no data' })
})
