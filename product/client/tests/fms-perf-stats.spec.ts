import { expect, logicTest as test } from './isolated-client-test'
import { abbaOrder, holm, median, medianLevel, nearestRank, plannedRuns, relativeEffect, type RunValue } from '../perf/stats'

// The performance harness (product/client/perf, #1510) turns runs into the intervals every D10 budget decision rests
// on. These vectors are derived by hand from the definitions, not from the code: nearest-rank percentiles, the ABBA
// order, the exact bootstrap of a three-run sample, blocking, and Holm's step-down adjustment.

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
  // 72; p = 2 x 27 / 81 = 2/3. The observed medians are 2 and 3: estimate 0.5; half-width (2 + 0.25) / 2 = 112.5 pp.
  const runs: RunValue[] = [
    ...[1, 2, 4].map(value => ({ block: 0, arm: 'A' as const, value })),
    ...[3, 3, 3].map(value => ({ block: 0, arm: 'B' as const, value })),
  ]
  const effect = relativeEffect(runs, { iterations: 1000, seed: 1 })
  expect(effect).toMatchObject({ exact: true, replicates: 81, estimate: 0.5, low: -0.25, high: 2 })
  expect(effect.p).toBeCloseTo(2 / 3, 12)
  expect(effect.halfWidthPp).toBeCloseTo(112.5, 9)
  // One arm alone: the 9 resamples of {1, 2, 4} have medians 1, 1.5, 1.5, 2, 2.5, 2.5, 3, 3, 4; 2.5% is rank 1 (1) and
  // 97.5% is rank 9 (4). Half-width (4 - 1) / 2 = 1.5, which is 75% of the median 2.
  expect(medianLevel([1, 2, 4], { iterations: 1000, seed: 1 }))
    .toMatchObject({ exact: true, replicates: 9, estimate: 2, low: 1, high: 4, halfWidthPct: 75 })
  // Two runs (one per browser round): 2^1 = 2 resamples, medians 10 and 30, around the median 20: half-width 50%.
  expect(medianLevel([10, 30], { iterations: 1000, seed: 1 })).toMatchObject({ replicates: 2, estimate: 20, low: 10, high: 30, halfWidthPct: 50 })
})

test('two runs per arm per block are resampled one at a time, so their spread is not understated', () => {
  // Block 0: A 10, 12; B 11, 13. With k - 1 = 1 draw per group there are 2 x 2 = 4 resamples, B/A - 1 =
  // 11/10 - 1 = 0.1, 13/10 - 1 = 0.3, 11/12 - 1 = -1/12, 13/12 - 1 = 1/12. Nearest rank over 4: 2.5% is rank 1, 97.5% rank 4.
  // Drawing k = 2 per group instead would give 16 resamples with the pair means 11 and 12, 5 of them at or below zero
  // (p = 0.625): a resample as large as the stratum halves the variance of a two-run group.
  const runs: RunValue[] = [
    { block: 0, arm: 'A', value: 10 }, { block: 0, arm: 'B', value: 11 }, { block: 0, arm: 'B', value: 13 }, { block: 0, arm: 'A', value: 12 },
  ]
  const effect = relativeEffect(runs, { iterations: 1000, seed: 1 })
  expect(effect).toMatchObject({ exact: true, replicates: 4 })
  expect(effect.low).toBeCloseTo(-1 / 12, 12)
  expect(effect.high).toBeCloseTo(0.3, 12)
  // At or below zero: 1 of 4; p = 2 x 1 / 4.
  expect(effect.p).toBe(0.5)
})

test('the bootstrap resamples runs within their ABBA block, so a drift between blocks adds no width', () => {
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
    expect(effect).toMatchObject({ exact, estimate: 1, low: 1, high: 1, halfWidthPp: 0, p: 0 })
    expect(effect.replicates).toBe(exact ? 16 : 10)
  }
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
