/**
 * Vertical navigation and predictions: altitude constraints, the planned vertical profile along the route with its
 * top of descent (T/D) and end of descent (E/D), and time and fuel predictions at each waypoint. This follows how
 * airline FMSs build the profile (Boeing 737 FCOM 11.31, VNAV): the descent path is built backward from the E/D at the
 * path angle, respecting each constraint; the climb forward from the aircraft at the climb rate, levelling at
 * "at or below" constraints and the cruise altitude. The rates and angles are representative.
 */

export type AltitudeConstraint =
  | { kind: "AT"; altitude: number }
  | { kind: "A"; altitude: number }
  | { kind: "B"; altitude: number }
  | { kind: "WINDOW"; lower: number; upper: number };

export const TRANSITION_ALTITUDE = 18000;

/**
 * An altitude: FL080 or FL80 as a flight level; otherwise feet. As a crew entry (entry = true) three digits are
 * hundreds of feet, as the FMS reads them (050 is 5000); stored values, such as a runway elevation of 168, are feet.
 */
export function parseAltitude(text: string, entry = false): number | null {
  const fl = /^FL(\d{2,3})$/.exec(text);
  if (fl) return Number(fl[1]) * 100;
  if (entry && /^\d{3}$/.test(text)) return Number(text) * 100;
  if (/^\d{1,5}$/.test(text)) return Number(text);
  return null;
}

/**
 * A constraint: 5000 (at), 5000A (at or above), 5000B (at or below), 7000B5000A or 5000A7000B (a window). With
 * entry = true the altitudes follow the crew entry rules of parseAltitude.
 */
export function parseConstraint(text: string | undefined, entry = false): AltitudeConstraint | null {
  if (!text) return null;
  const window = /^(FL\d{2,3}|\d{1,5})([AB])(FL\d{2,3}|\d{1,5})([AB])$/.exec(text);
  if (window && window[2] !== window[4]) {
    const a = parseAltitude(window[1], entry), b = parseAltitude(window[3], entry);
    if (a === null || b === null) return null;
    const lower = window[2] === "A" ? a : b, upper = window[2] === "B" ? a : b;
    return lower <= upper ? { kind: "WINDOW", lower, upper } : null;
  }
  const single = /^(FL\d{2,3}|\d{1,5})([AB]?)$/.exec(text);
  if (!single) return null;
  const altitude = parseAltitude(single[1], entry);
  if (altitude === null) return null;
  return single[2] === "A" ? { kind: "A", altitude } : single[2] === "B" ? { kind: "B", altitude } : { kind: "AT", altitude };
}

/** How a constraint is written back on LEGS: altitudes above the transition altitude as flight levels. */
export function formatConstraint(c: AltitudeConstraint) {
  const alt = (value: number) => (value >= TRANSITION_ALTITUDE ? `FL${String(Math.round(value / 100)).padStart(3, "0")}` : String(value));
  switch (c.kind) {
    case "AT": return alt(c.altitude);
    case "A": return `${alt(c.altitude)}A`;
    case "B": return `${alt(c.altitude)}B`;
    case "WINDOW": return `${alt(c.upper)}B${alt(c.lower)}A`;
  }
}

/** Clamp an altitude into what a constraint allows. */
export function applyConstraint(altitude: number, c: AltitudeConstraint | null) {
  if (!c) return altitude;
  switch (c.kind) {
    case "AT": return c.altitude;
    case "A": return Math.max(altitude, c.altitude);
    case "B": return Math.min(altitude, c.altitude);
    case "WINDOW": return Math.min(Math.max(altitude, c.lower), c.upper);
  }
}

/**
 * How far a prediction can be relied on: known geometry; estimated (after a course or heading leg that ends on an
 * event, the next leg is taken from the last fixed point); or unknown (past a route discontinuity or a manually
 * terminated leg, where the path is not defined). Unknown predictions carry no distance, time or fuel.
 */
export type PredictionBasis = "known" | "estimated" | "unknown";

export type ProfileInput = {
  /**
   * One entry per waypoint ahead, in order: the distance of the leg into it (null when that leg's path is not
   * defined), its constraint, how its prediction is based, and whether it belongs to the missed approach.
   */
  waypoints: {
    ident: string; legDistance: number | null; groundSpeed: number; constraint: AltitudeConstraint | null; endOfDescent: boolean;
    basis?: PredictionBasis; missed?: boolean;
  }[];
  altitude: number;
  cruiseAltitude: number;
  climbRate: number;
  pathAngle: number;
  fuel: number;
  fuelFlow: number;
  now: number;
};

export type ProfilePoint = {
  ident: string; distance: number | null; eta: number | null; fuel: number | null; basis: PredictionBasis;
  /** The predicted altitude; null past an unknown segment, where it cannot be predicted. */
  altitude: number | null;
  /** Whether the plan meets the constraint here; null where it is not evaluated (past an unknown segment). */
  constraintMet: boolean | null;
};
export type Profile = {
  points: ProfilePoint[];
  /** Distance ahead of the aircraft to the top of descent, null if the descent has begun or there is none. */
  topOfDescent: number | null;
  endOfDescent: string | null;
  /** The first constraint the plan does not meet: a climb it cannot make, or a restriction it stays above. */
  unableNext: string | null;
  /** The landing: the end of descent (the runway), or the last point before the missed approach. */
  destination: ProfilePoint | null;
  /** The altitude the climb may go to now: cruise, or the lowest "at" or "at or below" constraint ahead in the climb. */
  climbCap: number;
  /** Past the top of descent: the active waypoint is on the descent path. */
  descending: boolean;
};

const FT_PER_NM = 6076.12;

export function computeProfile(input: ProfileInput): Profile {
  const { waypoints, cruiseAltitude, pathAngle } = input;
  const tan = Math.tan((pathAngle * Math.PI) / 180);
  const cumulative: number[] = [];
  let total = 0;
  for (const w of waypoints) { total += w.legDistance ?? 0; cumulative.push(total); }
  // The basis only gets worse along the route: once the path is unknown, everything after it is too.
  const basis: PredictionBasis[] = [];
  const rank = { known: 0, estimated: 1, unknown: 2 } as const;
  waypoints.forEach((w, i) => {
    const own: PredictionBasis = w.legDistance === null ? "unknown" : w.basis ?? "known";
    const before = i > 0 ? basis[i - 1] : "known";
    basis.push(rank[own] > rank[before] ? own : before);
  });

  // The capping constraints of the climb: cruise, or an "at" or "at or below" constraint.
  const capOf = (c: AltitudeConstraint | null) =>
    c?.kind === "AT" || c?.kind === "B" ? c.altitude : c?.kind === "WINDOW" ? c.upper : Infinity;

  // Top of climb: the first waypoint by which the climb (at the climb rate, levelling at its constraints) reaches
  // cruise. Constraints before it are climb constraints; constraints after it below cruise are descent constraints.
  let topOfClimb = -1;
  {
    let altitude = input.altitude;
    if (altitude < cruiseAltitude - 1) {
      topOfClimb = waypoints.length;
      for (let i = 0; i < waypoints.length; i += 1) {
        const cap = Math.min(cruiseAltitude, capOf(waypoints[i].constraint));
        altitude = Math.min(Math.max(cap, altitude), altitude + input.climbRate * ((waypoints[i].legDistance ?? 0) / Math.max(30, waypoints[i].groundSpeed)) * 60);
        if (altitude >= cruiseAltitude - 1) { topOfClimb = i; break; }
      }
    }
  }

  // Descent: backward from the E/D at the path angle, capped at cruise and shaped by the descent constraints.
  const edIndex = waypoints.findIndex(w => w.endOfDescent);
  const descent: number[] = waypoints.map(() => Infinity);
  if (edIndex >= 0) {
    const ed = waypoints[edIndex].constraint;
    descent[edIndex] = ed?.kind === "AT" ? ed.altitude : ed?.kind === "B" ? ed.altitude : ed?.kind === "WINDOW" ? ed.lower : ed?.altitude ?? 0;
    for (let i = edIndex - 1; i >= 0; i -= 1) {
      // The path is not carried back across a leg whose length is unknown: no descent is planned from a gap.
      const leg = waypoints[i + 1].legDistance;
      if (leg === null) break;
      const up = descent[i + 1] + leg * FT_PER_NM * tan;
      // In the climb the path stops at cruise: those constraints belong to the climb.
      if (i <= topOfClimb && up >= cruiseAltitude) break;
      descent[i] = applyConstraint(Math.min(up, cruiseAltitude), waypoints[i].constraint);
    }
  }

  // Top of descent: where the backward path, rising at the path angle, reaches the cruise altitude.
  let topOfDescent: number | null = null;
  if (edIndex >= 0) {
    const firstBelow = descent.findIndex((alt, i) => i <= edIndex && alt < cruiseAltitude - 1);
    if (firstBelow >= 0 && basis[firstBelow] !== "unknown") {
      const back = (cruiseAltitude - descent[firstBelow]) / (FT_PER_NM * tan);
      const at = cumulative[firstBelow] - back;
      topOfDescent = at > 0 ? at : null;
    }
  }

  // The climb levels at the lowest "at" or "at or below" constraint ahead in the climb, until passing it.
  let climbCap = cruiseAltitude;
  // Only constraints on the known part of the route cap the climb: nothing behind a gap commands the connected segment.
  for (let i = 0; i < waypoints.length && descent[i] === Infinity && basis[i] !== "unknown"; i += 1) climbCap = Math.min(climbCap, capOf(waypoints[i].constraint));

  // The climb at each point levels at the lowest at-or-below constraint at or after it on the known climb segment, as
  // guidance does (climbCap): a restriction ahead holds the climb before it, not only at its own fix.
  const aheadCap: number[] = waypoints.map(() => cruiseAltitude);
  for (let i = waypoints.length - 1, lowest = cruiseAltitude; i >= 0; i -= 1) {
    if (descent[i] === Infinity && basis[i] !== "unknown") lowest = Math.min(lowest, capOf(waypoints[i].constraint));
    aheadCap[i] = lowest;
  }

  // Climb: forward from the aircraft at the climb rate, levelling at B constraints and cruise.
  const points: ProfilePoint[] = [];
  let altitude = input.altitude, time = input.now, fuel = input.fuel, unableNext: string | null = null;
  waypoints.forEach((w, i) => {
    const hours = (w.legDistance ?? 0) / Math.max(30, w.groundSpeed);
    time += hours * 3_600_000;
    fuel -= hours * input.fuelFlow;
    const cap = Math.min(cruiseAltitude, capOf(w.constraint), aheadCap[i]);
    const climbed = altitude < cap ? Math.min(cap, altitude + input.climbRate * hours * 60) : altitude;
    const predicted = Math.min(climbed, descent[i]);
    // Both bounds count: a restriction the plan stays above is missed just as one it cannot climb to.
    const lower = w.constraint?.kind === "A" || w.constraint?.kind === "AT" ? w.constraint.altitude : w.constraint?.kind === "WINDOW" ? w.constraint.lower : -Infinity;
    const upper = w.constraint?.kind === "B" || w.constraint?.kind === "AT" ? w.constraint.altitude : w.constraint?.kind === "WINDOW" ? w.constraint.upper : Infinity;
    const known = basis[i] !== "unknown";
    // Past an unknown segment a constraint is not evaluated: neither met nor missed, and it raises no UNABLE.
    const met = known ? predicted >= lower - 50 && predicted <= upper + 50 : null;
    if (met === false && unableNext === null) unableNext = w.ident;
    altitude = predicted;
    points.push({
      ident: w.ident, distance: known ? cumulative[i] : null, altitude: known ? predicted : null, eta: known ? time : null, fuel: known ? fuel : null,
      constraintMet: met, basis: basis[i],
    });
  });
  const landing = edIndex >= 0 ? edIndex : waypoints.findLastIndex(w => !w.missed);
  return {
    points, topOfDescent, endOfDescent: edIndex >= 0 ? waypoints[edIndex].ident : null, unableNext, climbCap,
    descending: edIndex >= 0 && topOfDescent === null && descent[0] !== Infinity,
    destination: landing >= 0 ? points[landing] : null,
  };
}

/**
 * Cold temperature correction for an altitude above the aerodrome: the true altitude is lower than indicated when it
 * is colder than standard, so constraint altitudes are raised. A simplified form of the ICAO PANS-OPS correction,
 * rounded up to 10 ft; zero at or above ISA.
 */
export function coldTemperatureCorrection(heightAboveAerodrome: number, aerodromeTemp: number, aerodromeElevation: number) {
  const isaAtAerodrome = 15 - (aerodromeElevation / 1000) * 2;
  if (aerodromeTemp >= isaAtAerodrome || heightAboveAerodrome <= 0) return 0;
  const correction = (heightAboveAerodrome * (isaAtAerodrome - aerodromeTemp)) / (273 + aerodromeTemp);
  return Math.ceil(correction / 10) * 10;
}
