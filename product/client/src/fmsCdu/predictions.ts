import type { Route } from "./fmsModel";
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
