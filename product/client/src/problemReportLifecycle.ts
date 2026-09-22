/**
 * The Problem Report lifecycle, for presentation only.
 *
 * The order mirrors `ProblemReportTransitionPolicy.CanonicalStates` in AeroLink.Domain, which is the
 * authority. This list decides where a state sits on the rail and nothing else: which transitions a
 * reader may perform is always the server's answer, arriving as `capabilities.availableTransitions`.
 * Never derive an offered action from this array — a state being adjacent here does not make its edge
 * allowed, authorized, or effective.
 *
 * `Rejected` is deliberately absent. It is terminal and off-path: a report that reaches it left the
 * lifecycle from wherever it was, so painting it as an eighth step would claim a progression that never
 * happened.
 */

export const CANONICAL_STATES = [
  "Draft",
  "ReadyForSccb",
  "Open",
  "Implementing",
  "Verifying",
  "WaitingForSqaToClose",
  "Closed",
] as const;

export type CanonicalState = (typeof CANONICAL_STATES)[number];

const LABELS: Record<string, string> = {
  Draft: "Draft",
  ReadyForSccb: "Ready for SCCB",
  Open: "Open",
  Implementing: "Implementing",
  Verifying: "Verifying",
  WaitingForSqaToClose: "Waiting for SQA to Close",
  Closed: "Closed",
  Rejected: "Rejected",
};

/** Spaces a PascalCase state for display when it is not one this build knows by name. */
const spaced = (value: string) => value.replace(/([a-z])([A-Z])/g, "$1 $2");

export const stateLabel = (state: string) => LABELS[state] ?? spaced(state);

/** Position on the rail, or -1 for a state that is not on it (Rejected, or one added server-side). */
export const stateIndex = (state: string) =>
  (CANONICAL_STATES as readonly string[]).indexOf(state);

export const isOnRail = (state: string) => stateIndex(state) >= 0;
