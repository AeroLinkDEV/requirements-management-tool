import { createHash } from 'node:crypto'
import { readFileSync } from 'node:fs'
import { expect, logicTest as test } from './isolated-client-test'
import { bearingDeg, distanceNm } from '../src/fmsCdu/fmsModel'
import { ScenarioRunner, procedureText, reportMarkdown, runHeadless, scenarioProblems, type Scenario } from '../src/fmsCdu/scenario'
import { SCENARIO_LIBRARY } from '../src/fmsCdu/scenarioLibrary'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'

// The real-data demonstration (kbtvDemo.ts): the FAA CIFP 2609 extract for Burlington, Vermont, bundled with the client,
// a one-action load, a start state 8 NM before STAEV, and library scenarios that fly RNAV (GPS) RWY 15 on its published
// FAS. The reader and the published FAS are proved in fms-cifp.spec.ts; this is the bench's use of them.
const FIXTURE = readFileSync('tests/fixtures/cifp/kbtv-2609.pc', 'latin1')
const START = Date.UTC(2026, 8, 27, 14, 0, 0)

type Demo = typeof import('../src/fmsCdu/kbtvDemo')
type Data = typeof import('../src/fmsCdu/data/kbtvCifp2609')
// Read without assuming the modules exist, so a missing demonstration fails on an expectation, not on loading the file.
const demo = async (): Promise<Demo | null> => import('../src/fmsCdu/kbtvDemo').catch(() => null)
const data = async (): Promise<Data | null> => import('../src/fmsCdu/data/kbtvCifp2609').catch(() => null)
const scenario = (id: string) => SCENARIO_LIBRARY.find(entry => entry.id === id)

test('the bundled KBTV data is the CIFP fixture, byte for byte, and its recorded SHA-256', async () => {
  const bundled = await data()
  expect(bundled, 'the bundled KBTV data module').not.toBeNull()
  expect(bundled!.KBTV_CIFP_2609).toBe(FIXTURE)
  expect(createHash('sha256').update(bundled!.KBTV_CIFP_2609, 'latin1').digest('hex')).toBe(bundled!.KBTV_CIFP_2609_SHA256)
})

test('loading the KBTV demonstration activates the CIFP cycle, keeps the demonstration route, and is idempotent', async () => {
  const kbtv = await demo()
  expect(kbtv, 'the KBTV demonstration module').not.toBeNull()
  const unit = new ScriptedFms(() => new Date(START))
  const route = unit.activeRoute.legs.map(leg => (leg.kind === 'wpt' ? leg.ident : leg.kind))
  expect(kbtv!.loadKbtvDemonstration(unit)).toEqual({ loaded: 'CIFP2609' })
  expect(unit.activeCycle).toMatchObject({ id: 'CIFP2609', source: kbtv!.KBTV_SOURCE })
  // The source says what the data is: real, public domain, and not for navigation.
  expect(kbtv!.KBTV_SOURCE).toMatch(/FAA CIFP 2609.*public-domain.*not for navigation/)
  expect(unit.navdb.airport('KBTV')).toBeDefined()
  // The built-in data is merged, not replaced: the demonstration route and its airports still resolve.
  expect(unit.navdb.airport('CYUL')).toBeDefined()
  expect(unit.activeRoute.legs.map(leg => (leg.kind === 'wpt' ? leg.ident : leg.kind))).toEqual(route)
  const log = unit.datasetLog.length
  expect(kbtv!.loadKbtvDemonstration(unit)).toEqual({ loaded: 'CIFP2609', already: true })
  expect(unit.datasetLog).toHaveLength(log)
})

test('the KBTV RNAV RWY 15 start state: the approach executed and armed, the aircraft 8 NM before STAEV at 3200 ft, recorded', async () => {
  const kbtv = await demo()
  expect(kbtv, 'the KBTV demonstration module').not.toBeNull()
  const unit = new ScriptedFms(() => new Date(START))
  expect(kbtv!.setUpKbtvRnav15(unit)).toEqual({ ready: true })
  expect(unit.activeRoute.dest).toBe('KBTV')
  expect(unit.activeRoute.legs.slice(0, 4).map(leg => (leg.kind === 'wpt' ? leg.ident : leg.kind))).toEqual(['STAEV', 'FOVES', 'JUNEL', 'RW15'])
  expect(unit.approachArmed).toBe(true)
  expect(unit.routeStatus).toBe('ACT')
  const staev = unit.coordinates('STAEV')!, foves = unit.coordinates('FOVES')!
  expect(distanceNm(unit.truePosition, staev)).toBeCloseTo(8, 3)
  // On the approach course extended back from STAEV, tracking toward it.
  expect(Math.abs(bearingDeg(unit.truePosition, staev) - bearingDeg(staev, foves))).toBeLessThan(0.5)
  expect(Math.abs(unit.track - bearingDeg(staev, foves))).toBeLessThan(0.5)
  expect(unit.altitude).toBe(3200)
  expect(unit.vnav.desNow).toBe(true)
  expect(unit.engineeringLog.at(-1)).toMatchObject({ action: 'PLACE AIRCRAFT', detail: expect.stringMatching(/^KBTV RNAV \(GPS\) RWY 15 set-up: N44\d{2}\.\dW073\d{2}\.\d, track 131°, 3200 FT$/) })
  // The approach flown is the published one, executed from the CIFP cycle.
  unit.updateNavigation(0)
  expect(unit.executedFas?.cycle).toBe('CIFP2609')
  expect(unit.executedFas?.fas.referencePathId).toBe('W15A')
})

test('the KBTV LPV scenario is in the library and passes: armed LPV, captured at FOVES, on the published path at the threshold', () => {
  const lpv = scenario('kbtv-rnav15-lpv')
  expect(lpv, 'the KBTV LPV library scenario').toBeDefined()
  const { runner } = runHeadless(lpv!)
  expect(runner.results.filter(result => result.status !== 'done' && result.status !== 'pass')).toEqual([])
  expect(runner.outcome).toBe('passed')
  // The capture check ran when JUNEL became active, that is, once the FAF was sequenced.
  expect(runner.results[2]).toMatchObject({ status: 'pass', actual: expect.stringMatching(/^LPV CAPTURED, APPR/) })
  // The report says what the data is.
  expect(reportMarkdown(runner)).toMatch(/^- Navigation data: CIFP2609 \(FAA CIFP 2609.*not for navigation\)$/m)
  expect(procedureText(lpv!).preconditions).toMatch(/start state KBTV RNAV \(GPS\) RWY 15/)
})

test('the KBTV integrity scenario passes: integrity lost after capture ends the approach to a hold, and TOGA climbs', () => {
  const lost = scenario('kbtv-rnav15-integrity-lost')
  expect(lost, 'the KBTV integrity library scenario').toBeDefined()
  const { runner } = runHeadless(lost!)
  expect(runner.outcome).toBe('passed')
  expect(runner.results[4]).toMatchObject({ status: 'pass', actual: expect.stringMatching(/^NO APPR OFF, ALT HOLD/) })
})

test('a scenario start state and the approach check are validated, and the check needs the flight simulation', () => {
  const base = { id: 't', title: 'T', objective: '', maxSeconds: 10, steps: [{ when: { kind: 'start' }, action: { kind: 'expectApproach', state: 'OFF' } }] } as unknown as Scenario
  expect(scenarioProblems(base)).toEqual([])
  expect(scenarioProblems({ ...base, start: 'nowhere' } as unknown as Scenario)).toEqual(['unknown start state "nowhere"'])
  expect(scenarioProblems({ ...base, steps: [{ when: { kind: 'start' }, action: { kind: 'expectApproach' } }] } as unknown as Scenario)).toEqual([
    'step 1: expectApproach needs at least one of type, state, verticalMode and maxVerticalFt',
  ])
  expect(scenarioProblems({ ...base, steps: [{ when: { kind: 'start' }, action: { kind: 'expectApproach', type: 'GLS' } }] } as unknown as Scenario)).toEqual([
    'step 1: expectApproach type must be ILS, LPV, LNAV/VNAV, LNAV or NO APPR',
  ])
  // A runner with no flight simulation cannot check the approach mode: an execution error, never a pass.
  const runner = new ScenarioRunner(base, new ScriptedFms(() => new Date(START)))
  expect(runner.outcome).toBe('error')
  expect(runner.results[0].actual).toMatch(/needs the flight simulation/)
})
