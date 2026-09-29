import { expect, logicTest as test } from './isolated-client-test'
import { FlightSimulator, angleDiff, legGeometry, racetrackOutline, sarTrack } from '../src/fmsCdu/flight'
import { courseDeg, distanceNm, offset, type LatLon } from '../src/fmsCdu/fmsModel'
import { groundVelocity, holdTrack, predictedGroundSpeed } from '../src/fmsCdu/kinematics'
import { LAB_AIRLINE_VNAV_PROFILE, type AircraftProfile } from '../src/fmsCdu/profile'
import { stimulusFor, type GpsOp } from '../src/fmsCdu/gpsStimulus'
import { checkAtTdn, planTransition } from '../src/fmsCdu/transition'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { screenText } from '../src/fmsCdu/screen'
import type { CduFunction } from '../src/fmsCdu/variants'

// The flight simulation: the aircraft flies the active route as an FMS-coupled autopilot would. These prove the
// guidance geometry (fly-by, holds with their entries, search patterns, the final approach path), not the pages.
const setup = (profile?: AircraftProfile) => {
  let now = Date.UTC(2026, 8, 27, 14, 0, 0)
  const unit = new ScriptedFms(() => new Date(now), { profile })
  const sim = new FlightSimulator(unit)
  const fly = (seconds: number, each?: () => boolean | void) => {
    for (let t = 0; t < seconds; t += 1) {
      now += 1000
      sim.step(1)
      if (each?.()) return t
    }
    return seconds
  }
  /** In the bench 0.25 s ticks, for what depends on the tick (a receiver change within one tick). */
  const ticks = (seconds: number, each?: () => boolean | void) => {
    for (let t = 0; t < seconds * 4; t += 1) {
      now += 250
      sim.step(0.25)
      if (each?.()) return
    }
  }
  return { unit, sim, fly, ticks }
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
  expect(sequenced).toEqual(['MUN', 'RDG', 'TOLGU', 'DEMEL', 'ALNIT', 'ULIDA', 'FERDI', 'RW24R', 'CYUL'])
  expect(worst).toBeLessThan(0.3)
  expect(unit.recallList.some(message => message.text === 'END OF ROUTE')).toBe(true)
})

test('a fly-by waypoint is sequenced before the aircraft reaches it, by the turn anticipation distance', () => {
  const { unit, fly } = setup()
  const tolgu = unit.coordinates('TOLGU')!
  const demel = unit.coordinates('DEMEL')!
  let atSequence = -1
  fly(3600, () => {
    if (activeIdent(unit) === 'DEMEL' && atSequence < 0) { atSequence = distanceNm(unit.position, tolgu); return true }
  })
  // RDG-TOLGU (077) to TOLGU-DEMEL (086) turns little, so the lead is small but real; the aircraft never overflies it.
  expect(atSequence).toBeGreaterThan(0.005)
  expect(atSequence).toBeLessThan(0.5)
  // After the turn the aircraft is established on the DEMEL leg.
  fly(300)
  expect(Math.abs(legGeometry(tolgu, demel, unit.position).crossTrack)).toBeLessThan(0.3)
})

test('the aircraft climbs to each leg constraint and descends on the VNAV path from the FAF to the runway', () => {
  const { unit, fly } = setup(LAB_AIRLINE_VNAV_PROFILE)
  // Beyond the FAF only a captured approach descends (review finding R03): the approach is loaded and armed. The
  // runway alone, reached without an approach procedure, no longer brings the aircraft down.
  unit.selectProcedure('APPROACH', 'R24R')
  unit.press('EXEC')
  unit.armApproach(true)
  fly(3600, () => activeIdent(unit) === 'TOLGU')
  expect(unit.altitude).toBeGreaterThan(4400)
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
    // The threshold is where the runway is sequenced; the missed approach follows it in this route.
    if (activeIdent(unit) !== 'RW24R' && lateVs.length && !threshold) { threshold = unit.altitude; return true }
  })
  // On the three-degree path over the last mile and a half, descending at about 580 fpm, near threshold height. The
  // straight-in final from ULIDA puts the aircraft at the FAF on the path, so it tracks it within 50 ft (150 ft was the
  // allowance when the route turned 148 degrees at the FAF and caught the path from above).
  expect(worstLate).toBeLessThan(50)
  expect(Math.min(...lateVs)).toBeGreaterThan(-900)
  expect(Math.max(...lateVs)).toBeLessThan(-450)
  expect(threshold).toBeGreaterThan(80)
  expect(threshold).toBeLessThan(400)
})

test('VNAV holds cruise until the top of descent, then descends on the planned path to the first descent constraint', () => {
  const { unit, fly } = setup(LAB_AIRLINE_VNAV_PROFILE)
  const profile = unit.profile()
  expect(profile.endOfDescent).toBe('RW24R')
  // The first constraint below cruise is DEMEL at 3000, joining the downwind. Descending 1500 ft at three degrees
  // (318.4 ft/NM) takes 4.71 NM: the T/D is that far before DEMEL.
  const demel = profile.points.find(p => p.ident === 'DEMEL')!
  expect(demel.altitude).toBe(3000)
  expect(demel.distance - profile.topOfDescent!).toBeCloseTo(4.71, 1)
  let leftCruiseAt = -1
  const alongAtLeaving = { toDemel: 0 }
  fly(3 * 3600, () => {
    if (activeIdent(unit) === 'DEMEL' && unit.altitude < 4480 && leftCruiseAt < 0) {
      leftCruiseAt = 1
      alongAtLeaving.toDemel = distanceNm(unit.position, unit.coordinates('DEMEL')!)
    }
    return activeIdent(unit) === 'RW24R'
  })
  expect(alongAtLeaving.toDemel).toBeGreaterThan(3.8)
  expect(alongAtLeaving.toDemel).toBeLessThan(5.8)
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

// Stage B1 of the helicopter-first plan: the aircraft flies a heading through the air, the wind carries the air mass,
// and the ground velocity is their vector sum. Expected values are worked by hand, independently of kinematics.ts.
test('the wind triangle gives the crab angle and ground speed, and refuses a track the airspeed cannot hold', () => {
  // TAS 100 kt, a pure 30 kt crosswind from the right: crab asin(0.3) = 17.458 deg into it, GS 100 cos(17.458) = 95.394 kt.
  const crosswind = holdTrack(100, 360, { direction: 90, speed: 30 })
  expect(crosswind).toMatchObject({ feasible: true })
  if (!crosswind.feasible) throw new Error('feasible')
  expect(crosswind.windCorrection).toBeCloseTo(17.458, 3)
  expect(crosswind.heading).toBeCloseTo(17.458, 3)
  expect(crosswind.groundSpeed).toBeCloseTo(95.394, 3)
  // 5 NM in 5 minutes (60 kt over the ground) with a pure 30 kt crosswind needs sqrt(60^2 + 30^2) = 67.082 kt TAS.
  const rta = holdTrack(Math.hypot(60, 30), 360, { direction: 270, speed: 30 })
  expect(rta.feasible && rta.groundSpeed).toBeCloseTo(60, 6)
  // A crosswind stronger than the airspeed, or a headwind that stops progress: infeasible, never a floor.
  expect(holdTrack(20, 360, { direction: 90, speed: 30 })).toMatchObject({ feasible: false })
  expect(holdTrack(20, 360, { direction: 360, speed: 25 })).toMatchObject({ feasible: false })
  expect(predictedGroundSpeed(20, 360, { direction: 360, speed: 25 })).toBeNull()
  // Flying 20 kt into a 20 kt wind holds the ground position: no ground speed and no track.
  expect(groundVelocity(20, 230, { direction: 230, speed: 20 })).toMatchObject({ speed: expect.closeTo(0, 9), track: null })
})

test('in a crosswind the aircraft crabs: its heading differs from its track by the wind correction, and LNAV holds the track', () => {
  const { unit, sim, fly } = setup()
  // The demonstration route's first leg runs about 115 degrees; a 30 kt wind from the north-east is a crosswind on it.
  unit.wind.direction = 25
  unit.wind.speed = 30
  fly(240)
  const leg = legGeometry(unit.activeLegStart, unit.coordinates(activeIdent(unit)!)!, unit.truePosition)
  expect(Math.abs(leg.crossTrack)).toBeLessThan(0.1)
  const expected = holdTrack(sim.tas, unit.track, unit.wind)
  if (!expected.feasible) throw new Error('feasible')
  expect(Math.abs(expected.windCorrection)).toBeGreaterThan(10)
  expect(unit.heading).toBeCloseTo(expected.heading, 0)
  expect(unit.groundSpeed).toBeCloseTo(expected.groundSpeed, 0)
})

test('the airspeed changes at the profile acceleration limit, not in one step', () => {
  const { unit, sim, fly } = setup()
  fly(30)
  // The crew selects indicated airspeed: 120 KIAS is about 125.5 kt true at 3,000 ft (ISA).
  expect(sim.indicatedAirspeed).toBeCloseTo(120, 6)
  const from = sim.tas
  sim.selectSpeed(80)
  fly(10)
  // 2 kt/s: ten seconds take 20 kt off, not the whole difference.
  expect(sim.tas).toBeCloseTo(from - 20, 6)
  fly(15)
  expect(sim.indicatedAirspeed).toBeCloseTo(80, 1)
  expect(unit.altitude).toBeCloseTo(3000, -1)
})

// Stage B3b: the rotorcraft autopilot's hover and low-speed modes, over the declared sea south of Southampton (87N),
// in a steady 230/20 wind (a laboratory test condition). Tolerances are the profile's (plan section 3a).
const offshore = (height = 100) => {
  const run = setup()
  const { unit, sim } = run
  unit.declareSurface('offshore-87n')
  unit.wind.direction = 230
  unit.wind.speed = 20
  unit.placeAircraft({ position: { lat: 40.7, lon: -72.45 }, track: 230, altitude: height }, 'test: offshore south of 87N')
  sim.engageAltitudeHold()
  sim.selectHeading(230)
  return run
}
const metres = (a: LatLon, b: LatLon) => distanceNm(a, b) * 1852
const slowToHover = (run: ReturnType<typeof offshore>) => {
  run.sim.selectSpeed(25)
  run.fly(120, () => run.sim.tas < 26)
  expect(run.sim.engageHover()).toBe(true)
}

test('HOV holds position in a 20 kt wind: ground speed about zero, airspeed about the wind, heading into it (B3b)', () => {
  const run = offshore()
  const { unit, sim, fly } = run
  slowToHover(run)
  fly(20)
  const anchor = unit.truePosition
  let worst = 0, worstHeight = 0
  fly(120, () => { worst = Math.max(worst, metres(anchor, unit.truePosition)); worstHeight = Math.max(worstHeight, Math.abs(unit.radioHeight.value! - 100)) })
  expect(worst).toBeLessThan(10)
  expect(worstHeight).toBeLessThan(5)
  expect(unit.groundSpeed).toBeLessThan(1)
  expect(sim.tas).toBeCloseTo(20, 0)
  expect(Math.abs(angleDiff(230, unit.heading))).toBeLessThan(2)
  expect(sim.axisModes).toEqual({ collective: 'RHT', pitch: 'HOV', roll: 'HOV' })
})

test('in HOV a heading selection yaws the aircraft through 360 degrees at the yaw rate while the position holds (B3b)', () => {
  const run = offshore()
  const { unit, sim, fly } = run
  slowToHover(run)
  fly(20)
  const anchor = unit.truePosition
  let worst = 0
  for (const heading of [350, 110, 230]) {
    sim.selectHeading(heading)
    fly(12, () => { worst = Math.max(worst, metres(anchor, unit.truePosition)) })
  }
  fly(20, () => { worst = Math.max(worst, metres(anchor, unit.truePosition)) })
  expect(Math.abs(angleDiff(230, unit.heading))).toBeLessThan(2)
  expect(worst).toBeLessThan(10)
})

test('TD/H from its window decelerates to a stop and descends to the hover height, then RHT and HOV hold there (B3b)', () => {
  const run = offshore(150)
  const { unit, sim, fly } = run
  sim.selectSpeed(60)
  fly(40)
  expect(sim.selectHoverHeight(50)).toBe(true)
  // Outside the window it is refused: above 210 ft, or at 85 kt or more.
  expect(sim.engageTransitionDownToHover()).toBe(true)
  fly(150, () => sim.axisModes.pitch === 'HOV' && sim.axisModes.collective === 'RHT')
  expect(sim.axisModes).toEqual({ collective: 'RHT', pitch: 'HOV', roll: 'HOV' })
  fly(30)
  expect(Math.abs(unit.radioHeight.value! - 50)).toBeLessThan(5)
  expect(unit.groundSpeed).toBeLessThan(1)
})

test('the TD/H window: refused above 210 ft RA and at 85 kt, and it never climbs to a hover height above it (B3b)', () => {
  const high = offshore(300)
  expect(high.sim.engageTransitionDownToHover()).toBe(false)
  const fast = offshore(150)
  fast.sim.selectSpeed(100)
  fast.fly(20)
  expect(fast.sim.engageTransitionDownToHover()).toBe(false)
})

test('TU from an exact hover releases the station lock; its axes complete on their own through 40 kt to 80 kt and 200 ft (B3b)', () => {
  const run = offshore(50)
  const { unit, sim, fly } = run
  slowToHover(run)
  fly(40)
  expect(unit.groundSpeed).toBeLessThan(1)
  expect(sim.engageTransitionUp()).toBe(true)
  let rht: number | null = null, coordinated: number | null = null
  fly(120, () => {
    if (rht === null && sim.axisModes.collective === 'RHT') rht = unit.radioHeight.value
    if (coordinated === null && !sim.inLowSpeedRegime) coordinated = sim.tas
    return sim.tas >= 79.9 && sim.axisModes.collective === 'RHT'
  })
  expect(rht).not.toBeNull()
  expect(Math.abs(rht! - 200)).toBeLessThan(25)
  // Heading hold takes the roll axis only in coordinated flight, from 45 kt (the hysteresis), not at 40.
  expect(coordinated!).toBeGreaterThanOrEqual(45)
  expect(sim.tas).toBeGreaterThan(79)
  expect(sim.axisModes.roll).toBe('HDG')
})

test('radio height lost in the hover: RHT gives way to ALT HOLD on the barometric altitude, HOV holds, LOW HT OFF (B3b, F3)', () => {
  const run = offshore()
  const { unit, sim, fly } = run
  slowToHover(run)
  fly(20)
  const altitude = unit.altitude
  unit.setCondition('raFail', true)
  fly(5)
  expect(sim.axisModes).toEqual({ collective: 'ALT', pitch: 'HOV', roll: 'HOV' })
  expect(sim.altitudeHoldReference).toBe(Math.round(altitude))
  expect(sim.lowHeightCaption).toBe('LOW HT OFF')
  expect(sim.modeEvents.at(-1)).toMatchObject({ event: 'RA LOST' })
})

test('hover feedback lost: HOV gives way to ATT on the last command; with the wind unchanged it stays, and a wind change drifts it (B3b, F5)', () => {
  const run = offshore()
  const { unit, sim, fly } = run
  slowToHover(run)
  fly(20)
  unit.setCondition('gpsLost', true)
  fly(3)
  expect(sim.axisModes.pitch).toBe('ATT')
  expect(sim.modeEvents.some(e => e.event === 'HOV LOST')).toBe(true)
  const before = unit.truePosition
  unit.wind.speed = 25
  fly(30)
  // The controller has no feedback, so nothing brings it back: the aircraft drifts with the extra 5 kt, about 77 m.
  expect(metres(before, unit.truePosition)).toBeGreaterThan(40)
})

// B3c: the arbitration and feedback cases of plan R3-02 and Astra's rev 3.1 clarifications, as small state-table tests.
const gps = (unit: ScriptedFms, receiver: 1 | 2, op: GpsOp) => expect(stimulusFor(unit).apply(receiver - 1, op)).toBe(true)
const hovering = () => {
  const run = offshore()
  slowToHover(run)
  run.fly(20)
  expect(run.sim.axisModes.pitch).toBe('HOV')
  return run
}

test('integrity lost while the position and velocity words still read NORMAL: not hover feedback, HOV gives way to ATT (B3c)', () => {
  const { unit, sim, ticks } = hovering()
  unit.setCondition('gpsIntegrity', true)
  ticks(3)
  expect(unit.hoverFeedback).toBeNull()
  expect(sim.axisModes.pitch).toBe('ATT')
})

test('velocity words invalid alone (166 NCD on both receivers): not hover feedback, HOV gives way to ATT (B3c)', () => {
  const { unit, sim, ticks } = hovering()
  for (const receiver of [1, 2] as const) gps(unit, receiver, { op: 'override', label: '166', kind: 'FORCE', amount: 0, ssm: 'NCD' })
  ticks(2)
  expect(unit.hoverFeedback).toBeNull()
  expect(sim.axisModes.pitch).toBe('ATT')
})

test('a continuous receiver takeover keeps HOV and the earth-fixed target; recovery does not switch back (B3c, #1251)', () => {
  const { unit, sim, ticks } = hovering()
  const anchor = unit.truePosition
  expect(unit.hoverFeedback?.source).toBe(1)
  gps(unit, 1, { op: 'fault', fault: 'RECEIVER', on: true })
  ticks(30)
  expect(unit.hoverFeedback?.source).toBe(2)
  expect(sim.axisModes.pitch).toBe('HOV')
  expect(metres(anchor, unit.truePosition)).toBeLessThan(10)
  gps(unit, 1, { op: 'fault', fault: 'RECEIVER', on: false })
  ticks(60)
  expect(unit.hoverFeedback?.source).toBe(2)
  expect(sim.axisModes.pitch).toBe('HOV')
})

test('a takeover onto a receiver 100 m off is not continuous: HOV gives way to ATT with the reason, no correction flown (B3c)', () => {
  const { unit, sim, ticks } = hovering()
  gps(unit, 2, { op: 'spoof', northM: 100, driftEastMps: 0 })
  ticks(2)
  gps(unit, 1, { op: 'fault', fault: 'RECEIVER', on: true })
  const anchor = unit.truePosition
  ticks(10)
  expect(sim.axisModes.pitch).toBe('ATT')
  expect(sim.modeEvents.find(e => e.event === 'HOV LOST')?.detail).toMatch(/GPS2 position \d+\.\d m from the last sample/)
  // It does not fly 100 m to null the new receiver's error.
  expect(metres(anchor, unit.truePosition)).toBeLessThan(10)
})

test('the hover steers on the measured velocity, not the true wind: a velocity bias moves the aircraft (B3c)', () => {
  const plain = hovering(), biased = hovering()
  for (const receiver of [1, 2] as const) gps(biased.unit, receiver, { op: 'override', label: '174', kind: 'BIAS', amount: 5 })
  plain.fly(60)
  biased.fly(60)
  expect(metres(plain.unit.truePosition, biased.unit.truePosition)).toBeGreaterThan(20)
})

test('FMS failure during TD/H drops the FMS target and hovers where it stops; either order with GPS loss ends the same (B3c, F9)', () => {
  const outcomes: string[] = []
  for (const order of ['fms-then-gps', 'gps-then-fms'] as const) {
    const { unit, sim, fly } = offshore(150)
    sim.selectSpeed(60)
    fly(40)
    const target = offset(unit.truePosition, unit.track, 0.35)
    expect(sim.engageTransitionDownToHover(target)).toBe(true)
    fly(5)
    if (order === 'fms-then-gps') { unit.setCondition('fmsFail', true); fly(2); unit.setCondition('gpsLost', true) }
    else { unit.setCondition('gpsLost', true); fly(2); unit.setCondition('fmsFail', true) }
    fly(10)
    outcomes.push(sim.axisModes.pitch)
  }
  expect(outcomes).toEqual(['ATT', 'ATT'])
  // With the FMS failed alone the stop is at the nominal rate, not at the target.
  // A target well beyond the stopping distance: the failure comes during the gate segment, so the nominal stop falls
  // far short of it.
  const run = offshore(150)
  run.sim.selectSpeed(60)
  run.fly(40)
  const target = offset(run.unit.truePosition, run.unit.track, 1.0)
  run.sim.engageTransitionDownToHover(target)
  run.fly(5)
  run.unit.setCondition('fmsFail', true)
  run.fly(120, () => run.sim.axisModes.pitch === 'HOV')
  expect(run.sim.modeEvents.find(e => e.event === 'HOV')?.detail).toBe('holding where it stopped')
  expect(metres(run.unit.truePosition, target)).toBeGreaterThan(50)
})

test('TD/H toward a target stops at it, correcting the cross-track, and holds it (B3c)', () => {
  const { unit, sim, fly } = offshore(150)
  sim.selectSpeed(60)
  fly(40)
  const target = offset(offset(unit.truePosition, unit.track, 0.5), unit.track + 90, 0.02)
  expect(sim.engageTransitionDownToHover(target)).toBe(true)
  fly(150, () => sim.axisModes.pitch === 'HOV')
  expect(sim.modeEvents.filter(e => e.event === 'HOV').at(-1)?.detail).toBe('holding the target')
  fly(30)
  expect(metres(unit.truePosition, target)).toBeLessThan(10)
})

test('a heading selection cancels a TD/H plan: HOV where the aircraft is (B3c, R3-02)', () => {
  const run = offshore(150)
  run.sim.selectSpeed(60)
  run.fly(40)
  run.sim.engageTransitionDownToHover(offset(run.unit.truePosition, run.unit.track, 0.5))
  run.fly(5)
  run.sim.selectHeading(200)
  expect(run.sim.axisModes.pitch).toBe('HOV')
  expect(run.sim.modeEvents.at(-1)?.event).toBe('TD/H CANCELLED')
})

test('TU above the gate height holds the height it has: it never descends (B3c)', () => {
  const run = offshore(250)
  slowToHover(run)
  run.fly(20)
  expect(run.sim.engageTransitionUp()).toBe(true)
  let lowest = Infinity
  run.fly(60, () => { lowest = Math.min(lowest, run.unit.radioHeight.value!) })
  expect(lowest).toBeGreaterThan(240)
})

test('GA from the hover needs no FMS and no missed approach; without feedback the lateral axis is ATT and the climb goes on (B3c)', () => {
  for (const feedback of [true, false]) {
    const { unit, sim, fly } = hovering()
    if (!feedback) { unit.setCondition('gpsLost', true); fly(2) }
    unit.setCondition('fmsFail', true)
    expect(sim.engageGoAround()).toBe(true)
    fly(20)
    expect(sim.axisModes.collective).toBe('GA')
    expect(sim.axisModes.roll).toBe(feedback ? 'LVL' : 'ATT')
    expect(unit.verticalSpeed).toBeGreaterThan(600)
  }
})

// Stage D: the CMA transition down to hover (M300 11-18…11-22, A-74…A-76, E-16, E-17, E-27; plan D-T), over the declared
// sea south of 87N in a steady 230/20 wind. The FMS places TDN from the shared trajectory (transition.ts), checks the
// state at TDN, and requests the transition; the autopilot flies TD, the gate segment and TD/H to MRK.
const hoverProcedure = (options: { height?: number; markNm?: number } = {}) => {
  const run = offshore(options.height ?? 500)
  const { unit, sim, fly } = run
  sim.selectSpeed(100)
  sim.armLnav()
  fly(40)
  const mark = offset(unit.truePosition, 230, options.markNm ?? 4)
  unit.open('HOVER')
  expect(unit.designateHoverMark({ ident: 'WPT', position: mark, label: null })).toBe(true)
  return { ...run, mark }
}
const hoverText = (unit: ScriptedFms) => screenText(unit.screen()).join('\n')

test('the HOVER page activates the transition: TDN and MRK fly-over at the head of the route, then EXEC and TRANSITION DOWN (Stage D)', () => {
  const { unit, mark } = hoverProcedure()
  expect(hoverText(unit)).toMatch(/ACTIVATE>/)
  unit.press('LSK6R')
  expect(unit.hover.status).toBe('MOD')
  // Into the 230 wind: the final track is 230, TDN on its reciprocal, the planned distance before MRK.
  expect(unit.hover.finalTrack).toBe(230)
  expect(unit.hover.dtra!).toBeGreaterThan(1.2)
  expect(unit.hover.dtra!).toBeLessThan(2.0)
  expect(unit.route.legs.slice(0, 3).map(leg => (leg.kind === 'wpt' ? `${leg.ident}${leg.qualifier ?? ''}` : leg.kind))).toEqual(['TDN/O', 'MRK/O', 'disco'])
  expect(distanceNm(unit.coordinates('TDN', unit.route)!, mark)).toBeCloseTo(unit.hover.dtra!, 6)
  expect(hoverText(unit)).toMatch(/^.*MOD.*HOVER/m)
  unit.press('EXEC')
  expect(unit.hover.status).toBe('ACT')
  expect(unit.recallList[0].text).toBe('TRANSITION DOWN')
})

test('the transition is flown to a hover at MRK: TD, the gate segment, TD/H, then RHT and HOV captured at the target (Stage D)', () => {
  const { unit, sim, fly, mark } = hoverProcedure()
  unit.press('LSK6R')
  unit.press('EXEC')
  fly(600, () => sim.hoverCaptured)
  expect(sim.hoverCaptured).toBe(true)
  const events = sim.modeEvents.map(e => e.event)
  expect(events).toEqual(expect.arrayContaining(['TD', 'TD/H', 'HOV']))
  expect(metres(unit.truePosition, mark)).toBeLessThan(50)
  fly(30)
  expect(metres(unit.truePosition, mark)).toBeLessThan(10)
  expect(Math.abs(unit.radioHeight.value! - 50)).toBeLessThan(5)
  expect(sim.axisModes).toEqual({ collective: 'RHT', pitch: 'HOV', roll: 'HOV' })
})

test('ACTIVATE needs a valid radio height; losing it between ACTIVATE and EXEC is RALT FAILED, and the modification stays (Stage D, E-27)', () => {
  const noRa = hoverProcedure()
  noRa.unit.setCondition('raFail', true)
  expect(hoverText(noRa.unit)).not.toMatch(/ACTIVATE>/)
  const { unit } = hoverProcedure()
  unit.press('LSK6R')
  unit.setCondition('raFail', true)
  unit.press('EXEC')
  expect(unit.recallList[0].text).toBe('RALT FAILED')
  expect(unit.hover.status).toBe('MOD')
  expect(unit.routeStatus).toBe('MOD')
})

test('at TDN, 0.3 NM off the final track: TDN NOT POSSIBLE, roll steering withdrawn, NAV gives way to HDG (Stage D, E-17)', () => {
  const { unit, sim, fly } = hoverProcedure()
  unit.press('LSK6R')
  unit.press('EXEC')
  // Just before TDN, displaced 0.3 NM across the final track.
  fly(600, () => distanceNm(unit.truePosition, unit.coordinates('TDN')!) < 0.25)
  unit.placeAircraft({ position: offset(unit.truePosition, 320, 0.3), track: 230, altitude: unit.altitude }, 'test: off the final track at TDN')
  fly(30, () => unit.hover.refused !== null)
  expect(unit.hover.refused).toBe('TDN NOT POSSIBLE')
  fly(2)
  expect(sim.lateralMode).toBe('HDG')
  expect(sim.modeEvents.some(e => e.event === 'NAV REMOVED')).toBe(true)
})

test('at TDN 400 ft higher than planned: the recomputed transition does not fit before MRK, TDN DIST SHORT (Stage D, T6)', () => {
  const { unit, sim, fly } = hoverProcedure()
  unit.press('LSK6R')
  unit.press('EXEC')
  sim.selectAltitude(900)
  sim.engageVerticalSpeed(1000)
  fly(600, () => unit.hover.refused !== null || unit.hover.request > 0)
  expect(unit.hover.refused).toBe('TDN DIST SHORT')
  expect(unit.hover.request).toBe(0)
})

test('radio height lost during TD/H: ALT on the barometric altitude, the horizontal plan to MRK goes on; TDN FUNCTION LOST (Stage D, F2)', () => {
  const { unit, sim, fly, mark } = hoverProcedure()
  unit.press('LSK6R')
  unit.press('EXEC')
  fly(600, () => sim.axisModes.pitch === 'TD/H')
  unit.setCondition('raFail', true)
  fly(3)
  expect(sim.axisModes.collective).toBe('ALT')
  expect(unit.recallList.some(m => m.text === 'TDN FUNCTION LOST')).toBe(true)
  // The accepted horizontal plan is retained: it still stops at MRK.
  fly(300, () => sim.hoverCaptured)
  expect(sim.hoverCaptured).toBe(true)
  expect(metres(unit.truePosition, mark)).toBeLessThan(50)
})

test('a direct-to during the transition ends the procedure and cancels the retained TD/H: HOV where it is (Stage D, F2)', () => {
  const { unit, sim, fly } = hoverProcedure()
  unit.press('LSK6R')
  unit.press('EXEC')
  fly(600, () => sim.axisModes.pitch === 'TD/H')
  unit.directTo('RDG')
  unit.press('EXEC')
  fly(2)
  expect(unit.hover.status).toBe('NONE')
  expect(sim.modeEvents.some(e => e.event === 'TD/H CANCELLED')).toBe(true)
  expect(sim.axisModes.pitch).toBe('HOV')
})

test('a refusal at TDN is one of the library messages, never a planner reason: below the gate speed is TDN NOT POSSIBLE (Stage D, E-17)', () => {
  const { unit, sim, fly } = hoverProcedure()
  unit.press('LSK6R')
  unit.press('EXEC')
  sim.selectSpeed(60)
  expect(() => fly(600, () => unit.hover.refused !== null)).not.toThrow()
  expect(unit.hover.refused).toBe('TDN NOT POSSIBLE')
  expect(unit.hover.refusedReason).toBe('BELOW GATE SPEED')
  expect(unit.hover.request).toBe(0)
})

test('no valid radio height at TDN is TDN FUNCTION LOST, and the simulation goes on (Stage D, E-16)', () => {
  const { unit, fly } = hoverProcedure()
  unit.press('LSK6R')
  unit.press('EXEC')
  fly(600, () => distanceNm(unit.truePosition, unit.coordinates('TDN')!) < 0.1)
  unit.setCondition('raFail', true)
  expect(() => fly(30, () => unit.hover.refused !== null)).not.toThrow()
  expect(unit.hover.refused).toBe('TDN FUNCTION LOST')
  expect(unit.hover.refusedReason).toBe('RADIO HEIGHT INVALID')
  expect(unit.hover.request).toBe(0)
})

test('a headwind at or above the gate true airspeed has no closure toward MRK: the transition is refused (Stage D)', () => {
  const start = { ias: 100, radioHeight: 500, verticalSpeed: 0, hoverHeight: 50 }
  expect(planTransition({ ...start, headwind: 20 }).refused).toBe(false)
  expect(planTransition({ ...start, headwind: 90 })).toEqual({ refused: true, reason: 'no closure' })
  expect(checkAtTdn({ ...start, headwind: 90 }, 5)).toEqual({ engage: false, reason: 'NO CLOSURE', gateNm: null })
})

test('no waypoint goes between TDN and MRK: !HOVER MRK WPT, and the route is unchanged (Stage D)', () => {
  const { unit } = hoverProcedure()
  unit.press('LSK6R')
  const before = JSON.stringify(unit.route.legs)
  unit.open('LEGS')
  while (screenText(unit.screen()).at(-1)!.trim()) unit.press('CLR')
  unit.setScratch('CYYZ')
  unit.press('LSK2L')
  expect(JSON.stringify(unit.route.legs)).toBe(before)
  expect(screenText(unit.screen()).join('\n')).toMatch(/!HOVER MRK WPT/)
  unit.press('CLR')
  unit.setScratch('TDN/0.5')
  unit.press('LSK1L')
  expect(JSON.stringify(unit.route.legs)).toBe(before)
})

test('a new mark over an active procedure offers ACTIVATE; its EXEC replaces the procedure and cancels the TD/H toward the old MRK (Stage D, A-76)', () => {
  const { unit, sim, fly } = hoverProcedure()
  expect(hoverText(unit)).toMatch(/<DES\+SAR/)
  unit.press('LSK6R')
  unit.press('EXEC')
  fly(600, () => sim.axisModes.pitch === 'TD/H')
  expect(hoverText(unit)).not.toMatch(/ACTIVATE>/)
  expect(unit.designateHoverMark({ ident: 'WPT', position: offset(unit.truePosition, 230, 3), label: null })).toBe(true)
  expect(hoverText(unit)).toMatch(/ACTIVATE>/)
  unit.press('LSK6R')
  expect(unit.hover.status).toBe('MOD')
  // Until EXEC the old procedure is still flown.
  fly(2)
  expect(sim.axisModes.pitch).toBe('TD/H')
  unit.press('EXEC')
  fly(2)
  expect(unit.hover.active!.id).toBe(2)
  expect(sim.modeEvents.some(e => e.event === 'TD/H CANCELLED')).toBe(true)
})

test('CANCEL of a new mark over an active procedure keeps the active one flying (Stage D, A-76)', () => {
  const { unit, sim, fly } = hoverProcedure()
  unit.press('LSK6R')
  unit.press('EXEC')
  fly(600, () => sim.axisModes.pitch === 'TD/H')
  const active = unit.hover.active!
  unit.designateHoverMark({ ident: 'WPT', position: offset(unit.truePosition, 230, 3), label: null })
  unit.press('LSK6R')
  unit.press('LSK6L')
  expect(unit.hover.status).toBe('ACT')
  expect(unit.hover.active).toBe(active)
  expect(unit.hover.finalTrack).toBe(active.finalTrack)
  fly(600, () => sim.hoverCaptured)
  expect(sim.hoverCaptured).toBe(true)
  expect(sim.modeEvents.some(e => e.event === 'TD/H CANCELLED')).toBe(false)
})
