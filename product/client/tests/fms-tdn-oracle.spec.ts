import { expect, logicTest as test } from './isolated-client-test'
import {
  TDN_PARAMETERS as P, checkAtTdn, climbGainFt, departFromHover, iasFromTas, planTransition, speedEvents, tasFromIas,
  tdhClosedLoop, verticalProfile, type DeparturePlan, type TransitionPlan, type TransitionStart,
} from './support/tdnOracle'

// The independent oracle for the helicopter transition down to hover (tests/support/tdnOracle.ts), which the
// production AFCS is compared against. Its expected values here come from hand arithmetic (shown beside each), from
// Astra's independently checked figures, or from a separate time-stepped simulation below: never from the oracle's
// own formulas, so a mistake in the oracle cannot agree with itself. Cases are R3-01's owner tests.

const nominal: TransitionStart = { iasKt: 100, raFt: 500, vsFpm: 0, headwindKt: 20, hoverFt: 50 }
const plan = (start: TransitionStart) => { const result = planTransition(start); if (result.refused) throw new Error(result.reason); return result as TransitionPlan }
const depart = (...args: Parameters<typeof departFromHover>) => { const result = departFromHover(...args); if (result.refused) throw new Error(result.reason); return result as DeparturePlan }
// A 20 kt wind from 230°T blows toward 050°T.
const windFrom230 = { northKt: 20 * Math.cos((50 * Math.PI) / 180), eastKt: 20 * Math.sin((50 * Math.PI) / 180) }

/**
 * A second, time-stepped implementation for cross-checking distances: vertical speed chases a stopping-limited
 * command at the 600 fpm/s limit, speed changes at its rate, and ground speed is summed each millisecond. It shares
 * only the ISA conversion with the oracle, which is pinned separately.
 */
function stepped(h0: number, v0: number, target: number, rate: number, ias0: number, iasTarget: number, iasRate: number, headwind: number) {
  const dt = 0.001, A = P.vsRateFpmPerS
  let t = 0, h = h0, v = v0, ias = ias0, x = 0, arrival = Number.NaN
  while (t < 300) {
    const error = target - h
    const command = Math.sign(error) * Math.min(rate, Math.sqrt(2 * A * 60 * Math.abs(error)))
    v += Math.max(-A * dt, Math.min(A * dt, command - v))
    h += (v / 60) * dt
    ias += Math.max(-iasRate * dt, Math.min(iasRate * dt, iasTarget - ias))
    x += ((tasFromIas(ias, h) - headwind) * dt) / 3600
    t += dt
    if (Math.abs(target - h) < 0.01 && Math.abs(v) < 1 && Math.abs(ias - iasTarget) < 1e-9) { arrival = t; break }
  }
  return { distanceNm: x, arrivalS: arrival }
}

test('ISA conversions: 152 KTAS at 2,000 ft is 147.587 KIAS, and GS 0 in a 20 kt headwind at 50 ft is 19.985 KIAS', () => {
  expect(iasFromTas(152, 2000)).toBeCloseTo(147.587, 3)
  expect(iasFromTas(20, 50)).toBeCloseTo(19.985, 3)
  expect(tasFromIas(iasFromTas(80, 200), 200)).toBeCloseTo(80, 9)
})

test('vertical moves are trapezoidal at 600 fpm/s: 200→50 ft at 150 fpm takes 60.25 s, 50→200 ft at 500 fpm 18.833 s', () => {
  // Ramps of 150/600 = 0.25 s cover 0.3125 ft each; 149.375 ft at 2.5 ft/s is 59.75 s.
  expect(verticalProfile(200, 0, 50, 150).arrivalS).toBeCloseTo(60.25, 9)
  // Ramps of 0.8333 s cover 3.472 ft each; 143.056 ft at 8.333 ft/s is 17.1667 s.
  expect(verticalProfile(50, 0, 200, 500).arrivalS).toBeCloseTo(18.8333, 4)
  // A move too short for a plateau is triangular: rest to rest over 1 ft takes 2·√(60/600) = 0.6325 s.
  const short = verticalProfile(0, 0, 1, 500)
  expect(short.arrivalS).toBeCloseTo(2 * Math.sqrt(0.1), 9)
  expect(Math.max(...short.segments.map(seg => Math.abs(seg.vsFpm)), Math.abs(short.vsAt(short.arrivalS / 2)))).toBeLessThan(500)
})

test('the GA climb over the first NM at 120 kt GS with the 800 fpm ramp is 391.111 ft, which fails a 400 ft/NM criterion', () => {
  // 30 s to the NM; the ramp takes 1.333 s and 8.889 ft, then 28.667 s at 800 fpm is 382.222 ft.
  expect(climbGainFt(120, 800)).toBeCloseTo(391.111, 3)
  expect(climbGainFt(120, 800)).toBeLessThan(400)
})

test('nominal TD from 500 ft and 100 KIAS: capture, completion and arrival are distinct, and distances follow the commands', () => {
  const td = plan(nominal).td
  // IAS: capture at 82 KIAS (18 s), completion at 81 (19 s), arrival at 80 (20 s).
  expect(td.ias).toEqual({ captureS: 18, completionS: 19, arrivalS: 20 })
  // RHT 500→200 at 500 fpm: arrival 0.8333 + 35.1667 + 0.8333 = 36.8333 s. On the final ramp VS falls to 200 fpm
  // 1/3 s before arrival (0.556 ft to go, inside 20 ft): capture 36.5 s. It falls to 50 fpm 1/12 s before: completion.
  expect(td.rht.arrivalS).toBeCloseTo(36.8333, 4)
  expect(td.rht.captureS).toBeCloseTo(36.5, 4)
  expect(td.rht.completionS).toBeCloseTo(36.75, 4)
  expect(td.durationS).toBeCloseTo(td.rht.arrivalS, 9)
  expect(td.gateHeightFt).toBe(200)
  // The distance is the command profile's, independently time-stepped.
  const reference = stepped(500, 0, 200, 500, 100, 80, 1.0, 20)
  expect(Math.abs(td.distanceNm - reference.distanceNm)).toBeLessThan(0.0005)
  expect(Math.abs(td.durationS - reference.arrivalS)).toBeLessThan(0.05)
})

test('nominal gate segment and TD/H: GS 60.235 kt at the gate, 0.672 NM and 80.3 s to MRK, and the hover descent', () => {
  const nominalPlan = plan(nominal)
  // 80 KIAS at 200 ft ISA is 80.235 KTAS; less the 20 kt headwind.
  expect(nominalPlan.gate.gsKt).toBeCloseTo(60.235, 3)
  expect(nominalPlan.gate.minimumDurationS).toBeCloseTo((0.2 / 60.2346) * 3600, 2)
  // 60.2346² / (2 · 0.75 · 3,600) = 0.6719 NM; 60.2346 / 0.75 = 80.31 s.
  expect(nominalPlan.tdh.distanceNm).toBeCloseTo(0.6719, 4)
  expect(nominalPlan.tdh.durationS).toBeCloseTo(80.313, 2)
  // 200→50 ft at 150 fpm: 60.25 s to arrival; at 150 fpm (inside the 200 fpm band) capture is 20 ft early, 8 s before.
  expect(nominalPlan.tdh.hoverHeightFt).toBe(50)
  expect(nominalPlan.tdh.rht.arrivalS).toBeCloseTo(60.25, 9)
  expect(nominalPlan.tdh.rht.captureS).toBeCloseTo(60.25 - 0.25 - (20 - 0.3125) / 2.5, 6)
  // The planned DTRA is the sum of its parts at full precision.
  expect(nominalPlan.plannedDtraNm).toBe(nominalPlan.td.distanceNm + 0.2 + nominalPlan.tdh.distanceNm)
  expect(nominalPlan.boundaries.map(entry => entry.name)).toEqual(['TDN', 'TD end', 'TD/H start', 'MRK'])
  expect(nominalPlan.boundaries.at(-1)).toMatchObject({ gsKt: 0, heightFt: 50 })
})

test('a selected hover height of 80 ft leaves the DTRA unchanged and ends the TD/H descent earlier', () => {
  const at50 = plan(nominal), at80 = plan({ ...nominal, hoverFt: 80 })
  expect(at80.plannedDtraNm).toBe(at50.plannedDtraNm)
  // 120 ft at 150 fpm with its ramps: 0.25 + (120 − 0.625)/2.5 + 0.25 = 48.25 s.
  expect(at80.tdh.rht.arrivalS).toBeCloseTo(48.25, 9)
  // A hover height above the gate height is capped by it: TD/H never climbs.
  expect(plan({ ...nominal, raFt: 150, iasKt: 80, hoverFt: 180 }).tdh.hoverHeightFt).toBe(150)
})

test('a nonzero initial vertical speed: +1,000 fpm climbs 13.889 ft while braking at 600 fpm/s, then descends', () => {
  const td = plan({ ...nominal, vsFpm: 1000 }).td
  // 1,000² / (2 · 600) / 60 = 13.889 ft, over 1.667 s.
  expect(td.maxHeightFt).toBeCloseTo(513.889, 3)
  // Then 313.889 ft rest to rest at 500 fpm: 0.8333 + 36.8333 + 0.8333 s after the braking 1.6667 s.
  expect(td.rht.arrivalS).toBeCloseTo(40.1667, 4)
  // Already descending at 500 fpm, there is no ramp-in: (300 − 3.472) ft at 8.333 ft/s, then the 0.8333 s ramp-out.
  expect(plan({ ...nominal, vsFpm: -500 }).td.rht.arrivalS).toBeCloseTo((300 - 250_000 / 1200 / 60) / (500 / 60) + 500 / 600, 9)
  // Descending faster than the command, it slows to the command first and still arrives at rest on the target.
  const fast = verticalProfile(500, -1000, 200, 500)
  expect(fast.vsAt(1)).toBeCloseTo(-500, 9)
  expect(fast.heightAt(fast.arrivalS)).toBeCloseTo(200, 9)
  expect(fast.minHeightFt).toBeCloseTo(200, 9)
})

test('admission: below the gate speed, beyond the VS limit, an invalid RA or an out-of-range hover height are refused', () => {
  expect(planTransition({ ...nominal, iasKt: 75 })).toEqual({ refused: true, reason: 'below gate speed' })
  expect(planTransition({ ...nominal, iasKt: 79.999 })).toEqual({ refused: true, reason: 'below gate speed' })
  expect(planTransition({ ...nominal, iasKt: 80 }).refused).toBe(false)
  expect(planTransition({ ...nominal, vsFpm: -1001 })).toEqual({ refused: true, reason: 'vertical speed limit' })
  expect(planTransition({ ...nominal, vsFpm: 1000 }).refused).toBe(false)
  for (const raFt of [null, Number.NaN, -1, 2501]) expect(planTransition({ ...nominal, raFt })).toEqual({ refused: true, reason: 'radio height invalid' })
  expect(planTransition({ ...nominal, hoverFt: 29 })).toEqual({ refused: true, reason: 'hover height out of range' })
  expect(planTransition({ ...nominal, hoverFt: 201 })).toEqual({ refused: true, reason: 'hover height out of range' })
  expect(planTransition({ ...nominal, raFt: 25 })).toEqual({ refused: true, reason: 'outside TD/H window' })
  // Zero closure along the final: never a division, a refusal.
  expect(planTransition({ ...nominal, headwindKt: 110 })).toEqual({ refused: true, reason: 'no closure' })
})

test('T6 at full precision: 0.001 NM of slack engages and 0.001 NM short refuses; faster or higher than planned', () => {
  const nominalPlan = plan(nominal)
  const exactly = nominalPlan.td.distanceNm + nominalPlan.tdh.distanceNm
  const over = checkAtTdn(nominal, exactly + 0.001), under = checkAtTdn(nominal, exactly - 0.001)
  expect(over.decision).toBe('engage')
  expect(over.gateNm).toBeCloseTo(0.001, 12)
  expect(under).toMatchObject({ decision: 'refuse', reason: 'TDN DIST SHORT' })
  expect(under.gateNm).toBeCloseTo(-0.001, 12)
  expect(checkAtTdn(nominal, exactly).decision).toBe('engage')
  // Against the fixed MRK of the nominal plan: 105 KIAS absorbs the extra distance; 125 KIAS or 900 ft cannot.
  const mrk = nominalPlan.plannedDtraNm
  const at105 = checkAtTdn({ ...nominal, iasKt: 105 }, mrk)
  expect(at105.decision).toBe('engage')
  expect(at105.gateNm!).toBeGreaterThan(0)
  expect(at105.gateNm!).toBeLessThan(0.2)
  expect(checkAtTdn({ ...nominal, iasKt: 125 }, mrk)).toMatchObject({ decision: 'refuse', reason: 'TDN DIST SHORT' })
  expect(checkAtTdn({ ...nominal, raFt: 900 }, mrk)).toMatchObject({ decision: 'refuse', reason: 'TDN DIST SHORT' })
  expect(checkAtTdn({ ...nominal, iasKt: 75 }, mrk)).toEqual({ decision: 'refuse', reason: 'below gate speed', gateNm: null })
})

test('TD/H closed loop: exact inside the bounds; saturated at 1.25 kt/s it overshoots MRK, at 0.5 kt/s it stops short', () => {
  // 60 kt with 0.672 NM: required 60²/(2 · 0.672 · 3,600) = 0.744 kt/s, inside the bounds, arriving at MRK.
  expect(tdhClosedLoop(60, 0.672)).toMatchObject({ saturated: 'none', stopOffsetNm: 0, outcome: 'arrived', hovCapturesAtStop: true })
  // 0.3 NM needs 1.667 kt/s: flies 1.25 and stops at 60²/(2 · 1.25 · 3,600) = 0.4 NM, 0.1 NM past MRK (185 m).
  const over = tdhClosedLoop(60, 0.3)
  expect(over).toMatchObject({ decelKtPerS: 1.25, saturated: 'upper', outcome: 'overshoot-recovery', hovCapturesAtStop: false })
  if ('stopOffsetNm' in over) expect(over.stopOffsetNm).toBeCloseTo(0.1, 12)
  // 1.5 NM needs 0.333 kt/s: flies 0.5 and stops at 1.0 NM, 0.5 NM short.
  const short = tdhClosedLoop(60, 1.5)
  expect(short).toMatchObject({ decelKtPerS: 0.5, saturated: 'lower', outcome: 'short-recovery' })
  if ('stopOffsetNm' in short) expect(short.stopOffsetNm).toBeCloseTo(-0.5, 12)
  // At or past MRK the expression is never evaluated: upper bound, all of the stopping distance is overshoot.
  expect(tdhClosedLoop(60, 0)).toMatchObject({ decelKtPerS: 1.25, saturated: 'upper', outcome: 'overshoot-recovery' })
  expect(tdhClosedLoop(60, -0.05)).toMatchObject({ saturated: 'upper' })
  // A small overshoot inside the 50 m HOV radius captures there; 28 m past MRK is not an "arrival".
  const slight = tdhClosedLoop(60, 0.385)
  expect(slight).toMatchObject({ saturated: 'upper', outcome: 'overshoot-recovery', hovCapturesAtStop: true })
  expect(tdhClosedLoop(0, 0.5)).toEqual({ invalid: 'no closure' })
})

test('TU-LAB from exact GS 0 at 50 ft in a 20 kt headwind: 19.985 KIAS, HDG at 40 KIAS, IAS and RHT independently', () => {
  const tu = depart({ raFt: 50, headingDeg: 230, groundVelocity: { northKt: 0, eastKt: 0 }, windVelocity: windFrom230 })
  expect(tu.initialForwardTasKt).toBeCloseTo(20, 9)
  expect(tu.initialIasKt).toBeCloseTo(19.985, 3)
  expect(tu.initialCrossAirKt).toBeCloseTo(0, 9)
  // (40 − 19.985) / 1.0 and (80 − 19.985) / 1.0.
  expect(tu.headingCaptureS).toBeCloseTo(20.015, 3)
  expect(tu.ias.arrivalS).toBeCloseTo(60.015, 3)
  expect(tu.ias.captureS).toBeCloseTo(58.015, 3)
  // 50→200 ft at 500 fpm: 18.833 s to arrival; VS falls to 200 fpm 1/3 s before, inside 20 ft: capture 18.5 s.
  expect(tu.rht.arrivalS).toBeCloseTo(18.8333, 4)
  expect(tu.rht.captureS).toBeCloseTo(18.5, 4)
  const reference = stepped(50, 0, 200, 500, iasFromTas(20, 50), 80, 1.0, 20)
  expect(Math.abs(tu.distanceNm - reference.distanceNm)).toBeLessThan(0.0005)
  expect(tu.distanceNm).toBeCloseTo(0.502, 3)
})

test('TU-LAB from a sideways drift starts from the true vector, and the lateral limit brings the drift to zero', () => {
  // Heading 230° in the same wind, drifting 3 kt to the right (toward 320°).
  const drift = { northKt: 3 * Math.cos((320 * Math.PI) / 180), eastKt: 3 * Math.sin((320 * Math.PI) / 180) }
  const tu = depart({ raFt: 50, headingDeg: 230, groundVelocity: drift, windVelocity: windFrom230 })
  expect(tu.initialCrossGroundKt).toBeCloseTo(3, 9)
  expect(tu.initialCrossAirKt).toBeCloseTo(3, 9)
  expect(tu.crossGroundZeroS).toBeCloseTo(2, 9) // 3 kt at 1.5 kt/s
  expect(tu.initialForwardTasKt).toBeCloseTo(20, 9)
  // Refusals: at or above 40 KIAS, below MUH, invalid RA.
  const fast = { northKt: -25 * Math.cos((50 * Math.PI) / 180), eastKt: -25 * Math.sin((50 * Math.PI) / 180) }
  expect(departFromHover({ raFt: 50, headingDeg: 230, groundVelocity: fast, windVelocity: windFrom230 })).toEqual({ refused: true, reason: 'not in the low-speed regime' })
  expect(departFromHover({ raFt: 29, headingDeg: 230, groundVelocity: drift, windVelocity: windFrom230 })).toEqual({ refused: true, reason: 'below minimum use height' })
  expect(departFromHover({ raFt: null, headingDeg: 230, groundVelocity: drift, windVelocity: windFrom230 })).toEqual({ refused: true, reason: 'radio height invalid' })
})

test('a speed axis already at its target has all three events at zero', () => {
  expect(speedEvents(80, 80, 1)).toEqual({ captureS: 0, completionS: 0, arrivalS: 0 })
  expect(speedEvents(81.5, 80, 1)).toEqual({ captureS: 0, completionS: 0.5, arrivalS: 1.5 })
})
