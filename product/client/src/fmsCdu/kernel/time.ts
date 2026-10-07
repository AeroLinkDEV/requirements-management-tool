/**
 * Kernel time (#1502 D1 3.1): an integer count of 100 ns units since the run epoch. It never goes backwards and never
 * skips; "strictly after t" is t + 1 unit. A unit's wall-clock reading is UTC0 plus kernel time, in milliseconds.
 */
export type KernelTime = number;

export const UNITS_PER_MS = 10_000;
export const UNITS_PER_SECOND = 10_000_000;
/** One frame, Δ = 0.25 s: the instants F_j = j × FRAME_UNITS at which actions, computations and the plant run. */
export const FRAME_UNITS = 2_500_000;
export const FRAME_SECONDS = FRAME_UNITS / UNITS_PER_SECOND;

/** UTC0 + t as epoch milliseconds. Exact while UTC0 is a whole millisecond and t a whole number of milliseconds. */
export const epochMs = (utc0Ms: number, time: KernelTime) => utc0Ms + time / UNITS_PER_MS;
