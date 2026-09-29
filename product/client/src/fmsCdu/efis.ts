import type { FlightSimulator, VerticalMode } from "./flight";
import { courseDeg, distanceNm, offset, type LatLon, type Route } from "./fmsModel";
import type { ScriptedFms } from "./scriptedFms";

// The FMS output bus and the aircraft data an EFIS draws from.
//
// On an aircraft the FMS does not draw the primary flight display or the navigation display: an EFIS does, from data
// words the FMS publishes (ARINC 429 labels such as 114 desired track, 116 cross-track, 117 vertical deviation, 121
// roll command and 251 distance to go) plus the aircraft's own sensors. The symbology therefore belongs to the EFIS
// installation. This module is that boundary for the bench: fmsOutputs() is everything the displays may take from the
// FMS, each word with a status like ARINC 429's sign/status matrix (normal, no computed data, failure). Nothing else in
// the FMS is visible to the EFIS. aircraftData() is what the EFIS takes from the aircraft (attitude, air data, heading)
// rather than from the FMS. A future FMS under test drives the displays by producing the same bus.
//
// The bus is generic engineering practice (FAA-H-8083-6, Boeing 737 FCOM 10 and 11 conventions), not a CMA-9000 or
// installation interface control document.

/** Word status, after ARINC 429's sign/status matrix: valid data, no computed data, or failure warning. */
export type WordStatus = "NORMAL" | "NCD" | "FAIL";
export type Word<T> = { value: T | null; status: WordStatus };

const normal = <T>(value: T): Word<T> => ({ value, status: "NORMAL" });
const ncd = <T>(): Word<T> => ({ value: null, status: "NCD" });
const fail = <T>(): Word<T> => ({ value: null, status: "FAIL" });

/** A point of the route as the navigation display draws it. */
export type RoutePoint = { ident: string; position: LatLon; active: boolean; constraint: string | null };

export type FmsOutputs = {
  /** Which FMS the displays show; the source annunciation keeps the crew from following the wrong one. */
  source: "FMS1";
  failed: boolean;
  /** Label 114: desired track, degrees true. */
  desiredTrack: Word<number>;
  /** Label 116: cross-track distance, NM, positive right of the desired track. */
  crossTrack: Word<number>;
  /**
   * Where the lateral guidance and cross-track come from: the selected GPS's 116 on an RNAV final (GPS), or the route
   * geometry (ROUTE); null without managed lateral guidance. A change is a change of source, shown, never substituted.
   */
  lateralSource: "GPS" | "ROUTE" | null;
  /** Label 117: vertical deviation, feet, positive above the path; `coupled` false for advisory information only. */
  verticalDeviation: Word<number>;
  verticalSource: "VNAV" | "APPR" | null;
  verticalCoupled: boolean;
  /** Label 121: roll steering command, degrees, positive right. */
  rollCommand: Word<number>;
  /** Label 251: distance to go to the active waypoint, NM. */
  distanceToGo: Word<number>;
  /** Active (TO) waypoint and its predicted time of arrival (epoch ms). */
  toWaypoint: Word<string>;
  eta: Word<number>;
  /** Targets the FMS commands (drawn magenta): speed in knots, altitude in feet. */
  targetSpeed: Word<number>;
  targetAltitude: Word<number>;
  /** Engaged and armed modes, from the controller state. */
  lateralMode: string;
  lateralArmed: string[];
  verticalMode: VerticalMode | null;
  verticalArmed: string[];
  approach: { type: string | null; state: "OFF" | "ARMED" | "CAPTURED" };
  /**
   * Full-scale lateral deviation for the phase (5 NM en route, 1 NM terminal, 0.3 NM approach: FAA-H-8083-6); on an RNAV
   * final, the selected GPS's angular scaling (GPS phase 3b).
   */
  lateralFullScaleNm: number;
  /**
   * Full-scale vertical deviation, feet: 400 ft for the VNAV path, 150 ft on the approach (laboratory values); on an RNAV
   * final, the selected GPS's angular scaling (GPS phase 3b).
   */
  verticalFullScaleFt: number;
  phase: string;
  rnp: number;
  anp: number;
  navMode: string;
  /** For the navigation display. */
  activeRoute: RoutePoint[];
  modifiedRoute: RoutePoint[] | null;
  /** The executed offset track, drawn dashed magenta, as a polyline. */
  offsetTrack: LatLon[] | null;
  holdFix: LatLon | null;
  /** Top and end of descent positions from the VNAV profile. */
  topOfDescent: LatLon | null;
  endOfDescent: LatLon | null;
};

/** What the EFIS takes from the aircraft, not the FMS: attitude, heading, air data and ground speed. */
export type AircraftData = {
  pitch: number;
  bank: number;
  heading: number;
  track: number;
  airspeed: number;
  groundSpeed: number;
  altitude: number;
  verticalSpeed: number;
  wind: { direction: number; speed: number };
  position: LatLon;
};

const LATERAL_FULL_SCALE = { "EN ROUTE": 5, TERMINAL: 1, APPROACH: 0.3 } as const;

/**
 * The connected points of a route for the map, up to the first discontinuity or the first fix without a position: the
 * line never bridges geometry the plan does not have. The active marker is on the active leg's fix only (the first leg
 * of the route), so an unresolved active fix leaves nothing marked active.
 */
function routePoints(fms: ScriptedFms, route: Route): RoutePoint[] {
  const points: RoutePoint[] = [];
  for (const [index, leg] of route.legs.entries()) {
    if (leg.kind === "disco") break;
    if (leg.kind !== "wpt") continue;
    const position = fms.coordinates(leg.ident, route);
    if (!position) break;
    points.push({ ident: leg.ident, position, active: index === 0, constraint: leg.altitude ?? null });
  }
  return points;
}

/** The position a given distance along a polyline, or null past its end. */
function alongPolyline(line: LatLon[], nm: number): LatLon | null {
  let left = nm;
  for (let i = 1; i < line.length; i += 1) {
    const leg = distanceNm(line[i - 1], line[i]);
    if (left <= leg) return offset(line[i - 1], courseDeg(line[i - 1], line[i]), left);
    left -= leg;
  }
  return null;
}

export function fmsOutputs(fms: ScriptedFms, sim: FlightSimulator): FmsOutputs {
  const failed = fms.hasCondition("fmsFail");
  const g = sim.guidance;
  const active = fms.activeRoute;
  const next = active.legs[0];
  const phase = fms.flightPhase;
  const empty: FmsOutputs = {
    source: "FMS1", failed, desiredTrack: fail(), crossTrack: fail(), lateralSource: null, verticalDeviation: fail(), verticalSource: null, verticalCoupled: false,
    rollCommand: fail(), distanceToGo: fail(), toWaypoint: fail(), eta: fail(), targetSpeed: fail(), targetAltitude: fail(),
    lateralMode: sim.lateralMode === "HDG" ? (sim.headingHeld ? "HDG HOLD" : "HDG SEL") : g.mode, lateralArmed: [],
    verticalMode: sim.verticalMode, verticalArmed: [], approach: { type: null, state: "OFF" },
    lateralFullScaleNm: LATERAL_FULL_SCALE[phase], verticalFullScaleFt: 400, phase, rnp: fms.navPerformance.rnp, anp: fms.navPerformance.anp,
    navMode: fms.navState.mode, activeRoute: [], modifiedRoute: null, offsetTrack: null, holdFix: null, topOfDescent: null, endOfDescent: null,
  };
  // A failed FMS publishes failure warnings; the displays remove its data and flag it. The modes remain: they are the
  // autopilot's (basic heading and altitude hold after the reversion).
  if (failed) return empty;

  const managed = sim.lateralMode === "LNAV" && g.desiredTrack !== null;
  const path = sim.verticalPath;
  const profile = fms.profile();
  const toIdent = next?.kind === "wpt" ? next.ident : null;
  const distanceToGo = g.distanceToGo;
  const activeRoute = routePoints(fms, active);
  const line = [fms.position, ...activeRoute.map(point => point.position)];
  const offsetNm = active.offset?.nm;
  // Armed, the vertical column names the approach when it has a vertical level: ILS, or the GPS's LPV or LNAV/VNAV (305).
  const type = fms.approachType;
  const verticalLevel = type === "ILS" || type === "LPV" || type === "LNAV/VNAV";
  // On an RNAV final the deviations are the GPS's, scaled as it scales them (the scaling beside 116/117).
  const onFinal = path?.source === "APPR" || sim.verticalFlag;
  const gpsScale = onFinal ? fms.gpsApproach?.scale ?? null : null;
  // On an RNAV final with GPS vertical guidance, the receiver's own 117 as it stands.
  const gpsVertical = onFinal && fms.gpsApproachVertical ? fms.gpsApproach!.verticalFt : null;
  // With GPS lateral authority (GPS-01), the receiver's own 116 as it stands, and its lateral scaling.
  const gpsLateral = sim.gpsLateralActive && fms.gpsApproachLateral ? fms.gpsApproach?.lateralFt ?? null : null;
  const lateralScale = gpsLateral !== null ? fms.gpsApproach?.scale ?? null : null;
  return {
    ...empty,
    desiredTrack: managed ? normal(g.desiredTrack!) : ncd(),
    // Captured on an RNAV final, the receiver's own 116 as it stands, converted to NM (GPS phase 3b).
    crossTrack: !managed ? ncd() : gpsLateral !== null ? normal(gpsLateral / 6076.12) : normal(g.crossTrack),
    lateralSource: !managed ? null : gpsLateral !== null ? "GPS" : "ROUTE",
    // Flagged on an RNAV final without GPS vertical guidance: the receiver withdrew it, so no path is shown (3b).
    verticalDeviation: sim.verticalFlag ? fail() : gpsVertical !== null ? normal(gpsVertical) : path ? normal(fms.altitude - path.altitude) : ncd(),
    verticalSource: path?.source ?? null,
    verticalCoupled: path?.coupled ?? false,
    rollCommand: managed ? normal(g.bankCommand) : ncd(),
    distanceToGo: distanceToGo !== null && toIdent ? normal(distanceToGo) : ncd(),
    toWaypoint: toIdent ? normal(toIdent) : ncd(),
    eta: distanceToGo !== null && fms.groundSpeed > 30 ? normal(fms.now.getTime() + (distanceToGo / fms.groundSpeed) * 3_600_000) : ncd(),
    targetSpeed: normal(fms.targetSpeed),
    targetAltitude: sim.altitudeHoldReference === null ? normal(g.targetAltitude) : ncd(),
    lateralArmed: sim.lnavIsArmed ? ["LNAV"] : [],
    verticalArmed: sim.approachMode === "ARMED" && verticalLevel ? [type] : [],
    approach: { type, state: sim.approachMode },
    lateralFullScaleNm: lateralScale ? lateralScale.lateralFullScaleFt / 6076.12 : LATERAL_FULL_SCALE[phase],
    verticalFullScaleFt: gpsScale ? gpsScale.verticalFullScaleFt : path?.source === "APPR" ? 150 : 400,
    activeRoute,
    modifiedRoute: fms.routeStatus === "MOD" ? routePoints(fms, fms.route) : null,
    offsetTrack: offsetNm ? line.slice(1).map((p, i) => offset(p, courseDeg(line[i], p) + (offsetNm > 0 ? 90 : -90), Math.abs(offsetNm))) : null,
    holdFix: active.hold ? fms.coordinates(active.hold.fix) ?? null : null,
    topOfDescent: profile.topOfDescent !== null ? alongPolyline(line, profile.topOfDescent) : null,
    endOfDescent: profile.endOfDescent ? fms.coordinates(profile.endOfDescent) ?? null : null,
  };
}

export function aircraftData(fms: ScriptedFms, sim: FlightSimulator): AircraftData {
  const track = fms.track;
  const airspeed = sim.tas;
  // Heading is the track corrected for the drift the wind causes (the crab angle).
  const crossWind = fms.wind.speed * Math.sin(((fms.wind.direction - track) * Math.PI) / 180);
  const drift = airspeed > 1 ? (Math.asin(Math.max(-1, Math.min(1, crossWind / airspeed))) * 180) / Math.PI : 0;
  // Pitch approximated from the flight path angle, for display: a point-mass model has no attitude of its own.
  const pitch = fms.groundSpeed > 1 ? (Math.atan(fms.verticalSpeed / (fms.groundSpeed * 101.27)) * 180) / Math.PI : 0;
  return {
    pitch, bank: sim.bankAngle, heading: (track + drift + 360) % 360, track, airspeed, groundSpeed: fms.groundSpeed,
    altitude: fms.altitude, verticalSpeed: fms.verticalSpeed, wind: fms.wind, position: fms.truePosition,
  };
}
