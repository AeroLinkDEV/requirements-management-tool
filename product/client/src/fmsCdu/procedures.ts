import type { Leg, LegSource, Route } from "./fmsModel";
import type { NavDatabase, Procedure, ProcedureLeg } from "./navData";

/**
 * Builds a route's legs from its selected departure, arrival and approach and its enroute legs, the way the FMS
 * strings procedures together: the SID's end joins the enroute legs where they share a fix (otherwise a route
 * discontinuity follows it), the enroute legs end where the STAR (or approach) begins, and the approach is followed
 * by its missed approach. Without an approach the destination airport is the last leg. A join is remembered on the
 * route, so rebuilding it later (another transition, an approach after the STAR) does not open a false gap.
 */

const toLegs = (legs: ProcedureLeg[], source: LegSource, via: string): Leg[] =>
  legs.map(leg => ("ident" in leg
    // An overfly fix (a runway threshold, a missed approach point) is flown over, never turned short of.
    ? {
      kind: "wpt", ident: leg.ident, altitude: leg.altitude, source, via, path: leg.path, course: leg.course, arc: leg.arc, qualifier: leg.overfly ? "/O" : undefined,
      ...(leg.position ? { position: leg.position } : {}), ...(leg.procedureTurn ? { procedureTurn: leg.procedureTurn } : {}),
      ...(leg.turnDirection ? { turnDirection: leg.turnDirection } : {}),
      ...(leg.speedLimit ? { speedLimit: leg.speedLimit } : {}), ...(leg.hold ? { hold: leg.hold } : {}),
    }
    : { kind: "cond", path: leg.path, course: leg.course, altitude: leg.altitude, source, via, ...(leg.speedLimit ? { speedLimit: leg.speedLimit } : {}) }));

/**
 * An approach transition joined to the final by the records' roles, not their names (Stage C, C.7). When the
 * transition ends at the fix the final begins at (its IF), that fix is flown once: the transition's leg to it (its
 * path, and the hold coded there, such as an HF course reversal) and then the final from it. M300 7-2 gives the
 * approach's common-waypoint speed/altitude constraints precedence; the coded hold retains its own restrictions.
 * A TF, an HF and an IF at the same fix are never collapsed merely because they share a name.
 */
export function joinTransition(transition: ProcedureLeg[], final: ProcedureLeg[], approachConstraints = true): ProcedureLeg[] {
  const last = transition.at(-1), first = final[0];
  if (!last || !first || !("ident" in last) || !("ident" in first) || last.ident !== first.ident || first.path !== undefined) return [...transition, ...final];
  const joined: ProcedureLeg = {
    ...first, ...last,
    ...(approachConstraints ? { altitude: first.altitude ?? last.altitude, speedLimit: first.speedLimit ?? last.speedLimit } : {}),
  };
  return [...transition.slice(0, -1), joined, ...final.slice(1)];
}

const isWpt = (leg: Leg | undefined, ident: string) => leg?.kind === "wpt" && leg.ident === ident;

/** The legs of a route that are not part of a procedure and are not the destination. */
export function enrouteLegs(route: Route): Leg[] {
  const legs = route.legs;
  const sourced = (leg: Leg, sources: LegSource[]) => leg.kind !== "disco" && leg.source !== undefined && sources.includes(leg.source);
  let start = 0;
  legs.forEach((leg, i) => { if (sourced(leg, ["SID"])) start = i + 1; });
  const arrival = legs.findIndex(leg => sourced(leg, ["STAR", "APPR", "MISSED"]));
  const end = arrival < 0 ? legs.length : arrival;
  let enroute = legs.slice(start, end).filter(leg => !(leg.kind !== "disco" && leg.source));
  const last = enroute.at(-1);
  if (last?.kind === "wpt" && last.ident === route.dest) enroute = enroute.slice(0, -1);
  // The discontinuities that joined procedures to the enroute legs are rebuilt with them, not kept.
  if (start > 0 && enroute[0]?.kind === "disco") enroute = enroute.slice(1);
  if (arrival >= 0 && enroute.at(-1)?.kind === "disco") enroute = enroute.slice(0, -1);
  return enroute;
}

export function findProcedure(db: NavDatabase, route: Route, kind: Procedure["kind"]) {
  const choice = kind === "SID" ? route.sid : kind === "STAR" ? route.star : route.approach;
  const airport = kind === "SID" ? route.origin : route.dest;
  return choice ? db.proceduresFor(airport, kind).find(p => p.ident === choice.ident) : undefined;
}

export function composeRoute(route: Route, db: NavDatabase, enroute: Leg[] = enrouteLegs(route)): Leg[] {
  const sid = findProcedure(db, route, "SID"), star = findProcedure(db, route, "STAR"), approach = findProcedure(db, route, "APPROACH");
  const sidLegs = sid ? toLegs(joinTransition(joinTransition(route.runway ? sid.runwayTransitions?.[route.runway] ?? [] : [], sid.legs, false),
    route.sid?.transition ? sid.transitions[route.sid.transition] ?? [] : [], false), "SID", sid.ident) : [];
  const starLegs = star ? toLegs([...(route.star?.transition ? star.transitions[route.star.transition] ?? [] : []), ...star.legs], "STAR", star.ident) : [];
  let apprLegs = approach ? toLegs(joinTransition(route.approach?.transition ? approach.transitions[route.approach.transition] ?? [] : [], approach.legs), "APPR", approach.ident) : [];
  const missedLegs = approach?.missed ? toLegs(approach.missed, "MISSED", "MISSED") : [];

  const result: Leg[] = [...sidLegs];
  let en = [...enroute];
  const sidEnd = sidLegs.at(-1);
  // A SID that ends in vectors (VM/FM) hands over to ATC: the route resumes after a discontinuity.
  if (sidEnd?.kind === "cond" && en.length && en[0].kind !== "disco") result.push({ kind: "disco" });
  if (sidEnd?.kind === "wpt") {
    const joins = en.findIndex(leg => isWpt(leg, sidEnd.ident));
    if (joins >= 0) { en = en.slice(joins + 1); route.departureJoin = sidEnd.ident; }
    else if (en.length && en[0].kind !== "disco" && route.departureJoin !== sidEnd.ident) result.push({ kind: "disco" });
  }
  result.push(...en);

  const arrivalStart = starLegs[0] ?? apprLegs[0];
  if (arrivalStart?.kind === "wpt") {
    const joins = result.findIndex((leg, i) => i >= sidLegs.length && isWpt(leg, arrivalStart.ident));
    if (joins >= 0) { result.splice(joins); route.arrivalJoin = arrivalStart.ident; }
    else if (result.length && result.at(-1)?.kind !== "disco" && route.arrivalJoin !== arrivalStart.ident) result.push({ kind: "disco" });
  }
  result.push(...starLegs);
  const starEnd = starLegs.at(-1);
  if (starEnd?.kind === "wpt" && apprLegs.length) {
    // The approach takes over at the STAR's last fix (dropping any transition legs before it), or a gap follows.
    const at = apprLegs.findIndex(leg => isWpt(leg, starEnd.ident));
    if (at >= 0) { result.pop(); apprLegs = apprLegs.slice(at); }
    else result.push({ kind: "disco" });
  }
  result.push(...apprLegs, ...missedLegs);
  if (!apprLegs.length) result.push({ kind: "wpt", ident: route.dest });
  return result;
}
