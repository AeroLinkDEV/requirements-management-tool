import type { ScenarioRunner } from "../scenario";
import type { LegacyPlant } from "./legacyPlantAdapter";
import { FRAME_UNITS, type KernelTime } from "./time";

/**
 * The phases at one instant, in their authoritative order (#1502 D1 3.2). Within a phase, events are ordered by class:
 * ACTION by submissionSeq; DELIVER by (channel registry index, receiver branch, transmitter epoch, channel seq);
 * DEADLINE by (deadline kind, owner registry index, creation seq); COMPUTE and SCHEDULE by unit (FMS1, FMS2, AFCS);
 * SAMPLE by (producer registry index, producer epoch, sample seq); TXSTART by (channel registry index, transmitter
 * epoch, channel seq). ACTION, COMPUTE, SCHEDULE and INTEGRATE occur at frame instants only.
 */
export const PHASES = ["ACTION", "DELIVER", "DEADLINE", "COMPUTE", "SCHEDULE", "INTEGRATE", "SAMPLE", "TXSTART"] as const;
export type Phase = (typeof PHASES)[number];

/** Whatever a run's ACTION phase polls: the v1 scenario runner, whose steps and checks are translated into ACTION. */
type ActionSource = Pick<ScenarioRunner, "poll" | "finished">;

export type AdvanceOptions = {
  /** A logical run may span browser tasks: refuse further integration after its terminal ACTION. */
  readonly haltOnRunEnd?: boolean;
  /** Flight freeze (D1 3.8): INTEGRATE holds the aircraft while the clock and every other phase run on. */
  readonly flightFreeze?: boolean;
  /** Called as each instant F_j closes, immediately before its INTEGRATE: where the frame digest D_j is taken. */
  readonly onInstantClose?: () => void;
};

/**
 * The simulation kernel (#1517 I1a). It owns kernel time and the order of events; the plant and both computers are
 * still inside the legacy plant adapter (T17), so of the phases only ACTION and INTEGRATE have events in I1:
 *
 * - **ACTION at F_j** runs the runner's poll: the translated v1 steps and checks due at F_j, in step order. Live
 *   submissions (I1b) due at F_j would come first, in submissionSeq order; there are none in I1a.
 * - **R_j**, the resting point between calls to advance(), is the still-open ACTION at F_j (D1 3.8a, the T17 legacy
 *   exception for I1 and I2): an input made there executes at F_j, after the poll and before F_j's INTEGRATE. In I1a
 *   the bench's controls still call the units directly there; I1b routes them through submit().
 * - **INTEGRATE at F_j** is the legacy composite: tick j+1's clock move and step, exactly once, for [F_j, F_{j+1}].
 * - DELIVER, DEADLINE, COMPUTE, SCHEDULE, SAMPLE and TXSTART have no events until units leave the adapter (D5 7.5).
 *
 * Once a later phase at an instant has begun, its ACTION is closed and never reopens.
 */
export class FmsKernel {
  private readonly plant: LegacyPlant;
  private readonly runner: ActionSource | null;
  private time: KernelTime;
  private failed = false;

  /**
   * Starts at the plant's present instant with ACTION open. A runner is constructed in ACTION at F_0: its surface, its
   * start state's placement and its t = 0 poll have already run.
   */
  constructor(plant: LegacyPlant, runner: ActionSource | null = null) {
    if (plant.clock.time % FRAME_UNITS !== 0) throw new RangeError("the kernel starts at a frame instant");
    this.plant = plant;
    this.runner = runner;
    this.time = plant.clock.time;
  }

  /** Kernel time: the instant F_j whose ACTION is open. */
  get now(): KernelTime { return this.time; }
  /** The units' clock, epoch milliseconds: equal to UTC0 + kernel time except while INTEGRATE runs. */
  get unitClockMs() { return this.plant.clock.ms; }
  /**
   * An INTEGRATE threw: kernel time moved on to F_{j+1}, the units' clock already reads it, and the poll at F_{j+1} did
   * not run. An input made now executes in that open ACTION, before the next INTEGRATE; the skipped poll stays skipped
   * (no extra poll, no repeated composite). Cleared when an INTEGRATE completes.
   */
  get faulted() { return this.failed; }
  /** The runner's terminal ACTION is open; deliberate free flight may still use ordinary advance(). */
  get runFinished() { return this.runner?.finished ?? false; }

  /**
   * Runs `frames` frames from the open ACTION at F_j: close F_j, INTEGRATE, then ACTION at F_{j+1}, and so on. A run that
   * ends in an ACTION halts there (the run-end halt): no INTEGRATE follows within this call. Returns the frames run.
   */
  advance(frames: number, options: AdvanceOptions = {}) {
    if (!Number.isSafeInteger(frames) || frames < 0) throw new RangeError(`advance needs a whole number of frames, not ${frames}`);
    if (options.haltOnRunEnd && this.runFinished) return 0;
    const hold = this.plant.hold;
    if (options.flightFreeze && !hold) throw new Error("this composition has no flight freeze");
    const running = this.runner !== null && !this.runner.finished;
    for (let done = 0; done < frames; done += 1) {
      for (const phase of PHASES) {
        switch (phase) {
          case "ACTION":
            // F_j's ACTION ran when the instant opened; it closes here, before any later phase begins.
            options.onInstantClose?.();
            break;
          case "INTEGRATE":
            try {
              if (options.flightFreeze) hold!(); else this.plant.integrate();
            } catch (error) {
              // The composite moved the units' clock before it threw: kernel time follows it, and F_{j+1}'s ACTION is
              // open without its poll, exactly where today's state rests after the throw.
              this.time += FRAME_UNITS;
              this.failed = true;
              throw error;
            }
            this.failed = false;
            break;
          default:
            // No events before units leave the adapter.
            break;
        }
      }
      this.time += FRAME_UNITS;
      // ACTION at F_{j+1}: the translated v1 steps and checks due now, in step order.
      this.runner?.poll();
      if (running && this.runner!.finished) return done + 1;
    }
    return frames;
  }
}
