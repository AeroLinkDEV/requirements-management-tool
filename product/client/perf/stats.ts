// The statistics every performance budget decision in the bus analyzer plan (#1502 D10, #1510) rests on.
//
// The experimental unit is one run: a fresh process (headless) or a fresh browser (browser runner). Ticks inside a run
// are autocorrelated and long-tailed, so they are summarised per run (percentiles) and never resampled. Runs are taken
// in ABBA blocks; the bootstrap resamples runs within each block and arm, so a drift of the host between blocks cancels
// instead of widening or biasing the interval. Effects are relative: B/A - 1 of the arm medians, with A the reference.
//
// Pure functions only: no clock, no randomness except the seeded generator the caller names and records.

export type Arm = 'A' | 'B'

/** One run's value for one measure, with the ABBA block it ran in. */
export type RunValue = { block: number; arm: Arm; value: number }

/** Nearest-rank percentile (0 < p <= 100): the smallest value with at least p% of the values at or below it. */
export function nearestRank(values: readonly number[], p: number): number {
  if (!values.length) throw new Error('nearestRank needs at least one value')
  if (!(p > 0 && p <= 100)) throw new Error(`percentile ${p} is outside (0, 100]`)
  const sorted = [...values].sort((a, b) => a - b)
  const rank = Math.ceil((p / 100) * sorted.length)
  return sorted[Math.max(1, rank) - 1]
}

/** The conventional median: the middle value, or the mean of the two middle values. */
export function median(values: readonly number[]): number {
  if (!values.length) throw new Error('median needs at least one value')
  const sorted = [...values].sort((a, b) => a - b)
  const mid = sorted.length >> 1
  return sorted.length % 2 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2
}

/** The run order for `blocks` ABBA blocks: A B B A, repeated. Each block gives each arm two runs. */
export function abbaOrder(blocks: number): Arm[] {
  if (!Number.isInteger(blocks) || blocks < 1) throw new Error('abbaOrder needs a whole number of blocks')
  const order: Arm[] = []
  for (let b = 0; b < blocks; b += 1) order.push('A', 'B', 'B', 'A')
  return order
}

/** Mulberry32: a small seeded generator in [0, 1). The seed is a reproducibility input recorded with every result. */
export function mulberry32(seed: number): () => number {
  let state = seed >>> 0
  return () => {
    state = (state + 0x6d2b79f5) >>> 0
    let t = state
    t = Math.imul(t ^ (t >>> 15), t | 1)
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61)
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296
  }
}

export type Effect = {
  /** B/A - 1 of the arm medians of the observed runs. */
  estimate: number
  /** 95% percentile bootstrap interval (nearest rank on the replicates). */
  low: number
  high: number
  /** Half the interval width, in percentage points. */
  halfWidthPp: number
  /** Two-sided bootstrap p-value for no effect: 2 x the smaller tail at zero, capped at 1. */
  p: number
  /** Replicates used; exact is true when every distinct resample was enumerated once instead of drawn. */
  replicates: number
  exact: boolean
  seed: number
}

type Group = { arm: Arm; values: number[] }

function groups(runs: readonly RunValue[]): Group[] {
  const byKey = new Map<string, Group>()
  for (const run of runs) {
    if (!Number.isFinite(run.value)) throw new Error(`run value ${run.value} is not finite`)
    const key = `${run.block}:${run.arm}`
    const group = byKey.get(key) ?? { arm: run.arm, values: [] }
    group.values.push(run.value)
    byKey.set(key, group)
  }
  return [...byKey.values()]
}

/** Draws per resample from a group of k runs: k - 1 (at least one). See resample. */
const draws = (group: Group) => Math.max(1, group.values.length - 1)

/**
 * Resamples runs with replacement within each (block, arm) group and evaluates `statistic` on each resample.
 *
 * Each group of k runs contributes k - 1 draws (McCarthy and Snowden's with-replacement bootstrap for stratified
 * samples). An ABBA block gives each arm only two runs, and a size-k resample of a stratum that small understates the
 * variance by the factor (k - 1) / k, a half here, which would make the A/A comparison anti-conservative; k - 1 draws
 * remove that bias for a linear statistic.
 *
 * When the number of distinct resamples (the product of k^(k-1) over groups) is no more than `iterations`, every one is
 * enumerated exactly once (the exact bootstrap); otherwise `iterations` are drawn with the seeded generator.
 */
function resample(list: readonly Group[], statistic: (picks: readonly number[][]) => number, options: { iterations: number; seed: number }) {
  const distinct = list.reduce((total, group) => total * group.values.length ** draws(group), 1)
  const replicates: number[] = []
  if (distinct <= options.iterations) {
    // A mixed-radix counter over every draw position: draw i of group g picks any of that group's k values.
    const positions = list.flatMap((group, g) => Array.from({ length: draws(group) }, () => g))
    const digits = positions.map(() => 0)
    for (let n = 0; n < distinct; n += 1) {
      const picks = list.map(() => [] as number[])
      positions.forEach((g, i) => picks[g].push(list[g].values[digits[i]]))
      replicates.push(statistic(picks))
      for (let i = 0; i < digits.length; i += 1) {
        digits[i] += 1
        if (digits[i] < list[positions[i]].values.length) break
        digits[i] = 0
      }
    }
  } else {
    const random = mulberry32(options.seed)
    for (let n = 0; n < options.iterations; n += 1)
      replicates.push(statistic(list.map(group => Array.from({ length: draws(group) }, () => group.values[Math.floor(random() * group.values.length)]))))
  }
  return { replicates, exact: distinct <= options.iterations }
}

/** The relative effect of arm B against reference arm A, bootstrapped within (block, arm) groups (see resample). */
export function relativeEffect(runs: readonly RunValue[], options: { iterations: number; seed: number }): Effect {
  const list = groups(runs)
  for (const arm of ['A', 'B'] as const) if (!list.some(group => group.arm === arm)) throw new Error(`no runs for arm ${arm}`)
  const pooled = (arm: Arm, picks: readonly number[][]) => list.flatMap((group, g) => group.arm === arm ? picks[g] : [])
  const relative = (picks: readonly number[][]) => median(pooled('B', picks)) / median(pooled('A', picks)) - 1
  const estimate = relative(list.map(group => group.values))
  const { replicates, exact } = resample(list, relative, options)
  const low = nearestRank(replicates, 2.5), high = nearestRank(replicates, 97.5)
  const below = replicates.filter(value => value <= 0).length, above = replicates.filter(value => value >= 0).length
  return {
    estimate, low, high, halfWidthPp: ((high - low) / 2) * 100,
    p: Math.min(1, (2 * Math.min(below, above)) / replicates.length),
    replicates: replicates.length, exact, seed: options.seed,
  }
}

export type Level = { estimate: number; low: number; high: number; halfWidthPct: number | null; replicates: number; exact: boolean; seed: number }

/**
 * One arm's median over runs, with a 95% interval from resampling those runs as one group (n - 1 draws; see resample).
 * An absolute level compares nothing, so there is no block effect to cancel; a browser round holds one run per cell,
 * so blocking it would leave nothing to resample. The half-width is a % of the median (null when the median is 0).
 */
export function medianLevel(values: readonly number[], options: { iterations: number; seed: number }): Level {
  const list = groups(values.map(value => ({ block: 0, arm: 'A' as const, value })))
  const estimate = median(values)
  const { replicates, exact } = resample(list, picks => median(picks.flat()), options)
  const low = nearestRank(replicates, 2.5), high = nearestRank(replicates, 97.5)
  return { estimate, low, high, halfWidthPct: estimate ? ((high - low) / 2 / Math.abs(estimate)) * 100 : null, replicates: replicates.length, exact, seed: options.seed }
}

/** Holm's step-down adjustment of a family of p-values, returned in the input order. */
export function holm(pValues: readonly number[]): number[] {
  const m = pValues.length
  const order = pValues.map((p, i) => ({ p, i })).sort((a, b) => a.p - b.p)
  const adjusted = new Array<number>(m)
  let running = 0
  order.forEach(({ p, i }, rank) => {
    running = Math.max(running, Math.min(1, (m - rank) * p))
    adjusted[i] = running
  })
  return adjusted
}

/**
 * Runs per arm needed for a target half-width, scaling the pilot's half-width by 1/sqrt(n): never fewer than the pilot,
 * never more than the cap, and even, because an ABBA block gives each arm two runs.
 */
export function plannedRuns(pilot: { halfWidthPp: number; runsPerArm: number }, targetPp: number, cap: number): number {
  const needed = Math.ceil(pilot.runsPerArm * (pilot.halfWidthPp / targetPp) ** 2)
  const bounded = Math.min(cap, Math.max(pilot.runsPerArm, needed))
  const even = bounded + (bounded % 2)
  return even > cap ? cap - (cap % 2) : even
}

/** One run's frame durations (milliseconds) summarised by nearest rank. */
export function frameSummary(frames: readonly number[]) {
  return { frames: frames.length, p50: nearestRank(frames, 50), p95: nearestRank(frames, 95), p99: nearestRank(frames, 99), max: Math.max(...frames) }
}
