/**
 * The holding pattern as a path over the ground (plan D-H; M300 10-1…10-16 for the entries, speeds and timing).
 *
 * The racetrack is built at the first fix passage from the true airspeed and the wind, and rebuilt at each fix passage
 * (M300 10-8): parallel inbound and outbound legs joined by half circles. The turn radius is sized for the fastest
 * ground speed the turn will see (TAS plus the wind speed) at the design bank: the rate-one bank at that speed, or the
 * bank limit if less. The bank then stays at or below the design value all round the turn, whatever the wind. This
 * conservative construction is a laboratory choice, not the OEM algorithm. When the wind is at least the airspeed the
 * pattern cannot be flown at all: UNABLE HOLD (laboratory; the manuals are silent).
 *
 * Distances NM, angles degrees true, speeds knots.
 */

import { offset, type LatLon } from "./fmsModel";
import { ACTIVE_PROFILE, type AircraftProfile } from "./profile";

const rad = (deg: number) => (deg * Math.PI) / 180;
const deg = (r: number) => (r * 180) / Math.PI;
const norm360 = (a: number) => ((a % 360) + 360) % 360;
const G_FT_S2 = 32.174;
const KT_FT_S = 1.68781;
/** Rate one: 3 degrees per second. */
const RATE_ONE = 3;

export type HoldSegment =
  | { kind: "line"; from: LatLon; to: LatLon }
  | { kind: "arc"; centre: LatLon; to: LatLon; turn: "L" | "R"; radius: number };

export type HoldGeometry = {
  /** The turn radius and the bank it is designed for. */
  radius: number;
  designBank: number;
  /** The length of the straight legs. */
  legNm: number;
  /** The racetrack from the fix: outbound turn, outbound leg, inbound turn, inbound leg back to the fix. */
  racetrack: HoldSegment[];
  /** The far end of the inbound leg, where the inbound turn ends. */
  inboundStart: LatLon;
};

/** The design bank: rate one at the fastest ground speed, capped by the bank limit. */
export function designBank(fastestGroundSpeed: number, bankLimit: number) {
  const rateOne = deg(Math.atan((fastestGroundSpeed * KT_FT_S * rad(RATE_ONE)) / G_FT_S2));
  return Math.min(rateOne, bankLimit);
}

/** The turn radius (NM) at a ground speed (kt) and bank (degrees). */
export const radiusAt = (groundSpeed: number, bank: number) => ((groundSpeed * KT_FT_S) ** 2 / (G_FT_S2 * Math.tan(rad(bank)))) / 6076.12;

/**
 * The racetrack for a hold at `fix`, inbound course `inbound` (true), turning `turn`, at a true airspeed in a wind,
 * with a leg of `legNm` (a distance leg as given; a timed leg is its time at the true airspeed, a still-air length).
 * Null when the pattern cannot be flown (the wind at least the airspeed).
 */
export function holdGeometry(fix: LatLon, inbound: number, turn: "LEFT" | "RIGHT", tas: number, windSpeed: number, legNm: number,
  bankLimit = ACTIVE_PROFILE.parameters.afcsBankLimit.value): HoldGeometry | null {
  if (windSpeed >= tas || tas <= 0) return null;
  const fastest = tas + windSpeed;
  const bank = designBank(fastest, bankLimit);
  const radius = radiusAt(fastest, bank);
  const s = turn === "RIGHT" ? 1 : -1;
  const side = norm360(inbound + 90 * s);
  const outbound = norm360(inbound + 180);
  const c1 = offset(fix, side, radius);
  const a = offset(fix, side, 2 * radius);
  const b = offset(a, outbound, legNm);
  const c2 = offset(b, norm360(side + 180), radius);
  const d = offset(b, norm360(side + 180), 2 * radius);
  const t = turn === "RIGHT" ? "R" : "L";
  return {
    radius, designBank: bank, legNm, inboundStart: d,
    racetrack: [
      { kind: "arc", centre: c1, to: a, turn: t, radius },
      { kind: "line", from: a, to: b },
      { kind: "arc", centre: c2, to: d, turn: t, radius },
      { kind: "line", from: d, to: fix },
    ],
  };
}

/**
 * The entry from the fix (M300 10-2, 10-4, 10-6): DIRECT goes straight into the racetrack. TEARDROP flies outbound on
 * a ground track 40 degrees from the reciprocal of the inbound course toward the holding side, for the leg, then turns
 * in the holding direction to intercept the inbound leg. PARALLEL flies outbound on the reciprocal of the inbound
 * course on the non-holding side, for about 2.6 turn radii (M300 10-2), then turns back through the holding side to
 * intercept the inbound leg. The intercepts end on the inbound leg, which is flown to the fix.
 */
export function entrySegments(kind: "DIRECT" | "TEARDROP" | "PARALLEL", fix: LatLon, inbound: number, turn: "LEFT" | "RIGHT", geometry: HoldGeometry): HoldSegment[] {
  if (kind === "DIRECT") return [];
  const s = turn === "RIGHT" ? 1 : -1;
  const outbound = norm360(inbound + 180);
  const intercept: HoldSegment = { kind: "line", from: geometry.inboundStart, to: fix };
  if (kind === "TEARDROP") {
    const end = offset(fix, norm360(outbound - 40 * s), geometry.legNm);
    return [{ kind: "line", from: fix, to: end }, intercept];
  }
  const end = offset(fix, outbound, 2.6 * geometry.radius);
  return [{ kind: "line", from: fix, to: end }, intercept];
}

/**
 * The holding speed limit at an altitude: for the helicopter, M300 10-8 Table 10-1 (the 6,000 ft boundary to the
 * lower row; none published above 14,000 ft); for the laboratory airline profile, the bench's former 230 kt.
 */
export const AIRLINE_HOLDING_SPEED = 230;
export function holdingSpeedLimit(altitudeFt: number, profile: AircraftProfile = ACTIVE_PROFILE): number | null {
  if (profile.verticalPolicy !== "ADVISORY") return AIRLINE_HOLDING_SPEED;
  const p = profile.parameters;
  if (altitudeFt <= 6000) return p.holdingSpeedLow.value;
  if (altitudeFt <= 14000) return p.holdingSpeedHigh.value;
  return null;
}

/** The default leg time: 1.0 minute at or below 14,000 ft, 1.5 above, fixed when the entry begins (M300 10-9). */
export const defaultLegMinutes = (altitudeFt: number) => (altitudeFt <= 14000 ? 1.0 : 1.5);
