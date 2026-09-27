import { courseDeg, distanceNm, offset, type Hold, type HoldEntry, type LatLon, type Sar, type SarPattern } from "./fmsModel";
import type { ScriptedFms } from "./scriptedFms";

/**
 * A simple flight model for the test bench: an aircraft that flies what the scripted FMS asks for, the way an
 * autopilot coupled to the FMS would. Lateral guidance follows the active leg with fly-by turn anticipation, holds
 * are flown as racetracks with their standard entry, search patterns are flown with their geometry, and the
 * aircraft climbs or descends to each leg's altitude constraint and follows the vertical path on final.
 *
 * It is a point-mass model with a bank-limited turn and a roll-rate limit, not a flight dynamics model.
 */

/** Navigation map ranges in NM. */
export const MAP_RANGES = [2, 5, 10, 20, 40, 80] as const;

export const MAX_BANK = 25;
const ROLL_RATE = 5;
const MAX_VS = 1000;
const G_TURN = 1091; // turn rate (deg/s) = 1091 * tan(bank) / TAS (kt)

export type GuidanceMode = "LNAV" | "HOLD" | "SAR" | "HDG";
export type Guidance = {
  mode: GuidanceMode;
  /** The leg being flown, for the map; null in heading mode. */
  legFrom: LatLon | null;
  legTo: LatLon | null;
  desiredTrack: number | null;
  /** Nautical miles, positive right of the desired track. */
  crossTrack: number;
  distanceToGo: number | null;
  bankCommand: number;
  targetAltitude: number;
};

type Local = { x: number; y: number };
const rad = (deg: number) => (deg * Math.PI) / 180;
const deg = (r: number) => (r * 180) / Math.PI;
const norm360 = (a: number) => ((a % 360) + 360) % 360;
/** Signed angle from a to b, -180..180. */
export const angleDiff = (a: number, b: number) => ((b - a + 540) % 360) - 180;
const clamp = (v: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, v));

/** East/north nautical miles from an origin; accurate enough over the tens of miles a leg spans. */
function toLocal(origin: LatLon, p: LatLon): Local {
  return { x: (p.lon - origin.lon) * 60 * Math.cos(rad(origin.lat)), y: (p.lat - origin.lat) * 60 };
}

/** Along-track, cross-track and distance to go on the line from `from` to `to`. */
export function legGeometry(from: LatLon, to: LatLon, at: LatLon) {
  const a = toLocal(from, to), p = toLocal(from, at);
  const length = Math.hypot(a.x, a.y) || 1e-9;
  const ux = a.x / length, uy = a.y / length;
  const along = p.x * ux + p.y * uy;
  return { track: norm360(deg(Math.atan2(ux, uy))), crossTrack: p.x * uy - p.y * ux, along, toGo: length - along, length };
}

/** Turn radius in NM at a true airspeed and bank angle. */
export const turnRadius = (tas: number, bank = MAX_BANK) => (tas * tas) / (11.26 * Math.tan(rad(bank))) / 6076.12;

/** The distance before a fly-by waypoint at which the turn onto the next leg begins. */
export const turnLead = (tas: number, courseChange: number) =>
  turnRadius(tas) * Math.tan(rad(Math.min(Math.abs(courseChange), 150) / 2));

/** A leg's altitude constraint as feet: "4500", "1500A" and "5000B" all give their number. */
export const constraintAltitude = (text: string | undefined) => {
  const match = /^(\d+)/.exec(text ?? "");
  return match ? Number(match[1]) : null;
};

// ---------------------------------------------------------------------------------------------- search patterns

/**
 * The track points of a search pattern from its start point. SQUARE is the expanding square (legs of one, one, two,
 * two, three... track spacings, turning right); LADDER is the parallel-track pattern (legs of the leg length,
 * stepping one spacing each time); SECTOR flies three triangles of 120-degree turns through the datum, each turned
 * by the sector angle.
 */
export function sarTrack(start: LatLon, sar: Sar, pattern: SarPattern): LatLon[] {
  const points = [start];
  let at = start;
  const go = (bearing: number, nm: number) => { at = offset(at, norm360(bearing), nm); points.push(at); };
  const b = sar.sarBearing;
  if (pattern === "SQUARE") {
    for (let leg = 0; leg < 12; leg += 1) go(b + 90 * leg, sar.trackSpacing * (Math.floor(leg / 2) + 1));
  } else if (pattern === "LADDER") {
    for (let track = 0; track < 8; track += 1) {
      go(track % 2 === 0 ? b : b + 180, sar.legLength);
      if (track < 7) go(b + 90, sar.trackSpacing);
    }
  } else {
    const r = sar.diameter / 2;
    for (let k = 0; k < 3; k += 1) {
      const heading = b + k * sar.angle;
      go(heading, r);
      go(heading + 120, r);
      go(heading + 240, r);
    }
  }
  return points;
}

// ---------------------------------------------------------------------------------------------- holds

type Segment =
  | { kind: "turn"; heading: number; direction: 1 | -1 }
  | { kind: "heading"; heading: number; seconds: number }
  | { kind: "toFix"; course: number };

/** The racetrack a hold flies after its entry: turn outbound, outbound leg, turn inbound, inbound to the fix. */
function racetrack(hold: Hold, legSeconds: number): Segment[] {
  const s = hold.turn === "RIGHT" ? 1 : -1;
  const outbound = norm360(hold.inbound + 180);
  return [
    { kind: "turn", heading: outbound, direction: s },
    { kind: "heading", heading: outbound, seconds: legSeconds },
    { kind: "turn", heading: hold.inbound, direction: s },
    { kind: "toFix", course: hold.inbound },
  ];
}

/** The entry procedure, flown from the fix, before the first racetrack. */
function entry(hold: Hold, kind: HoldEntry, legSeconds: number, track: number): Segment[] {
  const s = hold.turn === "RIGHT" ? 1 : -1;
  const outbound = norm360(hold.inbound + 180);
  const shortest = (heading: number) => (angleDiff(track, heading) >= 0 ? 1 : -1) as 1 | -1;
  if (kind === "TEARDROP") {
    // Outbound 30 degrees into the holding side, then turn in the holding direction onto the inbound course.
    const heading = norm360(outbound - s * 30);
    return [{ kind: "turn", heading, direction: shortest(heading) }, { kind: "heading", heading, seconds: legSeconds },
      { kind: "turn", heading: hold.inbound, direction: s }, { kind: "toFix", course: hold.inbound }];
  }
  if (kind === "PARALLEL") {
    // Outbound on the non-holding side, then turn back through the holding side to the fix.
    return [{ kind: "turn", heading: outbound, direction: shortest(outbound) }, { kind: "heading", heading: outbound, seconds: legSeconds },
      { kind: "turn", heading: norm360(hold.inbound - s * 45), direction: (-s) as 1 | -1 }, { kind: "toFix", course: hold.inbound }];
  }
  return [];
}

/** The racetrack outline for the map, starting and ending at the fix. */
export function racetrackOutline(fix: LatLon, hold: Hold, groundSpeed: number, tas: number): LatLon[] {
  const s = hold.turn === "RIGHT" ? 1 : -1;
  const r = turnRadius(tas);
  const leg = hold.legDistance ?? ((hold.legTime ?? 1) * groundSpeed) / 60;
  const inbound = hold.inbound, outbound = norm360(inbound + 180);
  const points: LatLon[] = [fix];
  const arc = (center: LatLon, from: number, sweep: number) => {
    for (let i = 1; i <= 12; i += 1) points.push(offset(center, norm360(from + (sweep * i) / 12), r));
  };
  // Turn outbound around a centre abeam the fix on the holding side.
  const c1 = offset(fix, inbound + s * 90, r);
  arc(c1, inbound - s * 90, s * 180);
  const outboundEnd = offset(points.at(-1)!, outbound, leg);
  points.push(outboundEnd);
  const c2 = offset(outboundEnd, outbound + s * 90, r);
  arc(c2, outbound - s * 90, s * 180);
  points.push(fix);
  return points;
}

// ---------------------------------------------------------------------------------------------- simulator

export class FlightSimulator {
  private readonly fms: ScriptedFms;
  /** True airspeed in knots. */
  tas = 120;
  private bank = 0;
  private holdPlan: { segments: Segment[]; index: number; elapsed: number; loop: Segment[] } | null = null;
  private sarPlan: { points: LatLon[]; index: number } | null = null;
  private last: Guidance;

  constructor(fms: ScriptedFms) {
    this.fms = fms;
    this.last = this.guide();
  }

  get guidance() { return this.last; }
  get bankAngle() { return this.bank; }
  get sarPath() { return this.sarPlan?.points ?? null; }

  /** Flies for dt seconds of simulated time, in steps of at most one second. */
  step(dt: number) {
    let left = dt;
    while (left > 1e-6) {
      const h = Math.min(1, left);
      this.integrate(h);
      left -= h;
    }
    this.fms.tick();
  }

  private integrate(dt: number) {
    const fms = this.fms;
    const guidance = this.guide(dt);
    this.last = guidance;
    // Bank toward the command at the roll-rate limit, then turn at the rate that bank gives.
    this.bank += clamp(guidance.bankCommand - this.bank, -ROLL_RATE * dt, ROLL_RATE * dt);
    const track = norm360(fms.track + (G_TURN * Math.tan(rad(this.bank)) / this.tas) * dt);
    const headwind = fms.wind.speed * Math.cos(rad(fms.wind.direction - track));
    const groundSpeed = Math.max(30, this.tas - headwind);
    const position = offset(fms.position, track, (groundSpeed * dt) / 3600);
    const vs = clamp((guidance.targetAltitude - fms.altitude) * 2, -MAX_VS, MAX_VS);
    const onPath = this.pathVerticalSpeed(groundSpeed);
    const verticalSpeed = onPath ?? vs;
    const altitude = fms.altitude + (verticalSpeed * dt) / 60;
    fms.setAircraft({ position, track, groundSpeed, altitude, verticalSpeed });
  }

  /** On final (FAF sequenced, runway active) the aircraft follows the VNAV path angle down. */
  private pathVerticalSpeed(groundSpeed: number) {
    const fms = this.fms;
    const leg = fms.route.legs[0];
    if (leg?.kind !== "wpt" || !/^RW\d{2}/.test(leg.ident) || !fms.lastSequenced) return null;
    const fafPos = fms.coordinates(fms.lastSequenced), rwyPos = fms.coordinates(leg.ident);
    if (!fafPos || !rwyPos) return null;
    const vpa = Math.atan((fms.vnav.fafAltitude - fms.vnav.runwayElevation) / (distanceNm(fafPos, rwyPos) * 6076.12));
    const pathAltitude = fms.vnav.runwayElevation + distanceNm(fms.position, rwyPos) * 6076.12 * Math.tan(vpa);
    // The path's descent rate, corrected toward the path.
    return -groundSpeed * 101.27 * Math.tan(vpa) + clamp((pathAltitude - fms.altitude) * 2, -300, 300);
  }

  private steer(desiredTrack: number, crossTrack: number) {
    // Intercept at up to 45 degrees, proportional to the cross-track error, then bank toward that track.
    const commanded = desiredTrack - clamp(crossTrack * 40, -45, 45);
    return clamp(angleDiff(this.fms.track, commanded) * 1.0, -MAX_BANK, MAX_BANK);
  }

  private targetAltitude() {
    const leg = this.fms.route.legs[0];
    const hold = this.fms.route.hold;
    if (this.holdPlan && hold) return constraintAltitude(hold.altitude) ?? this.fms.altitude;
    return (leg?.kind === "wpt" ? constraintAltitude(leg.altitude) : null) ?? this.fms.altitude;
  }

  /** Works out the guidance for this instant, and sequences waypoints, holds and patterns as they are reached. */
  private guide(dt = 0): Guidance {
    const fms = this.fms;
    const route = fms.activeRoute;
    const leg = route.legs[0];
    const base = { targetAltitude: this.targetAltitude() };

    if (this.holdPlan && !(route.hold && leg?.kind === "wpt" && leg.ident === route.hold.fix)) this.holdPlan = null;
    if (this.sarPlan && !(fms.sar.status === "IN PROGRESS")) this.sarPlan = null;

    if (this.holdPlan && route.hold) return { ...this.flyHold(route.hold, dt), ...base, mode: "HOLD" };
    if (this.sarPlan) return { ...this.flySar(dt), ...base, mode: "SAR" };

    if (leg?.kind !== "wpt") return { mode: "HDG", legFrom: null, legTo: null, desiredTrack: null, crossTrack: 0, distanceToGo: null, bankCommand: 0, ...base };
    const to = fms.coordinates(leg.ident);
    if (!to) return { mode: "HDG", legFrom: null, legTo: null, desiredTrack: null, crossTrack: 0, distanceToGo: null, bankCommand: 0, ...base };
    const from = fms.activeLegStart;
    const g = legGeometry(from, to, fms.position);

    // Fly-by: start the turn onto the next leg early; fly-over for holding fixes, search starts and /O waypoints.
    const next = route.legs[1];
    const nextTo = next?.kind === "wpt" ? fms.coordinates(next.ident) : undefined;
    const flyOver = leg.qualifier !== undefined || !nextTo;
    const lead = flyOver || !nextTo ? 0 : turnLead(this.tas, angleDiff(g.track, courseDeg(to, nextTo)));
    if (dt > 0 && (g.toGo <= lead || g.toGo <= 0.02)) {
      const result = fms.arrive();
      if (result === "hold") this.startHold();
      if (result === "sar") this.startSar(to);
      return this.guide();
    }
    return { mode: "LNAV", legFrom: from, legTo: to, desiredTrack: g.track, crossTrack: g.crossTrack, distanceToGo: g.toGo, bankCommand: this.steer(g.track, g.crossTrack), ...base };
  }

  private legSeconds(hold: Hold) {
    return hold.legDistance !== null ? (hold.legDistance / Math.max(60, this.fms.groundSpeed)) * 3600 : (hold.legTime ?? 1) * 60;
  }

  private startHold() {
    const hold = this.fms.activeRoute.hold!;
    const seconds = this.legSeconds(hold);
    const loop = racetrack(hold, seconds);
    this.holdPlan = { segments: [...entry(hold, this.fms.holdEntryFlown ?? "DIRECT", seconds, this.fms.track), ...loop], index: 0, elapsed: 0, loop };
  }

  private flyHold(hold: Hold, dt: number): Omit<Guidance, "targetAltitude" | "mode"> {
    const plan = this.holdPlan!;
    const fms = this.fms;
    const fix = fms.coordinates(hold.fix)!;
    const segment = plan.segments[plan.index];
    const advance = () => {
      plan.index += 1;
      plan.elapsed = 0;
      if (plan.index >= plan.segments.length) { plan.segments = racetrack(hold, this.legSeconds(hold)); plan.index = 0; }
    };
    if (segment.kind === "turn") {
      const error = angleDiff(fms.track, segment.heading);
      if (Math.abs(error) < 3 || (Math.sign(error) !== segment.direction && Math.abs(error) < 20)) advance();
      return { legFrom: null, legTo: null, desiredTrack: segment.heading, crossTrack: 0, distanceToGo: null, bankCommand: MAX_BANK * segment.direction };
    }
    if (segment.kind === "heading") {
      plan.elapsed += dt;
      if (plan.elapsed >= segment.seconds) advance();
      return { legFrom: null, legTo: null, desiredTrack: segment.heading, crossTrack: 0, distanceToGo: null, bankCommand: clamp(angleDiff(fms.track, segment.heading), -MAX_BANK, MAX_BANK) };
    }
    // Inbound: track the inbound course to the fix; crossing it completes a circuit (or exits when armed).
    const from = offset(fix, segment.course + 180, 10);
    const g = legGeometry(from, fix, fms.position);
    if (dt > 0 && g.toGo <= 0.02) {
      const result = fms.arrive();
      if (result === "hold") advance();
      else this.holdPlan = null;
    }
    return { legFrom: from, legTo: fix, desiredTrack: g.track, crossTrack: g.crossTrack, distanceToGo: g.toGo, bankCommand: this.steer(g.track, g.crossTrack) };
  }

  private startSar(start: LatLon) {
    const pattern = this.fms.sar.active;
    if (!pattern) return;
    this.sarPlan = { points: sarTrack(start, this.fms.sar, pattern), index: 1 };
  }

  private flySar(dt: number): Omit<Guidance, "targetAltitude" | "mode"> {
    const plan = this.sarPlan!;
    const fms = this.fms;
    const from = plan.points[plan.index - 1], to = plan.points[plan.index];
    const g = legGeometry(from, to, fms.position);
    const after = plan.points[plan.index + 1];
    const lead = after ? turnLead(this.tas, angleDiff(g.track, courseDeg(to, after))) : 0;
    if (dt > 0 && (g.toGo <= lead || g.toGo <= 0.02)) {
      plan.index += 1;
      if (plan.index >= plan.points.length) {
        this.sarPlan = null;
        fms.completeSar();
      }
    }
    return { legFrom: from, legTo: to, desiredTrack: g.track, crossTrack: g.crossTrack, distanceToGo: g.toGo, bankCommand: this.steer(g.track, g.crossTrack) };
  }
}
