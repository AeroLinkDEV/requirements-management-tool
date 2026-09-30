import { MAX_BANK } from "./flight";
import { bearingDeg, distanceNm, holdEntry, type Hold, type HoldEntry, type LatLon, type Route } from "./fmsModel";
import { defaultLegMinutes, entrySegments, holdGeometry, type HoldSegment } from "./holds";
import { predictedGroundSpeed, tasFromIas, type Wind } from "./kinematics";
import type { NavDatabase } from "./navData";
import { findProcedure } from "./procedures";
import type { EndpointKind } from "./vnav";

/**
 * Where the route's destination-type predictions end (plan C.11 and R3-03): the one place that decides it, for the
 * profile, the FUEL and PROGRESS pages and NOT ENOUGH FUEL.
 *
 * - An executed approach with imported endpoint data (Stage C, `Procedure.endpoint`) decides it: the landing site
 *   when it is a heliport or airport the route flies to as a waypoint (a crew direct-to 87N), SITE ARRIVAL there; a
 *   runway site, SITE ARRIVAL at its threshold; otherwise INSTRUMENT END at the missed approach point, shown as the MAP
 *   ("CRANN (MAP)"), never as the heliport.
 * - Without endpoint data (the demonstration approaches, or no approach): the first runway threshold in the route
 *   (SITE ARRIVAL), else the route's destination airport or heliport when it is a waypoint (SITE ARRIVAL), else none.
 *
 * Only legs before the missed approach count: a missed approach fix is never an endpoint. There is no LANDING
 * endpoint in v1 (no landing allowance is declared).
 */
export function predictionEndpoint(route: Route, db: NavDatabase): { legIndex: number; kind: EndpointKind; label: string } | null {
  const at = (test: (ident: string) => boolean) =>
    route.legs.findIndex(leg => leg.kind === "wpt" && leg.source !== "MISSED" && test(leg.ident));
  const site = (ident: string) => db.airport(ident) !== undefined;
  const runway = (ident: string) => /^RW\d{2}[LRC]?$/.test(ident);
  const found = (legIndex: number, kind: EndpointKind, label: string) => (legIndex >= 0 ? { legIndex, kind, label } : null);
  const endpoint = findProcedure(db, route, "APPROACH")?.endpoint;
  if (endpoint) {
    const { landingSite, instrumentEnd } = endpoint;
    if (landingSite.kind !== "RUNWAY") {
      const direct = at(ident => ident === landingSite.ident);
      if (direct >= 0) return { legIndex: direct, kind: "SITE ARRIVAL", label: landingSite.ident };
    } else {
      const threshold = at(ident => ident === landingSite.ident);
      if (threshold >= 0) return { legIndex: threshold, kind: "SITE ARRIVAL", label: `${landingSite.ident} (THR)` };
    }
    const map = at(ident => ident === instrumentEnd.fix);
    if (map >= 0) return { legIndex: map, kind: "INSTRUMENT END", label: `${instrumentEnd.fix} (MAP)` };
  }
  const threshold = at(runway);
  if (threshold >= 0) return found(threshold, "SITE ARRIVAL", `${(route.legs[threshold] as { ident: string }).ident} (THR)`);
  const destination = at(ident => ident === route.dest && site(ident));
  return found(destination, "SITE ARRIVAL", route.dest);
}

/** A straight piece of a ground path: its length, NM, and ground track, degrees true. */
export type PathPiece = { distance: number; course: number };

/**
 * The hold path the flight is flying (flight.ts): its entry and racetrack segments, the one being flown, and the one
 * that ends at the next fix passage (the end of the entry, or of the racetrack).
 */
export type HoldPathReport = { segments: HoldSegment[]; index: number; passageAt: number };

const ARC_STEP_DEG = 5;
const norm360 = (degrees: number) => ((degrees % 360) + 360) % 360;

/**
 * The ground path left from the aircraft to the hold's next fix passage (Astra F1, S300 5-17 "ETA at the next
 * crossing"): the rest of the segment being flown, then every segment up to the one ending at the passage. Straight
 * segments are one piece; turns are pieces of at most 5 degrees, each on its tangent track, so a caller can time every
 * piece at its own ground speed and the wind's effect on each part of the circuit counts (a leg flown into the wind and
 * back is d/(V−W) + d/(V+W), never 2d/V).
 */
export function holdPathToPassage(path: HoldPathReport, position: LatLon): PathPiece[] {
  const pieces: PathPiece[] = [];
  for (let k = path.index; k <= path.passageAt && k < path.segments.length; k += 1) {
    const segment = path.segments[k];
    const start = k === path.index ? position : path.segments[k - 1].to;
    if (segment.kind === "line") {
      const distance = distanceNm(start, segment.to);
      if (distance > 1e-6) pieces.push({ distance, course: bearingDeg(segment.from, segment.to) });
      continue;
    }
    const from = bearingDeg(segment.centre, start), to = bearingDeg(segment.centre, segment.to);
    const sweep = segment.turn === "R" ? norm360(to - from) : norm360(from - to);
    const steps = Math.max(1, Math.ceil(sweep / ARC_STEP_DEG));
    const step = sweep / steps, sign = segment.turn === "R" ? 1 : -1;
    for (let s = 0; s < steps; s += 1) {
      const middle = from + sign * step * (s + 0.5);
      pieces.push({ distance: (segment.radius * step * Math.PI) / 180, course: norm360(middle + sign * 90) });
    }
  }
  return pieces;
}

/** The time to fly pieces at their ground speeds, hours; null where any piece cannot be flown with progress. */
export function piecesHours(pieces: PathPiece[], groundSpeed: (course: number) => number | null): number | null {
  let hours = 0;
  for (const piece of pieces) {
    const gs = groundSpeed(piece.course);
    if (gs === null || gs <= 0) return null;
    hours += piece.distance / gs;
  }
  return hours;
}

/** The time a hold ahead adds to the predictions after its fix, or why the path beyond it is not defined. */
export type HoldAllowance =
  | { hours: number; entry: HoldEntry | null; racetracks: number }
  | { hours: null; reason: "UNABLE HOLD" };

/**
 * The time spent in a hold that leaves by itself (plan R3-03 and D-H): ONCE (HF), AT TGT ALT (HA, taken as met; the
 * caller makes it CONDITIONAL when the altitude is not predicted there) and the missed-approach hold. A MANUAL hold is
 * not timed here: its exit is the crew's, so the predictions assume the next crossing (CONDITIONAL, HOLD-ETA).
 *
 * The pattern is the one the flight flies (holds.ts, as flight.ts builds it): sized at the faster of the planned true
 * airspeed and the holding speed (IAS) as TAS at the hold altitude, the leg its coded distance or its time (or the
 * default for the altitude) at that TAS. A racetrack is its two turns and two legs; a teardrop or parallel entry is its
 * outbound segment, a half turn, and the way back along the inbound course from abeam the end of the outbound segment.
 * Each piece is timed at its own ground speed through the wind (Astra F1): into the wind and back is d/(V−W) + d/(V+W),
 * longer than 2d/V, so a still-air mean would underestimate every hold flown in a wind.
 *
 * Racetracks after the entry: ONCE and AT TGT ALT leave at the first crossing after it (one racetrack after a direct
 * entry, none after a teardrop or parallel entry); the missed-approach hold after one whole racetrack. A hold already
 * in progress adds what remains of that rule from the fix, without an entry. Unflyable (the wind at least the TAS):
 * UNABLE HOLD, and the path beyond the fix is not defined.
 */
export function holdAllowance(hold: Hold, fix: LatLon, arrivalTrack: number, tas: number, wind: Wind, altitudeFt: number): HoldAllowance | null {
  if (hold.exit === "MANUAL" && !hold.missed) return null;
  const holdTas = Math.max(tas, tasFromIas(hold.speed, altitudeFt));
  const legNm = hold.legDistance ?? ((hold.legTime ?? defaultLegMinutes(altitudeFt)) * holdTas) / 60;
  const geometry = holdGeometry(fix, hold.inbound, hold.turn, holdTas, wind.speed, legNm, MAX_BANK);
  if (!geometry) return { hours: null, reason: "UNABLE HOLD" };
  const entered = hold.status === "IN PROGRESS" || hold.status === "EXIT ARMED";
  const entry = entered ? null : holdEntry(arrivalTrack, hold.inbound, hold.turn);
  const pieces: PathPiece[] = [];
  if (entry !== null && entry !== "DIRECT") {
    // The outbound segment, the half turn, and back along the inbound course from abeam where the outbound ended (the
    // flight intercepts the inbound line there, not at its far end).
    const [outbound] = entrySegments(entry, fix, hold.inbound, hold.turn, geometry);
    if (outbound.kind === "line") {
      const out = distanceNm(fix, outbound.to), outCourse = bearingDeg(fix, outbound.to);
      const back = out * Math.cos(((outCourse - (hold.inbound + 180)) * Math.PI) / 180);
      pieces.push({ distance: out, course: outCourse });
      const sign = hold.turn === "RIGHT" ? 1 : -1, steps = Math.ceil(180 / ARC_STEP_DEG);
      for (let s = 0; s < steps; s += 1) pieces.push({ distance: (Math.PI * geometry.radius) / steps, course: norm360(outCourse + sign * (180 / steps) * (s + 0.5)) });
      if (back > 0) pieces.push({ distance: back, course: hold.inbound });
    }
  }
  const racetracks = hold.status === "EXIT ARMED" ? 0
    : hold.missed ? Math.max(0, 1 - (hold.circuits ?? 0))
      : entry === "DIRECT" ? 1 : 0;
  // Each racetrack from the fix: its turn, outbound leg, turn and inbound leg, every piece at its own ground speed.
  const racetrack = holdPathToPassage({ segments: geometry.racetrack, index: 0, passageAt: geometry.racetrack.length - 1 }, fix);
  for (let r = 0; r < racetracks; r += 1) pieces.push(...racetrack);
  const hours = piecesHours(pieces, course => predictedGroundSpeed(holdTas, course, wind));
  if (hours === null) return { hours: null, reason: "UNABLE HOLD" };
  return { hours, entry, racetracks };
}
