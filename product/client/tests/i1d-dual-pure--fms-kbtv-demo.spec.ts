import { createHash } from 'node:crypto'
import { readFileSync } from 'node:fs'
import { expect, logicTest as test } from './isolated-client-test'
import { bearingDeg, distanceNm } from '../src/fmsCdu/fmsModel'
import { ScenarioRunner, procedureText, reportMarkdown, runHeadless, scenarioProblems, type Scenario } from './fixtures/fms-headless-outcome-census-shim'
import { SCENARIO_LIBRARY } from '../src/fmsCdu/scenarioLibrary'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { LAB_AIRLINE_VNAV_PROFILE, LATER_SBAS_PROFILE } from '../src/fmsCdu/profile'
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
  expect.soft(bundled, 'the bundled KBTV data module').not.toBeNull()
  expect.soft(bundled!.KBTV_CIFP_2609).toBe(FIXTURE)
  expect.soft(createHash('sha256').update(bundled!.KBTV_CIFP_2609, 'latin1').digest('hex')).toBe(bundled!.KBTV_CIFP_2609_SHA256)
})

test('loading the KBTV demonstration activates the CIFP cycle, keeps the demonstration route, and is idempotent', async () => {
  const kbtv = await demo()
  expect.soft(kbtv, 'the KBTV demonstration module').not.toBeNull()
  const unit = new ScriptedFms(() => new Date(START))
  const route = unit.activeRoute.legs.map(leg => (leg.kind === 'wpt' ? leg.ident : leg.kind))
  expect.soft(kbtv!.loadKbtvDemonstration(unit)).toEqual({ loaded: 'CIFP2609' })
  expect.soft(unit.activeCycle).toMatchObject({ id: 'CIFP2609', source: kbtv!.KBTV_SOURCE })
  // The source says what the data is: real, public domain, and not for navigation.
  expect.soft(kbtv!.KBTV_SOURCE).toMatch(/FAA CIFP 2609.*public-domain.*not for navigation/)
  expect.soft(unit.navdb.airport('KBTV')).toBeDefined()
  // The built-in data is merged, not replaced: the demonstration route and its airports still resolve.
  expect.soft(unit.navdb.airport('CYUL')).toBeDefined()
  expect.soft(unit.activeRoute.legs.map(leg => (leg.kind === 'wpt' ? leg.ident : leg.kind))).toEqual(route)
  const log = unit.datasetLog.length
  expect.soft(kbtv!.loadKbtvDemonstration(unit)).toEqual({ loaded: 'CIFP2609', already: true })
  expect.soft(unit.datasetLog).toHaveLength(log)
})

test('the later-SBAS KBTV RNAV RWY 15 start state: the approach executed and armed, 8 NM before STAEV at 3200 ft', async () => {
  const kbtv = await demo()
  expect.soft(kbtv, 'the KBTV demonstration module').not.toBeNull()
  const unit = new ScriptedFms(() => new Date(START), { profile: LATER_SBAS_PROFILE })
  const sim = new FlightSimulator(unit)
  expect.soft(kbtv!.setUpKbtvRnav15(unit, sim)).toEqual({ ready: true })
  expect.soft(unit.activeRoute.dest).toBe('KBTV')
  expect.soft(unit.activeRoute.legs.slice(0, 4).map(leg => (leg.kind === 'wpt' ? leg.ident : leg.kind))).toEqual(['STAEV', 'FOVES', 'JUNEL', 'RW15'])
  expect.soft(unit.approachArmed).toBe(true)
  expect.soft(unit.routeStatus).toBe('ACT')
  const staev = unit.coordinates('STAEV')!, foves = unit.coordinates('FOVES')!
  expect.soft(distanceNm(unit.truePosition, staev)).toBeCloseTo(8, 3)
  // On the approach course extended back from STAEV, tracking toward it.
  expect.soft(Math.abs(bearingDeg(unit.truePosition, staev) - bearingDeg(staev, foves))).toBeLessThan(0.5)
  expect.soft(Math.abs(unit.track - bearingDeg(staev, foves))).toBeLessThan(0.5)
  expect.soft(unit.altitude).toBe(3200)
  // The helicopter profile: the crew preselects the FAF altitude and descends to it in VS; the FMS descends nothing.
  expect.soft(sim.selectedAltitude).toBe(Math.round(unit.fafAltitudeCorrected))
  expect.soft(sim.verticalSpeedTarget).toBe(-500)
  expect.soft(unit.vnav.desNow).toBe(false)
  // The laboratory airline-style VNAV profile keeps the DES NOW set-up instead.
  const lab = new ScriptedFms(() => new Date(START), { profile: LAB_AIRLINE_VNAV_PROFILE })
  expect.soft(kbtv!.setUpKbtvRnav15(lab, new FlightSimulator(lab))).toEqual({ ready: true })
  expect.soft(lab.vnav.desNow).toBe(true)
  expect.soft(unit.engineeringLog.at(-1)).toMatchObject({ action: 'PLACE AIRCRAFT', detail: expect.stringMatching(/^KBTV RNAV \(GPS\) RWY 15 set-up: N44\d{2}\.\dW073\d{2}\.\d, track 131°, 3200 FT$/) })
  // The approach flown is the published one, executed from the CIFP cycle.
  unit.updateNavigation(0)
  expect.soft(unit.executedFas?.cycle).toBe('CIFP2609')
  expect.soft(unit.executedFas?.fas.referencePathId).toBe('W15A')
})

test('the KBTV LPV scenario is in the library and passes: armed LPV, captured at FOVES, on the published path at the threshold', () => {
  const lpv = scenario('kbtv-rnav15-lpv')
  expect.soft(lpv, 'the KBTV LPV library scenario').toBeDefined()
  const { runner } = runHeadless(lpv!)
  expect.soft(runner.results.filter(result => result.status !== 'done' && result.status !== 'pass')).toEqual([])
  expect.soft(runner.outcome).toBe('passed')
  // The capture check ran when JUNEL became active, that is, once the FAF was sequenced.
  expect.soft(runner.results[2]).toMatchObject({ status: 'pass', actual: expect.stringMatching(/^LPV CAPTURED, APPR/) })
  // The report says what the data is.
  expect.soft(reportMarkdown(runner)).toMatch(/^- Navigation data: CIFP2609 \(FAA CIFP 2609.*not for navigation\)$/m)
  expect.soft(procedureText(lpv!).preconditions).toMatch(/start state KBTV RNAV \(GPS\) RWY 15/)
})

test('the default KBTV mission flies LNAV with advisory VNAV and crew VS, while LPV explicitly names the later profile', () => {
  const nominal = scenario('kbtv-rnav15-advisory')!
  expect.soft(nominal.profile).toBe('cma9000-s300-heli-civil')
  const { runner, fms, sim } = runHeadless(nominal)
  expect.soft(runner.results.filter(result => result.status !== 'done' && result.status !== 'pass')).toEqual([])
  expect.soft(runner.outcome).toBe('passed')
  expect.soft(fms.executedFas).toBeNull()
  expect.soft(sim.modeEvents.some(event => event.event === 'APPR CAPTURED')).toBe(false)
  expect.soft(scenario('kbtv-rnav15-lpv')?.profile).toBe(LATER_SBAS_PROFILE.id)
})

test('the KBTV integrity scenario passes: integrity lost after capture ends the approach to a hold, and TOGA climbs', () => {
  const lost = scenario('kbtv-rnav15-integrity-lost')
  expect.soft(lost, 'the KBTV integrity library scenario').toBeDefined()
  const { runner } = runHeadless(lost!)
  expect.soft(runner.outcome).toBe('passed')
  expect.soft(runner.results[4]).toMatchObject({ status: 'pass', actual: expect.stringMatching(/^NO APPR OFF, ALT HOLD/) })
})

test('the approach check fails on each field that differs: the type, the approach mode, and the deviation from the path', () => {
  // At the start state the approach is LPV and armed. Under the laboratory airline-style VNAV profile the aircraft is
  // 1300 ft below the VNAV path (capped at the demonstration's cruise altitude until DES NOW brings the path down); under
  // the helicopter profile there is no en-route path at all. Each failing check below names one wrong field.
  const check = (action: Record<string, unknown>, profile = LATER_SBAS_PROFILE.id) => {
    const { runner } = runHeadless({ id: 'c', title: 'C', objective: '', maxSeconds: 1, start: 'kbtv-rnav15', profile, steps: [{ when: { kind: 'start' }, action: { kind: 'expectApproach', ...action }, within: 0.5 }] } as unknown as Scenario)
    return runner.results[0]
  }
  expect.soft(check({ type: 'LPV', state: 'ARMED' })).toMatchObject({ status: 'pass' })
  expect.soft(check({ type: 'LNAV', state: 'ARMED' })).toMatchObject({ status: 'fail', actual: expect.stringMatching(/^LPV ARMED/) })
  expect.soft(check({ type: 'LPV', state: 'CAPTURED' })).toMatchObject({ status: 'fail', actual: expect.stringMatching(/^LPV ARMED/) })
  expect.soft(check({ maxVerticalFt: 2000 }, 'lab-airline-vnav')).toMatchObject({ status: 'pass' })
  expect.soft(check({ maxVerticalFt: 30 }, 'lab-airline-vnav')).toMatchObject({ status: 'fail', actual: expect.stringMatching(/-1300 ft from the path$/) })
  expect.soft(check({ maxVerticalFt: 2000 })).toMatchObject({ status: 'fail', actual: expect.stringMatching(/no path$/) })
})

test('a scenario start state and the approach check are validated, and the check needs the flight simulation', () => {
  const base = { id: 't', title: 'T', objective: '', maxSeconds: 10, steps: [{ when: { kind: 'start' }, action: { kind: 'expectApproach', state: 'OFF' } }] } as unknown as Scenario
  expect.soft(scenarioProblems(base)).toEqual([])
  expect.soft(scenarioProblems({ ...base, start: 'nowhere' } as unknown as Scenario)).toEqual(['unknown start state "nowhere"'])
  expect.soft(scenarioProblems({ ...base, steps: [{ when: { kind: 'start' }, action: { kind: 'expectApproach' } }] } as unknown as Scenario)).toEqual([
    'step 1: expectApproach needs at least one of type, state, verticalMode and maxVerticalFt',
  ])
  expect.soft(scenarioProblems({ ...base, steps: [{ when: { kind: 'start' }, action: { kind: 'expectApproach', type: 'GLS' } }] } as unknown as Scenario)).toEqual([
    'step 1: expectApproach type must be ILS, LPV, LNAV/VNAV, LNAV or NO APPR',
  ])
  // A runner with no flight simulation cannot check the approach mode: an execution error, never a pass.
  const runner = new ScenarioRunner(base, new ScriptedFms(() => new Date(START)))
  expect.soft(runner.outcome).toBe('error')
  expect.soft(runner.results[0].actual).toMatch(/needs the flight simulation/)
})
