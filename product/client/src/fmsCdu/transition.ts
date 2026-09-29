/**
 * The CMA transition down to hover: the trajectory the FMS places TDN with and the autopilot flies (plan D-T T5 and
 * T6, as amended by R3-01). One shared contract, so the FMS and the AFCS never compute different stopping distances:
 *
 * - TD: the collective to min(200 ft RA, the radio height at TDN) at 500 fpm, the speed to 80 KIAS at 1.0 kt/s, each
 *   axis on its own, every vertical-speed change limited to 600 fpm/s (so the vertical profile has its ramps). TD ends
 *   when both axes have arrived.
 * - The gate segment: level at the gate height and 80 KIAS, at least 0.20 NM when TDN is placed; it is the only slack.
 * - TD/H: over the ground at 0.75 kt/s to a stop at MRK, the collective to the hover height at 150 fpm.
 *
 * This is the production arithmetic, integrated in small time steps through the same limits the flight simulation
 * applies. The independent test oracle (tests/support/tdnOracle.ts) computes the same contract in closed form; the
 * two are compared, and must not share code. Units: KIAS, kt, ft, fpm, NM, s. The final track is into the wind, so
 * the wind is a headwind along it.
 */

import { tasFromIas } from "./kinematics";
import { ACTIVE_PROFILE } from "./profile";

const P = ACTIVE_PROFILE.parameters;
const VS_RATE = P.verticalAccel.value; // fpm per second
const STEP_S = 0.05;

export type TransitionStart = {
  /** Indicated airspeed at TDN, KIAS. */
  ias: number;
  /** Radio height at TDN, feet, or null when the radio altimeter has no valid height. */
  radioHeight: number | null;
  /** Vertical speed at TDN, fpm. */
  verticalSpeed: number;
  /** The wind component along the final track, blowing against it (kt; the final track is into the wind). */
  headwind: number;
  /** The selected hover height, feet. */
  hoverHeight: number;
  /** Surface elevation under the transition, feet MSL (ISA is taken at elevation plus height). */
  elevation?: number;
};

export type TransitionRefusal = { refused: true; reason: "below gate speed" | "vertical speed limit" | "radio height invalid" | "hover height out of range" | "below minimum use height" | "no closure" };

export type TransitionPlan = {
  refused: false;
  /** Along-track distance flown in TD (NM), its duration (s), and the ground speed at its end (kt). */
  td: { distanceNm: number; seconds: number; gateHeight: number; groundSpeed: number };
  /** TD/H from the gate ground speed to a stop: distance (NM) and duration (s). */
  tdh: { distanceNm: number; seconds: number; hoverHeight: number };
  /** The minimum gate segment and the planned distance from TDN to MRK. */
  gateMinimumNm: number;
  dtraNm: number;
};

/** One vertical step toward a height at a rate, as the autopilot flies it (rate to the stopping distance, then hold). */
function verticalStep(height: number, vs: number, target: number, rate: number, dt: number) {
  const toGo = target - height;
  const stopping = ((Math.abs(rate) / 60) ** 2) / (2 * (VS_RATE / 60)) + 0.5;
  const command = Math.sign(toGo) === Math.sign(rate) && Math.abs(toGo) > stopping ? rate : Math.max(-1000, Math.min(1000, toGo * 10));
  const next = vs + Math.max(-VS_RATE * dt, Math.min(VS_RATE * dt, command - vs));
  return { height: height + (next * dt) / 60, vs: next };
}

/**
 * The planned transition from the state at TDN, or the reason it is refused: below the 80 KIAS gate speed (TD never
 * accelerates), a vertical speed beyond the profile's limit, no valid radio height, a hover height out of its range,
 * or below the minimum use height, or no ground speed toward MRK at the gate (no closure).
 */
export function planTransition(start: TransitionStart): TransitionPlan | TransitionRefusal {
  const elevation = start.elevation ?? 0;
  if (start.radioHeight === null) return { refused: true, reason: "radio height invalid" };
  if (start.ias < P.gateSpeed.value) return { refused: true, reason: "below gate speed" };
  if (Math.abs(start.verticalSpeed) > P.maxVerticalSpeed.value) return { refused: true, reason: "vertical speed limit" };
  if (start.hoverHeight < P.hoverHeightMin.value || start.hoverHeight > P.hoverHeightMax.value) return { refused: true, reason: "hover height out of range" };
  if (start.radioHeight < P.minimumUseHeight.value) return { refused: true, reason: "below minimum use height" };
  const gateHeight = Math.min(P.gateHeight.value, start.radioHeight);
  // A headwind at or above the gate true airspeed leaves no ground speed toward MRK: the transition never closes.
  if (tasFromIas(P.gateSpeed.value, elevation + gateHeight) - start.headwind <= 0) return { refused: true, reason: "no closure" };
  // TD: integrate both axes until each has arrived.
  let t = 0, height = start.radioHeight, vs = start.verticalSpeed, ias = start.ias, distance = 0;
  const arrived = () => Math.abs(height - gateHeight) < 0.05 && Math.abs(vs) < 1 && ias <= P.gateSpeed.value + 1e-9;
  while (!arrived() && t < 3600) {
    const ground = tasFromIas(ias, elevation + height) - start.headwind;
    distance += (ground * STEP_S) / 3600;
    ({ height, vs } = verticalStep(height, vs, gateHeight, -P.tdDescentRate.value, STEP_S));
    ias = Math.max(P.gateSpeed.value, ias - P.tdDeceleration.value * STEP_S);
    t += STEP_S;
  }
  const groundSpeed = tasFromIas(P.gateSpeed.value, elevation + gateHeight) - start.headwind;
  // TD/H: a constant ground deceleration to a stop (closed form for this piece: a stop from GS at a).
  const rate = P.tdhDeceleration.value;
  const tdh = { distanceNm: (groundSpeed * groundSpeed) / (2 * rate * 3600), seconds: groundSpeed / rate, hoverHeight: Math.min(start.hoverHeight, gateHeight) };
  return {
    refused: false,
    td: { distanceNm: distance, seconds: t, gateHeight, groundSpeed },
    tdh,
    gateMinimumNm: P.gateSegmentMinimum.value,
    dtraNm: distance + P.gateSegmentMinimum.value + tdh.distanceNm,
  };
}

export type TdnDecision = { engage: true; gateNm: number; plan: TransitionPlan } | { engage: false; reason: string; gateNm: number | null };

/**
 * T6: at TDN the transition is recomputed from the actual state against the fixed MRK. gate = remaining − D(TD) −
 * D(TD/H) at full precision: at or above zero it engages (the slack is the gate segment); below zero it is refused
 * (TDN DIST SHORT). MRK never moves.
 */
export function checkAtTdn(start: TransitionStart, remainingNm: number): TdnDecision {
  const plan = planTransition(start);
  if (plan.refused) return { engage: false, reason: plan.reason.toUpperCase(), gateNm: null };
  const gateNm = remainingNm - plan.td.distanceNm - plan.tdh.distanceNm;
  return gateNm >= 0 ? { engage: true, gateNm, plan } : { engage: false, reason: "TDN DIST SHORT", gateNm };
}


