import { bearingDeg, courseDeg, distanceNm, offset, type Hold, type HoldEntry, type LatLon, type Leg, type Sar, type SarPattern } from "./fmsModel";
import type { ScriptedFms } from "./scriptedFms";
import type { VerticalPhase } from "./vnav";

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
/** Vertical acceleration limit, fpm per second: the vertical speed changes over seconds, not in one step. */
const VS_RATE = 600;
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

/** Track, cross-track (positive right) and distance to go along an RF arc to its end fix. */
export function arcGeometry(arc: { centre: LatLon; turn: "L" | "R" }, to: LatLon, at: LatLon) {
  const r = distanceNm(arc.centre, to), d = distanceNm(arc.centre, at);
  const a = bearingDeg(arc.centre, at), b = bearingDeg(arc.centre, to);
  const sweep = arc.turn === "R" ? (b - a + 360) % 360 : (a - b + 360) % 360;
  return {
    track: norm360(a + (arc.turn === "R" ? 90 : -90)),
    // The centre is on the inside of the turn: outside the radius is left of a right-hand arc.
    crossTrack: arc.turn === "R" ? -(d - r) : d - r,
    // Past the end fix the sweep wraps to nearly a full circle: that is behind, not ahead.
    toGo: sweep > 330 ? -0.01 : r * rad(sweep),
    along: 0,
    length: 0,
  };
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

/**
 * The vertical mode that commands the aircraft, named for the branch of the controller that produced the vertical
 * speed (not inferred from the motion): basic altitude hold, the tactical descent, the VNAV path (the descent path
 * or the final approach path), DES NOW, a VNAV climb or descent to the target altitude, or level at it. Generic
 * engineering names, not CMA mode annunciations.
 */
export type VerticalMode = "ALT HOLD" | "TDN" | "APPR" | "VNAV PTH" | "DES NOW" | "VNAV CLB" | "VNAV DES" | "VNAV ALT";

/** A recorded change of mode or authority: what happened and the references it set. */
export type ModeEvent = { at: Date; event: string; detail: string };

/**
 * The vertical path at the aircraft's position, for a vertical deviation display: the VNAV descent path, or the final
 * approach path on the final leg. `coupled` says whether the aircraft is being flown on it; an approach path that has
 * not been captured is advisory information only.
 */
export type VerticalPath = { altitude: number; source: "VNAV" | "APPR"; coupled: boolean };

export class FlightSimulator {
  private readonly fms: ScriptedFms;
  /** True airspeed in knots: the speed VNAV flies (cruise speed, or a speed constraint). */
  get tas() { return this.fms.targetSpeed; }
  private bank = 0;
  private lateral: "LNAV" | "HDG" = "LNAV";
  private lnavArmed = false;
  private heading = 0;
  /** Whether the heading was latched by a reversion (heading hold) rather than selected by the crew. */
  private held = false;
  /**
   * Basic altitude hold: the altitude latched when managed vertical guidance was lost or not selected. While set it
   * alone commands the vertical axis, until VNAV is selected again.
   */
  private altitudeHold: number | null = null;
  private vertical: VerticalMode = "VNAV ALT";
  /** Whether the FMS had failed at the last step, to catch the failure and the recovery as transitions. */
  private fmsFailed = false;
  /** The approach mode: off, armed, or captured on the final leg (the only mode that descends beyond the FAF). */
  private approach: "OFF" | "ARMED" | "CAPTURED" = "OFF";
  /** The FMS's go-around count at the last step, to take each accepted TOGA as a transition (watchGoAround). */
  private goArounds: number;
  private events: ModeEvent[] = [];
  /** The FMS's latched VNAV phase at the last step, to record each change as a mode event. */
  private phase: VerticalPhase;
  private path: VerticalPath | null = null;
  private holdPlan: { segments: Segment[]; index: number; elapsed: number; loop: Segment[] } | null = null;
  private sarPlan: { points: LatLon[]; index: number } | null = null;
  private last: Guidance;

  constructor(fms: ScriptedFms) {
    this.fms = fms;
    this.goArounds = fms.goArounds;
    this.phase = fms.verticalPhase;
    this.last = this.guide();
  }

  get guidance() { return this.last; }
  get verticalMode() { return this.vertical; }
  get approachMode() { return this.approach; }
  /** The vertical path here, or null where there is none (climb, cruise, or no computable path). */
  get verticalPath() { return this.path; }

  /** The final approach path altitude at the aircraft, from the FAF (at its corrected altitude) to the runway. */
  private finalPathAltitude(): number | null {
    const fms = this.fms;
    const leg = fms.activeRoute.legs[0];
    if (!this.onFinal || leg?.kind !== "wpt" || !fms.lastSequenced) return null;
    const fafPos = fms.coordinates(fms.lastSequenced), rwyPos = fms.coordinates(leg.ident);
    if (!fafPos || !rwyPos) return null;
    const tan = (fms.fafAltitudeCorrected - fms.vnav.runwayElevation) / (distanceNm(fafPos, rwyPos) * 6076.12);
    return fms.vnav.runwayElevation + distanceNm(fms.position, rwyPos) * 6076.12 * tan;
  }

  /** The VNAV descent path altitude at the aircraft, once past the top of descent; null otherwise. */
  private descentPathAltitude(): number | null {
    const profile = this.fms.profile();
    const first = profile.points[0];
    if (!profile.descending || !first || first.distance === null || first.altitude === null) return null;
    return Math.min(this.fms.vnav.cruiseAltitude, first.altitude + first.distance * 6076.12 * Math.tan(rad(this.fms.vnav.pathAngle)));
  }

  /** On the final leg: the FAF has been sequenced and the runway is the active waypoint. */
  private get onFinal() {
    const leg = this.fms.activeRoute.legs[0];
    return leg?.kind === "wpt" && /^RW\d{2}/.test(leg.ident) && this.fms.lastSequenced !== null;
  }

  /**
   * The laboratory approach contract (Q-A1, a labelled engineering assumption): the approach captures on the final
   * leg only when armed, with valid approach capability (ILS, or LPV with integrity), LNAV engaged and the aircraft
   * within 1 NM of the final course and not moving away from it. Loss of capability after capture drops the approach to a latched altitude
   * hold; its return does not re-capture, because the approach is disarmed and must be armed again.
   *
   * APPR is an arm and disengage control. Before capture, pressing it off disarms. After capture, pressing it off, or
   * leaving LNAV (HDG SEL), cancels the approach: the aircraft levels in a latched altitude hold at the altitude it had,
   * and VNAV or TOGA must be selected to go on. TOGA leaves the approach with a climb (watchGoAround).
   */
  private previousCrossTrack: number | null = null;

  private updateApproach(crossTrack: number) {
    const converging = this.previousCrossTrack === null || Math.abs(crossTrack) <= Math.abs(this.previousCrossTrack) + 1e-6;
    this.previousCrossTrack = crossTrack;
    const fms = this.fms;
    const capable = fms.approachType === "ILS" || fms.approachType === "LPV";
    if (this.approach === "CAPTURED") {
      if (!this.onFinal || fms.hasCondition("fmsFail")) { this.approach = fms.approachArmed ? "ARMED" : "OFF"; return; }
      const cancel = !fms.approachArmed ? "APPR pressed off" : this.lateral !== "LNAV" ? "HDG SEL" : null;
      if (cancel) {
        this.approach = "OFF";
        fms.armApproach(false);
        this.altitudeHold = Math.round(fms.altitude);
        this.record("APPR CANCELLED", `${cancel}; ALT HOLD ${this.altitudeHold} FT`);
        return;
      }
      if (!capable) {
        this.approach = "OFF";
        fms.armApproach(false);
        this.altitudeHold = Math.round(fms.altitude);
        this.record("APPR LOST", `approach capability lost (${fms.approachType ?? "none"}); ALT HOLD ${this.altitudeHold} FT`);
      }
      return;
    }
    if (fms.approachArmed && capable && this.onFinal && this.lateral === "LNAV" && Math.abs(crossTrack) < 1 && converging && this.altitudeHold === null) {
      this.approach = "CAPTURED";
      this.record("APPR CAPTURED", `${fms.approachType} final approach path`);
      return;
    }
    this.approach = fms.approachArmed ? "ARMED" : "OFF";
  }
  /** Mode and authority changes, oldest first: failure, reversion, recovery, LNAV lost. */
  get modeEvents(): readonly ModeEvent[] { return this.events; }

  private record(event: string, detail: string) { this.events = [...this.events, { at: this.fms.now, event, detail }]; }

  /**
   * The laboratory reversion on FMS failure (a labelled engineering assumption, not CMA installation behaviour):
   * managed guidance becomes invalid, the heading (true) and altitude at that moment are latched once, and basic
   * heading and altitude hold fly them through the aircraft's own dynamics. On recovery nothing managed resumes by
   * itself: LNAV and VNAV must be selected again.
   */
  private watchFailure() {
    const failed = this.fms.hasCondition("fmsFail");
    if (failed && !this.fmsFailed) {
      this.lateral = "HDG";
      this.lnavArmed = false;
      this.heading = Math.round(norm360(this.fms.track));
      this.held = true;
      this.altitudeHold = Math.round(this.fms.altitude);
      this.record("FMS FAILURE", `managed guidance invalid; HDG HOLD ${String(this.heading).padStart(3, "0")}°T, ALT HOLD ${this.altitudeHold} FT`);
    } else if (!failed && this.fmsFailed) {
      this.record("FMS RECOVERED", "basic modes kept; select LNAV and VNAV to resume managed guidance");
    }
    this.fmsFailed = failed;
  }

  /**
   * The laboratory go-around (a labelled engineering assumption, not a certified TOGA law): an accepted TOGA makes the
   * missed approach active (ScriptedFms.goAround), ends any approach mode, releases a latched altitude hold, and climbs
   * in VNAV to the missed approach altitude. It is taken the same way whether the approach was captured, cancelled, or
   * lost its integrity, so the one control never means two things. TOGA is refused while the FMS has failed.
   */
  private watchGoAround() {
    if (this.fms.goArounds === this.goArounds) return;
    this.goArounds = this.fms.goArounds;
    const released = this.altitudeHold;
    this.altitudeHold = null;
    this.approach = "OFF";
    this.previousCrossTrack = null;
    this.record("GO AROUND", `missed approach active; VNAV climbs on the missed approach altitudes${released === null ? "" : `; ALT HOLD ${released} FT released`}`);
  }

  /** VNAV: managed vertical guidance again, after an altitude hold. Refused while the FMS has failed. */
  engageVnav() {
    if (this.fms.hasCondition("fmsFail") || this.altitudeHold === null) return false;
    this.altitudeHold = null;
    this.record("VNAV SELECTED", "managed vertical guidance");
    return true;
  }

  get altitudeHoldReference() { return this.altitudeHold; }
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
    this.watchFailure();
    this.watchGoAround();
    const guidance = this.guide(dt);
    this.updateApproach(guidance.crossTrack);
    this.last = guidance;
    // Bank toward the command at the roll-rate limit, then turn at the rate that bank gives.
    this.bank += clamp(guidance.bankCommand - this.bank, -ROLL_RATE * dt, ROLL_RATE * dt);
    const track = norm360(fms.track + (G_TURN * Math.tan(rad(this.bank)) / this.tas) * dt);
    const headwind = fms.wind.speed * Math.cos(rad(fms.wind.direction - track));
    const groundSpeed = Math.max(30, this.tas - headwind);
    // The aircraft moves from where it really is; guidance above steered it from where the FMS believes it is.
    const position = offset(fms.truePosition, track, (groundSpeed * dt) / 3600);
    const vs = clamp((guidance.targetAltitude - fms.altitude) * 2, -MAX_VS, MAX_VS);
    // The final approach path first, then the VNAV descent path, then climbing or holding the target altitude. A hold
    // or an altitude-terminated leg keeps its own altitude.
    const ownAltitude = this.holdPlan || fms.activeRoute.legs[0]?.kind === "cond";
    // A tactical descent flies its own angle down to its altitude.
    const tdnAngle = fms.tdn.active ? fms.tdnAngle() : null;
    const tdn = tdnAngle === null ? null : -groundSpeed * 101.27 * Math.tan(rad(tdnAngle));
    // Altitude hold, when latched, is the only vertical authority; otherwise the managed branches in order.
    let verticalSpeed: number;
    if (this.altitudeHold !== null) { verticalSpeed = clamp((this.altitudeHold - fms.altitude) * 2, -MAX_VS, MAX_VS); this.vertical = "ALT HOLD"; }
    else if (tdn !== null) { verticalSpeed = tdn; this.vertical = "TDN"; }
    else {
      const final = this.pathVerticalSpeed(groundSpeed);
      const descent = final === null && !ownAltitude ? this.descentVerticalSpeed(groundSpeed) : null;
      if (final !== null) { verticalSpeed = final; this.vertical = "APPR"; }
      else if (descent !== null) { verticalSpeed = descent; this.vertical = fms.vnav.desNow ? "DES NOW" : "VNAV PTH"; }
      else { verticalSpeed = vs; this.vertical = guidance.targetAltitude > fms.altitude + 50 ? "VNAV CLB" : guidance.targetAltitude < fms.altitude - 50 ? "VNAV DES" : "VNAV ALT"; }
    }
    // The commanded vertical speed is reached at the vertical acceleration limit, so captures have a transient.
    verticalSpeed = fms.verticalSpeed + clamp(verticalSpeed - fms.verticalSpeed, -VS_RATE * dt, VS_RATE * dt);
    const altitude = fms.altitude + (verticalSpeed * dt) / 60;
    const trackError = guidance.desiredTrack === null ? 0 : angleDiff(guidance.desiredTrack, track);
    fms.setAircraft({ position, track, groundSpeed, altitude, verticalSpeed, crossTrack: guidance.crossTrack, trackError });
    // The path for the deviation display: the final approach path on final (coupled only when captured), otherwise the
    // descent path. None while the FMS has failed: it computes nothing to show.
    const final = this.fms.hasCondition("fmsFail") ? null : this.finalPathAltitude();
    const descentPath = final === null && !this.fms.hasCondition("fmsFail") ? this.descentPathAltitude() : null;
    this.path = final !== null ? { altitude: final, source: "APPR", coupled: this.approach === "CAPTURED" }
      : descentPath !== null ? { altitude: descentPath, source: "VNAV", coupled: this.vertical === "VNAV PTH" && this.altitudeHold === null } : null;
    fms.updateNavigation(dt);
    fms.updatePerformance(dt);
    if (fms.verticalPhase !== this.phase) { this.phase = fms.verticalPhase; this.record(`VNAV ${this.phase}`, fms.verticalPhaseReason); }
    // DES NOW ends once the aircraft is on the descent path: the path, rising behind the active fix, has come down to it.
    if (fms.vnav.desNow) {
      const first = fms.profile().points[0];
      const onPath = first?.distance != null && first.altitude !== null
        && first.altitude + first.distance * 6076.12 * Math.tan(rad(fms.vnav.pathAngle)) <= fms.altitude + 50;
      if (onPath) fms.vnav.desNow = false;
    }
  }

  /**
   * VNAV PTH descent: past the top of descent (or after DES NOW) the aircraft follows the planned path. Below the path
   * it holds its altitude until the path comes down to it; after DES NOW it descends at 1000 fpm to capture it.
   */
  private descentVerticalSpeed(groundSpeed: number) {
    const fms = this.fms;
    const profile = fms.profile();
    const first = profile.points[0];
    // No descent is flown toward a point whose distance is not known, and beyond the FAF only the approach descends.
    if (this.onFinal && this.approach !== "CAPTURED") return null;
    if (!first || first.distance === null || first.altitude === null || !profile.endOfDescent || (!profile.descending && !fms.vnav.desNow)) return null;
    const tan = Math.tan(rad(fms.vnav.pathAngle));
    // DES NOW: 1000 fpm down to the planned altitude at the active fix, levelling there, until the path comes down to
    // the aircraft (integrate ends DES NOW there). Never upward.
    if (fms.vnav.desNow) return clamp((first.altitude - fms.altitude) * 2, -MAX_VS, 0);
    const pathAltitude = Math.min(fms.vnav.cruiseAltitude, first.altitude + first.distance * 6076.12 * tan);
    const above = fms.altitude - pathAltitude;
    if (above < -50) return 0;
    return -groundSpeed * 101.27 * tan + clamp(-above * 2, -300, 300);
  }

  /** With the approach captured on final, the aircraft follows the final approach path angle down (active route only). */
  private pathVerticalSpeed(groundSpeed: number) {
    const fms = this.fms;
    const leg = fms.activeRoute.legs[0];
    if (this.approach !== "CAPTURED" || leg?.kind !== "wpt" || !fms.lastSequenced) return null;
    const fafPos = fms.coordinates(fms.lastSequenced), rwyPos = fms.coordinates(leg.ident);
    if (!fafPos || !rwyPos) return null;
    const vpa = Math.atan((fms.fafAltitudeCorrected - fms.vnav.runwayElevation) / (distanceNm(fafPos, rwyPos) * 6076.12));
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
    // The commanded target is the one the controlling authority flies: a latched altitude hold, when there is one.
    if (this.altitudeHold !== null) return this.altitudeHold;
    const leg = this.fms.activeRoute.legs[0];
    const hold = this.fms.activeRoute.hold;
    if (this.holdPlan && hold) return constraintAltitude(hold.altitude) ?? this.fms.altitude;
    if (leg?.kind === "cond" && leg.altitude !== undefined) return Math.max(leg.altitude, this.fms.altitude);
    if (this.fms.tdn.active || this.fms.tdn.level) return this.fms.tdn.targetAltitude;
    // On final without a captured approach, nothing authorizes a descent below the FAF altitude.
    if (this.onFinal && this.approach !== "CAPTURED") return Math.max(this.fms.fafAltitudeCorrected, Math.min(this.fms.altitude, this.fms.vnav.cruiseAltitude));
    // VNAV: in the climb, the cruise altitude or the lowest restriction ahead; in the descent, the planned altitude at
    // the active waypoint (in the latched descent phase never above the aircraft: computeProfile plans no climb there).
    const profile = this.fms.profile();
    if (profile.descending) return profile.points[0]?.altitude ?? this.fms.altitude;
    return Math.max(profile.climbCap, Math.min(this.fms.altitude, this.fms.vnav.cruiseAltitude));
  }

  // ------------------------------------------------------------------ selected and managed lateral guidance

  /** HDG SEL: fly a selected heading. LNAV stays armed if it was, and captures the route when the aircraft nears it. */
  selectHeading(heading: number) {
    this.lateral = "HDG";
    this.heading = norm360(heading);
    this.held = false;
  }

  /** LNAV: engages at once when the aircraft is close to the active leg, otherwise arms until it gets there. */
  armLnav() {
    if (this.lateral === "LNAV" || this.fms.hasCondition("fmsFail")) return;
    this.lnavArmed = true;
  }

  get lateralMode() { return this.lateral; }
  get lnavIsArmed() { return this.lnavArmed; }
  get selectedHeading() { return this.heading; }
  get headingHeld() { return this.held; }

  /** Works out the guidance for this instant, and sequences waypoints, holds and patterns as they are reached. */
  private guide(dt = 0): Guidance {
    // With the FMS failed there is no managed guidance to consume: basic heading hold only.
    if (this.fms.hasCondition("fmsFail")) {
      return {
        mode: "HDG", legFrom: null, legTo: null, desiredTrack: null, crossTrack: 0, distanceToGo: null,
        bankCommand: clamp(angleDiff(this.fms.track, this.heading), -MAX_BANK, MAX_BANK), targetAltitude: this.altitudeHold ?? this.fms.altitude,
      };
    }
    const managed = this.managedGuidance(dt);
    // LNAV with no leg to fly (a discontinuity or the end of the route) is lost: heading hold on the current track.
    if (this.lateral === "LNAV" && managed.mode === "HDG" && managed.desiredTrack === null) {
      this.lateral = "HDG";
      this.heading = Math.round(norm360(this.fms.track));
      this.held = true;
      if (dt > 0) this.record("LNAV LOST", `no active leg; HDG HOLD ${String(this.heading).padStart(3, "0")}°T`);
    }
    if (this.lateral === "LNAV") return managed;
    // Capture when the managed path is close and the aircraft is not heading away from it.
    if (this.lnavArmed && managed.desiredTrack !== null && Math.abs(managed.crossTrack) < 0.6 && Math.abs(angleDiff(this.fms.track, managed.desiredTrack)) < 100) {
      this.lateral = "LNAV";
      this.lnavArmed = false;
      return managed;
    }
    return { ...managed, mode: "HDG", bankCommand: clamp(angleDiff(this.fms.track, this.heading), -MAX_BANK, MAX_BANK) };
  }

  /** The guidance LNAV would fly. With dt > 0 (and LNAV engaged) it also sequences what the aircraft has reached. */
  private managedGuidance(dt: number): Guidance {
    const fms = this.fms;
    const route = fms.activeRoute;
    const leg = route.legs[0];
    const base = { targetAltitude: this.targetAltitude() };
    const sequencing = dt > 0 && this.lateral === "LNAV";

    if (this.holdPlan && !(route.hold && leg?.kind === "wpt" && leg.ident === route.hold.fix)) this.holdPlan = null;
    if (this.sarPlan && !(fms.sar.status === "IN PROGRESS")) this.sarPlan = null;

    if (this.holdPlan && route.hold) return { ...this.flyHold(route.hold, sequencing ? dt : 0), ...base, mode: "HOLD" };
    if (this.sarPlan) return { ...this.flySar(sequencing ? dt : 0), ...base, mode: "SAR" };

    const none = { mode: "LNAV" as const, legFrom: null, legTo: null, desiredTrack: null, crossTrack: 0, distanceToGo: null, bankCommand: 0, ...base };
    if (!leg || leg.kind === "disco") return { ...none, mode: "HDG" };
    if (leg.kind === "cond") return { ...this.flyConditional(leg, route.legs[1], sequencing), ...base };

    const to = fms.coordinates(leg.ident);
    if (!to) return { ...none, mode: "HDG" };
    // A course-to-fix leg is the published course line into the fix; otherwise the line from where the leg began.
    const from = leg.path === "CF" && leg.course !== undefined ? offset(to, leg.course + 180, 30) : fms.activeLegStart;
    const g = leg.path === "RF" && leg.arc ? arcGeometry(leg.arc, to, fms.position) : legGeometry(from, to, fms.position);
    // A lateral offset shifts the path flown; the aircraft intercepts the offset track as it would the route.
    const shift = this.offsetApplies(leg) ? route.offset!.nm : 0;
    const crossTrack = g.crossTrack - shift;

    // Fly-by: start the turn onto the next leg early; fly-over for holding fixes, search starts and /O waypoints.
    const next = route.legs[1];
    const nextTo = next?.kind === "wpt" ? fms.coordinates(next.ident) : undefined;
    const flyOver = leg.qualifier !== undefined || !nextTo || next?.kind === "wpt" && next.path === "RF";
    const outbound = next?.kind === "wpt" && next.path === "CF" && next.course !== undefined ? next.course : nextTo ? courseDeg(to, nextTo) : g.track;
    const lead = flyOver ? 0 : turnLead(this.tas, angleDiff(g.track, outbound));
    if (sequencing && (g.toGo <= lead || g.toGo <= 0.02)) {
      const result = fms.arrive();
      if (result === "hold") this.startHold();
      if (result === "sar") this.startSar(to);
      return this.managedGuidance(0);
    }
    // On an arc, bank into the turn the arc needs, and steer out the error on top of it.
    const feedForward = leg.path === "RF" && leg.arc
      ? (leg.arc.turn === "R" ? 1 : -1) * deg(Math.atan((this.tas * this.tas) / (68625 * Math.max(0.5, distanceNm(leg.arc.centre, to)))))
      : 0;
    return {
      mode: "LNAV", legFrom: leg.path === "RF" ? null : from, legTo: to, desiredTrack: g.track, crossTrack, distanceToGo: g.toGo,
      bankCommand: clamp(feedForward + this.steer(g.track, crossTrack), -MAX_BANK - 5, MAX_BANK + 5), ...base,
    };
  }

  /** Conditional legs: fly the course or heading until the altitude, the intercept, or (for VM/FM) never. */
  private flyConditional(leg: Extract<Leg, { kind: "cond" }>, next: Leg | undefined, sequencing: boolean): Omit<Guidance, "targetAltitude"> {
    const fms = this.fms;
    const headingLeg = leg.path[0] === "V";
    // A heading leg drifts with the wind; a course or track leg corrects for it. Both are flown as a track here, the
    // heading leg without wind correction: its track is the heading plus the drift the wind gives.
    const drift = headingLeg ? deg(Math.asin(clamp((fms.wind.speed * Math.sin(rad(fms.wind.direction + 180 - leg.course))) / this.tas, -1, 1))) : 0;
    const track = norm360(leg.course + drift);
    const result = { mode: "LNAV" as const, legFrom: null, legTo: null, desiredTrack: track, crossTrack: 0, distanceToGo: null, bankCommand: clamp(angleDiff(fms.track, track), -MAX_BANK, MAX_BANK) };
    if (!sequencing) return result;
    let done = false;
    if ((leg.path === "CA" || leg.path === "FA" || leg.path === "VA") && leg.altitude !== undefined) done = fms.altitude >= leg.altitude - 20;
    if (leg.path === "VI" && next?.kind === "wpt") {
      // Intercept: the next leg's line (its course into its fix) is reached.
      const to = fms.coordinates(next.ident);
      const course = next.course ?? (to ? courseDeg(fms.position, to) : track);
      if (to) done = Math.abs(legGeometry(offset(to, course + 180, 30), to, fms.position).crossTrack) < 0.3;
    }
    if (done) { fms.arrive(); return this.managedGuidance(0); }
    return result;
  }

  /** Whether the route's lateral offset applies to this leg: after its start, before its end, never on an approach. */
  private offsetApplies(leg: Extract<Leg, { kind: "wpt" }>) {
    const route = this.fms.activeRoute;
    const offset = route.offset;
    if (!offset || leg.source === "APPR" || leg.source === "MISSED" || leg.path === "RF" || leg.qualifier) return false;
    // The offset begins on the first leg after its start waypoint: while the start is still ahead, it does not apply.
    const ahead = route.legs.some(l => l.kind === "wpt" && l.ident === offset.start);
    return !(offset.start && ahead);
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
