import { COLUMNS, type CduColor, type Line, type Segment } from "./screen";
import type { ScriptedFms } from "./scriptedFms";
import type { ProcedureHold, SpeedLimit } from "./navData";

/**
 * Shared state shapes, geometry and screen helpers for the scripted CMA-9000. The navigation database is in
 * navData.ts. Bearings are true: the simulation applies no magnetic variation.
 */

export type LatLon = { lat: number; lon: number };

/** Present position of the simulated aircraft at the start of a session, just east of CYOW. */
export const START_POSITION: LatLon = { lat: 45.3100, lon: -75.6817 };

const toRad = (deg: number) => (deg * Math.PI) / 180;
const toDeg = (rad: number) => (rad * 180) / Math.PI;

/** Great-circle distance in nautical miles. */
export function distanceNm(a: LatLon, b: LatLon) {
  const dLat = toRad(b.lat - a.lat), dLon = toRad(b.lon - a.lon);
  const h = Math.sin(dLat / 2) ** 2 + Math.cos(toRad(a.lat)) * Math.cos(toRad(b.lat)) * Math.sin(dLon / 2) ** 2;
  return 2 * 3440.065 * Math.asin(Math.min(1, Math.sqrt(h)));
}

/** Initial true course in degrees, 1..360. */
export function courseDeg(a: LatLon, b: LatLon) {
  const y = Math.sin(toRad(b.lon - a.lon)) * Math.cos(toRad(b.lat));
  const x = Math.cos(toRad(a.lat)) * Math.sin(toRad(b.lat)) - Math.sin(toRad(a.lat)) * Math.cos(toRad(b.lat)) * Math.cos(toRad(b.lon - a.lon));
  const course = Math.round((toDeg(Math.atan2(y, x)) + 360) % 360);
  return course === 0 ? 360 : course;
}

/** Initial true bearing in degrees, 0..360, unrounded; for guidance geometry. */
export function bearingDeg(a: LatLon, b: LatLon) {
  const y = Math.sin(toRad(b.lon - a.lon)) * Math.cos(toRad(b.lat));
  const x = Math.cos(toRad(a.lat)) * Math.sin(toRad(b.lat)) - Math.sin(toRad(a.lat)) * Math.cos(toRad(b.lat)) * Math.cos(toRad(b.lon - a.lon));
  return (toDeg(Math.atan2(y, x)) + 360) % 360;
}

/** The angle an RF arc sweeps from one point to another about its centre, in its turn direction, 0..360. */
export function arcSweep(from: LatLon, to: LatLon, arc: { centre: LatLon; turn: "L" | "R" }) {
  const a = bearingDeg(arc.centre, from), b = bearingDeg(arc.centre, to);
  return arc.turn === "R" ? (b - a + 360) % 360 : (a - b + 360) % 360;
}

/** Length in NM of an RF arc between two points on it. */
export function arcLength(from: LatLon, to: LatLon, arc: { centre: LatLon; turn: "L" | "R" }) {
  return distanceNm(arc.centre, to) * toRad(arcSweep(from, to, arc));
}

/** A point at a true bearing and distance from another, for relative-position entries such as SAR REF ID. */
export function offset(from: LatLon, bearing: number, nm: number): LatLon {
  const d = nm / 3440.065, b = toRad(bearing), lat1 = toRad(from.lat), lon1 = toRad(from.lon);
  const lat2 = Math.asin(Math.sin(lat1) * Math.cos(d) + Math.cos(lat1) * Math.sin(d) * Math.cos(b));
  const lon2 = lon1 + Math.atan2(Math.sin(b) * Math.sin(d) * Math.cos(lat1), Math.cos(d) - Math.sin(lat1) * Math.sin(lat2));
  return { lat: toDeg(lat2), lon: wrapLongitude(toDeg(lon2)) };
}

/** A longitude brought into -180..180, so positions either side of the date line compare correctly (R17). */
export const wrapLongitude = (lon: number) => ((((lon + 180) % 360) + 360) % 360) - 180;

/** The short way from one longitude to another, -180..180 degrees: east positive, across the date line if nearer. */
export const longitudeDelta = (from: number, to: number) => wrapLongitude(to - from);

/** East/north nautical miles from an origin; accurate enough over the tens of miles the simulation works in. */
export function toLocal(origin: LatLon, p: LatLon) {
  return { x: longitudeDelta(origin.lon, p.lon) * 60 * Math.cos(toRad(origin.lat)), y: (p.lat - origin.lat) * 60 };
}

export function fromLocal(origin: LatLon, { x, y }: { x: number; y: number }): LatLon {
  return { lat: origin.lat + y / 60, lon: wrapLongitude(origin.lon + x / (60 * Math.cos(toRad(origin.lat)))) };
}

/** Where two true bearings from two places cross, ahead of both; null if they are parallel or cross behind. */
export function bearingIntersection(p1: LatLon, b1: number, p2: LatLon, b2: number): LatLon | null {
  const q = toLocal(p1, p2);
  const d1 = { x: Math.sin(toRad(b1)), y: Math.cos(toRad(b1)) }, d2 = { x: Math.sin(toRad(b2)), y: Math.cos(toRad(b2)) };
  const cross = d1.x * d2.y - d1.y * d2.x;
  if (Math.abs(cross) < 1e-6) return null;
  const t1 = (q.x * d2.y - q.y * d2.x) / cross, t2 = (q.x * d1.y - q.y * d1.x) / cross;
  if (t1 <= 0 || t2 <= 0) return null;
  return fromLocal(p1, { x: d1.x * t1, y: d1.y * t1 });
}

/**
 * A position entry in the form formatPosition shows ("N4000.0W07000.0": degrees and minutes to a tenth), or null when
 * it is not that form or its minutes, latitude or longitude are out of range (R26).
 */
export function parsePosition(text: string): LatLon | null {
  const m = /^([NS])(\d{2})(\d{2}\.\d)([EW])(\d{3})(\d{2}\.\d)$/.exec(text);
  if (!m) return null;
  const [latDeg, latMin, lonDeg, lonMin] = [m[2], m[3], m[5], m[6]].map(Number);
  if (latMin >= 60 || lonMin >= 60) return null;
  const lat = latDeg + latMin / 60, lon = lonDeg + lonMin / 60;
  if (lat > 90 || lon > 180) return null;
  return { lat: m[1] === "S" ? -lat : lat, lon: m[4] === "W" ? -lon : lon };
}

export function formatPosition(p: LatLon) {
  const part = (value: number, positive: string, negative: string, width: number) => {
    const abs = Math.abs(value), deg = Math.floor(abs), min = (abs - deg) * 60;
    return `${value >= 0 ? positive : negative}${String(deg).padStart(width, "0")}${min.toFixed(1).padStart(4, "0")}`;
  };
  return `${part(p.lat, "N", "S", 2)}${part(p.lon, "E", "W", 3)}`;
}

// ---------------------------------------------------------------------------------------------- state shapes

/** A route entry: a waypoint (optionally a holding or search fix), or the discontinuity a direct-to leaves. */
export type LegSource = "SID" | "STAR" | "APPR" | "MISSED";
/**
 * ARINC 424 path terminators. A waypoint leg ends at a fix: TF (track between fixes, the default), CF (a published
 * course to the fix), DF (direct from wherever the leg begins) or RF (a constant-radius arc about a centre). A
 * conditional leg ends at an event instead of a place: CA, FA and VA climb on a course, track or heading to an
 * altitude; VI flies a heading until the next leg is intercepted; VM and FM fly a heading or track until the crew
 * takes over (manual termination).
 */
export type FixPath = "TF" | "CF" | "DF" | "RF";
export type ConditionalPath = "CA" | "FA" | "VA" | "VI" | "VM" | "FM";
export type Leg =
  | {
    kind: "wpt"; ident: string; altitude?: string; qualifier?: "/H" | "/S" | "/O"; via?: string; source?: LegSource;
    path?: FixPath; course?: number; arc?: { centre: LatLon; turn: "L" | "R" };
    /** A speed constraint at the fix, knots. */
    speed?: number;
    /**
     * Procedure data carried with the leg (Stage C): the coded speed limit, and the hold the procedure codes at the fix.
     * Data only: what the flight does with them is its own business.
     */
    speedLimit?: SpeedLimit; hold?: ProcedureHold;
  }
  | { kind: "cond"; path: ConditionalPath; course: number; altitude?: number; via?: string; source?: LegSource; speedLimit?: SpeedLimit }
  | { kind: "disco" };

/** How a conditional leg shows on LEGS: (3000) for an altitude, (INTC) for an intercept, (VECTOR) for manual. */
export const conditionalLabel = (leg: Extract<Leg, { kind: "cond" }>) =>
  leg.path === "VI" ? "(INTC)" : leg.path === "VM" || leg.path === "FM" ? "(VECTOR)" : `(${leg.altitude ?? "----"})`;

/** A lateral offset: nm positive right of the route, starting after `start` and ending at `end` when given. */
export type Offset = { nm: number; start?: string; end?: string };

export type ProcedureChoice = { ident: string; transition?: string };
export type Route = {
  origin: string; dest: string; coRoute: string; flightNo: string; runway?: string; legs: Leg[]; hold?: Hold;
  sid?: ProcedureChoice; star?: ProcedureChoice; approach?: ProcedureChoice;
  /** Where the enroute legs were joined to the departure and the arrival, so rebuilding the route keeps the join. */
  departureJoin?: string; arrivalJoin?: string;
  offset?: Offset;
};

/** A hold is INACTIVE while only in a modification, ARMED once executed, IN PROGRESS from the first fix crossing. */
export type HoldStatus = "INACTIVE" | "ARMED" | "IN PROGRESS" | "EXIT ARMED";
export type HoldEntry = "DIRECT" | "TEARDROP" | "PARALLEL";
/**
 * The hold's EXIT TYPE (M300 10-10): MANUAL (held until the crew arms EXIT HOLD), ONCE (a course reversal, HF: out at
 * the first fix crossing after the entry), AT TGT ALT (HA: out at the first fix crossing with the target altitude
 * reached). `missed` marks the missed-approach hold, which the S300 flies for one racetrack and then leaves (M300 7-16
 * NOTE; plan MISSED-HOLD), whatever its coded exit.
 */
export type HoldExit = "MANUAL" | "ONCE" | "AT TGT ALT";
export type Hold = {
  fix: string; turn: "RIGHT" | "LEFT"; inbound: number; legTime: number | null; legDistance: number | null;
  exit: HoldExit; speed: number; altitude: string; status: HoldStatus; missed?: boolean;
  /** Whole racetracks flown since the entry: the missed-approach hold leaves after one. */
  circuits?: number;
};

/**
 * The standard entry for a hold, from the track the aircraft arrives on. Relative to the inbound course and seen
 * from the holding side, the direct sector is 180 degrees wide, the teardrop 70 and the parallel 110.
 */
export function holdEntry(arrivalTrack: number, inbound: number, turn: "RIGHT" | "LEFT"): HoldEntry {
  const relative = ((turn === "RIGHT" ? arrivalTrack - inbound : inbound - arrivalTrack) % 360 + 360) % 360;
  if (relative <= 110 || relative >= 290) return "DIRECT";
  return relative <= 180 ? "TEARDROP" : "PARALLEL";
}

export type SarPattern = "SQUARE" | "LADDER" | "SECTOR";
export const SAR_PATTERNS: readonly SarPattern[] = ["SQUARE", "LADDER", "SECTOR"];
export type SarStatus = "ARMED" | "IN PROGRESS";
export type Sar = {
  id: Record<SarPattern, string>; refId: string | null; relativeBearing: number | null; distance: number | null;
  trackSpacing: number; legLength: number; diameter: number; angle: number; sarBearing: number;
  /** The pattern ACTIVATE> put in the modification, waiting for EXEC. */
  pending: SarPattern | null;
  active: SarPattern | null; status: SarStatus | null;
};

/** The highest ground speed at which the aircraft can fly the pattern's turns inside its track spacing. */
export function maxSarGroundSpeed(sar: Sar, pattern: SarPattern) {
  const size = pattern === "SECTOR" ? sar.diameter / 2 : sar.trackSpacing;
  return Math.min(250, Math.round(60 + size * 40));
}

export type Message = { text: string; alert: boolean };

export type Uplink = { id: number; at: Date; text: string; response: "OPEN" | "WILCO" | "UNABLE" | "STANDBY" };

/**
 * Whether an uplink still needs a crew response. STANDBY acknowledges without answering, so the uplink stays
 * outstanding (it keeps the ATC lamp lit and can still be answered) until WILCO or UNABLE (R14).
 */
export const isOutstanding = (uplink: Uplink) => uplink.response === "OPEN" || uplink.response === "STANDBY";

// ---------------------------------------------------------------------------------------------- pages

export type CorePageId =
  | "MENU" | "INIT_REF" | "IDENT" | "POS" | "MSG_RECALL" | "LEGS" | "PROG" | "RADIO" | "FUEL" | "HOLD" | "FIX" | "PREDEF"
  | "VNAV" | "TIMER" | "MAINT" | "PLAN_DATA" | "USER_WPT";
export type PlanningPageId = "RTE" | "DEP_ARR" | "DEPARTURES" | "ARRIVALS" | "NAV_DATA" | "SELECT_WPT" | "SEC_FPLN";
export type NavPageId = "NAV_STATUS" | "NAV_OPTIONS" | "GPS_STATUS" | "POS_SENSORS" | "PREDICT_RAIM" | "SAT_DESELECT";
export type TacticalPageId = "TACT" | "SAR" | "TACT_APPR" | "HOVER" | "RNDZ" | "MOVING_WPT" | "TDN";
export type DatalinkPageId = "ATC" | "FMC_COMM" | "ANS";
export type PageId = CorePageId | PlanningPageId | NavPageId | TacticalPageId | DatalinkPageId;

export type Page = {
  pages: (fms: ScriptedFms) => number;
  render: (fms: ScriptedFms, index: number) => (Line | undefined)[];
  /** row 1..6, side L or R, with the current scratchpad text. */
  lsk?: (fms: ScriptedFms, side: "L" | "R", row: number, scratch: string, index: number) => LskResult;
};

// ---------------------------------------------------------------------------------------------- formatting

export const pad = (value: string, length: number) => value.padEnd(length);
export const fixed = (value: number, digits: number) => value.toFixed(digits);
export const three = (value: number) => String(Math.round(value)).padStart(3, "0");
export const hhmm = (date: Date) =>
  `${String(date.getUTCHours()).padStart(2, "0")}${String(date.getUTCMinutes()).padStart(2, "0")}.${Math.floor(date.getUTCSeconds() / 6)}Z`;

export const title = (text: string, page?: string, status?: "ACT" | "MOD"): Line => ({
  left: [
    ...(status ? [{ text: status, color: "cyan" as const, inverse: status === "MOD" }, { text: " " }] : []),
    { text, color: "cyan" },
  ],
  right: page ? { text: page, color: "white", size: "small" } : undefined,
});
export const caption = (left?: string, right?: string, center?: string): Line => ({
  left: left ? { text: left, color: "green", size: "small" } : undefined,
  right: right ? { text: right, color: "green", size: "small" } : undefined,
  center: center ? { text: center, color: "green", size: "small" } : undefined,
});
export const prompt = (text: string): Segment => ({ text, color: "cyan" });
export const value = (text: string, size: "large" | "medium" = "large"): Segment => ({ text, color: "white", size });
export const boxes = (count: number): Segment => ({ text: "□".repeat(count), color: "amber" });
export const dashes = (count: number): Segment => ({ text: "-".repeat(count), color: "white" });
export const medium = (text: string, color: CduColor = "white"): Segment => ({ text, color, size: "medium" });
export const small = (text: string, color: CduColor = "white"): Segment => ({ text, color, size: "small" });
export const simulated = (text = "SIMULATED PAGE"): Line => ({ center: { text, color: "amber", size: "small" } });

export const ICAO = /^[A-Z]{4}$/;
export const WAYPOINT = /^[A-Z0-9]{2,5}$/;

/** A numeric entry within limits, or null when the scratchpad does not hold one. */
export function numberIn(text: string, min: number, max: number, shape = /^[+-]?\d+(\.\d+)?$/) {
  if (!shape.test(text)) return null;
  const n = Number(text);
  return n >= min && n <= max ? n : null;
}

/** Word-wraps free text (datalink and SMS messages) to display lines. */
export function wrap(text: string, width = COLUMNS) {
  const lines: string[] = [];
  let line = "";
  for (const word of text.split(/\s+/).filter(Boolean)) {
    if (line && line.length + 1 + word.length > width) { lines.push(line); line = ""; }
    line = line ? `${line} ${word}` : word.slice(0, width);
  }
  if (line) lines.push(line);
  return lines;
}

export type LskResult = void | "invalid" | "not-allowed" | "not-in-database";
