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
  /** Plan C1: "receiver" when the accuracy is the receiver's own figure (the GPS HFOM); "laboratory" when any contributor
   * is a declared model or allowance; null with no accuracy. A laboratory value is a simulator estimate, not a validated
   * 95% bound. */
  accuracyBasis: "receiver" | "laboratory" | null;
  /** Plan C1 (transitive provenance): whether the solution depends, directly or through any input it was derived from,
   * on GPS. A GPS-dependent solution is never C1's GPS-independent NAIM backup. A newer epoch does not clear it. */
  gpsDependent: boolean;
  /** The integrity bound NP, NM; null when the sensor gives none or the data is insufficient (civil option, M300 1-3). */
  integrityNm: number | null;
  integrityBasis: IntegrityBasis;
  /** The laboratory NAIM comparison (plan C1, F5): |GPS - backup| + the backup's 95% accuracy, NM, when an uncertain GPS
   * was judged against a qualifying radio fix. It decides only whether the uncertain GPS may stay selected; it is never
   * the integrity bound and never gives integrity. */
  naimComparisonNm: number | null;
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

/**
 * Plan C4: the Stage F output vocabulary, declared with F2 so every later item names its values the same way. F13 turns
 * each entry into a tag in the exhaustive output catalogue (#1345, #1376) once that lands; this list neither duplicates
 * the catalogue nor is read by the EFIS. Units are NM unless stated; each value carries a validity (NORMAL, NCD, FAIL).
 */
export const NAV_OUTPUT_VOCABULARY = {
  /** Every navigation mode the bench may annunciate (configured or not: F0 guards which exist). */
  modes: ["GPS", "DME/DME", "VOR/DME", "VOR/DME/TCN", "KALMAN", "DVS", "DR"],
  source: ["gpsReceiver", "dmeIdents", "vorIdent", "tacanChannel", "owningComputer"],
  performance: ["accuracy95Nm", "accuracyBasis", "integrityBoundNm", "integrityValid", "uncertain", "naimComparisonNm", "gpsDependent", "rnp", "phase"],
  annunciations: ["INT", "GPS integrity lost", "Unable RNP", "POS"],
  radios: ["activeFrequency", "commandStatus", "controlPath", "measurementBus", "receiver", "stationIdent", "vorRadial", "dmeDistance", "tacanBearing", "tacanDistance", "adfBearing"],
  cdi: ["fullScaleNm"],
} as const;
