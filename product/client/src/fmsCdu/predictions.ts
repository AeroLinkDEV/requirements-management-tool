import { MAX_BANK } from "./flight";
import { bearingDeg, distanceNm, holdEntry, type Hold, type HoldEntry, type LatLon, type Route } from "./fmsModel";
import { defaultLegMinutes, entrySegments, holdGeometry } from "./holds";
import { tasFromIas } from "./kinematics";
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
 * default for the altitude) at that TAS. A racetrack is 2·leg + 2·π·r long; a teardrop or parallel entry is its
 * outbound segment, a half turn, and the way back along the inbound course from abeam the end of the outbound segment. The time is that length at the TAS: a still-air mean, a laboratory allowance
 * (the wind shortens one leg as much as it lengthens the other only on average).
 *
 * Racetracks after the entry: ONCE and AT TGT ALT leave at the first crossing after it (one racetrack after a direct
 * entry, none after a teardrop or parallel entry); the missed-approach hold after one whole racetrack. A hold already
 * in progress adds what remains of that rule from the fix, without an entry. Unflyable (the wind at least the TAS):
 * UNABLE HOLD, and the path beyond the fix is not defined.
 */
export function holdAllowance(hold: Hold, fix: LatLon, arrivalTrack: number, tas: number, windSpeed: number, altitudeFt: number): HoldAllowance | null {
  if (hold.exit === "MANUAL" && !hold.missed) return null;
  const holdTas = Math.max(tas, tasFromIas(hold.speed, altitudeFt));
  const legNm = hold.legDistance ?? ((hold.legTime ?? defaultLegMinutes(altitudeFt)) * holdTas) / 60;
  const geometry = holdGeometry(fix, hold.inbound, hold.turn, holdTas, windSpeed, legNm, MAX_BANK);
  if (!geometry) return { hours: null, reason: "UNABLE HOLD" };
  const racetrackNm = 2 * geometry.legNm + 2 * Math.PI * geometry.radius;
  const entered = hold.status === "IN PROGRESS" || hold.status === "EXIT ARMED";
  const entry = entered ? null : holdEntry(arrivalTrack, hold.inbound, hold.turn);
  let entryNm = 0;
  if (entry !== null && entry !== "DIRECT") {
    // The outbound segment, the half turn, and back along the inbound course from abeam where the outbound ended (the
    // flight intercepts the inbound line there, not at its far end).
    const [outbound] = entrySegments(entry, fix, hold.inbound, hold.turn, geometry);
    if (outbound.kind === "line") {
      const out = distanceNm(fix, outbound.to);
      const back = out * Math.cos(((bearingDeg(fix, outbound.to) - (hold.inbound + 180)) * Math.PI) / 180);
      entryNm = out + Math.PI * geometry.radius + Math.max(0, back);
    }
  }
  const racetracks = hold.status === "EXIT ARMED" ? 0
    : hold.missed ? Math.max(0, 1 - (hold.circuits ?? 0))
      : entry === "DIRECT" ? 1 : 0;
  return { hours: (entryNm + racetracks * racetrackNm) / holdTas, entry, racetracks };
}
