import { expect, logicTest as test } from './isolated-client-test'
import { FlightSimulator, legGeometry, racetrackOutline, sarTrack } from '../src/fmsCdu/flight'
import { courseDeg, distanceNm, offset, type LatLon } from '../src/fmsCdu/fmsModel'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import type { CduFunction } from '../src/fmsCdu/variants'

// The flight simulation: the aircraft flies the active route as an FMS-coupled autopilot would. These prove the
// guidance geometry (fly-by, holds with their entries, search patterns, the final approach path), not the pages.
const setup = () => {
  let now = Date.UTC(2026, 8, 27, 14, 0, 0)
  const unit = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(unit)
  const fly = (seconds: number, each?: () => boolean | void) => {
    for (let t = 0; t < seconds; t += 1) {
      now += 1000
      sim.step(1)
      if (each?.()) return t
    }
    return seconds
  }
  return { unit, sim, fly }
}
const press = (unit: ScriptedFms, ...fns: CduFunction[]) => { for (const fn of fns) unit.press(fn) }
const typeText = (unit: ScriptedFms, text: string) => {
  for (const ch of text) unit.press(ch === '.' ? 'DOT' : ch === '/' ? 'SLASH' : `CHAR_${ch}`)
}
const activeIdent = (unit: ScriptedFms) => { const leg = unit.activeRoute.legs[0]; return leg?.kind === 'wpt' ? leg.ident : null }

test('the aircraft flies the demonstration route leg by leg, on track, and reports END OF ROUTE', () => {
  const { unit, sim, fly } = setup()
  const sequenced: string[] = []
  let worst = 0
  let last = activeIdent(unit)
  fly(3 * 3600, () => {
    const now = activeIdent(unit)
    if (now !== last) { if (last) sequenced.push(last); last = now }
    // Once established (after each leg's first two miles), the aircraft stays close to the leg.
    const g = sim.guidance
    if (g.mode === 'LNAV' && g.legFrom && g.legTo && distanceNm(g.legFrom, unit.position) > 2 && (g.distanceToGo ?? 0) > 2)
      worst = Math.max(worst, Math.abs(g.crossTrack))
    return now === null
  })
  expect(sequenced).toEqual(['MUN', 'RDG', 'TOLGU', 'FERDI', 'RW24R', 'CYUL'])
  expect(worst).toBeLessThan(0.3)
  expect(unit.recallList.some(message => message.text === 'END OF ROUTE')).toBe(true)
})

test('a fly-by waypoint is sequenced before the aircraft reaches it, by the turn anticipation distance', () => {
  const { unit, fly } = setup()
  const tolgu = unit.coordinates('TOLGU')!
  const ferdi = unit.coordinates('FERDI')!
  let atSequence = -1
  fly(3600, () => {
    if (activeIdent(unit) === 'FERDI' && atSequence < 0) { atSequence = distanceNm(unit.position, tolgu); return true }
  })
  // TOLGU to FERDI turns little, so the lead is small but real; the aircraft never overflies it.
  expect(atSequence).toBeGreaterThan(0.005)
  expect(atSequence).toBeLessThan(0.5)
  // After the turn the aircraft is established on the FERDI leg.
  fly(300)
  expect(Math.abs(legGeometry(tolgu, ferdi, unit.position).crossTrack)).toBeLessThan(0.3)
})

test('the aircraft climbs to each leg constraint and descends on the VNAV path from the FAF to the runway', () => {
  const { unit, fly } = setup()
  fly(3600, () => activeIdent(unit) === 'TOLGU')
  expect(unit.altitude).toBeGreaterThan(4400)
  // The demonstration route turns 148 degrees at the FAF, so the fly-by turn rolls out on final short of it and the
  // aircraft catches the path from above. What matters is that it is on the path well before the threshold.
  const runway = unit.coordinates('RW24R')!, faf = unit.coordinates('FERDI')!
  const vpa = Math.atan((1500 - 118) / (distanceNm(faf, runway) * 6076.12))
  let threshold = 0, worstLate = 0
  const lateVs: number[] = []
  fly(3600, () => {
    const toGo = distanceNm(unit.position, runway)
    if (activeIdent(unit) === 'RW24R' && toGo < 1.5 && toGo > 0.3) {
      worstLate = Math.max(worstLate, Math.abs(unit.altitude - (118 + toGo * 6076.12 * Math.tan(vpa))))
      lateVs.push(unit.verticalSpeed)
    }
    if (activeIdent(unit) === 'CYUL' && !threshold) { threshold = unit.altitude; return true }
  })
  // On the three-degree path over the last mile and a half, descending at about 630 fpm, near threshold height.
  expect(worstLate).toBeLessThan(150)
  expect(Math.min(...lateVs)).toBeGreaterThan(-900)
  expect(Math.max(...lateVs)).toBeLessThan(-450)
  expect(threshold).toBeGreaterThan(80)
  expect(threshold).toBeLessThan(400)
})

test('VNAV holds cruise until the top of descent, then descends on the planned path to the FAF', () => {
  const { unit, fly } = setup()
  const profile = unit.profile()
  expect(profile.endOfDescent).toBe('RW24R')
  // Descending 3000 ft at three degrees takes about 9.4 NM: the T/D is that far before the FAF.
  const ferdi = profile.points.find(p => p.ident === 'FERDI')!
  expect(ferdi.altitude).toBe(1500)
  expect(ferdi.distance - profile.topOfDescent!).toBeCloseTo(9.4, 0)
  let leftCruiseAt = -1
  const alongAtLeaving = { toFerdi: 0 }
  fly(3 * 3600, () => {
    if (activeIdent(unit) === 'FERDI' && unit.altitude < 4480 && leftCruiseAt < 0) {
      leftCruiseAt = 1
      alongAtLeaving.toFerdi = distanceNm(unit.position, unit.coordinates('FERDI')!)
    }
    return activeIdent(unit) === 'RW24R'
  })
  expect(alongAtLeaving.toFerdi).toBeGreaterThan(8.5)
  expect(alongAtLeaving.toFerdi).toBeLessThan(10.5)
})

/** Signed distance from the inbound course line through the fix, positive right of the inbound course. */
const sideOfInbound = (fix: LatLon, inbound: number, at: LatLon) => legGeometry(offset(fix, inbound + 180, 10), fix, at).crossTrack

const holdAtRdg = (unit: ScriptedFms, ...changes: [string, CduFunction][]) => {
  press(unit, 'HOLD', 'LSK2L')
  for (const [text, lsk] of changes) { if (text) typeText(unit, text); unit.press(lsk) }
  unit.press('EXEC')
}

test('a direct-entry hold is flown as a racetrack on the holding side, circuit after circuit', () => {
  const { unit, fly } = setup()
  holdAtRdg(unit)
  const rdg = unit.coordinates('RDG')!
  fly(3600, () => unit.activeRoute.hold?.status === 'IN PROGRESS')
  expect(unit.holdEntryFlown).toBe('DIRECT')
  const inbound = unit.activeRoute.hold!.inbound
  let furthest = 0, wrongSide = 0
  fly(15 * 60, () => {
    furthest = Math.max(furthest, distanceNm(rdg, unit.position))
    wrongSide = Math.min(wrongSide, sideOfInbound(rdg, inbound, unit.position))
  })
  expect(unit.activeRoute.hold?.status).toBe('IN PROGRESS')
  expect(activeIdent(unit)).toBe('RDG')
  // One-minute legs at 120 kt: the racetrack stays within about three miles of the fix, right of the inbound course.
  expect(furthest).toBeLessThan(3.5)
  expect(furthest).toBeGreaterThan(1.5)
  expect(wrongSide).toBeGreaterThan(-0.4)
})

test('a parallel entry flies outbound on the non-holding side first', () => {
  const { unit, fly } = setup()
  // Inbound 263 with left turns makes the arrival from the east a parallel entry (see the entry sectors test).
  holdAtRdg(unit, ['263', 'LSK3L'], ['', 'LSK2L'])
  const rdg = unit.coordinates('RDG')!
  fly(3600, () => unit.activeRoute.hold?.status === 'IN PROGRESS')
  expect(unit.holdEntryFlown).toBe('PARALLEL')
  // Left turns put the holding side on the left (negative). Outbound, a parallel entry stays off the holding side;
  // then it turns back through the holding side, more than 180 degrees, to intercept the inbound course.
  let outboundMin = 0, returnMin = 0
  fly(60, () => { outboundMin = Math.min(outboundMin, sideOfInbound(rdg, 263, unit.position)) })
  fly(180, () => { returnMin = Math.min(returnMin, sideOfInbound(rdg, 263, unit.position)) })
  expect(outboundMin).toBeGreaterThan(-0.1)
  expect(returnMin).toBeLessThan(-0.3)
})

test('a teardrop entry goes outbound into the holding side', () => {
  const { unit, fly } = setup()
  holdAtRdg(unit, ['263', 'LSK3L'])
  const rdg = unit.coordinates('RDG')!
  fly(3600, () => unit.activeRoute.hold?.status === 'IN PROGRESS')
  expect(unit.holdEntryFlown).toBe('TEARDROP')
  let right = 0, left = 0
  fly(100, () => {
    const side = sideOfInbound(rdg, 263, unit.position)
    right = Math.max(right, side)
    left = Math.min(left, side)
  })
  // Right turns: the holding side is right of the inbound course, and the teardrop stays there.
  expect(right).toBeGreaterThan(0.3)
  expect(left).toBeGreaterThan(-0.3)
})

test('EXIT HOLD, once executed, leaves the hold at the next fix crossing and continues the route', () => {
  const { unit, fly } = setup()
  holdAtRdg(unit)
  fly(3600, () => unit.activeRoute.hold?.status === 'IN PROGRESS')
  fly(60)
  press(unit, 'HOLD', 'LSK5R', 'EXEC')
  expect(unit.activeRoute.hold?.status).toBe('EXIT ARMED')
  const seconds = fly(900, () => activeIdent(unit) === 'TOLGU')
  expect(seconds).toBeLessThan(900)
  expect(unit.activeRoute.hold).toBeUndefined()
})

test('a search pattern is flown along its geometry from the start point, then the route continues', () => {
  const { unit, sim, fly } = setup()
  press(unit, 'TACT', 'LSK2L', 'LSK6R', 'EXEC')
  expect(unit.sar.active).toBe('SQUARE')
  fly(600, () => unit.sar.status === 'IN PROGRESS')
  const path = sim.sarPath!
  expect(path).toHaveLength(13)
  let worst = 0
  const seconds = fly(4 * 3600, () => {
    const nearest = Math.min(...path.slice(0, -1).map((p, i) => Math.abs(legGeometry(p, path[i + 1], unit.position).crossTrack)))
    worst = Math.max(worst, nearest)
    return unit.sar.active === null
  })
  expect(seconds).toBeLessThan(4 * 3600)
  expect(worst).toBeLessThan(0.6)
  expect(activeIdent(unit)).toBe('MUN')
})

test('search pattern geometry: the expanding square grows by one spacing every two legs; the sector closes on its datum', () => {
  const start = { lat: 45, lon: -75 }
  const unit = new ScriptedFms()
  const square = sarTrack(start, unit.sar, 'SQUARE')
  expect(distanceNm(square[0], square[1])).toBeCloseTo(2, 1)
  expect(distanceNm(square[2], square[3])).toBeCloseTo(4, 1)
  expect(distanceNm(square[11], square[12])).toBeCloseTo(12, 1)
  // First leg on the search bearing, then right turns.
  expect(courseDeg(square[0], square[1])).toBe(90)
  expect(courseDeg(square[1], square[2])).toBe(180)
  expect(courseDeg(square[2], square[3])).toBe(270)
  const sector = sarTrack(start, unit.sar, 'SECTOR')
  expect(sector).toHaveLength(10)
  for (const i of [3, 6, 9]) expect(distanceNm(sector[i], start)).toBeLessThan(0.05)
  const ladder = sarTrack(start, unit.sar, 'LADDER')
  expect(ladder).toHaveLength(16)
})

test('the racetrack outline starts and ends at the fix and reaches one leg length outbound', () => {
  const fix = { lat: 45, lon: -75 }
  const hold = { fix: 'X', turn: 'RIGHT' as const, inbound: 360, legTime: 1, legDistance: null, exit: 'MANUAL' as const, speed: 220, altitude: '5000', status: 'ARMED' as const }
  const outline = racetrackOutline(fix, hold, 120, 120)
  expect(outline[0]).toEqual(fix)
  expect(outline.at(-1)).toEqual(fix)
  // With right turns and inbound north, the pattern lies east of the fix and south of it.
  expect(Math.max(...outline.map(p => p.lon))).toBeGreaterThan(fix.lon)
  expect(Math.min(...outline.map(p => p.lat))).toBeLessThan(fix.lat - 1.5 / 60)
})

test('a direct-to starts the active leg at present position', () => {
  const { unit, fly } = setup()
  fly(120)
  const here = unit.position
  press(unit, 'LEGS')
  typeText(unit, 'TOLGU')
  press(unit, 'LSK1L', 'EXEC')
  expect(unit.activeLegStart).toEqual(here)
  fly(3600, () => activeIdent(unit) === 'FERDI')
  expect(activeIdent(unit)).toBe('FERDI')
})
