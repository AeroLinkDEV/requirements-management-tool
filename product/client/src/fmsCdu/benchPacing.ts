import { FRAME_SECONDS } from "./kernel/time";
import type { FmsKernel } from "./kernel/kernel";

/** The owning browser's monotonic clock and tasks. Simulation time belongs exclusively to the kernel. */
export interface TickSchedulingHost {
  /** `measure`/`clearMeasures` are optional: a host with them receives the pacing timeline (PACING_TIMELINE). */
  performance: Pick<Performance, "now"> & Partial<Pick<Performance, "measure" | "clearMeasures">>;
  setTimeout(callback: () => void, milliseconds: number): number;
  clearTimeout(id: number): void;
}

/** One task's run of frame attempts. `admitted` is the batch admitted at its start (0 for a continuation). */
export type TickSlice = { readonly batch: number; readonly admitted: number; readonly attempted: number; readonly remaining: number;
  readonly began: number; readonly ended: number };
/** Admitted frames that will never be attempted: the batch was cancelled, the run ended or a frame faulted. */
export type TickDiscard = { readonly batch: number; readonly discarded: number; readonly at: number };
export type TickTrace = { readonly slice: (slice: TickSlice) => void; readonly discard: (discard: TickDiscard) => void };

/**
 * One finite rate-sized batch, admitted at monotonic deadlines. Slow hosts expire old wall-time admissions rather
 * than queueing catch-up batches. Every admitted frame is whole; cancellation discards only unexecuted work.
 * The 32 ms slice target is checked between frames and cannot preempt an indivisible slow frame.
 * `trace` sees every slice that attempted a frame (however it ended) and every discard, so admitted frames are
 * conserved: admitted = attempted + discarded + still outstanding.
 */
export function startTickPacing(rate: number, advance: () => boolean, host: TickSchedulingHost, onFault?: (error: unknown) => void, onSlice?: () => void, trace?: TickTrace): () => void {
  if (!Number.isSafeInteger(rate) || rate < 1) throw new RangeError("pacing rate must be a positive whole number");
  const period = FRAME_SECONDS * 1000;
  let deadline = host.performance.now() + period;
  let remaining = 0, stopped = false, batch = 0;
  // A stop inside a slice is traced after that slice, so each trace entry leaves the outstanding debt it reports.
  let waking = false, discarded = 0;
  let timer: number | null = null;
  const discard = () => {
    if (discarded > 0) trace?.discard({ batch, discarded, at: host.performance.now() });
    discarded = 0;
  };
  const stop = () => {
    discarded += remaining;
    stopped = true; remaining = 0;
    if (timer !== null) host.clearTimeout(timer);
    timer = null;
    if (!waking) discard();
  };
  const schedule = (delay: number) => { timer = host.setTimeout(wake, delay); };
  const wake = () => {
    timer = null;
    if (stopped) return;
    let admitted = 0;
    if (!remaining) {
      const now = host.performance.now();
      if (now < deadline) { schedule(deadline - now); return; }
      remaining = admitted = rate; batch += 1;
      // At most this batch is retained. Missed wall deadlines do not become a backlog of simulation batches.
      deadline = Math.max(deadline + period, now);
    }
    const began = host.performance.now();
    let attempted = 0;
    waking = true;
    try {
      do {
        remaining -= 1; attempted += 1;
        if (!advance()) { stop(); return; }
        if (stopped) return;
      } while (remaining > 0 && host.performance.now() - began < 32);
      onSlice?.();
    } catch (error) {
      stop();
      if (onFault) { onFault(error); return; }
      throw error;
    } finally {
      waking = false;
      trace?.slice({ batch, admitted, attempted, remaining: remaining + discarded, began, ended: host.performance.now() });
      discard();
    }
    // A task yield services input/painting without depending on visible-window animation frames.
    schedule(remaining ? 0 : Math.max(0, deadline - host.performance.now()));
  };
  schedule(period);
  return stop;
}

export type PacingProgress = { readonly completedFrames: number; readonly wallMilliseconds: number; readonly achievedRate: number };
export type KernelPacingOptions = {
  /** True for a scenario run; false for a deliberate Fly after that run already ended. */
  readonly haltOnRunEnd?: boolean;
  readonly onInstantClose?: () => void;
  readonly frame?: <T>(action: () => T) => T;
  readonly onProgress?: (progress: PacingProgress) => void;
  readonly onStopped?: () => void;
  readonly onFault: (error: unknown) => void;
};

/**
 * The pacing timeline (#1557): User Timing measures on the host's performance timeline, visible in a browser
 * profiler's Timings track and to any PerformanceObserver of type "measure". One SLICE per task that attempted
 * frames, spanning that task's frame attempts, and one DISCARD per batch remainder that will never be attempted.
 * Details are PacingSliceDetail / PacingDiscardDetail. Within one epoch (one Fly), batches number from 1 and each
 * entry leaves the outstanding debt it reports, so a reader can conserve admitted = attempted + discarded + debt.
 * Each measure is cleared from the performance entry buffer as soon as it is queued: observers still receive it,
 * and a long session does not grow the buffer.
 */
export const PACING_TIMELINE = { schema: 1, slice: "aerolink.fms-bench.pacing-slice", discard: "aerolink.fms-bench.pacing-discard" } as const;
export type PacingSliceDetail = { readonly schema: 1; readonly epoch: number; readonly rate: number; readonly batch: number;
  readonly admitted: number; readonly attempted: number; readonly completed: number; readonly remaining: number };
export type PacingDiscardDetail = { readonly schema: 1; readonly epoch: number; readonly rate: number; readonly batch: number; readonly discarded: number };
let pacingEpochs = 0;

function pacingTimeline(rate: number, host: TickSchedulingHost, completedSinceLastSlice: () => number): TickTrace | undefined {
  const { measure, clearMeasures } = host.performance;
  if (!measure || !clearMeasures) return undefined;
  const epoch = ++pacingEpochs;
  const emit = (name: string, start: number, end: number, detail: PacingSliceDetail | PacingDiscardDetail) => {
    // Observation must never fault the simulation; a lost entry breaks the reader's conservation instead.
    try { measure.call(host.performance, name, { start, end, detail }); clearMeasures.call(host.performance, name); } catch { /* reader refuses */ }
  };
  return {
    slice: ({ batch, admitted, attempted, remaining, began, ended }) => emit(PACING_TIMELINE.slice, began, ended,
      { schema: PACING_TIMELINE.schema, epoch, rate, batch, admitted, attempted, completed: completedSinceLastSlice(), remaining }),
    discard: ({ batch, discarded, at }) => emit(PACING_TIMELINE.discard, at, at,
      { schema: PACING_TIMELINE.schema, epoch, rate, batch, discarded }),
  };
}

/** The actual bench driver. Kernel-owned terminal/fault state applies across every task and slice. */
export function startKernelPacing(rate: number, kernel: FmsKernel, host: TickSchedulingHost, options: KernelPacingOptions): () => void {
  const began = host.performance.now();
  const haltOnRunEnd = options.haltOnRunEnd ?? true;
  let completedFrames = 0, traced = 0, lastReport = began;
  // Completion instants belong to this Fly epoch. They count successful whole frames only, not admitted debt.
  const completions: number[] = [];
  let progressTimer: number | null = null, stopped = false;
  const report = (force = false) => {
    const now = host.performance.now();
    if (!force && now - lastReport < 250) return;
    lastReport = now;
    const wallMilliseconds = Math.max(0, now - began);
    const windowStart = Math.max(began, now - 3000);
    while (completions.length && completions[0] <= windowStart) completions.shift();
    const windowMilliseconds = Math.max(0, now - windowStart);
    options.onProgress?.({ completedFrames, wallMilliseconds,
      achievedRate: windowMilliseconds > 0 ? completions.length * FRAME_SECONDS * 1000 / windowMilliseconds : 0 });
  };
  const cancelProgress = () => {
    stopped = true;
    if (progressTimer !== null) host.clearTimeout(progressTimer);
    progressTimer = null;
  };
  const progressWake = () => {
    progressTimer = null;
    if (stopped) return;
    // A late wake expires old work even if no new INTEGRATE has completed. This task never advances the kernel.
    report();
    progressTimer = host.setTimeout(progressWake, 250);
  };
  const stopFrames = startTickPacing(rate, () => {
    const advance = () => kernel.advance(1, { haltOnRunEnd, onInstantClose: options.onInstantClose });
    const done = options.frame ? options.frame(advance) : advance();
    // A throwing INTEGRATE moves kernel.now but never returns a completed frame, so it is never counted here.
    completedFrames += done;
    for (let i = 0; i < done; i++) completions.push(host.performance.now());
    const ended = haltOnRunEnd && kernel.runFinished;
    if (ended) report(true);
    if (done === 0 || ended) cancelProgress();
    if (ended) options.onStopped?.();
    return done > 0 && !ended;
  }, host, error => { cancelProgress(); report(true); options.onFault(error); }, () => report(), pacingTimeline(rate, host, () => {
    const done = completedFrames - traced; traced = completedFrames; return done;
  }));
  if (options.onProgress) progressTimer = host.setTimeout(progressWake, 250);
  return () => { cancelProgress(); stopFrames(); };
}
