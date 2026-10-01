import { NAV_MODES, type NavMode } from "./navigation";
import type { RadioFix } from "./radioNavigation";
import { withinLimit, type SensorSolution } from "./sensorState";

/**
 * Mode selection and transitions (Stage F plan F3; M300 1-3 to 1-5, 12-1). The ordered resolver, first match wins:
 *   0. candidates: available, fresh, not deselected, phase-eligible (radio modes not in the approach phase, 15-3);
 *   1. GPS with integrity (selected on integrity, not accuracy, 1-4);
 *   2. the uncertain-GPS retention exception (no qualifying backup, or the laboratory NAIM comparison strictly below
 *      the RNP), which precedes step 3;
 *   3. radio modes with integrity: the accuracy comparator with 100 m hysteresis (none for VOR/DME to DME/DME);
 *   4. no integrity anywhere: civil priority (approved radio, KALMAN, DVS, DR).
 * CivilNavigation applies steps 1, 2 and 4; chooseRadio is step 3. Reversions fall out of the order and are immediate.
 */
export const MODE_HYSTERESIS_M = 100;

/** Priority order, highest first (M300 1-4 and 12-1 as equipped; DEC-150 places DVS below KALMAN). */
const PRIORITY = Object.fromEntries(NAV_MODES.map((mode, index) => [mode, index])) as Record<NavMode, number>;

/** The hysteresis of an accuracy-based transition from one radio mode to another, in metres (M300 1-3). */
export const accuracyHysteresisM = (from: NavMode, to: NavMode) => (from === "VOR/DME" && to === "DME/DME" ? 0 : MODE_HYSTERESIS_M);

/**
 * Chooses the radio solution to navigate on from this update's radio fixes. With integrity (95% accuracy strictly inside
 * the RNP), the most accurate wins, but the radio mode already in use is kept until another is better by the hysteresis.
 * A mode that has lost integrity is left at once. Without any radio integrity, the highest-priority fix is returned.
 */
export function chooseRadio(fixes: readonly RadioFix[], current: NavMode, rnp: number): RadioFix | null {
  const qualified = fixes.filter(fix => withinLimit(fix.anp, rnp))
    .sort((a, b) => a.anp - b.anp || PRIORITY[a.mode] - PRIORITY[b.mode]);
  if (qualified.length === 0) return [...fixes].sort((a, b) => PRIORITY[a.mode] - PRIORITY[b.mode] || a.anp - b.anp)[0] ?? null;
  const best = qualified[0], held = qualified.find(fix => fix.mode === current);
  if (!held || held.mode === best.mode) return best;
  return (held.anp - best.anp) * 1852 >= accuracyHysteresisM(held.mode, best.mode) ? best : held;
}

/** Whether a mode's solution can still be navigated on: available, and with integrity unless it never has any. */
export const usable = (sensor: SensorSolution | undefined) =>
  sensor !== undefined && sensor.available && (sensor.integrity || sensor.integrityBasis === "none");

/**
 * The alert of a transition (M300 Appendix E): leaving a mode for a lower one because it can no longer be navigated on
 * raises that mode's NAV LOST. A lower mode chosen only for accuracy (DME/DME to VOR/DME) and any move up raise none.
 */
export function transitionAlert(from: NavMode, to: NavMode, sensors: readonly SensorSolution[],
  causes: { vorDmeReceiversFailed?: boolean } = {}): string | null {
  if (from === to || from === "DR" || PRIORITY[to] < PRIORITY[from]) return null;
  if (usable(sensors.find(sensor => sensor.mode === from))) return null;
  // E-17: VOR/DME NAV LOST is a VOR and/or DME receiver failure on the onside and offside computers; losing the mode for
  // any other reason (a deselected or out-of-range station) raises nothing.
  if (from === "VOR/DME" && !causes.vorDmeReceiversFailed) return null;
  return `${from} NAV LOST`;
}

export type ModeTransition = { from: NavMode; to: NavMode;
  /** What makes the FMS change mode. */
  trigger: string;
  /** The accuracy hysteresis applied, metres (0 where the transition is not accuracy-based or is exempt). */
  hysteresisM: number;
  /** The alert raised, if any, and any condition it needs beyond the mode becoming unusable. */
  message: string | null;
  messageCondition?: string;
  /** Where the new mode's position comes from: its own measurement (a step the guidance sees, M300 12-16), the
   * emulated INS's propagated position, or the previous solution carried on. */
  continuity: "measured" | "emulated INS" | "continued" };

const RADIO = new Set<NavMode>(["DME/DME", "VOR/DME"]);
const continuity = (to: NavMode): ModeTransition["continuity"] =>
  to === "KALMAN" ? "emulated INS" : to === "DVS" || to === "DR" ? "continued" : "measured";

/** The transition table: every ordered pair of equipped modes (plan F3 exit condition). */
export const MODE_TRANSITIONS: readonly ModeTransition[] = NAV_MODES.flatMap(from => NAV_MODES.filter(to => to !== from).map(to => {
  const up = PRIORITY[to] < PRIORITY[from];
  const accuracy = RADIO.has(from) && RADIO.has(to);
  const trigger = accuracy
    ? up ? `${to} with integrity is more accurate, or ${from} loses integrity or availability`
      : `${to} with integrity is at least ${MODE_HYSTERESIS_M} m more accurate, or ${from} loses integrity or availability`
    : up ? `${to} becomes available${to === "GPS" ? " with integrity" : ""}`
      : `${from} loses ${from === "KALMAN" || from === "DVS" || from === "DR" ? "availability" : "integrity or availability"}`;
  return { from, to, trigger, hysteresisM: accuracy ? accuracyHysteresisM(from, to) : 0,
    message: up || from === "DR" ? null : `${from} NAV LOST`,
    ...(!up && from === "VOR/DME" ? { messageCondition: "its VOR or DME receivers have failed on both computers (E-17)" } : {}),
    continuity: continuity(to) };
}));
