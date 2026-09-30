import type { FlightPhase, NavMode } from "./navigation";

/**
 * Stage F plan F2 (M300 1-3, 15-1 to 15-4): a navigation sensor solution's availability, 95% accuracy, integrity bound
 * and phase eligibility are separate facts. One ANP number no longer stands for all four.
 */
export type PhaseEligibility = Readonly<Record<FlightPhase, boolean>>;

/**
 * How a solution's integrity is established (M300 1-3): by its integrity bound NP against the active error limit; by
 * other criteria where NP does not cover every variable (the radio modes' reasonableness checks, M300 15-3); or never
 * (dead reckoning).
 */
export type IntegrityBasis = "NP" | "criteria" | "none";

export type SensorSolution = {
  mode: NavMode;
  /** Enough data to compute the solution and no critical failure (M300 1-3). */
  available: boolean;
  /** The 95% radial position error, NM (M300 15-1); null when unknown. */
  accuracy95Nm: number | null;
  /** The integrity bound NP, NM; null when the sensor gives none or the data is insufficient (civil option, M300 1-3). */
  integrityNm: number | null;
  integrityBasis: IntegrityBasis;
  /** Whether the solution has integrity against the active limit, judged by its basis. */
  integrity: boolean;
  eligibility: PhaseEligibility;
};

/** "Less than the active error limit" (M300 1-3): strictly. A bound equal to the limit is not within it. */
export const withinLimit = (value: number | null, limit: number) =>
  value !== null && Number.isFinite(value) && Number.isFinite(limit) && value < limit;

const ALL: PhaseEligibility = { "EN ROUTE": true, TERMINAL: true, APPROACH: true };
const NOT_APPROACH: PhaseEligibility = { "EN ROUTE": true, TERMINAL: true, APPROACH: false };

/** Phase eligibility by mode. DME/DME and VOR/DME are not available for approach (M300 15-3); DR never is. */
export const ELIGIBILITY: Readonly<Record<NavMode, PhaseEligibility>> = {
  GPS: ALL,
  "DME/DME": NOT_APPROACH,
  "VOR/DME": NOT_APPROACH,
  DR: NOT_APPROACH,
};
