import { expect, logicTest as test } from './isolated-client-test'
import { FlightSimulator, SAR_SEARCH_WAYPOINTS, angleDiff, legGeometry, racetrackOutline, sarTrack } from '../src/fmsCdu/flight'
import { courseDeg, distanceNm, offset, type LatLon } from '../src/fmsCdu/fmsModel'
import { groundVelocity, holdTrack, predictedGroundSpeed } from '../src/fmsCdu/kinematics'
import { HELICOPTER_PROFILE, LAB_AIRLINE_VNAV_PROFILE, type AircraftProfile } from '../src/fmsCdu/profile'
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

test('an altitude capture levels off firmly: from an 800 fpm climb it settles on the preselection within 30 s of ALT CAPTURED, without overshoot, under 0.1 g', () => {
  const { unit, sim, ticks } = setup()
  unit.placeAircraft({ position: unit.truePosition, track: 0, altitude: 1000 }, 'test: the climb starts')
  sim.selectAltitude(2000)
  expect(sim.engageVerticalSpeed(800)).toBe(true)
  let t = 0, captured: number | null = null, capturedAt = 0, highest = 0, harshest = 0, previous = unit.verticalSpeed
  const trace: { t: number; altitude: number }[] = []
  ticks(300, () => {
    t += 0.25
    if (captured === null && sim.modeEvents.some(e => e.event === 'ALT CAPTURED')) { captured = t; capturedAt = unit.altitude }
    highest = Math.max(highest, unit.altitude)
    // The level-off's vertical acceleration (the climb's own start is the acceleration limit's business).
    if (unit.altitude >= 1800) harshest = Math.max(harshest, Math.abs(unit.verticalSpeed - previous) / 0.25)
    previous = unit.verticalSpeed
    trace.push({ t, altitude: unit.altitude })
  })
  expect(captured).not.toBeNull()
  expect(Math.abs(capturedAt - 2000)).toBeLessThanOrEqual(20)
  // Within 30 s of the capture the altimeter reads 2,000 (to the foot), and it stays there.
  for (const k of trace.filter(k => k.t >= captured! + 30)) expect(Math.abs(k.altitude - 2000), `at ${k.t} s`).toBeLessThan(0.5)
  expect(highest).toBeLessThanOrEqual(2000.5)
  // 0.1 g is 193 fpm/s.
  expect(harshest).toBeLessThanOrEqual(193)
  expect(sim.axisModes.collective).toBe('ALT')
})

test('the declared bank envelope and roll rate govern the selected computer, without an extra five degrees', () => {
  const profile = structuredClone(HELICOPTER_PROFILE)
  profile.parameters.afcsBankLimit.value = 8
  profile.parameters.fmsRollSteeringLimit.value = 6
  profile.parameters.rollRate.value = 2
  const { unit, sim, ticks } = setup(profile)
  sim.selectHeading(unit.heading + 90)
  ticks(0.25)
  expect(Math.abs(sim.guidance.bankCommand)).toBe(8)
  expect(Math.abs(sim.bankAngle)).toBeCloseTo(0.5, 8)
  ticks(8)
  expect(Math.abs(sim.bankAngle)).toBeCloseTo(8, 8)

  // FMS steering has its own configured cap within the AFCS envelope. This second computer does not inherit the
  // default profile's controller constants. A ninety-degree intercept saturates the real guidance output.
  const second = new ScriptedFms(() => new Date(Date.UTC(2026, 8, 29)), { profile })
  const target = second.coordinates(activeIdent(second)!)!
  const heading = courseDeg(second.position, target) + 90
  second.setAircraft({ heading, track: heading })
  const other = new FlightSimulator(second)
  expect(Math.abs(other.guidance.bankCommand)).toBe(6)
  expect(HELICOPTER_PROFILE.parameters.afcsBankLimit.inForce).toBe(true)
  expect(HELICOPTER_PROFILE.parameters.fmsRollSteeringLimit.inForce).toBe(true)
  expect(HELICOPTER_PROFILE.parameters.rollRate.inForce).toBe(true)
  expect(HELICOPTER_PROFILE.parameters.settlingTime.inForce).toBe(false)
})

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

test('the hold is a ground racetrack in any wind, either turn: the inbound leg is held after the entry, and every circuit crosses the fix (D-H)', () => {
  for (const turn of ['RIGHT', 'LEFT'] as const) {
    for (const from of [0, 90, 180, 270]) {
      const { unit, sim, fly } = setup()
      unit.wind.direction = from
      unit.wind.speed = 30
      holdAtRdg(unit, ...(turn === 'LEFT' ? [['', 'LSK2L'] as [string, CduFunction]] : []))
      const label = `${turn} turns, wind ${from}/30`
      const rdg = unit.coordinates('RDG')!
      expect(fly(3600, () => unit.activeRoute.hold?.status === 'IN PROGRESS'), label).toBeLessThan(3600)
      const hold = unit.activeRoute.hold!
      expect(hold.turn, label).toBe(turn)
      // Let the entry finish: two fix crossings after the hold begins.
      const passes: number[] = []
      // A fix crossing is a local minimum of the distance to the fix within a mile of it.
      let before = Infinity, last = Infinity
      const watchFix = () => {
        const d = distanceNm(rdg, unit.position)
        if (last < before && last <= d && last < 1) passes.push(last)
        before = last
        last = d
      }
      fly(1800, () => { watchFix(); return passes.length >= 2 })
      passes.length = 0
      // Then two full circuits: the inbound leg (its last two thirds, before the fix) within 0.1 NM, the fix crossed
      // within 0.1 NM, and the whole pattern on the holding side, bar the fix crossing.
      let inboundWorst = 0, wrongSide = 0
      const s = turn === 'RIGHT' ? 1 : -1
      fly(1800, () => {
        watchFix()
        const g = legGeometry(offset(rdg, hold.inbound + 180, 10), rdg, unit.position)
        if (Math.abs(g.crossTrack) < 0.5 && g.toGo > 0.2 && g.toGo < 0.6 * sim.holdLegNm! && angleDiff(unit.track, hold.inbound) < 30)
          inboundWorst = Math.max(inboundWorst, Math.abs(g.crossTrack))
        wrongSide = Math.min(wrongSide, s * g.crossTrack)
        return passes.length >= 2
      })
      expect(passes.length, label).toBe(2)
      for (const pass of passes) expect(pass, label).toBeLessThan(0.1)
      expect(inboundWorst, label).toBeLessThan(0.1)
      expect(wrongSide, label).toBeGreaterThan(-0.3)
    }
  }
})

test('the hold entry advisories: DIRECT, TEARDROP and PARALLEL HOLD ENTRY a minute before the fix on track inbound; DIRECT leaves at the fix, the others at the second passage (D-H, M300 10-2, 10-4, 10-6)', () => {
  const cases: { changes: [string, CduFunction][]; text: string; passes: number }[] = [
    { changes: [], text: 'DIRECT HOLD ENTRY', passes: 1 },
    { changes: [['263', 'LSK3L']], text: 'TEARDROP HOLD ENTRY', passes: 2 },
    { changes: [['263', 'LSK3L'], ['', 'LSK2L']], text: 'PARALLEL HOLD ENTRY', passes: 2 },
  ]
  for (const c of cases) {
    const { unit, fly } = setup()
    holdAtRdg(unit, ...c.changes)
    const rdg = unit.coordinates('RDG')!
    const scratch = () => screenText(unit.screen())[13].trim()
    let toFix: number | null = null
    fly(3600, () => { if (scratch() === c.text) { toFix = (distanceNm(unit.position, rdg) / unit.groundSpeed) * 3600; return true } })
    // On track inbound: shown one minute before the fix (the first one-second step inside it).
    expect(toFix, c.text).not.toBeNull()
    expect(toFix!, c.text).toBeLessThanOrEqual(60)
    expect(toFix!, c.text).toBeGreaterThan(58)
    // An advisory: MSG stays dark.
    expect(unit.lamps().has('MSG'), c.text).toBe(false)
    // Passages of the fix: local minima of the distance to it within a mile.
    let passes = 0, before = Infinity, last = Infinity
    const shownAtPass: boolean[] = []
    fly(1800, () => {
      const d = distanceNm(rdg, unit.position)
      if (last < before && last <= d && last < 1) { passes += 1; shownAtPass.push(scratch() === c.text) }
      before = last
      last = d
      return passes >= 2
    })
    // Still shown after each passage before the last it waits for, gone after that one.
    expect(shownAtPass, c.text).toEqual(c.passes === 1 ? [false, false] : [true, false])
  }
})

test('the entries flown in a 30 kt wind from four azimuths: the teardrop on a 40 degree ground track for the leg, the parallel outbound for 2.6 turn radii, each on its side (D-H, M300 10-2, 10-4)', () => {
  for (const kind of ['TEARDROP', 'PARALLEL'] as const) {
    for (const from of [0, 90, 180, 270]) {
      const { unit, sim, fly } = setup()
      Object.assign(unit.wind, { direction: from, speed: 30 })
      holdAtRdg(unit, ['263', 'LSK3L'], ...(kind === 'PARALLEL' ? [['', 'LSK2L'] as [string, CduFunction]] : []))
      const label = `${kind}, wind ${from}/30`
      expect(fly(3600, () => unit.activeRoute.hold?.status === 'IN PROGRESS'), label).toBeLessThan(3600)
      expect(unit.holdEntryFlown, label).toBe(kind)
      const hold = unit.activeRoute.hold!, rdg = unit.coordinates('RDG')!
      const s = hold.turn === 'RIGHT' ? 1 : -1
      const outbound = (hold.inbound + 180) % 360
      fly(1)
      const progress = sim.holdProgress!
      const leg = progress.segments[0]
      if (leg.kind !== 'line') throw new Error('entry leg')
      const radius = progress.segments.find(seg => seg.kind === 'arc')!
      if (radius.kind !== 'arc') throw new Error('arc')
      const length = distanceNm(leg.from, leg.to), track = courseDeg(leg.from, leg.to)
      // The construction.
      if (kind === 'TEARDROP') {
        expect(Math.abs(angleDiff(track, outbound - 40 * s)), label).toBeLessThan(0.01)
        expect(length, label).toBeCloseTo(sim.holdLegNm!, 6)
      } else {
        expect(Math.abs(angleDiff(track, outbound)), label).toBeLessThan(0.01)
        expect(length / radius.radius, label).toBeCloseTo(2.6, 6)
      }
      // Flown: the ground track on the latter part of the leg, whatever the wind, and the side of the inbound course.
      const tracks: number[] = []
      let worstSide = 0
      fly(900, () => {
        if (sim.holdProgress?.index !== 0) return true
        const along = distanceNm(leg.from, unit.position)
        if (along > (kind === 'TEARDROP' ? 0.6 : 0.75) * length && along < 0.95 * length) tracks.push(unit.track)
        const side = s * sideOfInbound(rdg, hold.inbound, unit.position)
        worstSide = kind === 'TEARDROP' ? Math.min(worstSide, side) : Math.max(worstSide, side)
      })
      expect(tracks.length, label).toBeGreaterThan(5)
      // The parallel leg is short (2.6 radii) and begins with the turn off the arrival track: its last quarter within 5°.
      for (const t of tracks) expect(Math.abs(angleDiff(t, track)), label).toBeLessThan(kind === 'TEARDROP' ? 3 : 5)
      // The teardrop leg is on the holding side, the parallel leg not on it (a hair either way at the fix).
      if (kind === 'TEARDROP') expect(worstSide, label).toBeGreaterThan(-0.05)
      else expect(worstSide, label).toBeLessThan(0.05)
    }
  }
})

test('every circuit, in a 30 kt wind from four azimuths and both turns: the outbound leg flown for its length, and the inbound leg within 0.1 NM from its capture to the fix (D-H oracles)', () => {
  for (const turn of ['RIGHT', 'LEFT'] as const) {
    for (const from of [0, 90, 180, 270]) {
      const { unit, sim, fly } = setup()
      Object.assign(unit.wind, { direction: from, speed: 30 })
      holdAtRdg(unit, ...(turn === 'LEFT' ? [['', 'LSK2L'] as [string, CduFunction]] : []))
      const label = `${turn} turns, wind ${from}/30`
      expect(fly(3600, () => unit.activeRoute.hold?.status === 'IN PROGRESS'), label).toBeLessThan(3600)
      const hold = unit.activeRoute.hold!, rdg = unit.coordinates('RDG')!
      // Two whole circuits of the racetrack (outbound turn, outbound leg, inbound turn, inbound leg).
      let circuits = 0, previous = -1, outboundFlown = 0, lastAt = unit.position
      let captured = false, inboundWorst = 0
      const outboundLengths: number[] = [], inboundWorsts: number[] = []
      fly(3600, () => {
        const p = sim.holdProgress
        if (!p || p.entryEnd >= 0) { lastAt = unit.position; return false }
        if (p.index === 1) outboundFlown += distanceNm(lastAt, unit.position)
        if (p.index === 3) {
          const xtk = Math.abs(sideOfInbound(rdg, hold.inbound, unit.position))
          if (!captured && xtk < 0.05) captured = true
          if (captured) inboundWorst = Math.max(inboundWorst, xtk)
        }
        if (previous === 3 && p.index === 0) {
          circuits += 1
          outboundLengths.push(outboundFlown)
          inboundWorsts.push(captured ? inboundWorst : Infinity)
          outboundFlown = 0; captured = false; inboundWorst = 0
        }
        previous = p.index
        lastAt = unit.position
        return circuits >= 2
      })
      expect(circuits, label).toBe(2)
      for (const flown of outboundLengths) expect(Math.abs(flown - sim.holdLegNm!), label).toBeLessThan(0.05)
      for (const worst of inboundWorsts) expect(worst, label).toBeLessThan(0.1)
    }
  }
})

test('a wind at or above the true airspeed cannot be held: UNABLE HOLD (D-H, laboratory)', () => {
  const { unit, sim, fly } = setup()
  holdAtRdg(unit)
  fly(3600, () => unit.activeRoute.hold?.status === 'IN PROGRESS')
  unit.wind.speed = Math.ceil(sim.tas) + 5
  fly(10)
  expect(unit.recallList.some(m => m.text === 'UNABLE HOLD')).toBe(true)
  // F8: hold guidance withdrawn and NAV gives way to a latched heading hold; the hold fix is not sequenced.
  expect(sim.lateralMode).toBe('HDG')
  expect(sim.headingHeld).toBe(true)
  expect(sim.guidance.mode).not.toBe('HOLD')
  expect(activeIdent(unit)).toBe('RDG')
})

test('the helicopter hold defaults to its holding speed limit and leg time for the altitude, and warns above the limit (D-H, M300 10-8, 10-9)', () => {
  const { unit, fly } = setup()
  press(unit, 'HOLD', 'LSK2L')
  const hold = unit.route.hold!
  expect(hold.speed).toBe(unit.altitude <= 6000 ? 100 : 170)
  expect(hold.legTime).toBe(unit.altitude <= 14000 ? 1 : 1.5)
  unit.changeHold(h => { h.speed = unit.altitude <= 6000 ? 120 : 190 })
  unit.press('EXEC')
  fly(3600, () => unit.activeRoute.hold?.status === 'IN PROGRESS')
  expect(unit.recallList.some(m => m.text === 'HIGH HOLDING SPEED')).toBe(true)
})

test('a search pattern is flown along its geometry to its 80th search waypoint, then END OF SEARCH and the route continues (M300 11-15)', () => {
  const { unit, sim, fly } = setup()
  press(unit, 'TACT', 'LSK4L', 'LSK6R', 'EXEC')
  expect(unit.sar.active).toBe('SECTOR')
  fly(600, () => unit.sar.status === 'IN PROGRESS')
  const path = sim.sarPath!
  expect(path).toHaveLength(81)
  let worst = 0
  const seconds = fly(4 * 3600, () => {
    const nearest = Math.min(...path.slice(0, -1).map((p, i) => Math.abs(legGeometry(p, path[i + 1], unit.position).crossTrack)))
    worst = Math.max(worst, nearest)
    return unit.sar.active === null
  })
  expect(seconds).toBeLessThan(4 * 3600)
  expect(worst).toBeLessThan(0.6)
  expect(screenText(unit.screen())[13].trim()).toBe('END OF SEARCH')
  expect(activeIdent(unit)).toBe('MUN')
})

test('search pattern geometry to 80 search waypoints: the square grows by one spacing every two legs turning right; the ladder alternates legs and steps; the sector closes on its datum, turned by the angle (M300 11-1, 11-15)', () => {
  const start = { lat: 45, lon: -75 }
  const unit = new ScriptedFms()
  const sar = unit.sar
  for (const pattern of ['SQUARE', 'LADDER', 'SECTOR'] as const) expect(sarTrack(start, sar, pattern), pattern).toHaveLength(81)
  const square = sarTrack(start, sar, 'SQUARE')
  for (let k = 0; k < SAR_SEARCH_WAYPOINTS; k += 4) {
    expect(distanceNm(square[k], square[k + 1]) / sar.trackSpacing, `square leg ${k}`).toBeCloseTo(Math.floor(k / 2) + 1, 1)
    expect(Math.abs(angleDiff(courseDeg(square[k], square[k + 1]), sar.sarBearing + 90 * k)), `square leg ${k}`).toBeLessThan(0.5)
  }
  const ladder = sarTrack(start, sar, 'LADDER')
  for (let k = 0; k < 8; k += 1) {
    const long = k % 2 === 0
    expect(distanceNm(ladder[k], ladder[k + 1]), `ladder leg ${k}`).toBeCloseTo(long ? sar.legLength : sar.trackSpacing, 1)
    expect(Math.abs(angleDiff(courseDeg(ladder[k], ladder[k + 1]), long ? sar.sarBearing + (k % 4 === 0 ? 0 : 180) : sar.sarBearing + 90)), `ladder leg ${k}`).toBeLessThan(0.5)
  }
  const sector = sarTrack(start, sar, 'SECTOR')
  for (let i = 3; i <= SAR_SEARCH_WAYPOINTS; i += 3) expect(distanceNm(sector[i], start), `sector point ${i}`).toBeLessThan(0.05)
  for (let t = 0; t < 26; t += 1) expect(Math.abs(angleDiff(courseDeg(sector[3 * t], sector[3 * t + 1]), sar.sarBearing + t * sar.angle)), `sector triangle ${t}`).toBeLessThan(0.5)
})

test('the search pages take the M300 field ranges, refuse changes once the search is engaged, and offer PPOS only with no other search waypoint in the route (M300 11-5, 11-6, 11-15, A-157…A-176)', () => {
  const { unit, fly } = setup()
  const scratch = () => screenText(unit.screen())[13].trim()
  const enter = (text: string, lsk: CduFunction) => { typeText(unit, text); unit.press(lsk) }
  // CLR takes the message first, then the entry a character at a time.
  const clearAll = () => { for (let i = 0; i < 30 && scratch() !== ''; i++) unit.press('CLR') }
  press(unit, 'TACT', 'LSK3L')
  // The ladder: leg length and track spacing 0.1 to 40 NM; SAR bearing 000 to 360.
  enter('0.1', 'LSK3R')
  expect(unit.sar.legLength).toBe(0.1)
  enter('40', 'LSK3R')
  expect(unit.sar.legLength).toBe(40)
  enter('41', 'LSK3R')
  expect(unit.sar.legLength).toBe(40)
  expect(scratch()).toBe('INVALID ENTRY')
  clearAll()
  enter('0', 'LSK2R')
  expect(unit.sar.sarBearing).toBe(0)
  enter('4', 'LSK3R')
  // The sector: diameter 0.1 to 40 NM, angle 5 to 90 degrees.
  press(unit, 'TACT', 'LSK4L')
  enter('0.1', 'LSK3R')
  expect(unit.sar.diameter).toBe(0.1)
  enter('4', 'LSK3R')
  for (const [text, ok] of [['4', false], ['5', true], ['90', true], ['91', false]] as const) {
    const before = unit.sar.angle
    enter(text, 'LSK4R')
    expect(unit.sar.angle, `angle ${text}`).toBe(ok ? Number(text) : before)
    if (!ok) clearAll()
  }
  // Engaged at present position: the parameters no longer change, and PPOS is not offered again.
  press(unit, 'TACT', 'LSK2L', 'LSK6R', 'EXEC')
  fly(600, () => unit.sar.status === 'IN PROGRESS')
  expect(unit.sar.status).toBe('IN PROGRESS')
  const spacing = unit.sar.trackSpacing
  enter('3', 'LSK1R')
  expect(unit.sar.trackSpacing).toBe(spacing)
  expect(scratch()).toBe('NOT ALLOWED')
  clearAll()
  press(unit, 'TACT', 'LSK3L')
  unit.sar.refId = 'MUN'
  unit.press('LSK6L')
  expect(unit.sar.refId).toBe('MUN')
  expect(scratch()).toBe('NOT ALLOWED')
})

test('the square and sector searches are joined fly-by onto their first leg, the ladder entry waypoint is flown over (M300 11-2, 11-4)', () => {
  const closest: Record<string, number> = {}
  for (const [pattern, lsk] of [['SQUARE', 'LSK2L'], ['LADDER', 'LSK3L'], ['SECTOR', 'LSK4L']] as const) {
    const { unit, fly } = setup()
    fly(10)
    // A search fix 5 NM ahead on the present track, its first leg 90 degrees to the right.
    const fix = offset(unit.position, unit.track, 5)
    unit.definePoint('SRCH1', fix)
    press(unit, 'TACT', lsk)
    typeText(unit, 'SRCH1')
    unit.press('LSK2L')
    typeText(unit, String(Math.round((unit.track + 90) % 360)).padStart(3, '0'))
    unit.press('LSK2R')
    press(unit, 'LSK6R', 'EXEC')
    let nearest = Infinity
    fly(900, () => { nearest = Math.min(nearest, distanceNm(unit.position, fix)); return unit.sar.status === 'IN PROGRESS' && distanceNm(unit.position, fix) > 1 })
    closest[pattern] = nearest
  }
  // Fly-by: the turn starts before the fix, so the aircraft passes inside it; fly-over: it crosses the fix itself.
  expect(closest.SQUARE).toBeGreaterThan(0.1)
  expect(closest.SECTOR).toBeGreaterThan(0.1)
  expect(closest.LADDER).toBeLessThan(0.05)
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
  // Phase 1: JN, the end of the joining path, on the final course before TDN.
  expect(unit.route.legs.slice(0, 4).map(leg => (leg.kind === 'wpt' ? `${leg.ident}${leg.qualifier ?? ''}` : leg.kind))).toEqual(['JN', 'TDN/O', 'MRK/O', 'disco'])
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

test('the cyclic force-trim release ends the TD/H plan the autopilot kept after the FMS withdrew its request: HOV where it is, not at MRK (R3-02.5, F2)', () => {
  const { unit, sim, fly, mark } = hoverProcedure()
  unit.press('LSK6R')
  unit.press('EXEC')
  // Into TD/H toward MRK, then every radio altimeter fails: TDN FUNCTION LOST withdraws the request; the plan is kept.
  fly(900, () => sim.axisModes.pitch === 'TD/H' && sim.indicatedAirspeed < 50)
  expect(sim.axisModes.pitch).toBe('TD/H')
  unit.setCondition('raFail', true)
  fly(2)
  expect(unit.hover.requestData).toBeNull()
  expect(sim.axisModes).toMatchObject({ collective: 'ALT', pitch: 'TD/H', roll: 'TD/H' })
  const released = unit.truePosition
  expect(sim.releaseForceTrim()).toBe(true)
  expect(sim.modeEvents.some(e => e.event === 'TD/H CANCELLED' && /force-trim release/.test(e.detail))).toBe(true)
  expect(sim.axisModes).toMatchObject({ pitch: 'HOV', roll: 'HOV' })
  fly(90)
  // It stops near where the release was made, short of MRK; without the release the plan would have taken it to MRK.
  expect(unit.groundSpeed).toBeLessThan(1)
  expect(metres(unit.truePosition, mark)).toBeGreaterThan(100)
  expect(metres(unit.truePosition, released)).toBeLessThan(metres(released, mark))
})

test('the cyclic force-trim release in HOV takes the present position as the hover target: moved off it, HOV no longer returns (laboratory)', () => {
  const run = offshore()
  const { unit, sim, fly } = run
  slowToHover(run)
  fly(30)
  const first = unit.truePosition
  // Displaced 40 m (the pilot moves the aircraft on the cyclic): without the release HOV brings it back.
  unit.placeAircraft({ position: offset(first, 320, 40 / 1852), track: 230, altitude: unit.altitude }, 'test: moved on the cyclic')
  fly(60)
  expect(metres(unit.truePosition, first)).toBeLessThan(5)
  // Displaced again, and the force trim released there: HOV holds the new position.
  const second = offset(first, 320, 40 / 1852)
  unit.placeAircraft({ position: second, track: 230, altitude: unit.altitude }, 'test: moved on the cyclic')
  fly(1)
  expect(sim.releaseForceTrim()).toBe(true)
  fly(60)
  expect(metres(unit.truePosition, second)).toBeLessThan(5)
  expect(sim.axisModes).toMatchObject({ pitch: 'HOV', roll: 'HOV' })
  expect(sim.modeEvents.at(-1)).toMatchObject({ event: 'FTR' })
  // The laboratory airline profile has no force-trim release.
  const lab = setup(LAB_AIRLINE_VNAV_PROFILE)
  expect(lab.sim.releaseForceTrim()).toBe(false)
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

test('MRK designation on the HOVER page: mark on top (4L), a database or user waypoint by ident (1L), coordinates (1R); a moving waypoint is refused (T1, M300 A-75)', () => {
  const { unit } = offshore(500)
  unit.open('HOVER')
  unit.press('LSK4L')
  expect(unit.hover.mark).toMatchObject({ ident: 'MRK01', label: 'MARK ON TOP POS' })
  expect(distanceNm(unit.hover.mark!.position, unit.position)).toBeLessThan(1e-9)
  // A user waypoint, and a database one, by ident.
  const sighting = offset(unit.position, 180, 1)
  expect(unit.createUserWaypoint('SGT1', sighting)).toBeUndefined()
  typeText(unit, 'SGT1')
  unit.press('LSK1L')
  expect(unit.hover.mark).toEqual({ ident: 'SGT1', position: sighting, label: null })
  typeText(unit, 'MUN')
  unit.press('LSK1L')
  expect(unit.hover.mark).toEqual({ ident: 'MUN', position: unit.coordinates('MUN'), label: null })
  // Coordinates on 1R.
  typeText(unit, 'N4042.0W07227.0')
  unit.press('LSK1R')
  expect(unit.hover.mark!.position.lat).toBeCloseTo(40.7, 9)
  expect(unit.hover.mark!.position.lon).toBeCloseTo(-72.45, 9)
  // A moving waypoint is refused, and the mark stays as it was.
  unit.defineMoving('SHIP1', offset(unit.position, 90, 2), 270, 20)
  typeText(unit, 'SHIP1')
  unit.press('LSK1L')
  expect(screenText(unit.screen())[13].trim()).toBe('INVALID ENTRY')
  expect(unit.hover.mark!.position.lat).toBeCloseTo(40.7, 9)
})

test('the final track at MRK: into the wind from 5 kt, below it the bearing to MRK; the wind direction frozen at ACTIVATE, its speed taken at TDN (T3, M300 11-19, A-75)', () => {
  for (const speed of [4.9, 5.1]) {
    const { unit, mark } = hoverProcedure({ markNm: 3 })
    Object.assign(unit.wind, { direction: 300, speed })
    unit.press('LSK6R')
    expect(unit.hover.status).toBe('MOD')
    if (speed < 5) expect(unit.hover.finalTrack).toBeCloseTo(courseDeg(unit.position, mark), 9)
    else expect(unit.hover.finalTrack).toBe(300)
  }
  // Activated in 230/20; before TDN the wind turns to 260/30. The final track stays 230, and at TDN the transition is
  // planned with 30 kt along it: the speed now, the direction frozen.
  const { unit, fly } = hoverProcedure()
  unit.press('LSK6R')
  unit.press('EXEC')
  Object.assign(unit.wind, { direction: 260, speed: 30 })
  fly(900, () => unit.hover.atTdn !== null)
  expect(unit.hover.active!.finalTrack).toBe(230)
  expect(unit.hover.atTdn!.start!.headwind).toBeCloseTo(30, 9)
  expect(unit.hover.windSpeed).toBe(30)
})

test('TRANSITION DOWN is shown from EXEC until TDN: at TDN it leaves the scratchpad and MSG, and stays in the recall list (T7, M300 E-36)', () => {
  const { unit, fly } = hoverProcedure()
  unit.press('LSK6R')
  unit.press('EXEC')
  const scratchpad = () => screenText(unit.screen())[13].trim()
  expect(scratchpad()).toBe('TRANSITION DOWN')
  expect(unit.lamps().has('MSG')).toBe(true)
  fly(600, () => unit.lastSequenced === 'JN')
  expect(scratchpad()).toBe('TRANSITION DOWN')
  fly(600, () => unit.hover.atTdn !== null)
  expect(unit.hover.request).toBe(1)
  expect(scratchpad()).toBe('')
  expect(unit.lamps().has('MSG')).toBe(false)
  expect(unit.recallList.map(m => m.text)).toContain('TRANSITION DOWN')
  // A procedure ended before TDN by a direct-to withdraws it too.
  const early = hoverProcedure()
  early.unit.press('LSK6R')
  early.unit.press('EXEC')
  expect(early.unit.directTo('MUN')).toBeUndefined()
  early.unit.press('EXEC')
  early.fly(2)
  expect(early.unit.hover.active).toBeNull()
  expect(screenText(early.unit.screen())[13].trim()).toBe('')
})

test('the route to MRK cancelled (TDN and MRK deleted on LEGS, EXEC) ends the procedure: no request, no transition, the aircraft flies on (T9, M300 11-21)', () => {
  const { unit, sim, fly } = hoverProcedure()
  unit.press('LSK6R')
  unit.press('EXEC')
  unit.open('LEGS')
  // JN, TDN, MRK at the head of the route: delete TDN, then MRK (now second). CLR first acknowledges TRANSITION DOWN.
  for (let i = 0; i < 2; i++) {
    while (screenText(unit.screen())[13].trim() !== 'DELETE') unit.press('CLR')
    unit.press('LSK2L')
  }
  unit.press('EXEC')
  expect(unit.activeRoute.legs.some(leg => leg.kind === 'wpt' && (leg.ident === 'TDN' || leg.ident === 'MRK'))).toBe(false)
  fly(2)
  expect(unit.hover.active).toBeNull()
  fly(300)
  expect(unit.hover.request).toBe(0)
  expect(sim.modeEvents.some(e => e.event === 'TD')).toBe(false)
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

test('a new procedure pending over an active one leaves the active TDN and MRK where they are, through CANCEL; EXEC moves them (Stage D)', () => {
  const { unit, fly } = hoverProcedure()
  unit.press('LSK6R')
  unit.press('EXEC')
  fly(20)
  const active = unit.activeRoute
  const tdn = unit.coordinates('TDN', active)!, mrk = unit.coordinates('MRK', active)!
  const second = offset(unit.truePosition, 180, 3)
  unit.designateHoverMark({ ident: 'WPT', position: second, label: null })
  unit.press('LSK6R')
  expect(unit.routeStatus).toBe('MOD')
  expect(unit.coordinates('TDN', unit.activeRoute)).toEqual(tdn)
  expect(unit.coordinates('MRK', unit.activeRoute)).toEqual(mrk)
  // The modified route shows the new pair.
  expect(unit.coordinates('MRK', unit.route)).toEqual(second)
  unit.press('LSK6L')
  expect(unit.coordinates('TDN', unit.activeRoute)).toEqual(tdn)
  expect(unit.coordinates('MRK', unit.activeRoute)).toEqual(mrk)
  unit.designateHoverMark({ ident: 'WPT', position: second, label: null })
  unit.press('LSK6R')
  unit.press('EXEC')
  expect(unit.coordinates('MRK', unit.activeRoute)).toEqual(second)
  expect(distanceNm(unit.coordinates('TDN', unit.activeRoute)!, second)).toBeCloseTo(unit.hover.active!.dtra, 6)
})

test('a hover modification edited until TDN is gone executes as a plain route change and leaves no pending TDN or MRK behind (Stage D)', () => {
  const { unit } = hoverProcedure()
  unit.press('LSK6R')
  unit.modify(route => { route.legs = route.legs.filter(leg => !(leg.kind === 'wpt' && leg.ident === 'TDN')) })
  unit.press('EXEC')
  expect(unit.hover.status).toBe('NONE')
  expect(unit.hover.active).toBeNull()
  // A later modification resolves TDN from nothing: no stale pending point.
  unit.modify(() => {})
  expect(unit.coordinates('TDN', unit.route)).toBeUndefined()
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

test('D-H: a crew hold takes its default leg time and speed from the altitude when the entry begins, not when it is made (M300 10-9)', () => {
  const { unit, fly } = setup()
  const fix = activeIdent(unit)!
  // Made at 16,000 ft: the defaults shown then are for above 14,000 ft (1.5 minutes, the high holding speed).
  unit.placeAircraft({ position: unit.position, track: unit.track, altitude: 16000 }, 'test: high when the hold is made')
  expect(unit.defineHold(fix)).toBeUndefined()
  expect(unit.route.hold).toMatchObject({ legTime: 1.5, speed: 170 })
  unit.press('EXEC')
  // Down to 5,000 ft before the fix: the entry begins there, so the defaults become 1.0 minute and 100 kt.
  unit.placeAircraft({ position: unit.position, track: unit.track, altitude: 5000 }, 'test: low when the entry begins')
  expect(fly(3600, () => unit.activeRoute.hold?.status === 'IN PROGRESS')).toBeLessThan(3600)
  expect(unit.activeRoute.hold).toMatchObject({ legTime: 1, speed: 100 })
  // Fixed from the entry on: climbing through 14,000 ft does not change them (M300 10-9).
  unit.placeAircraft({ position: unit.position, track: unit.track, altitude: 15000 }, 'test: climbing in the hold')
  fly(30)
  expect(unit.activeRoute.hold).toMatchObject({ legTime: 1, speed: 100 })
})

test('D-H: a leg time or speed the crew entered is kept at the entry, whatever the altitude', () => {
  const { unit, fly } = setup()
  unit.placeAircraft({ position: unit.position, track: unit.track, altitude: 16000 }, 'test: high when the hold is made')
  press(unit, 'HOLD', 'LSK2L')
  expect(unit.route.hold).toBeDefined()
  typeText(unit, '2.5'); unit.press('LSK4L')
  typeText(unit, '150'); unit.press('LSK1R')
  expect(unit.route.hold).toMatchObject({ legTime: 2.5, speed: 150 })
  unit.press('EXEC')
  unit.placeAircraft({ position: unit.position, track: unit.track, altitude: 5000 }, 'test: low when the entry begins')
  expect(fly(3600, () => unit.activeRoute.hold?.status === 'IN PROGRESS')).toBeLessThan(3600)
  expect(unit.activeRoute.hold).toMatchObject({ legTime: 2.5, speed: 150 })
})
