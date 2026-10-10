import { FRAME_SECONDS } from "./kernel/time";
import type { FmsKernel } from "./kernel/kernel";

/** The owning browser's monotonic clock and tasks. Simulation time belongs exclusively to the kernel. */
export interface TickSchedulingHost {
  performance: Pick<Performance, "now">;
  setTimeout(callback: () => void, milliseconds: number): number;
  clearTimeout(id: number): void;
}

/**
 * One finite rate-sized batch, admitted at monotonic deadlines. Slow hosts expire old wall-time admissions rather
 * than queueing catch-up batches. Every admitted frame is whole; cancellation discards only unexecuted work.
 * The 32 ms slice target is checked between frames and cannot preempt an indivisible slow frame.
 */
export function startTickPacing(rate: number, advance: () => boolean, host: TickSchedulingHost, onFault?: (error: unknown) => void, onSlice?: () => void): () => void {
  if (!Number.isSafeInteger(rate) || rate < 1) throw new RangeError("pacing rate must be a positive whole number");
  const period = FRAME_SECONDS * 1000;
  let deadline = host.performance.now() + period;
  let remaining = 0, stopped = false;
  let timer: number | null = null;
  const stop = () => {
    stopped = true; remaining = 0;
    if (timer !== null) host.clearTimeout(timer);
    timer = null;
  };
  const schedule = (delay: number) => { timer = host.setTimeout(wake, delay); };
  const wake = () => {
    timer = null;
    if (stopped) return;
    if (!remaining) {
      const now = host.performance.now();
      if (now < deadline) { schedule(deadline - now); return; }
      remaining = rate;
      // At most this batch is retained. Missed wall deadlines do not become a backlog of simulation batches.
      deadline = Math.max(deadline + period, now);
    }
    const began = host.performance.now();
    try {
      do {
        remaining -= 1;
        if (!advance()) { stop(); return; }
        if (stopped) return;
      } while (remaining > 0 && host.performance.now() - began < 32);
      onSlice?.();
    } catch (error) {
      stop();
      if (onFault) { onFault(error); return; }
      throw error;
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

/** The actual bench driver. Kernel-owned terminal/fault state applies across every task and slice. */
export function startKernelPacing(rate: number, kernel: FmsKernel, host: TickSchedulingHost, options: KernelPacingOptions): () => void {
  const began = host.performance.now();
  const haltOnRunEnd = options.haltOnRunEnd ?? true;
  let completedFrames = 0, lastReport = began;
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
  }, host, error => { cancelProgress(); report(true); options.onFault(error); }, () => report());
  if (options.onProgress) progressTimer = host.setTimeout(progressWake, 250);
  return () => { cancelProgress(); stopFrames(); };
}
