import { expect, logicTest as test } from './isolated-client-test'
import { FlightSimulator, angleDiff, legGeometry } from '../src/fmsCdu/flight'
import { bearingDeg, distanceNm, offset } from '../src/fmsCdu/fmsModel'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { screenText } from '../src/fmsCdu/screen'
import type { CduFunction } from '../src/fmsCdu/variants'

// Lateral guidance fidelity, per the PBN functional requirements and airline practice (the FMS test bench research roadmap):
// ARINC 424 path terminators, INTC CRS and ABEAM PTS on a direct-to, a flown lateral offset, and selected heading
// versus LNAV with arm and capture.
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
const enter = (unit: ScriptedFms, text: string, lsk: CduFunction) => { typeText(unit, text); unit.press(lsk) }
const lines = (unit: ScriptedFms) => screenText(unit.screen())
const active = (unit: ScriptedFms) => {
  const leg = unit.activeRoute.legs[0]
  return leg?.kind === 'wpt' ? leg.ident : leg?.kind === 'cond' ? `(${leg.path})` : null
}

test('a course-to-altitude leg holds its course until the altitude, then the direct-to-fix leg starts from there', () => {
  const { unit, fly } = setup()
  unit.setAircraft({ altitude: 400, track: 251 })
  unit.selectProcedure('SID', 'RIDEA3', 'MUN')
  unit.press('EXEC')
  expect(active(unit)).toBe('(CA)')
  press(unit, 'LEGS')
  expect(lines(unit)[1]).toMatch(/^ 251° CRS/)
  expect(lines(unit)[2]).toMatch(/^\(1200\)/)
  const tracks: number[] = []
  fly(600, () => { if (active(unit) === '(CA)') tracks.push(unit.track); return active(unit) === 'OW501' })
  // A course leg corrects for wind: the track stays on 251.
  expect(Math.max(...tracks.slice(10).map(t => Math.abs(angleDiff(251, t))))).toBeLessThan(2)
  expect(unit.altitude).toBeGreaterThan(1150)
  // DF: the leg to OW501 begins where the climb ended, not at an earlier fix.
  expect(distanceNm(unit.activeLegStart, unit.position)).toBeLessThan(0.2)
})

test('a heading leg drifts with the wind; a heading-to-intercept leg ends on the next course, which is then flown', () => {
  const { unit, fly } = setup()
  unit.setAircraft({ altitude: 400, track: 71 })
  unit.selectProcedure('SID', 'GATIN2', 'RDG')
  unit.press('EXEC')
  const vaTracks: number[] = []
  fly(600, () => { if (active(unit) === '(VA)') vaTracks.push(unit.track); return active(unit) === '(VI)' })
  // Wind 270/12 across a 071 heading drifts the track a couple of degrees: a heading leg does not correct for it.
  const settled = vaTracks.slice(-5)
  expect(Math.min(...settled.map(t => Math.abs(angleDiff(71, t))))).toBeGreaterThan(1)
  expect(Math.max(...settled.map(t => Math.abs(angleDiff(71, t))))).toBeLessThan(4)
  fly(1800, () => active(unit) === 'OW511')
  expect(active(unit)).toBe('OW511')
  const ow511 = unit.coordinates('OW511')!
  // At the intercept the aircraft is on the 090 course line into OW511.
  expect(Math.abs(legGeometry(offset(ow511, 270, 30), ow511, unit.position).crossTrack)).toBeLessThan(0.35)
  fly(240)
  if (active(unit) === 'OW511') expect(Math.abs(angleDiff(90, unit.track))).toBeLessThan(5)
})

test('a radius-to-fix leg is flown as its arc, at its radius, onto the final approach course', () => {
  const { unit, sim, fly } = setup()
  unit.selectProcedure('APPROACH', 'R06L', 'UL402')
  unit.press('EXEC')
  press(unit, 'LEGS')
  enter(unit, 'UL603', 'LSK1L')
  unit.press('EXEC')
  const ul603 = unit.coordinates('UL603')!
  unit.setAircraft({ position: offset(ul603, 30, 4), track: 210, altitude: 3000 })
  fly(900, () => active(unit) === 'UL601')
  expect(active(unit)).toBe('UL601')
  const leg = unit.activeRoute.legs[0]
  if (leg?.kind !== 'wpt' || !leg.arc) throw new Error('UL601 should be an RF leg')
  const centre = leg.arc.centre
  const radii: number[] = []
  fly(900, () => {
    if (active(unit) === 'UL601' && sim.guidance.distanceToGo !== null && sim.guidance.distanceToGo < 5.5) radii.push(distanceNm(centre, unit.position))
    return active(unit) === 'RW06L'
  })
  expect(active(unit)).toBe('RW06L')
  // Once established on the arc the aircraft keeps within 0.3 NM of its 3 NM radius.
  const established = radii.slice(Math.floor(radii.length / 4))
  expect(Math.max(...established.map(r => Math.abs(r - 3)))).toBeLessThan(0.3)
  // And it rolls out on the final approach course.
  expect(Math.abs(angleDiff(57, bearingDeg(unit.coordinates('UL601')!, unit.coordinates('RW06L')!)))).toBeLessThan(3)
  fly(60)
  expect(Math.abs(angleDiff(57, unit.track))).toBeLessThan(8)
})

test('INTC CRS turns a direct-to into a course into the fix, which the aircraft intercepts and holds', () => {
  const { unit, fly } = setup()
  press(unit, 'LEGS')
  enter(unit, 'TOLGU', 'LSK1L')
  expect(lines(unit)[11]).toMatch(/INTC CRS $/)
  enter(unit, '045', 'LSK6R')
  expect(lines(unit)[12]).toMatch(/045$/)
  unit.press('EXEC')
  const tolgu = unit.coordinates('TOLGU')!
  let onCourse = false
  fly(3600, () => {
    const line = legGeometry(offset(tolgu, 225, 30), tolgu, unit.position)
    if (line.toGo < 3 && line.toGo > 0.5) onCourse = Math.abs(line.crossTrack) < 0.3 && Math.abs(angleDiff(45, unit.track)) < 5
    return active(unit) !== 'TOLGU'
  })
  expect(onCourse).toBe(true)
})

test('ABEAM PTS keeps the points a direct-to bypassed, placed abeam them on the new track', () => {
  const unit = new ScriptedFms()
  press(unit, 'LEGS')
  enter(unit, 'TOLGU', 'LSK1L')
  expect(lines(unit)[10]).toMatch(/ABEAM PTS>$/)
  unit.press('LSK5R')
  const idents = unit.route.legs.map(leg => (leg.kind === 'wpt' ? leg.ident : '-'))
  expect(idents.slice(0, 3)).toEqual(['MUN01', 'RDG01', 'TOLGU'])
  const here = unit.position, tolgu = unit.coordinates('TOLGU')!
  for (const ident of ['MUN01', 'RDG01']) {
    expect(Math.abs(legGeometry(here, tolgu, unit.coordinates(ident)!).crossTrack), ident).toBeLessThan(0.05)
  }
  // Each is abeam its original point: the original lies square off the track from it.
  const mun = unit.coordinates('MUN')!, mun01 = unit.coordinates('MUN01')!
  expect(Math.abs(angleDiff(bearingDeg(here, tolgu), bearingDeg(mun01, mun)) ) % 180).toBeCloseTo(90, -1)
  expect(lines(unit)[10]).not.toMatch(/ABEAM PTS>$/)
})

test('a lateral offset is flown parallel to the route, from after its start waypoint to its end waypoint', () => {
  const { unit, fly } = setup()
  press(unit, 'PROG', 'PREV')
  enter(unit, 'R2', 'LSK1L')
  enter(unit, 'RDG', 'LSK2L')
  enter(unit, 'TOLGU', 'LSK2R')
  expect(lines(unit)[4]).toMatch(/^RDG\s+TOLGU$/)
  unit.press('EXEC')
  const rdg = unit.coordinates('RDG')!, tolgu = unit.coordinates('TOLGU')!
  // Before the start waypoint the aircraft stays on the route: the MUN to RDG leg is flown on its centre line.
  const mun = unit.coordinates('MUN')!
  let beforeStart = 0
  fly(3600, () => {
    const g = legGeometry(mun, rdg, unit.position)
    if (active(unit) === 'RDG' && g.along > 3 && g.toGo > 3) beforeStart = Math.max(beforeStart, Math.abs(g.crossTrack))
    return active(unit) === 'TOLGU'
  })
  expect(beforeStart).toBeLessThan(0.3)
  const offsets: number[] = []
  fly(3600, () => {
    const g = legGeometry(rdg, tolgu, unit.position)
    if (g.along > 8 && g.toGo > 3) offsets.push(g.crossTrack)
    return active(unit) === 'FERDI'
  })
  // Established on the offset: two miles right of the RDG-TOLGU leg.
  expect(offsets.length).toBeGreaterThan(30)
  expect(Math.min(...offsets)).toBeGreaterThan(1.7)
  expect(Math.max(...offsets)).toBeLessThan(2.3)
  // The offset ends at TOLGU.
  expect(unit.activeRoute.offset).toBeUndefined()
  expect(unit.hasCondition('offset')).toBe(false)
})

test('HDG SEL flies the selected heading without sequencing; LNAV armed captures the route and resumes', () => {
  const { unit, sim, fly } = setup()
  // A heading close to the route: the aircraft passes abeam MUN, but in HDG SEL the FMS does not sequence it.
  sim.selectHeading(125)
  const mun = unit.coordinates('MUN')!
  let passedAbeam = false
  fly(900, () => { passedAbeam ||= legGeometry(unit.activeLegStart, mun, unit.position).toGo < -1 })
  expect(passedAbeam).toBe(true)
  expect(sim.lateralMode).toBe('HDG')
  // HDG SEL flies the heading; the track differs from it by the drift the wind gives.
  expect(Math.abs(angleDiff(125, unit.heading))).toBeLessThan(3)
  expect(active(unit)).toBe('MUN')
  // Back toward the route on a 45-degree intercept: LNAV armed, then engaged when it captures the leg.
  sim.selectHeading(70)
  sim.armLnav()
  expect(sim.lnavIsArmed).toBe(true)
  const captured = fly(1200, () => sim.lateralMode === 'LNAV')
  expect(captured).toBeLessThan(1200)
  expect(sim.lnavIsArmed).toBe(false)
  fly(3600, () => active(unit) === 'RDG')
  expect(active(unit)).toBe('RDG')
})

test('a direct-to-fix leg after a fly-by waypoint starts where the aircraft turned, not at the waypoint', () => {
  const { unit, fly } = setup()
  unit.replaceLegs([{ kind: 'wpt', ident: 'MUN' }, { kind: 'wpt', ident: 'RDG', path: 'DF' }, { kind: 'wpt', ident: 'CYUL' }])
  unit.press('EXEC')
  let turnedAt = { lat: 0, lon: 0 }
  fly(3600, () => { if (active(unit) === 'RDG') { turnedAt = unit.position; return true } })
  // MUN is a fly-by: the turn starts short of it, and the DF leg begins there.
  expect(distanceNm(turnedAt, unit.coordinates('MUN')!)).toBeGreaterThan(0.1)
  expect(distanceNm(unit.activeLegStart, turnedAt)).toBeLessThan(0.05)
})

test('a hold with EXIT TYPE ONCE leaves after its first circuit on its own', () => {
  const { unit, fly } = setup()
  press(unit, 'HOLD', 'LSK2L', 'LSK5L', 'EXEC')
  expect(unit.activeRoute.hold?.exit).toBe('ONCE')
  fly(3600, () => unit.activeRoute.hold?.status === 'IN PROGRESS')
  const seconds = fly(900, () => active(unit) === 'TOLGU')
  expect(seconds).toBeLessThan(900)
  expect(unit.activeRoute.hold).toBeUndefined()
})
