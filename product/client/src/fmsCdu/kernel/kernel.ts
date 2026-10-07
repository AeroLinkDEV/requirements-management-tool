import type { ScenarioRunner, Translation } from "../scenario";
import { executeAction, type ActionContext, type ActionSource, type ActionTarget, type KernelAction, type KernelOutcome } from "./actions";
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
export type ActionRunner = Pick<ScenarioRunner, "poll" | "finished" | "takeTranslations" | "runId" | "abandon">;

export type AdvanceOptions = {
  /** Called as each instant F_j closes, immediately before its INTEGRATE: where the frame digest D_j is taken. */
  readonly onInstantClose?: () => void;
};

/** The receipt `submit()` returns at once (D5 7.1). It carries no outcome: that is published on the event stream. */
export type SubmissionReceipt = { readonly submissionSeq: number };

/**
 * One executed action (D5 7.1). `recordOrdinal` orders it with the control records; `orderInPhase` within its ACTION.
 * A v1 step or check carries the kind it was translated to (`scenario.step`, `scenario.check`, `ios.place`).
 */
export type JournalRecord = {
  readonly recordOrdinal: number;
  readonly submissionSeq: number;
  readonly frame: number;
  readonly phase: "ACTION";
  readonly orderInPhase: number;
  readonly origin: "native" | "v1-translated";
  readonly source: ActionSource;
  readonly target: ActionTarget;
  readonly kind: KernelAction["kind"] | Translation["kind"];
  /** As executed: a toggle or a "guidance" target resolved, so replay needs nothing but the record. */
  readonly payload: unknown;
  readonly outcome: KernelOutcome;
  /** The digest of the action's effects: the kernel digests no state before I1c, so it is null in I1b. */
  readonly effectsDigest: null;
};

/**
 * Where a control record falls relative to actions and kernel execution (D1 3.8a): the instant, the cursor (the phase,
 * and where in it), and `recordOrdinal`, the one ordinal the control records share with the journal.
 */
export type ControlCursor = {
  readonly phase: Phase;
  /**
   * rest: R_j, the open ACTION between calls; run-end: the open ACTION after a run ended; faulted: the open ACTION after
   * a failed INTEGRATE; during: inside the phase (an INTEGRATE that threw).
   */
  readonly boundary: "rest" | "run-end" | "faulted" | "during";
};
type ControlBase = { readonly recordOrdinal: number; readonly controlSeq: number; readonly instant: KernelTime; readonly cursor: ControlCursor };
/**
 * A control-plane record (D1 3.8a). It never allocates a submissionSeq. Queue-full and discarded records reference the
 * submission they concern; their shape is defined now and they are produced with the live queue, which is deferred to
 * its first real production caller.
 */
export type ControlRecord = ControlBase & (
  | { readonly kind: "halt" | "resume" | "reset" }
  | { readonly kind: "rate"; readonly rate: number }
  | { readonly kind: "execution-error"; readonly message: string }
  | { readonly kind: "queue-full"; readonly submissionSeq: number; readonly source: ActionSource }
  | { readonly kind: "discarded"; readonly submissionSeq: number; readonly reason: "restore" | "reset" }
);
export type ControlOperation = { readonly kind: "halt" | "resume" | "reset" } | { readonly kind: "rate"; readonly rate: number };

/** The live queue's state (D5 7.1): explicit and versioned, and always empty in I1 (the queue is deferred). */
export type LiveQueueState = { readonly v: 1; readonly entries: readonly never[] };

/** An outcome as the kernel publishes it (D5 7.1), with the payload as executed. The UI shows outcomes from this. */
export type OutcomeEvent = { readonly kind: "outcome"; readonly submissionSeq: number; readonly frame: number; readonly outcome: KernelOutcome; readonly payload: unknown };
export type KernelEvent = OutcomeEvent | { readonly kind: "control"; readonly record: ControlRecord } | { readonly kind: "freeze"; readonly on: boolean };

/** The kernel's ordering record, as exported: the journal, the control records, the live queue and the counters. */
export type KernelRecord = {
  readonly v: 1;
  /** The frame the kernel rests at: F_frame's ACTION is open. */
  readonly frame: number;
  readonly journal: readonly JournalRecord[];
  readonly control: readonly ControlRecord[];
  readonly liveQueue: LiveQueueState;
  /** The next value of each authoritative ordering counter. */
  readonly counters: { readonly submissionSeq: number; readonly controlSeq: number; readonly recordOrdinal: number };
};

export type KernelOptions = {
  /** The run's initial flight freeze (the bench starting paused): INTEGRATE holds the aircraft until it is released. */
  readonly flightFreeze?: boolean;
};

const EMPTY_LIVE_QUEUE: LiveQueueState = Object.freeze({ v: 1, entries: Object.freeze([]) as readonly never[] });

type Pending = { readonly submissionSeq: number; readonly action: KernelAction; readonly source: ActionSource };

/**
 * The simulation kernel (#1517 I1a, I1b). It owns kernel time, the order of events, the submission boundary and the
 * journal. The plant and both computers are still inside the legacy plant adapter (T17), so of the phases only ACTION
 * and INTEGRATE have events in I1:
 *
 * - **ACTION at F_j** (the ACTION rule, D1 3.8a): pending submissions due at F_j in submissionSeq order, then the
 *   runner's poll (the translated v1 steps and checks due at F_j, in step order), then the submissions made at R_j.
 * - **R_j**, the resting point between calls to advance(), is the still-open ACTION at F_j (the T17 legacy exception for
 *   I1 and I2): `submit()` executes there at once, after the poll and before F_j's INTEGRATE. An operator pause, the
 *   run-end halt and the faulted state are open ACTIONs too: input executes there, and nothing scheduled runs because
 *   of it (no poll, no INTEGRATE).
 * - **INTEGRATE at F_j** is the legacy composite: tick j+1's clock move and step, exactly once, for [F_j, F_{j+1}]; in a
 *   flight freeze, a hold.
 * - DELIVER, DEADLINE, COMPUTE, SCHEDULE, SAMPLE and TXSTART have no events until units leave the adapter (D5 7.5).
 *
 * Once a later phase at an instant has begun, its ACTION is closed and never reopens: an input made then is pending,
 * for the next instant's ACTION. Fly/resume, halt, rate and reset are control-plane operations: they never allocate a
 * submissionSeq, and each control record carries its controlSeq and replay position.
 */
export class FmsKernel {
  private readonly plant: LegacyPlant;
  private runner: ActionRunner | null = null;
  private time: KernelTime;
  private failed = false;
  /** Whether the ACTION at the present instant is open (no later phase at it has begun). */
  private open = true;
  private executing = false;
  private frozen: boolean;
  private stopped = false;
  private advanced = false;
  private readonly pending: Pending[] = [];
  private readonly records: JournalRecord[] = [];
  private readonly controls: ControlRecord[] = [];
  private nextSubmission = 1;
  private nextControl = 1;
  private nextOrdinal = 1;
  private orderFrame = -1;
  private order = 0;
  private readonly listeners = new Set<(event: KernelEvent) => void>();
  private readonly context: ActionContext;

  /**
   * Starts at the plant's present instant with ACTION open. A runner given here was constructed in ACTION at F_0: its
   * start state's placement and its t = 0 poll have already run, and are journaled now (run()).
   */
  constructor(plant: LegacyPlant, runner: ActionRunner | null = null, options: KernelOptions = {}) {
    if (plant.clock.time % FRAME_UNITS !== 0) throw new RangeError("the kernel starts at a frame instant");
    if (options.flightFreeze && !plant.hold) throw new Error("this composition has no flight freeze");
    this.plant = plant;
    this.time = plant.clock.time;
    this.frozen = options.flightFreeze === true;
    // eslint-disable-next-line @typescript-eslint/no-this-alias
    const kernel = this;
    this.context = {
      units: plant.units,
      get runner() { return kernel.runner; },
      canFreeze: plant.hold !== undefined,
      setFlightFreeze: on => { kernel.frozen = on; kernel.emit({ kind: "freeze", on }); },
    };
    if (runner) this.run(runner);
  }

  /** Kernel time: the instant F_j whose ACTION is open. */
  get now(): KernelTime { return this.time; }
  /** The index j of that instant F_j. */
  get frame() { return this.time / FRAME_UNITS; }
  /** The units' clock, epoch milliseconds: equal to UTC0 + kernel time except while INTEGRATE runs. */
  get unitClockMs() { return this.plant.clock.ms; }
  /**
   * An INTEGRATE threw: kernel time moved on to F_{j+1}, the units' clock already reads it, and the poll at F_{j+1} did
   * not run. An input made now executes in that open ACTION, before the next INTEGRATE; the skipped poll stays skipped
   * (no extra poll, no repeated composite). Cleared when an INTEGRATE completes.
   */
  get faulted() { return this.failed; }
  /** The flight freeze (journaled `ios.flightFreeze`): INTEGRATE holds the aircraft while the clock runs. */
  get flightFreeze() { return this.frozen; }
  /** An operator pause during a run (control plane): advance() runs nothing until resume. */
  get halted() { return this.stopped; }
  /** The live queue's state: the queue is deferred to its first real production caller, so it is always empty in I1. */
  get liveQueue(): LiveQueueState { return EMPTY_LIVE_QUEUE; }
  get journal(): readonly JournalRecord[] { return this.records; }
  get controlRecords(): readonly ControlRecord[] { return this.controls; }

  /** The ordering record as plain data: journal, control records, live queue and counters (the bench exports it). */
  record(): KernelRecord {
    return structuredClone({
      v: 1 as const, frame: this.frame, journal: this.records, control: this.controls, liveQueue: this.liveQueue,
      counters: { submissionSeq: this.nextSubmission, controlSeq: this.nextControl, recordOrdinal: this.nextOrdinal },
    });
  }

  /** Listens to the kernel's event stream: outcomes, control records and freeze changes. Returns the unsubscribe. */
  subscribe(listener: (event: KernelEvent) => void): () => void {
    this.listeners.add(listener);
    return () => { this.listeners.delete(listener); };
  }

  /**
   * Starts a v1 run in the open ACTION at F_0, before the kernel has advanced. What the runner's construction ran (its
   * start state's placement, its t = 0 poll) is journaled now, as ACTION at F_0.
   */
  run(runner: ActionRunner) {
    if (this.runner) throw new Error("the kernel already has a run");
    if (this.advanced) throw new Error("a run starts at F_0, before the kernel advances");
    this.runner = runner;
    this.journalTranslations();
  }

  /**
   * The submission boundary (D5 7.1): the only way outside code changes simulation state. The receipt carries no
   * outcome. The action executes in the open ACTION at once (R_j, an operator pause, the run-end halt or the faulted
   * state), after anything already pending there; once a later phase has begun it is pending, for the next ACTION.
   */
  submit(action: KernelAction, source: ActionSource): SubmissionReceipt {
    const submissionSeq = this.nextSubmission++;
    this.pending.push({ submissionSeq, action: structuredClone(action), source: { ...source } });
    if (this.open && !this.executing) this.executePending();
    return { submissionSeq };
  }

  /** A control-plane operation (D1 3.8a): fly/resume, halt, rate or reset. It never allocates a submissionSeq. */
  control(operation: ControlOperation): ControlRecord {
    switch (operation.kind) {
      case "halt":
        if (this.stopped) throw new Error("the kernel is already halted");
        if (!this.runner || this.runner.finished) throw new Error("only a run in progress halts; without one a pause is a flight freeze");
        this.stopped = true;
        break;
      case "resume":
        if (!this.stopped) throw new Error("the kernel is not halted");
        this.stopped = false;
        break;
      case "rate":
        if (!Number.isSafeInteger(operation.rate) || operation.rate < 1) throw new RangeError(`a rate is a whole number of frames per callback, not ${operation.rate}`);
        break;
      case "reset": break;
    }
    const cursor: ControlCursor = { phase: "ACTION", boundary: this.failed ? "faulted" : this.runner?.finished ? "run-end" : "rest" };
    return this.writeControl(operation.kind === "rate" ? { kind: "rate", rate: operation.rate } : { kind: operation.kind }, cursor);
  }

  /**
   * Runs `frames` frames from the open ACTION at F_j: close F_j, INTEGRATE, then ACTION at F_{j+1}, and so on. A run that
   * ends in an ACTION halts there (the run-end halt): no INTEGRATE follows within this call. Halted during a run, it runs
   * nothing. In a flight freeze, or in a halt that outlived its run (the bench's paused bench after a stop), INTEGRATE
   * holds the aircraft. Returns the frames run.
   */
  advance(frames: number, options: AdvanceOptions = {}) {
    if (!Number.isSafeInteger(frames) || frames < 0) throw new RangeError(`advance needs a whole number of frames, not ${frames}`);
    if (this.executing) throw new Error("advance() called by an executing action");
    const running = this.runner !== null && !this.runner.finished;
    if (this.stopped && running) return 0;
    const holding = this.frozen || this.stopped;
    if (holding && !this.plant.hold) throw new Error("this composition has no flight freeze");
    // Input left pending at an open ACTION (one made while the last INTEGRATE ran, before it threw) goes first.
    if (this.open) this.executePending();
    for (let done = 0; done < frames; done += 1) {
      this.advanced = true;
      for (const phase of PHASES) {
        switch (phase) {
          case "ACTION":
            // F_j's ACTION ran when the instant opened; it closes here, before any later phase begins.
            this.open = false;
            options.onInstantClose?.();
            break;
          case "INTEGRATE":
            try {
              if (holding) this.plant.hold!(); else this.plant.integrate();
            } catch (error) {
              // The composite moved the units' clock before it threw: kernel time follows it, and F_{j+1}'s ACTION is
              // open without its poll, exactly where today's state rests after the throw.
              this.writeControl({ kind: "execution-error", message: error instanceof Error ? error.message : String(error) }, { phase: "INTEGRATE", boundary: "during" });
              this.time += FRAME_UNITS;
              this.failed = true;
              this.open = true;
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
      // ACTION at F_{j+1}: what was submitted while F_j's later phases ran, then the translated v1 steps and checks due.
      this.open = true;
      this.executePending();
      if (this.runner) { this.runner.poll(); this.journalTranslations(); }
      if (running && this.runner!.finished) return done + 1;
    }
    return frames;
  }

  private executePending() {
    while (this.pending.length) {
      const { submissionSeq, action, source } = this.pending.shift()!;
      this.executing = true;
      let execution;
      try { execution = executeAction(this.context, action); } finally { this.executing = false; }
      this.writeJournal({ submissionSeq, origin: "native", source, kind: action.kind, payload: execution.payload, outcome: execution.outcome, target: execution.target });
      // An action can end the run (scenario.stop); nothing the runner ran is left unjournaled.
      this.journalTranslations();
    }
  }

  private journalTranslations() {
    const runner = this.runner;
    if (!runner) return;
    for (const translation of runner.takeTranslations()) {
      const id = translation.step === undefined ? `${runner.runId} start` : `${runner.runId} step ${translation.step}`;
      this.writeJournal({
        submissionSeq: this.nextSubmission++, origin: "v1-translated", source: { kind: "scenario", id, surface: "main" },
        kind: translation.kind, payload: translation.payload, outcome: translation.outcome, target: translation.target,
      });
    }
  }

  private writeJournal(entry: Pick<JournalRecord, "submissionSeq" | "origin" | "source" | "kind" | "payload" | "outcome" | "target">) {
    const frame = this.frame;
    if (frame !== this.orderFrame) { this.orderFrame = frame; this.order = 0; }
    const record: JournalRecord = { recordOrdinal: this.nextOrdinal++, frame, phase: "ACTION", orderInPhase: this.order++, ...entry, effectsDigest: null };
    this.records.push(record);
    this.emit({ kind: "outcome", submissionSeq: record.submissionSeq, frame, outcome: record.outcome, payload: record.payload });
  }

  private writeControl(entry: { kind: "halt" | "resume" | "reset" } | { kind: "rate"; rate: number } | { kind: "execution-error"; message: string }, cursor: ControlCursor) {
    const record = { recordOrdinal: this.nextOrdinal++, controlSeq: this.nextControl++, instant: this.time, cursor, ...entry } as ControlRecord;
    this.controls.push(record);
    this.emit({ kind: "control", record });
    return record;
  }

  private emit(event: KernelEvent) { for (const listener of this.listeners) listener(event); }
}
