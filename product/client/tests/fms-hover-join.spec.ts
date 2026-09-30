import { expect, logicTest as test } from './isolated-client-test'
import { FlightSimulator, arcGeometry, legGeometry } from '../src/fmsCdu/flight'
import { courseDeg, distanceNm, offset, type LatLon } from '../src/fmsCdu/fmsModel'
import { setUp87nOffshoreSar } from '../src/fmsCdu/heliDemo'
import { designBank, radiusAt, type HoldSegment } from '../src/fmsCdu/holds'
import { JOIN_BEFORE_TDN_NM, joiningPath } from '../src/fmsCdu/joining'
import { HELICOPTER_PROFILE } from '../src/fmsCdu/profile'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'

// Phase 1 of the transition down to hover (plan D-T; Astra's review of v1, Q9): an FMS path that reaches TDN aligned
// with the into-wind final. A declared laboratory turn–straight–turn construction (joining.ts) to JN, 0.5 NM before TDN
// on the final course; previewed in MOD, committed on EXEC, flown under NAV. Crew vectoring stays an option (JN deleted).

const START = Date.UTC(2026, 8, 29, 15, 0, 0)
const angleOff = (a: number, b: number) => Math.abs(((a - b + 540) % 360) - 180)

/** The track at the end of a segment, as flown into its end point. */
function endTrack(segment: HoldSegment) {
  if (segment.kind === 'line') return courseDeg(segment.from, segment.to)
  return arcGeometry({ centre: segment.centre, turn: segment.turn }, segment.to, segment.to).track
}

test('the joining path: turns of one radius, continuous, ending on JN on the final track', () => {
  const j: LatLon = { lat: 40.7, lon: -72.45 }
  const r = 0.6
  for (const [track, from] of [[230, offset(j, 230, 2)], [50, offset(j, 230, 2)], [140, offset(j, 180, 1.5)], [320, offset(j, 90, 3)], [230, offset(j, 50, 4)]] as const) {
    const path = joiningPath(from, track, j, 230, r)
    const segments = path.segments
    expect(segments.length).toBeGreaterThan(0)
    // Continuous: each segment starts where the one before ends (the first where the aircraft is).
    let at = from
    for (const s of segments) {
      if (s.kind === 'line') expect(distanceNm(s.from, at)).toBeLessThan(0.005)
      else {
        expect(s.radius).toBe(r)
        expect(Math.abs(distanceNm(s.centre, at) - r)).toBeLessThan(0.005)
        expect(Math.abs(distanceNm(s.centre, s.to) - r)).toBeLessThan(0.005)
      }
      at = s.to
    }
    expect(distanceNm(at, j)).toBeLessThan(1e-9)
    // Arriving on the final track.
    expect(angleOff(endTrack(segments.at(-1)!), 230)).toBeLessThan(1)
    // Never shorter than the straight line to JN (0.01 NM: the local plane against the great circle); the straight
    // segments add up to no more than the stated length.
    expect(path.lengthNm).toBeGreaterThanOrEqual(distanceNm(from, j) - 0.01)
    const sum = segments.reduce((total, s) => total + (s.kind === 'line' ? distanceNm(s.from, s.to) : 0), 0)
    expect(sum).toBeLessThanOrEqual(path.lengthNm + 0.01)
  }
})

test('started over MRK into the wind, the join goes outbound and back: its far point is past JN, downwind', () => {
  const mrk: LatLon = { lat: 40.7, lon: -72.45 }
  const tdn = offset(mrk, 50, 1.5), jn = offset(tdn, 50, JOIN_BEFORE_TDN_NM)
  const path = joiningPath(mrk, 230, jn, 230, 0.6)
  const far = Math.max(...path.segments.map(s => distanceNm(mrk, s.to)))
  expect(far).toBeGreaterThan(distanceNm(mrk, jn))
  expect(path.word === 'LSL' || path.word === 'RSR').toBe(true)
})

/** The 87N start, the sighting at once (MARK ON TOP over the aircraft), ACTIVATE, and EXEC unless told not to. */
function activatedOverMark(initialTrack = 230, execute = true) {
  let now = START
  const fms = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(fms)
  expect(setUp87nOffshoreSar(fms, sim)).toEqual({ ready: true })
  fms.placeAircraft({ position: fms.truePosition, track: initialTrack, altitude: 500 }, 'test: over the sighting')
  const tick = () => { now += 250; sim.step(0.25) }
  for (let i = 0; i < 4; i++) tick()
  for (const key of ['TACT', 'LSK1R', 'LSK4L', 'LSK6R'] as const) fms.press(key)
  if (execute) fms.press('EXEC')
  return { fms, sim, tick }
}

test('ACTIVATE previews the join in MOD (JN before TDN) without touching the active route; EXEC commits it from the state then; CANCEL drops it', () => {
  const { fms, tick } = activatedOverMark(230, false)
  expect(fms.hover.status).toBe('MOD')
  expect(fms.route.legs.slice(0, 4).map(l => (l.kind === 'wpt' ? l.ident : l.kind))).toEqual(['JN', 'TDN', 'MRK', 'disco'])
  expect(fms.activeRoute.legs.some(l => l.kind === 'wpt' && l.ident === 'JN')).toBe(false)
  expect(fms.hoverJoinPreview).not.toBeNull()
  expect(fms.hoverJoin).toBeNull()
  // JN on the final course, 0.5 NM before TDN.
  const jn = fms.coordinates('JN', fms.route)!, tdn = fms.coordinates('TDN', fms.route)!
  expect(distanceNm(jn, tdn)).toBeCloseTo(JOIN_BEFORE_TDN_NM, 6)
  expect(angleOff(courseDeg(jn, tdn), fms.hover.finalTrack!)).toBeLessThan(0.1)
  for (let i = 0; i < 20; i++) tick()
  fms.press('EXEC')
  const join = fms.hoverJoin!
  expect(join).not.toBeNull()
  // Rebuilt from where the aircraft is at EXEC, not where it was at ACTIVATE.
  const first = join.segments[0]
  const startsAt = first.kind === 'line' ? first.from : null
  if (first.kind === 'arc') expect(Math.abs(distanceNm(first.centre, fms.truePosition) - first.radius)).toBeLessThan(0.02)
  else expect(distanceNm(startsAt!, fms.truePosition)).toBeLessThan(0.02)
  // CANCEL of a new MOD drops its preview.
  const other = activatedOverMark(230, false).fms
  other.press('LSK6L')
  expect(other.hover.status).toBe('NONE')
  expect(other.hoverJoinPreview).toBeNull()
})

for (const initialTrack of [230, 50, 140, 320]) {
  test(`activated over MRK on a ${initialTrack} track, the join reaches TDN on the final track within the bank limit, and TD engages (Phase 1)`, () => {
    const { fms, sim, tick } = activatedOverMark(initialTrack)
    expect(fms.hover.status).toBe('ACT')
    const finalTrack = fms.hover.finalTrack!
    const tas = sim.tas, fastest = tas + fms.wind.speed
    const bank = designBank(fastest, HELICOPTER_PROFILE.parameters.afcsBankLimit.value)
    expect(fms.hoverJoin!.radiusNm).toBeCloseTo(radiusAt(fastest, bank), 6)
    let steepest = 0, t = 0
    // Fly the join under NAV to TDN.
    while (fms.hover.request === 0 && !fms.hover.refused && t < 1200) {
      tick()
      t += 0.25
      if (sim.lateralMode === 'LNAV') steepest = Math.max(steepest, Math.abs(sim.bankAngle))
    }
    expect(fms.hover.refused, fms.hover.refusedReason ?? '').toBeNull()
    expect(fms.hover.request).toBeGreaterThan(0)
    // At TDN: on the final course and track (the TDN checks allow 0.2 NM and 20 degrees; the join does far better).
    const jn = fms.coordinates('JN')!, tdn = fms.coordinates('TDN')!
    const g = legGeometry(offset(tdn, finalTrack + 180, 10), tdn, fms.truePosition)
    expect(Math.abs(g.crossTrack)).toBeLessThan(0.05)
    expect(angleOff(fms.track, finalTrack)).toBeLessThan(5)
    expect(distanceNm(jn, tdn)).toBeCloseTo(JOIN_BEFORE_TDN_NM, 6)
    // Within the design bank, plus the cross-track correction's margin (the flown bank is limited at the bank limit).
    expect(steepest).toBeLessThanOrEqual(HELICOPTER_PROFILE.parameters.afcsBankLimit.value + 0.01)
    for (let i = 0; i < 40; i++) tick()
    expect(sim.axisModes.pitch).toBe('TD')
  })
}

test('no waypoint goes between JN and TDN either: !HOVER MRK WPT', () => {
  const { fms } = activatedOverMark(230, false)
  expect(fms.splitsHover(fms.route.legs, 1)).toBe(true)
  expect(fms.splitsHover(fms.route.legs, 2)).toBe(true)
  expect(fms.splitsHover(fms.route.legs, 3)).toBe(false)
})
