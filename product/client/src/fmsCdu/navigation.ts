/**
 * Shared phase defaults and the laboratory radio service volume. The measured position estimator is in
 * civilNavigation.ts; radioNavigation.ts consumes timestamped observations. No civil inertial unit is configured.
 * Phase defaults and time-to-alert remain representative bench values, not a CMA installation's approval table.
 */

/** The navigation modes the estimator can select. Stage F adds modes here only with the equipment that provides them
 * (configuration.ts STAGE_F_SENSORS, plan F0). */
export const NAV_MODES = ["GPS", "DME/DME", "VOR/DME", "KALMAN", "DVS", "DR"] as const;
export type NavMode = typeof NAV_MODES[number];
export type FlightPhase = "EN ROUTE" | "TERMINAL" | "APPROACH";

/** Default RNP and time to alert by phase of flight (seconds). */
export const RNP_DEFAULTS: Record<FlightPhase, { rnp: number; alertSeconds: number }> = {
  "EN ROUTE": { rnp: 2.0, alertSeconds: 80 },
  TERMINAL: { rnp: 1.0, alertSeconds: 60 },
  APPROACH: { rnp: 0.3, alertSeconds: 10 },
};

/** Radio line of sight in NM from an altitude in feet, capped at the typical DME service range. */
export const radioRange = (altitude: number) => Math.min(160, 1.23 * Math.sqrt(Math.max(0, altitude)));
