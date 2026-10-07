import { DualFmsSystem } from "../dualFms";
import { FlightSimulator } from "../flight";
import { ScriptedFms } from "../scriptedFms";
import { FRAME_SECONDS, FRAME_UNITS, epochMs, type KernelTime } from "./time";

/**
 * The legacy plant adapter, T17 (#1502 D5 7.5; #1517 I1). Until a unit leaves it (FMS COMPUTE in I3, AFCS and the
 * plant in I5a), today's composite step runs whole as the kernel's INTEGRATE event: at F_j it runs what was tick j+1's
 * L1 (the unit clock moves to F_{j+1}) and L2 (`step(0.25)`), exactly once. That is the declared internal-clock
 * exception: while it runs, the units' clock reads F_{j+1} and kernel time is still F_j, as today's one-frame skew.
 * Nothing else may step a FlightSimulator or DualFmsSystem, or move a unit clock.
 */
export class LegacyUnitClock {
  /** The run epoch's UTC, epoch milliseconds (initial state). */
  readonly utc0Ms: number;
  private units: KernelTime = 0;
  constructor(utc0Ms: number) {
    // Whole milliseconds keep UTC0 + t exact, as today's millisecond clock was.
    if (!Number.isSafeInteger(utc0Ms)) throw new RangeError(`UTC0 must be a whole number of epoch milliseconds, not ${utc0Ms}`);
    this.utc0Ms = utc0Ms;
  }
  /** What the units read: the adapter's clock, F_{j+1} while INTEGRATE at F_j runs. */
  get time(): KernelTime { return this.units; }
  get ms() { return epochMs(this.utc0Ms, this.units); }
  readonly now = () => new Date(this.ms);
  /** L1: the clock moves one frame. */
  advanceFrame() { this.units += FRAME_UNITS; }
}

/** One composition's legacy step, run as INTEGRATE. */
export interface LegacyPlant {
  readonly clock: LegacyUnitClock;
  /** INTEGRATE while flying: L1, then L2 (`step(0.25)`). */
  integrate(): void;
  /** INTEGRATE in a flight freeze: L1, then the computers tick while the aircraft stands still. Absent where unsupported. */
  readonly hold?: () => void;
}

type FmsOptions = NonNullable<ConstructorParameters<typeof ScriptedFms>[1]>;
type DualOptions = NonNullable<ConstructorParameters<typeof DualFmsSystem>[1]>;

/** runHeadless's composition: one computer and its flight simulator. It has no flight freeze. */
export function singleComposition(utc0Ms: number, options: FmsOptions = {}) {
  const clock = new LegacyUnitClock(utc0Ms);
  const fms = new ScriptedFms(clock.now, options);
  const sim = new FlightSimulator(fms);
  const plant: LegacyPlant = { clock, integrate: () => { clock.advanceFrame(); sim.step(FRAME_SECONDS); } };
  return { clock, fms, sim, plant };
}

/** The bench's composition: two computers, one aircraft (DualFmsSystem). */
export function dualComposition(utc0Ms: number, options: DualOptions = {}) {
  const clock = new LegacyUnitClock(utc0Ms);
  const system = new DualFmsSystem(clock.now, options);
  const plant: LegacyPlant = {
    clock,
    integrate: () => { clock.advanceFrame(); system.step(FRAME_SECONDS); },
    hold: () => { clock.advanceFrame(); system.tick(); },
  };
  return { clock, system, plant };
}
