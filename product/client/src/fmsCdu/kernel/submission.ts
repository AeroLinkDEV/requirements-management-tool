import type { FmsSide } from "../crossTalk";
import type { CduBackend } from "../screen";
import type { FmsView } from "../scriptedFms";
import type { ActionSource, KernelAction } from "./actions";
import type { FmsKernel, OutcomeEvent, SubmissionReceipt } from "./kernel";

/** Who a component says submitted: the kind of source and the control; the bench adds the surface it is on. */
export type ControlSource = { readonly kind: ActionSource["kind"]; readonly id: string; readonly side?: FmsSide };

/**
 * How the bench's components change the simulation (#1517 I1b): they submit an action and, when they show what came
 * of it, are told the outcome from the kernel's event stream. A component is given this and read-only views, so it has
 * no other way to change anything.
 */
export type Submit = (action: KernelAction, source: ControlSource, then?: (event: OutcomeEvent) => void) => void;

/**
 * The bench's side of the submission boundary: submits to the kernel and hands each submission's outcome event to the
 * caller that asked for it. In I1 an action at R_j executes inside submit(), so its event arrives before the receipt
 * does; one that is pending (submitted while a later phase ran) arrives when it executes.
 */
export class SubmissionPort {
  private readonly kernel: FmsKernel;
  private readonly waiting = new Map<number, (event: OutcomeEvent) => void>();
  private arrived: OutcomeEvent[] | null = null;

  constructor(kernel: FmsKernel) {
    this.kernel = kernel;
    kernel.subscribe(event => {
      if (event.kind !== "outcome") return;
      const then = this.waiting.get(event.submissionSeq);
      if (then) { this.waiting.delete(event.submissionSeq); then(event); } else this.arrived?.push(event);
    });
  }

  submit(action: KernelAction, source: ActionSource, then?: (event: OutcomeEvent) => void): SubmissionReceipt {
    const arrived: OutcomeEvent[] = [];
    this.arrived = arrived;
    let receipt: SubmissionReceipt;
    try { receipt = this.kernel.submit(action, source); } finally { this.arrived = null; }
    if (then) {
      const event = arrived.find(entry => entry.submissionSeq === receipt.submissionSeq);
      if (event) then(event); else this.waiting.set(receipt.submissionSeq, then);
    }
    return receipt;
  }
}

/**
 * A CDU whose keys are submitted to the kernel as `cdu.key` (D5 7.3), with the side, the held flag and the surface the
 * panel is on; the screen, lamps and brightness are read from the computer's view. The panel drives it exactly as it
 * drove the computer.
 */
export function kernelCdu(fms: FmsView, side: FmsSide, submit: (action: KernelAction, source: ActionSource) => void, surface: () => ActionSource["surface"]): CduBackend {
  return {
    press: (fn, options = {}) => submit({ kind: "cdu.key", side, fn, held: options.held === true }, { kind: "cdu", id: `CDU ${side}`, surface: surface(), side }),
    screen: () => fms.screen(),
    lamps: () => fms.lamps(),
    brightness: () => fms.brightness(),
    subscribe: listener => fms.subscribe(listener),
    revision: () => fms.revision(),
  };
}
