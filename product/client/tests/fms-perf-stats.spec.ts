import { expect, logicTest as test } from './isolated-client-test'
import { abbaOrder, holm, judgeFamily, median, medianLevel, mulberry32, nearestRank, permutationTest, plannedRuns, relativeEffect, type RunValue } from '../perf/stats'

// The performance harness (product/client/perf, #1510) turns runs into the intervals every D10 budget decision rests
// on. These vectors are derived by hand from the definitions, not from the code: nearest-rank percentiles, the ABBA
// order, the exact bootstrap of a three-run sample, blocking, the within-block permutation test (protocol revision 2)
// and its calibration under a null with block effects, and Holm's step-down adjustment.

test('nearest-rank percentiles take the smallest value with at least p% at or below it', () => {
  // ceil(p/100 * n): p5 of 5 -> rank 1, p30 -> 2, p40 -> 2, p50 -> 3, p100 -> 5.
  const five = [50, 15, 40, 20, 35]
  expect([5, 30, 40, 50, 100].map(p => nearestRank(five, p))).toEqual([15, 20, 20, 35, 50])
  // Twenty values 1..20 in a scrambled order: p50 -> rank 10, p95 -> rank 19, p99 -> rank 20.
  const twenty = [7, 19, 3, 12, 20, 1, 16, 9, 5, 14, 2, 18, 11, 6, 13, 4, 17, 8, 15, 10]
  expect([50, 95, 99].map(p => nearestRank(twenty, p))).toEqual([10, 19, 20])
  expect(() => nearestRank(twenty, 0)).toThrow()
  expect(median([3, 1, 2])).toBe(2)
  expect(median([4, 1, 3, 2])).toBe(2.5)
})

test('runs are ordered in ABBA blocks', () => {
  expect(abbaOrder(2).join('')).toBe('ABBAABBA')
})

test('the exact bootstrap of a three-run sample matches its hand enumeration', () => {
  // Arm A runs 1, 2, 4; arm B runs 3, 3, 3; one block. A group of k = 3 runs gives k - 1 = 2 draws, so each arm has
  // 3^2 = 9 equally likely ordered resamples, 81 together. The median of two draws is their mean:
  //   (1,1) 1; (1,2) (2,1) 1.5; (2,2) 2; (1,4) (4,1) 2.5; (2,4) (4,2) 3; (4,4) 4.
  // B's median is always 3, so B/A - 1 = 3/m - 1 takes, over 81: -0.25 (9), 0 (18), 0.2 (18), 0.5 (9), 1 (18), 2 (9).
  // Nearest rank: 2.5% of 81 is rank 3 (-0.25); 97.5% is rank 79, past 72, so 2. At or below zero: 27; at or above:
  // 72. The observed medians are 2 and 3: estimate 0.5; half-width (2 + 0.25) / 2 = 112.5 pp.
  const runs: RunValue[] = [
    ...[1, 2, 4].map(value => ({ block: 0, arm: 'A' as const, value })),
    ...[3, 3, 3].map(value => ({ block: 0, arm: 'B' as const, value })),
  ]
  const effect = relativeEffect(runs, { iterations: 1000, seed: 1 })
  expect(effect).toMatchObject({ exact: true, replicates: 81, estimate: 0.5, low: -0.25, high: 2 })
  expect(effect.halfWidthPp).toBeCloseTo(112.5, 9)
  // One arm alone: the 9 resamples of {1, 2, 4} have medians 1, 1.5, 1.5, 2, 2.5, 2.5, 3, 3, 4; 2.5% is rank 1 (1) and
  // 97.5% is rank 9 (4). Half-width (4 - 1) / 2 = 1.5, which is 75% of the median 2.
  expect(medianLevel([1, 2, 4], { iterations: 1000, seed: 1 }))
    .toMatchObject({ exact: true, replicates: 9, estimate: 2, low: 1, high: 4, halfWidthPct: 75 })
  // Two runs (one per browser round): 2^1 = 2 resamples, medians 10 and 30, around the median 20: half-width 50%.
  expect(medianLevel([10, 30], { iterations: 1000, seed: 1 })).toMatchObject({ replicates: 2, estimate: 20, low: 10, high: 30, halfWidthPct: 50 })
})

test('a two-run group contributes one draw (k - 1) to each bootstrap resample', () => {
  // Block 0: A 10, 12; B 11, 13. With k - 1 = 1 draw per group there are 2 x 2 = 4 resamples, B/A - 1 =
  // 11/10 - 1 = 0.1, 13/10 - 1 = 0.3, 11/12 - 1 = -1/12, 13/12 - 1 = 1/12. Nearest rank over 4: 2.5% is rank 1, 97.5% rank 4.
  // Drawing k = 2 per group instead would give 16 resamples, adding the pair means 11 and 12.
  const runs: RunValue[] = [
    { block: 0, arm: 'A', value: 10 }, { block: 0, arm: 'B', value: 11 }, { block: 0, arm: 'B', value: 13 }, { block: 0, arm: 'A', value: 12 },
  ]
  const effect = relativeEffect(runs, { iterations: 1000, seed: 1 })
  expect(effect).toMatchObject({ exact: true, replicates: 4 })
  expect(effect.low).toBeCloseTo(-1 / 12, 12)
  expect(effect.high).toBeCloseTo(0.3, 12)
})

test('the bootstrap resamples runs within their ABBA block: a drift between blocks adds no width here', () => {
  // Block 0 ran on a quiet host (A 1, 1; B 2, 2), block 1 on a slower one (A 3, 3; B 6, 6). Within each block every
  // draw is that block's value, so every replicate is median(2, 6) / median(1, 3) - 1 = 4 / 2 - 1 = 1.
  // Pooling the blocks before resampling would let a replicate draw A = {1, 1} against B = {6, 6}.
  const runs: RunValue[] = [
    { block: 0, arm: 'A', value: 1 }, { block: 0, arm: 'B', value: 2 }, { block: 0, arm: 'B', value: 2 }, { block: 0, arm: 'A', value: 1 },
    { block: 1, arm: 'A', value: 3 }, { block: 1, arm: 'B', value: 6 }, { block: 1, arm: 'B', value: 6 }, { block: 1, arm: 'A', value: 3 },
  ]
  // 4 groups of 2 runs, one draw each: 2^4 = 16 distinct resamples. 10 iterations draw them; 1000 enumerate them.
  for (const [iterations, exact] of [[10, false], [1000, true]] as const) {
    const effect = relativeEffect(runs, { iterations, seed: 20261006 })
    expect(effect).toMatchObject({ exact, estimate: 1, low: 1, high: 1, halfWidthPp: 0 })
    expect(effect.replicates).toBe(exact ? 16 : 10)
  }
})

test('the drawn bootstrap agrees with the exact enumeration it samples', () => {
  // Six blocks; each arm's two runs in a block differ, so every one of the 2^12 = 4096 resamples (one draw from each
  // of 12 two-run groups) is equally likely and the exact interval is known. 4000 drawn resamples (fewer than 4096,
  // so the drawn path) must land on the same interval within Monte Carlo error. A draw that always took the first
  // value collapses the interval to a point; k draws per group in the drawn path narrows it by about a third.
  const runs: RunValue[] = []
  for (let block = 0; block < 6; block += 1) {
    runs.push({ block, arm: 'A', value: 100 + 3 * block }, { block, arm: 'A', value: 108 + 3 * block })
    runs.push({ block, arm: 'B', value: 101 + 3 * block }, { block, arm: 'B', value: 113 + 3 * block })
  }
  const exact = relativeEffect(runs, { iterations: 5000, seed: 7 })
  const drawn = relativeEffect(runs, { iterations: 4000, seed: 7 })
  expect([exact.exact, exact.replicates, drawn.exact, drawn.replicates]).toEqual([true, 4096, false, 4000])
  expect(drawn.estimate).toBe(exact.estimate)
  expect(Math.abs(drawn.low - exact.low)).toBeLessThan(0.15 * (exact.high - exact.low))
  expect(Math.abs(drawn.high - exact.high)).toBeLessThan(0.15 * (exact.high - exact.low))
  expect(drawn.halfWidthPp / exact.halfWidthPp).toBeGreaterThan(0.85)
  expect(drawn.halfWidthPp / exact.halfWidthPp).toBeLessThan(1.15)
})

test('the within-block permutation test matches its hand enumeration and states its smallest attainable p', () => {
  // One block, A 1, 2 and B 3, 4. The 6 ways to label two of the four runs A, with |ln(median B / median A)|:
  // {1,2} ln(3.5/1.5); {3,4} the same, negated; {1,3} and {2,4} ln 1.5; {1,4} and {2,3} 0. Two of six reach the
  // observed {1,2}: p = 1/3.
  const one: RunValue[] = [{ block: 0, arm: 'A', value: 1 }, { block: 0, arm: 'B', value: 3 }, { block: 0, arm: 'B', value: 4 }, { block: 0, arm: 'A', value: 2 }]
  expect(permutationTest(one, { permutations: 1000, seed: 1 })).toMatchObject({ exact: true, permutations: 6, p: 1 / 3, estimate: 3.5 / 1.5 - 1 })
  // Two such blocks: 36 labellings; observed A = {1, 2, 1, 2} (median 1.5) against B = {3, 4, 3, 4} (3.5). With A = {1, 2}
  // in one block, the other block's A of {1, 2} or {1, 3} keeps 1.5 against 3.5 (A {1, 1, 2, 3}, B {2, 3, 4, 4}), while
  // {1, 4} gives B a median of 3 and the rest raise A's: 3 labellings, and their 3 mirror images: p = 6/36. The design's
  // floor is the observed labelling and its mirror alone: 2/36.
  const two = [...one, ...one.map(run => ({ ...run, block: 1 }))]
  const pair = permutationTest(two, { permutations: 1000, seed: 1 })
  expect(pair).toMatchObject({ exact: true, permutations: 36, smallestP: 2 / 36 })
  expect(pair.p).toBeCloseTo(6 / 36, 12)
  // The declared family: 40 tests at alpha 0.05 need p below 0.05 / 40 = 0.00125 to reject at all. Ten ABBA blocks
  // (6^10 labellings) drawn R = 19999 times reach 1 / 20000; an arm flip per block (two runs, 2^10 = 1024 labellings)
  // cannot go below 2 / 1024, so it could never reject.
  const abba: RunValue[] = [], flip: RunValue[] = []
  for (let block = 0; block < 10; block += 1) {
    ;(['A', 'B', 'B', 'A'] as const).forEach((arm, i) => abba.push({ block, arm, value: 10 + block + i }))
    flip.push({ block, arm: 'A', value: 10 + block }, { block, arm: 'B', value: 11 + block })
  }
  expect(permutationTest(abba, { permutations: 19999, seed: 1 }).smallestP).toBe(1 / 20000)
  // A drawn p is (c + 1) / (R + 1), so it is a whole number of 1 / 20000 steps and never 0, even when, as here (A at
  // 1, 2 and B at 100, 101 in every block), almost no drawn labelling reaches the observed one.
  const extreme: RunValue[] = []
  for (let block = 0; block < 10; block += 1)
    extreme.push({ block, arm: 'A', value: 1 }, { block, arm: 'B', value: 100 }, { block, arm: 'B', value: 101 }, { block, arm: 'A', value: 2 })
  const drawn = permutationTest(extreme, { permutations: 19999, seed: 1 })
  expect(drawn).toMatchObject({ exact: false, permutations: 19999 })
  expect(drawn.p).toBeGreaterThanOrEqual(1 / 20000)
  expect(drawn.p).toBeLessThan(0.00125)
  expect(drawn.p * 20000).toBeCloseTo(Math.round(drawn.p * 20000), 9)
  expect(permutationTest(flip, { permutations: 19999, seed: 1 })).toMatchObject({ exact: true, smallestP: 2 / 1024 })
  expect(() => permutationTest([{ block: 0, arm: 'A', value: 1 }, { block: 0, arm: 'A', value: 2 }], { permutations: 10, seed: 1 })).toThrow()
  // An unbalanced block (A 1, 2; B 3) has 3 labellings and no mirror labelling, so its floor is 1/3, not 2/3.
  const unbalanced: RunValue[] = [{ block: 0, arm: 'A', value: 1 }, { block: 0, arm: 'A', value: 2 }, { block: 0, arm: 'B', value: 3 }]
  expect(permutationTest(unbalanced, { permutations: 100, seed: 1 })).toMatchObject({ exact: true, permutations: 3, smallestP: 1 / 3 })
})

test('an A/A family is judged only when complete, clean and under the statistics committed with it', () => {
  // Synthetic session records and p-values only: no measured data is scored here.
  const statistics = { revision: 2, test: 'within-block permutation', permutations: 19999 }
  const recorded = { permutations: 19999, test: 'within-block permutation', revision: 2 }
  const commit = 'c'.repeat(40)
  const session = { protocol: { statistics: recorded }, committedStatistics: recorded, harness: { commit, dirtyEntries: 0 }, end: { commit, dirtyEntries: 0 } }
  const base = { pilot: false, sessions: [session], statistics, alpha: 0.05, declaredFamilySize: 40 }
  const of = (n: number, p: number) => Array.from({ length: n }, () => p)
  // Complete (40 of 40), a reachable floor (1/20000 < 0.05/40): judged; one Holm p at alpha fails it.
  expect(judgeFamily({ ...base, adjusted: of(40, 1), smallestP: of(40, 1 / 20000) }).verdict).toBe('passed')
  expect(judgeFamily({ ...base, adjusted: [0.05, ...of(39, 1)], smallestP: of(40, 1 / 20000) })).toMatchObject({ verdict: 'failed', significant: 1 })
  // Refused, never passed: one ABBA block's floor (1/3); an empty family (the failed-run rule can empty it); 8 of 40.
  expect(judgeFamily({ ...base, adjusted: of(40, 1), smallestP: of(40, 1 / 3) }).verdict).toBe('refused')
  expect(judgeFamily({ ...base, adjusted: [], smallestP: [] }).verdict).toBe('refused')
  expect(judgeFamily({ ...base, adjusted: of(8, 1), smallestP: of(8, 1 / 20000) }).verdict).toBe('refused')
  // Not judged: another statistics block (revision 1), the same test with a different R, a declaration other than the
  // one committed at the session's harness commit, no session, the smoke override, a dirty start, no end record, an
  // end at another commit or on a dirty tree.
  const judgedWith = (sessions: object[]) => judgeFamily({ ...base, sessions, adjusted: of(40, 1), smallestP: of(40, 1 / 20000) }).verdict
  const otherR = { ...recorded, permutations: 9999 }
  expect(judgedWith([session, { ...session, protocol: { statistics: { iterations: 10000, seed: 20261006 } } }])).toBe('not judged')
  expect(judgedWith([{ ...session, protocol: { statistics: otherR }, committedStatistics: otherR }])).toBe('not judged')
  expect(judgedWith([{ ...session, committedStatistics: otherR }])).toBe('not judged')
  expect(judgedWith([{ ...session, committedStatistics: null }])).toBe('not judged')
  expect(judgedWith([])).toBe('not judged')
  expect(judgedWith([{ ...session, dirtyOverride: true }])).toBe('not judged')
  expect(judgedWith([{ ...session, harness: { commit, dirtyEntries: 2 } }])).toBe('not judged')
  expect(judgedWith([{ ...session, end: null }])).toBe('not judged')
  expect(judgedWith([{ ...session, end: { commit: 'd'.repeat(40), dirtyEntries: 0 } }])).toBe('not judged')
  expect(judgedWith([{ ...session, end: { commit, dirtyEntries: 1 } }])).toBe('not judged')
  expect(judgeFamily({ ...base, pilot: true, adjusted: of(40, 0), smallestP: of(40, 1 / 20000) }).verdict).toBe('pilot')
})

test('under a null with block effects the permutation test rejects at its nominal rate', () => {
  // 400 synthetic A/A sets of ten ABBA blocks: each block has its own level (the host drifting between blocks, up to
  // 50% of the noise-free value) and every run independent noise, with no arm effect. At alpha 0.05 and R = 99 the
  // rejection rate must stay within 3.5 binomial standard deviations (0.011 for 400 sets) of 0.05. This checks gross
  // validity at 0.05 only, not the far tail where Holm's alpha / 40 sits; the tail rests on the exact construction.
  const random = mulberry32(20261006)
  let rejected = 0
  const sets = 400
  for (let n = 0; n < sets; n += 1) {
    const runs: RunValue[] = []
    for (let block = 0; block < 10; block += 1) {
      const level = 1 + 0.5 * random()
      for (const arm of ['A', 'B', 'B', 'A'] as const) runs.push({ block, arm, value: level * (1 + 0.1 * (random() + random() + random() - 1.5)) })
    }
    if (permutationTest(runs, { permutations: 99, seed: n + 1 }).p <= 0.05) rejected += 1
  }
  expect(Math.abs(rejected / sets - 0.05)).toBeLessThan(3.5 * Math.sqrt((0.05 * 0.95) / sets))
})

test('a drawn bootstrap is reproduced by its recorded seed', () => {
  const runs: RunValue[] = []
  for (let block = 0; block < 6; block += 1)
    for (const [i, arm] of (['A', 'B', 'B', 'A'] as const).entries()) runs.push({ block, arm, value: 10 + ((block * 7 + i * 3) % 5) })
  const first = relativeEffect(runs, { iterations: 2000, seed: 42 })
  expect(first.exact).toBe(false)
  expect(relativeEffect(runs, { iterations: 2000, seed: 42 })).toEqual(first)
})

test('Holm adjusts a family step-down and keeps the input order', () => {
  // Sorted 0.005, 0.01, 0.03, 0.04 times 4, 3, 2, 1 = 0.02, 0.03, 0.06, 0.04; monotone: 0.02, 0.03, 0.06, 0.06.
  const adjusted = holm([0.01, 0.04, 0.03, 0.005])
  ;[0.03, 0.06, 0.06, 0.02].forEach((value, i) => expect(adjusted[i]).toBeCloseTo(value, 12))
  // Capped at 1: 0.5 x 2 = 1, then 0.6 x 1 = 0.6 is raised to the running 1.
  expect(holm([0.6, 0.5])).toEqual([1, 1])
})

test('the pilot plans an even number of runs per arm between the pilot and the cap', () => {
  // n = ceil(n0 (h / target)^2): 4 x 4 = 16; 4 x 9 = 36 capped at 20; 4 x 0.25 = 1 raised to the pilot's 4;
  // 4 x 2.25 = 9 rounded up to 10 so each ABBA block stays whole.
  expect(plannedRuns({ halfWidthPp: 2, runsPerArm: 4 }, 1, 20)).toBe(16)
  expect(plannedRuns({ halfWidthPp: 3, runsPerArm: 4 }, 1, 20)).toBe(20)
  expect(plannedRuns({ halfWidthPp: 0.5, runsPerArm: 4 }, 1, 20)).toBe(4)
  expect(plannedRuns({ halfWidthPp: 1.5, runsPerArm: 4 }, 1, 20)).toBe(10)
})
