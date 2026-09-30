import { bearingDeg, courseDeg, distanceNm, longitudeDelta, offset, type Hold, type LatLon, type Leg, type Sar, type SarPattern } from "./fmsModel";
import { defaultLegMinutes, entrySegments, holdGeometry, type HoldSegment } from "./holds";
import { groundVelocity, iasFromTas, tasFromIas } from "./kinematics";
import { ACTIVE_PROFILE, fmsBankLimit, type AircraftProfile } from "./profile";
import type { ScriptedFms } from "./scriptedFms";
import { speedCommandIas, verticalArrived, verticalCommand } from "./transition";
import { altitudeMeets, type AltitudeConstraint, type ProfilePoint, type VerticalPhase } from "./vnav";
import type { GuidanceOutputPort } from "./sensorPorts";

/**
 * A simple flight model for the test bench: an aircraft that flies what the scripted FMS asks for, the way an
 * autopilot coupled to the FMS would. Lateral guidance follows the active leg with fly-by turn anticipation, holds
 * are flown as racetracks with their standard entry, search patterns are flown with their geometry, and the
 * aircraft climbs or descends to each leg's altitude constraint and follows the vertical path on final.
 *
 * It is a point-mass model with a bank-limited turn and a roll-rate limit, not a flight dynamics model. The aircraft
 * flies a heading through the air at its true airspeed, which changes at the profile's acceleration limit; the wind
 * carries the air mass, so the track and ground speed are the vector sum (kinematics.ts), with no speed floor.
 */

/** Navigation map ranges in NM. */
export const MAP_RANGES = [2, 5, 10, 20, 40, 80] as const;

export const MAX_BANK = fmsBankLimit(ACTIVE_PROFILE);
/** The modelled pitch (the air-relative flight-path angle) is taken over at least this airspeed, kt, and held within this many degrees. */
const PITCH_SPEED_FLOOR = 30;
const PITCH_LIMIT = 20;
const G_TURN = 1091; // turn rate (deg/s) = 1091 * tan(bank) / TAS (kt)
/** The band in which a selected altitude is captured, feet. */
const ALT_CAPTURE_FT = 20;
/** The radio-height datum hold, fpm per foot of error (a firm hold; the vertical-acceleration limit shapes it). */
const RHT_GAIN = 10;
/**
 * The barometric altitude capture and hold, fpm per foot of error: the same firm law as the radio-height hold (a 6 s
 * time constant). Approaching a preselection the rate eases once it exceeds this times the distance to go, so an
 * 800 fpm climb starts its level-off 80 ft out and settles within the foot about 25 s later, about 0.07 g, well inside
 * the vertical-acceleration limit; the earlier 2 fpm/ft (30 s) left the aircraft short of a captured altitude for
 * minutes. Laboratory value: no published AFCS altitude-loop figure (ADS-33E-PRF heave response, T <= 5 s, is the
 * inner axis; an outer altitude loop is slower).
 */
const ALT_HOLD_GAIN = 10;

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
  return { x: longitudeDelta(origin.lon, p.lon) * 60 * Math.cos(rad(origin.lat)), y: (p.lat - origin.lat) * 60 };
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
export const turnLead = (tas: number, courseChange: number, bank = MAX_BANK, rollRate?: number) => {
  const change = Math.min(Math.abs(courseChange), 150);
  // During roll-in the aircraft covers distance before achieving the design turn rate. Account for half the
  // ramp time in the anticipation distance; the sine makes the allowance disappear for a straight leg.
  const rollLead = rollRate && rollRate > 0 ? (tas / 3600) * (bank / rollRate / 2) * Math.sin(rad(Math.min(change, 90))) : 0;
  return turnRadius(tas, bank) * Math.tan(rad(change / 2)) + rollLead;
};

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
/**
 * The search pattern from its start, to its 80th search waypoint: after it the FMS sequences to the next waypoint of
 * the plan and says END OF SEARCH (M300 11-15). The expanding square turns right through 90 degrees, its legs one,
 * one, two, two, … track spacings; the ladder alternates legs of the leg length with steps of the track spacing to the
 * right; the sector flies triangles of the radius, each turned by the angle from the one before.
 */
export const SAR_SEARCH_WAYPOINTS = 80;
export function sarTrack(start: LatLon, sar: Sar, pattern: SarPattern): LatLon[] {
  const points = [start];
  let at = start;
  const go = (bearing: number, nm: number) => { if (points.length <= SAR_SEARCH_WAYPOINTS) { at = offset(at, norm360(bearing), nm); points.push(at); } };
  const b = sar.sarBearing;
  if (pattern === "SQUARE") {
    for (let leg = 0; points.length <= SAR_SEARCH_WAYPOINTS; leg += 1) go(b + 90 * leg, sar.trackSpacing * (Math.floor(leg / 2) + 1));
  } else if (pattern === "LADDER") {
    for (let track = 0; points.length <= SAR_SEARCH_WAYPOINTS; track += 1) {
      go(track % 2 === 0 ? b : b + 180, sar.legLength);
      go(b + 90, sar.trackSpacing);
    }
  } else {
    const r = sar.diameter / 2;
    for (let k = 0; points.length <= SAR_SEARCH_WAYPOINTS; k += 1) {
      const heading = b + k * sar.angle;
      go(heading, r);
      go(heading + 120, r);
      go(heading + 240, r);
    }
  }
  return points;
}

// ---------------------------------------------------------------------------------------------- holds

/**
 * The racetrack outline for the map, starting and ending at the fix: the ground path the hold flies (holds.ts), at the
 * true airspeed and wind given; still air draws the pattern the wind would stretch. Just the fix when it cannot be flown.
 */
export function racetrackOutline(fix: LatLon, hold: Hold, _groundSpeed: number, tas: number, windSpeed = 0): LatLon[] {
  const legNm = hold.legDistance ?? ((hold.legTime ?? 1) * tas) / 60;
  const geometry = holdGeometry(fix, hold.inbound, hold.turn, tas, windSpeed, legNm, MAX_BANK);
  if (!geometry) return [fix];
  const points: LatLon[] = [fix];
  let at = fix;
  for (const segment of geometry.racetrack) {
    if (segment.kind === "line") { points.push(segment.to); at = segment.to; continue; }
    const from = bearingDeg(segment.centre, at), sweep = segment.turn === "R" ? 180 : -180;
    for (let i = 1; i <= 12; i += 1) points.push(offset(segment.centre, norm360(from + (sweep * i) / 12), segment.radius));
    at = segment.to;
  }
  return points;
}

/** A path of hold-style segments from a start point as points for the map: each arc sampled every 15 degrees or so. */
export function segmentsOutline(start: LatLon, segments: readonly HoldSegment[]): LatLon[] {
  const points: LatLon[] = [start];
  let at = start;
  for (const segment of segments) {
    if (segment.kind === "line") { points.push(segment.to); at = segment.to; continue; }
    const from = bearingDeg(segment.centre, at), to = bearingDeg(segment.centre, segment.to);
    const sweep = segment.turn === "R" ? (to - from + 360) % 360 : -((from - to + 360) % 360);
    const steps = Math.max(1, Math.ceil(Math.abs(sweep) / 15));
    for (let i = 1; i <= steps; i += 1) points.push(offset(segment.centre, norm360(from + (sweep * i) / steps), segment.radius));
    at = segment.to;
  }
  return points;
}

// ---------------------------------------------------------------------------------------------- simulator

/**
 * The vertical mode that commands the aircraft, named for the branch of the controller that produced the vertical
 * speed (not inferred from the motion): basic altitude hold, the tactical descent, the VNAV path (the descent path
 * or the final approach path), DES NOW, a VNAV climb or descent to the target altitude, or level at it. Generic
 * engineering names, not CMA mode annunciations.
 */
export type VerticalMode = "ALT HOLD" | "VS" | "GA" | "TDN" | "APPR" | "VNAV PTH" | "DES NOW" | "VNAV CLB" | "VNAV DES" | "VNAV ALT";

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
  private outputSequence = 0;
  private readonly outputPort: GuidanceOutputPort<Guidance> | null;
  private readonly profile: AircraftProfile["parameters"];
  private get bankLimit() { return this.profile.afcsBankLimit.value; }
  private get steeringLimit() { return fmsBankLimit(this.fms.aircraftProfile); }
  /** True airspeed in knots. It approaches the FMS target speed (cruise, or a speed constraint) at this.profile.longitudinalAccel.value. */
  private airspeed: number;
  get tas() { return this.airspeed; }
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
  /**
   * The crew's selections on the autopilot, which command the vertical axis and the speed under the ADVISORY policy
   * (the helicopter profile, plan A4): the preselected altitude, an engaged vertical speed (null when not in VS), a go-
   * around climb, and the selected speed. The FMS constraints are advisories the crew flies with these.
   */
  private selectedAlt: number;
  private vsTarget: number | null = null;
  private goingAround = false;
  private selectedTas: number;
  /** The FMS's latched VNAV phase at the last step, to record each change as a mode event. */
  private phase: VerticalPhase;
  /**
   * The fly-by turn lead at the active fix, NM: the fix is sequenced this far before it. The descent path reaches the
   * fix's altitude there, so a constraint at a fly-by fix is met where the turn begins, not lost with it.
   */
  private lead = 0;
  /** The planned path altitude at the fix the active leg began from, and the fix it leads to (see descentPath). */
  private legStartPath: { to: string; altitude: number } | null = null;
  private path: VerticalPath | null = null;
  /** The hold being flown: its segments, the one being flown, the straight-leg length, and where the entry ends (-1: none). */
  private holdPlan: { segments: HoldSegment[]; index: number; legNm: number; entryEnd: number; designBank: number } | null = null;
  /** Phase 1: the joining path to JN being flown (joining.ts), for the hover procedure with this id. */
  private joinPlan: { segments: HoldSegment[]; index: number; id: number } | null = null;
  /** The straight-leg length of the hold being flown, NM (null when none). */
  get holdLegNm() { return this.holdPlan?.legNm ?? null; }
  private sarPlan: { points: LatLon[]; index: number } | null = null;
  private last: Guidance;

  constructor(fms: ScriptedFms, outputPort?: GuidanceOutputPort<Guidance>) {
    this.fms = fms;
    // The FMS times the manual hold's next crossing along the path flown here: where the aircraft is in the entry or the
    // racetrack, and which segment ends at the fix passage (Astra F1). In this bench the flight builds the hold path the
    // FMS would own, so it reports it rather than the FMS guessing it.
    fms.attachHoldPath(() => {
      const plan = this.holdPlan;
      return plan ? { segments: plan.segments, index: plan.index, passageAt: plan.index <= plan.entryEnd ? plan.entryEnd : plan.segments.length - 1 } : null;
    });
    this.outputPort = outputPort ?? null;
    this.profile = fms.aircraftProfile.parameters;
    this.hoverHeightFt = this.profile.hoverHeightDefault.value;
    this.airspeed = fms.targetSpeed;
    this.selectedAlt = Math.round(fms.altitude);
    this.selectedTas = fms.vnav.cruiseSpeed;
    // Under the ADVISORY policy the aircraft starts level in altitude hold at its altitude.
    if (fms.aircraftProfile.verticalPolicy === "ADVISORY") { this.altitudeHold = Math.round(fms.altitude); this.vertical = "ALT HOLD"; }
    this.goArounds = fms.goArounds;
    this.phase = fms.verticalPhase;
    this.last = this.guide();
  }

  get guidance() { return this.last; }
  get verticalMode() { return this.vertical; }
  get approachMode() { return this.approach; }
  /** The vertical path here, or null where there is none (climb, cruise, or no computable path). */
  get verticalPath() { return this.path; }

  /**
   * The final approach path altitude at the aircraft. On an RNAV approach it is the selected GPS's (the aircraft less
   * its 117 vertical deviation from the FAS path), and there is none while the GPS gives no vertical guidance
   * (verticalFlag). Otherwise, from the FAF (at its corrected altitude) to the runway.
   */
  private finalPathAltitude(): number | null {
    const fms = this.fms;
    const leg = fms.activeRoute.legs[0];
    if (!this.onFinal || leg?.kind !== "wpt" || !fms.lastSequenced) return null;
    if (this.rnavApproach) {
      const vertical = fms.gpsApproachVertical ? fms.gpsApproach!.verticalFt! : null;
      return vertical === null ? null : fms.altitude - vertical;
    }
    const fafPos = fms.coordinates(fms.finalApproachFix ?? fms.lastSequenced), rwyPos = fms.finalRunway ? fms.coordinates(fms.finalRunway) : undefined;
    if (!fafPos || !rwyPos) return null;
    const tan = (fms.fafAltitudeCorrected - fms.vnav.runwayElevation) / (distanceNm(fafPos, rwyPos) * 6076.12);
    return fms.vnav.runwayElevation + distanceNm(fms.position, rwyPos) * 6076.12 * tan;
  }

  /** The VNAV descent path altitude at the aircraft, once past the top of descent; null otherwise. */
  private descentPathAltitude(): number | null {
    const profile = this.fms.profile();
    const first = profile.points[0];
    if (!profile.descending || !first) return null;
    return this.descentPath(first)?.altitude ?? null;
  }

  /**
   * The descent path at the aircraft, toward the active fix. It rises behind the fix's planned altitude at the path
   * angle from where that altitude is due (the fix, less the fly-by lead, where the fix is sequenced), and is capped by
   * cruise and by the planned altitude at the fix the leg began from, so a leg between two constraints at the same
   * altitude is level. sloped: whether the path descends here.
   */
  private descentPath(first: ProfilePoint): { altitude: number; sloped: boolean } | null {
    if (first.distance === null || first.altitude === null) return null;
    const along = Math.max(0, first.distance - this.lead);
    const raw = first.altitude + along * 6076.12 * Math.tan(rad(this.fms.vnav.pathAngle));
    const start = this.legStartPath?.to === first.ident ? this.legStartPath.altitude : Infinity;
    const ceiling = Math.min(this.fms.vnav.cruiseAltitude, start);
    return { altitude: Math.min(ceiling, raw), sloped: along > 0 && raw < ceiling };
  }

  /** The active approach is an RNAV approach, guided on final by the GPS (GPS phase 3b). */
  private get rnavApproach() { const type = this.fms.approachType; return type !== null && type !== "ILS"; }

  /**
   * The vertical deviation is flagged: on the final leg of an RNAV approach without GPS vertical guidance (a level
   * without it, 117 withdrawn, or no GPS). The display shows the flag instead of a path (GPS phase 3b).
   */
  get verticalFlag() { return !this.fms.s300Advisory && !this.fms.hasCondition("fmsFail") && this.onFinal && this.rnavApproach && !this.fms.gpsApproachVertical; }

  /** On the final leg: the FAF has been sequenced and the runway is the active waypoint. */
  /** On the final approach segment: past the executed approach's FAF, with the runway ahead (ScriptedFms.onFinalSegment). */
  private get onFinal() { return this.fms.onFinalSegment; }

  /**
   * The laboratory approach contract (Q-A1, a labelled engineering assumption): the approach captures on the final
   * leg only when armed, with vertical approach capability (an ILS, or an RNAV approach whose GPS reports LPV or
   * LNAV/VNAV with its vertical deviation valid; an LNAV level has none, GPS phase 3b), LNAV engaged and the aircraft
   * within 1 NM of the final course and not moving away from it. Loss of capability after capture drops the approach to a latched altitude
   * hold; its return does not re-capture, because the approach is disarmed and must be armed again.
   *
   * APPR is an arm and disengage control. Before capture, pressing it off disarms. After capture, pressing it off, or
   * leaving LNAV (HDG SEL), cancels the approach: the aircraft levels in a latched altitude hold at the altitude it had,
   * and VNAV or TOGA must be selected to go on. TOGA leaves the approach with a climb (watchGoAround).
   */
  private previousCrossTrack: number | null = null;
  /**
   * GPS lateral authority on an RNAV final (the GPS review's GPS-01): set at capture, the lateral steers the selected
   * receiver's 116, and keeps steering it after a vertical loss (annunciated LNAV). It ends when 116 may no longer be used
   * (watchGpsLateral), when the approach is cancelled, on TOGA, or off the final leg.
   */
  private gpsLateral = false;

  /** The lateral is steering the selected GPS's 116 deviation, not the route geometry. */
  get gpsLateralActive() { return this.gpsLateral; }

  /**
   * The lateral half of the GPS-01 contract, checked before the guidance is built each step. When the selected receiver's
   * 116 may no longer steer the approach (withdrawn, invalid, or the approach vetoed: gpsApproachAuthority), the approach
   * is lost if it is still captured, to the latched altitude hold, and LNAV reverts to the route. Both are recorded, and
   * the EFIS names the new source (lateralSource): never a silent substitute for the GPS deviation.
   */
  private watchGpsLateral() {
    if (!this.gpsLateral) return;
    const fms = this.fms;
    if (!this.onFinal || fms.hasCondition("fmsFail") || this.lateral !== "LNAV") { this.gpsLateral = false; return; }
    if (fms.gpsApproachLateral) return;
    this.gpsLateral = false;
    const reason = fms.gpsApproachAuthority.reason;
    if (this.approach === "CAPTURED") {
      this.approach = "OFF";
      fms.armApproach(false);
      this.altitudeHold = Math.round(fms.altitude);
      this.record("APPR LOST", `GPS lateral guidance lost (${reason}); ALT HOLD ${this.altitudeHold} FT; LNAV ON ROUTE`);
    } else this.record("GPS LATERAL LOST", `${reason}; LNAV ON ROUTE`);
  }

  private updateApproach(crossTrack: number) {
    const converging = this.previousCrossTrack === null || Math.abs(crossTrack) <= Math.abs(this.previousCrossTrack) + 1e-6;
    this.previousCrossTrack = crossTrack;
    const fms = this.fms;
    // S300 FMS approach arming controls lateral phase/RNP. Its barometric VNAV never arms the AFCS vertical axis.
    if (fms.s300Advisory) { this.approach = "OFF"; this.gpsLateral = false; return; }
    const capable = fms.approachVertical;
    if (this.approach === "CAPTURED") {
      if (!this.onFinal || fms.hasCondition("fmsFail")) { this.approach = fms.approachArmed ? "ARMED" : "OFF"; return; }
      const cancel = !fms.approachArmed ? "APPR pressed off" : this.lateral !== "LNAV" ? "HDG SEL" : null;
      if (cancel) {
        this.approach = "OFF";
        this.gpsLateral = false;
        fms.armApproach(false);
        this.altitudeHold = Math.round(fms.altitude);
        this.record("APPR CANCELLED", `${cancel}; ALT HOLD ${this.altitudeHold} FT`);
        return;
      }
      if (!capable) {
        // Vertical lost: the latched hold. Laterally the GPS's 116 keeps steering while it may (GPS-01).
        this.approach = "OFF";
        fms.armApproach(false);
        this.altitudeHold = Math.round(fms.altitude);
        const lateral = this.gpsLateral ? `; LNAV ON GPS (${fms.gpsApproachAuthority.reason})` : "";
        this.record("APPR LOST", `approach capability lost (${fms.approachType ?? "none"}); ALT HOLD ${this.altitudeHold} FT${lateral}`);
      }
      return;
    }
    if (fms.approachArmed && capable && this.onFinal && this.lateral === "LNAV" && Math.abs(crossTrack) < 1 && converging && (this.altitudeHold === null || this.advisory)) {
      this.approach = "CAPTURED";
      // Under the ADVISORY policy the approach takes the vertical axis from the crew's altitude hold or VS.
      if (this.advisory) { this.altitudeHold = null; this.vsTarget = null; this.goingAround = false; }
      this.gpsLateral = this.rnavApproach;
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
      this.heading = Math.round(norm360(this.fms.heading));
      this.held = true;
      // Under the ADVISORY policy the vertical modes are the autopilot's own (ALT, VS, GA, the radio-height modes): they
      // fly on through an FMS failure. The laboratory airline-style VNAV reverts to a latched altitude hold.
      if (!this.advisory) {
        this.altitudeHold = Math.round(this.fms.altitude);
        this.vsTarget = null;
        this.goingAround = false;
      }
      // F9: the distance to a target is FMS data. TD/H goes on to a stop at its nominal rate and holds there.
      if (this.lowHorizontal?.mode === "TDH") this.lowHorizontal.target = null;
      const vertical = this.altitudeHold !== null ? `ALT HOLD ${this.altitudeHold} FT` : `${this.axisModes.collective} kept (the autopilot's own)`;
      this.record("FMS FAILURE", `managed guidance invalid; HDG HOLD ${String(this.heading).padStart(3, "0")}°T, ${vertical}`);
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
    this.gpsLateral = false;
    // A failure that arrived after TOGA was accepted, before this step, has the authority: the missed approach is the
    // active route, but the failure reversion's basic modes stay until LNAV and VNAV are selected after recovery.
    if (this.fms.hasCondition("fmsFail")) {
      this.approach = "OFF";
      this.previousCrossTrack = null;
      this.record("GO AROUND", `missed approach active; not flown: FMS failed, ALT HOLD ${this.altitudeHold} FT kept`);
      return;
    }
    const released = this.altitudeHold;
    this.altitudeHold = null;
    this.approach = "OFF";
    this.previousCrossTrack = null;
    // Under the ADVISORY policy TOGA is also the autopilot's GA (engageGoAround): taken once, whichever came first.
    if (this.advisory) { if (!this.goingAround) this.startGoAround("missed approach active"); return; }
    this.record("GO AROUND", `missed approach active; VNAV climbs on the missed approach altitudes${released === null ? "" : `; ALT HOLD ${released} FT released`}`);
  }

  /**
   * GA on the autopilot (ADVISORY policy): the vertical and speed axes only, whatever the FMS can do (it may have
   * failed, or have no missed approach, as over the sea). The bench TOGA control does this and the FMS missed-approach
   * request together; it never changes the route or the lateral mode by itself.
   */
  engageGoAround() {
    if (!this.advisory || this.goingAround) return false;
    this.altitudeHold = null;
    this.startGoAround("autopilot GA");
    return true;
  }

  /**
   * The laboratory go-around under the ADVISORY policy: a climb at the go-around rate to the preselected altitude, or,
   * when that is not above the aircraft, to the missed approach hold altitude or 1,000 ft up (a labelled assumption; the
   * crew normally preselects the missed approach altitude). From the low-speed regime the station lock is released and
   * the departure acceleration runs with the climb.
   */
  private startGoAround(cause: string) {
    // The missed approach hold altitude is FMS data: not used when the FMS has failed.
    const missed = this.fms.hasCondition("fmsFail") ? null : constraintAltitude(this.fms.activeRoute.hold?.altitude) ?? null;
    if (this.selectedAlt <= this.fms.altitude + 100) this.selectedAlt = Math.round(missed !== null && missed > this.fms.altitude + 100 ? missed : this.fms.altitude + 1000);
    this.goingAround = true;
    this.vsTarget = null;
    this.lowCollective = null;
    this.tdSpeed = false;
    if (this.lowHorizontal) this.startDeparture();
    this.record("GO AROUND", `${cause}; GA climbs at ${this.profile.goAroundClimbRate.value} FPM to ${this.selectedAlt} FT`);
  }

  /** VNAV: managed vertical guidance again, after an altitude hold. Refused while the FMS has failed. */
  engageVnav() {
    if (this.advisory || this.fms.hasCondition("fmsFail") || this.altitudeHold === null) return false;
    this.altitudeHold = null;
    this.record("VNAV SELECTED", "managed vertical guidance");
    return true;
  }

  /** Under the ADVISORY policy the crew commands the vertical axis and the speed; the FMS constraints are advisories. */
  get advisory() { return this.fms.aircraftProfile.verticalPolicy === "ADVISORY"; }
  get selectedAltitude() { return this.selectedAlt; }

  /**
   * Under the ADVISORY policy, where the crew's selected altitude does not meet the missed approach altitude the FMS
   * plans (Astra F4): shown on the PFD for the crew to reselect, never flown instead of the selection. Only on the
   * approach, going around or on the missed approach, where it is the altitude a go-around levels at; null otherwise,
   * and when the FMS has failed (its missed approach data is then unavailable).
   */
  get missedAltitudeConflict(): { target: AltitudeConstraint; selected: number } | null {
    if (!this.advisory || this.fms.hasCondition("fmsFail")) return null;
    const leg = this.fms.activeRoute.legs[0];
    const onMissed = leg !== undefined && leg.kind !== "disco" && leg.source === "MISSED";
    if (!onMissed && !this.goingAround && !(leg && leg.kind !== "disco" && leg.source === "APPR")) return null;
    const target = this.fms.profile().missedTarget;
    return target && !altitudeMeets(target, this.selectedAlt) ? { target, selected: this.selectedAlt } : null;
  }

  get selectedSpeed() { return this.selectedTas; }
  get verticalSpeedTarget() { return this.vsTarget; }

  /** ALT SEL: preselects the altitude a vertical-speed climb or descent, or a go-around, captures. Moves nothing by itself. */
  selectAltitude(feet: number) {
    if (!Number.isFinite(feet)) return false;
    this.selectedAlt = Math.round(feet);
    this.record("ALT SELECTED", `${this.selectedAlt} FT`);
    return true;
  }

  /**
   * VS: climbs or descends at the given rate (ADVISORY policy only), capturing the preselected altitude when it reaches
   * it; a rate away from the preselection flies on until another mode is chosen. Available with the FMS failed: it is
   * the autopilot's own mode.
   */
  engageVerticalSpeed(fpm: number) {
    if (!this.advisory || !Number.isFinite(fpm)) return false;
    this.vsTarget = clamp(Math.round(fpm), -this.profile.maxVerticalSpeed.value, this.profile.maxVerticalSpeed.value);
    this.altitudeHold = null;
    this.lowCollective = null;
    this.goingAround = false;
    this.record("VS", `${this.vsTarget} FPM to ${this.selectedAlt} FT`);
    return true;
  }

  /** ALT: holds the present altitude (ADVISORY policy only). */
  engageAltitudeHold() {
    if (!this.advisory) return false;
    this.vsTarget = null;
    this.goingAround = false;
    this.lowCollective = null;
    this.altitudeHold = Math.round(this.fms.altitude);
    this.record("ALT HOLD", `${this.altitudeHold} FT`);
    return true;
  }

  /** SPD: the speed the autopilot holds under the ADVISORY policy (knots TAS until IAS is modelled). */
  selectSpeed(knots: number) {
    if (!Number.isFinite(knots)) return false;
    this.selectedTas = clamp(Math.round(knots), 0, this.profile.maximumSpeed.value);
    this.record("SPD SELECTED", `${this.selectedTas} KT`);
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
    fms.refreshSensorInput();
    this.watchFailure();
    this.watchGoAround();
    this.watchGpsLateral();
    this.watchHover();
    const computed = this.guide(dt);
    this.updateApproach(computed.crossTrack);
    // An approach that ended this step (cancelled or lost) latched a hold after the guidance was built: publish the
    // target the hold flies from this first step, not the approach's.
    const guidance = this.altitudeHold !== null && computed.targetAltitude !== this.altitudeHold ? { ...computed, targetAltitude: this.altitudeHold } : computed;
    this.last = guidance;
    this.outputPort?.write({ at: fms.now.getTime(), sequence: ++this.outputSequence,
      status: fms.hasCondition("fmsFail") ? "FAIL" : guidance.desiredTrack === null ? "NCD" : "NORMAL",
      value: fms.hasCondition("fmsFail") ? null : structuredClone(guidance) });
    // The airspeed moves toward the target at the acceleration limit. Bank toward the command at the roll-rate limit,
    // then the heading turns at the rate that bank gives through the air; the wind makes the track and ground speed.
    // Under the ADVISORY policy the crew's selected speed; otherwise the FMS speed (cruise, constraints, rendezvous).
    // In the low-speed regime the hover modes fly the horizontal axes (integrateLowSpeed), wings level.
    let heading: number, track: number, groundSpeed: number, position: LatLon;
    if (this.lowHorizontal) {
      ({ heading, track, groundSpeed, position } = this.integrateLowSpeed(dt));
      this.bank += clamp(-this.bank, -this.profile.rollRate.value * dt, this.profile.rollRate.value * dt);
    } else {
      // The crew selects indicated airspeed (the helicopter profile); the aircraft flies the true airspeed it means here.
      const speedTarget = this.advisory ? tasFromIas(this.selectedTas, fms.altitude) : fms.targetSpeed;
      if (this.tdSpeed) {
        // TD brings the indicated airspeed back to the gate speed at the TD rate, in IAS (the shared command law); the
        // aircraft flies the true airspeed that IAS means at its altitude.
        this.tdIas = speedCommandIas(this.tdIas, this.selectedTas, this.profile.tdDeceleration.value, dt);
        this.airspeed = tasFromIas(this.tdIas, fms.altitude);
        if (this.tdIas <= this.selectedTas + 1e-9) this.tdSpeed = false;
      } else this.airspeed += clamp(speedTarget - this.airspeed, -this.profile.longitudinalAccel.value * dt, this.profile.longitudinalAccel.value * dt);
      this.bank += clamp(guidance.bankCommand - this.bank, -this.profile.rollRate.value * dt, this.profile.rollRate.value * dt);
      heading = norm360(fms.heading + (this.airspeed > 1 ? G_TURN * Math.tan(rad(this.bank)) / this.airspeed : 0) * dt);
      const ground = groundVelocity(this.airspeed, heading, fms.wind);
      // With no ground motion there is no track: the last one stands.
      track = ground.track ?? fms.track;
      groundSpeed = ground.speed;
      // The aircraft moves from where it really is; guidance above steered it from where the FMS believes it is.
      position = offset(fms.truePosition, track, (groundSpeed * dt) / 3600);
    }
    const vs = clamp((guidance.targetAltitude - fms.altitude) * ALT_HOLD_GAIN, -this.profile.maxVerticalSpeed.value, this.profile.maxVerticalSpeed.value);
    // The final approach path first, then the VNAV descent path, then climbing or holding the target altitude. A hold
    // or an altitude-terminated leg keeps its own altitude.
    const ownAltitude = this.holdPlan || fms.activeRoute.legs[0]?.kind === "cond";
    // A tactical descent flies its own angle down to its altitude.
    const tdnAngle = fms.tdn.active ? fms.tdnAngle() : null;
    const tdn = tdnAngle === null ? null : -groundSpeed * 101.27 * Math.tan(rad(tdnAngle));
    // Altitude hold, when latched, is the only vertical authority; otherwise the managed branches in order.
    let verticalSpeed: number;
    // The radio-height modes fly the collective when engaged (lowCollectiveSpeed).
    const low = this.lowCollectiveSpeed(dt);
    // Under the ADVISORY policy altitude hold is the crew's ordinary mode, so the FMS's tactical descent takes the axis
    // from it; otherwise a latched hold is the only vertical authority.
    if (low !== null) verticalSpeed = low;
    else if (this.advisory && tdn !== null && !fms.hasCondition("fmsFail")) { verticalSpeed = tdn; this.vertical = "TDN"; this.altitudeHold = null; this.vsTarget = null; this.goingAround = false; }
    else if (this.altitudeHold !== null) { verticalSpeed = clamp((this.altitudeHold - fms.altitude) * ALT_HOLD_GAIN, -this.profile.maxVerticalSpeed.value, this.profile.maxVerticalSpeed.value); this.vertical = "ALT HOLD"; }
    else if (tdn !== null) { verticalSpeed = tdn; this.vertical = "TDN"; }
    else if (this.advisory) verticalSpeed = this.advisoryVerticalSpeed(groundSpeed);
    else {
      const final = this.pathVerticalSpeed(groundSpeed);
      const descent = final === null && !ownAltitude ? this.descentVerticalSpeed(groundSpeed) : null;
      if (final !== null) { verticalSpeed = final; this.vertical = "APPR"; }
      else if (descent !== null) { verticalSpeed = descent; this.vertical = fms.vnav.desNow ? "DES NOW" : "VNAV PTH"; }
      else { verticalSpeed = vs; this.vertical = guidance.targetAltitude > fms.altitude + 50 ? "VNAV CLB" : guidance.targetAltitude < fms.altitude - 50 ? "VNAV DES" : "VNAV ALT"; }
    }
    // The commanded vertical speed is reached at the vertical acceleration limit, so captures have a transient.
    verticalSpeed = fms.verticalSpeed + clamp(verticalSpeed - fms.verticalSpeed, -this.profile.verticalAccel.value * dt, this.profile.verticalAccel.value * dt);
    const altitude = fms.altitude + (verticalSpeed * dt) / 60;
    const trackError = guidance.desiredTrack === null ? 0 : angleDiff(guidance.desiredTrack, track);
    // Bank and pitch too: they tilt the GPS antennas. The point-mass model has no attitude of its own, so pitch is the
    // air-relative flight-path angle, over at least PITCH_SPEED_FLOOR of airspeed and within PITCH_LIMIT (laboratory).
    // Over the ground it would be meaningless at low speed: a helicopter climbing out of a hover at 1 kt of ground speed
    // is nearly level, not pointing its antennas at the horizon.
    const pitch = clamp(deg(Math.atan(verticalSpeed / 60 / (Math.max(this.airspeed, PITCH_SPEED_FLOOR) * 1.68781))), -PITCH_LIMIT, PITCH_LIMIT);
    fms.setAircraft({ position, track, heading, groundSpeed, tas: this.airspeed, altitude, verticalSpeed, crossTrack: guidance.crossTrack, trackError, bank: this.bank, pitch });
    // The path for the deviation display: the final approach path on final (coupled only when captured), otherwise the
    // descent path. None while the FMS has failed: it computes nothing to show.
    const final = this.fms.hasCondition("fmsFail") ? null : this.finalPathAltitude();
    // No en-route VNAV path under the ADVISORY policy: only the final approach path is shown.
    const descentPath = final === null && !this.advisory && !this.fms.hasCondition("fmsFail") && !this.verticalFlag ? this.descentPathAltitude() : null;
    this.path = final !== null ? { altitude: final, source: "APPR", coupled: this.approach === "CAPTURED" }
      : descentPath !== null ? { altitude: descentPath, source: "VNAV", coupled: this.vertical === "VNAV PTH" && this.altitudeHold === null } : null;
    fms.updateNavigation(dt);
    fms.updatePerformance(dt);
    // The airspeed indication and what the autopilot publishes to the FMS.
    const ias = this.indicatedAirspeed;
    if (this.iasOk && ias < this.profile.unreliableIasBelow.value) this.iasOk = false;
    else if (!this.iasOk && ias >= this.profile.reliableIasAgainAt.value) this.iasOk = true;
    fms.afcs = { hoverHeight: this.hoverHeightFt, ias, ...this.groundVelocityAxes };
    if (fms.verticalPhase !== this.phase && !this.advisory) { this.phase = fms.verticalPhase; this.record(`VNAV ${this.phase}`, fms.verticalPhaseReason); }
    // DES NOW ends once the aircraft is on the descent path: the path, rising behind the active fix, has come down to it.
    if (fms.vnav.desNow && !this.advisory) {
      const first = fms.profile().points[0];
      const path = first ? this.descentPath(first) : null;
      // The path, uncapped by cruise: DES NOW began below it, so it ends where the sloping path reaches the aircraft.
      if (path && path.sloped && path.altitude <= fms.altitude + 50) fms.vnav.desNow = false;
    }
  }

  /**
   * The vertical axis under the ADVISORY policy: a captured approach flies its path; a go-around climbs at the go-around
   * rate and VS at its rate, each capturing the preselected altitude (then altitude hold) when it reaches it.
   */
  private advisoryVerticalSpeed(groundSpeed: number) {
    const final = this.pathVerticalSpeed(groundSpeed);
    if (final !== null) { this.vertical = "APPR"; return final; }
    const alt = this.fms.altitude;
    const rate = this.goingAround ? this.profile.goAroundClimbRate.value : this.vsTarget;
    if (rate === null) {
      // Nothing commands the axis (a tactical descent has ended level, or nothing was selected): hold there.
      this.altitudeHold = this.fms.tdn.level ? this.fms.tdn.targetAltitude : Math.round(alt);
      this.vertical = "ALT HOLD";
      return 0;
    }
    const toward = Math.sign(this.selectedAlt - alt);
    if (Math.abs(this.selectedAlt - alt) <= ALT_CAPTURE_FT && (toward === 0 || Math.sign(rate) === toward || rate === 0)) {
      this.altitudeHold = this.selectedAlt;
      this.record("ALT CAPTURED", `${this.selectedAlt} FT`);
      this.vsTarget = null;
      this.goingAround = false;
      this.vertical = "ALT HOLD";
      return clamp((this.selectedAlt - alt) * ALT_HOLD_GAIN, -this.profile.maxVerticalSpeed.value, this.profile.maxVerticalSpeed.value);
    }
    this.vertical = this.goingAround ? "GA" : "VS";
    // Capture: close to the preselection in the direction of flight, the rate eases toward it.
    return Math.sign(rate) === toward && Math.abs(this.selectedAlt - alt) < Math.abs(rate) / ALT_HOLD_GAIN ? (this.selectedAlt - alt) * ALT_HOLD_GAIN : rate;
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
    if (fms.vnav.desNow) return clamp((first.altitude - fms.altitude) * ALT_HOLD_GAIN, -this.profile.maxVerticalSpeed.value, 0);
    const path = this.descentPath(first)!;
    const above = fms.altitude - path.altitude;
    if (above < -50) return 0;
    // The path's descent rate where it slopes; where it is level only the correction toward it.
    return (path.sloped ? -groundSpeed * 101.27 * tan : 0) + clamp(-above * 2, -300, 300);
  }

  /** With the approach captured on final, the aircraft follows the final approach path angle down (active route only). */
  private pathVerticalSpeed(groundSpeed: number) {
    const fms = this.fms;
    const leg = fms.activeRoute.legs[0];
    if (this.approach !== "CAPTURED" || leg?.kind !== "wpt" || !fms.lastSequenced) return null;
    const fafPos = fms.coordinates(fms.finalApproachFix ?? fms.lastSequenced), rwyPos = fms.finalRunway ? fms.coordinates(fms.finalRunway) : undefined;
    if (!fafPos || !rwyPos) return null;
    // On an RNAV approach the path is the FAS's glide path angle; otherwise the angle from the FAF to the runway.
    const fasAngle = this.rnavApproach ? fms.executedFas?.fas.gpaDeg ?? null : null;
    const vpa = fasAngle !== null ? (fasAngle * Math.PI) / 180 : Math.atan((fms.fafAltitudeCorrected - fms.vnav.runwayElevation) / (distanceNm(fafPos, rwyPos) * 6076.12));
    // On an RNAV approach the correction is toward the GPS's FAS path, from its 117 deviation (GPS phase 3b).
    if (this.rnavApproach && fms.gpsApproachVertical) return -groundSpeed * 101.27 * Math.tan(vpa) + clamp(-fms.gpsApproach!.verticalFt! * 2, -300, 300);
    const pathAltitude = fms.vnav.runwayElevation + distanceNm(fms.position, rwyPos) * 6076.12 * Math.tan(vpa);
    // The path's descent rate, corrected toward the path.
    return -groundSpeed * 101.27 * Math.tan(vpa) + clamp((pathAltitude - fms.altitude) * 2, -300, 300);
  }

  private preferredTurnLeg: Leg | undefined;
  private preferredTurnPending = false;
  private steer(desiredTrack: number, crossTrack: number, leg?: Extract<Leg, { kind: "wpt" }>) {
    // Intercept at up to 45 degrees, proportional to the cross-track error, then bank toward that track.
    const commanded = desiredTrack - clamp(crossTrack * 40, -45, 45);
    let error = angleDiff(this.fms.track, commanded);
    if (leg !== this.preferredTurnLeg) { this.preferredTurnLeg = leg; this.preferredTurnPending = leg?.turnDirection !== undefined; }
    if (this.preferredTurnPending && leg) {
      if (Math.abs(error) < 10) this.preferredTurnPending = false;
      else if (leg.turnDirection === "RIGHT" && error < 0) error += 360;
      else if (leg.turnDirection === "LEFT" && error > 0) error -= 360;
    }
    return clamp(error, -this.steeringLimit, this.steeringLimit);
  }

  private targetAltitude() {
    // The commanded target is the one the controlling authority flies: a latched altitude hold, when there is one.
    if (this.altitudeHold !== null) return this.altitudeHold;
    // Under the ADVISORY policy the crew's preselected altitude, when VS or a go-around is flying toward it.
    if (this.advisory) return this.vsTarget !== null || this.goingAround ? this.selectedAlt : this.fms.altitude;
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

  // ------------------------------------------------------------------ rotorcraft autopilot: hover and low speed

  /**
   * The representative rotorcraft autopilot's hover and low-speed modes (plan Stage B3; values from profile.ts, some
   * borrowed from public AW189 descriptions and marked so; the sequencing logic is laboratory). Axes:
   * - collective (`lowCollective`): RHT holds a radio-height datum; TD descends to the gate height (200 ft RA);
   *   TD/H descends to the hover height; TU climbs to the gate height. Null: the ordinary vertical modes (ALT, VS, GA).
   * - horizontal (`lowHorizontal`): HOV holds a position; TD/H decelerates to a stop, then holds it; TU accelerates
   *   along the heading from low speed (lateral ground velocity held at zero: LVL); ATT holds the last air-velocity
   *   command (the trim attitude, a reduced-order laboratory approximation). Null: coordinated flight (LNAV, HDG).
   * Sensor validity is required throughout the modes that consume it (radio height for the collective modes, the
   * hover feedback for HOV, TD/H and LVL); entry windows apply only when a mode is engaged.
   */
  /** `rate`: the transition's constant rate (fpm) while it is still flying it toward the datum; null once held. */
  private lowCollective: { mode: "RHT" | "TD" | "TDH" | "TU"; datum: number; rate: number | null } | null = null;
  /** The departure's lateral hold lost its feedback: it stays lost until TU or GA is engaged again. */
  private lvlLost = false;
  /**
   * `holding`: TD/H toward a target, still in the gate segment (speed and gate height held until the stopping
   * distance). `captured`: HOV has the capture conditions (GS within 1 kt and within 50 m of its target); until then a
   * HOV after a stop short of or past the target is a recovery, not an arrival.
   */
  private lowHorizontal: { mode: "HOV" | "TDH" | "TU" | "ATT"; target: LatLon | null; speed: number; track: number; holding?: boolean; captured?: boolean; hoverDatum?: number } | null = null;
  /** The FMS transition request being flown: TD first, then TD/H to this MRK (watchHover). */
  private pendingTdh: LatLon | null = null;
  private hoverRequest = 0;
  /** Whether the TD/H being flown (or pending) came from the FMS hover procedure, which can withdraw it. */
  private fmsTransition: number | null = null;
  private hoverRefusal: string | null = null;
  /** In the low-speed regime the air velocity is its own vector (knots north, east), not tied to the heading. */
  private airVelocity: { north: number; east: number } | null = null;
  /** The heading the low-speed regime holds, turned at the yaw-rate limit toward a selection. */
  private hoverHeading = 0;
  private hoverHeightFt: number;
  /** The TD pitch axis: decelerating toward the gate speed at the TD rate until it gets there. */
  private tdSpeed = false;
  /** TD's speed command, in indicated airspeed: it decreases at the TD rate in IAS, as the planner assumes (transition.ts). */
  private tdIas = 0;
  /** The last hover feedback used, for the receiver-change continuity check (R3-02, Astra rev 3.1). */
  private lastFeedback: { at: number; source: 1 | 2; position: LatLon; north: number; east: number } | null = null;
  private lowHeight: "ACTIVE" | "OFF" | null = null;

  get hoverHeight() { return this.hoverHeightFt; }
  /** Whether the indicated airspeed is reliable: below 30 kt it is not, and it is again from 33 kt (the profile's). */
  private iasOk = true;
  /** Indicated airspeed: from the air velocity along the heading (a pitot reads forward, not sideways), ISA. */
  get indicatedAirspeed() {
    const air = this.airVelocity, heading = rad(this.fms.heading);
    const forward = air ? Math.max(0, air.north * Math.cos(heading) + air.east * Math.sin(heading)) : this.airspeed;
    return iasFromTas(forward, this.fms.altitude);
  }
  get iasReliable() { return this.iasOk; }
  /** The ground velocity in aircraft axes: VX along the heading, VY to its right (knots). */
  get groundVelocityAxes(): { vx: number | null; vy: number | null } {
    // As measured (the hover feedback's 166/174, rotated into aircraft axes), never the true motion: a sensor error
    // shows. None without eligible feedback.
    const feedback = this.fms.hoverFeedback;
    if (!feedback) return { vx: null, vy: null };
    const h = (this.fms.heading * Math.PI) / 180;
    return { vx: feedback.north * Math.cos(h) + feedback.east * Math.sin(h), vy: -feedback.north * Math.sin(h) + feedback.east * Math.cos(h) };
  }
  /** LOW HT when the protection is raising the collective; LOW HT OFF when it cannot work (no valid radio height). */
  get lowHeightCaption() { return this.lowHeight === "ACTIVE" ? "LOW HT" : this.lowHeight === "OFF" ? "LOW HT OFF" : null; }
  get inLowSpeedRegime() { return this.lowHorizontal !== null; }
  /** HOV holds its target within the capture conditions: an arrival, as distinct from a recovery toward it. */
  get hoverCaptured() { return this.lowHorizontal?.mode === "HOV" && this.lowHorizontal.captured === true; }

  /** The engaged modes per axis, as the FMA shows them (collective, pitch, roll/yaw): what is actually flying each axis. */
  get axisModes() {
    const c = this.lowCollective, h = this.lowHorizontal;
    const collective = c ? (c.mode === "TDH" ? "TD/H" : c.mode) : this.vertical === "ALT HOLD" ? "ALT" : this.vertical;
    const pitch = h ? (h.mode === "TDH" ? "TD/H" : h.mode === "TU" ? (this.goingAround ? "GA" : "TU") : h.mode) : this.tdSpeed ? "TD" : this.advisory ? "IAS" : "SPD";
    // TU: heading hold shows on the roll axis from 40 kt (the low-speed controller flies it until 45 kt).
    const tuRoll = this.indicatedAirspeed >= this.profile.coordinatedLeaveBelow.value ? "HDG" : this.lvlLost ? "ATT" : "LVL";
    const roll = h ? (h.mode === "TU" ? tuRoll : h.mode === "TDH" ? "TD/H" : h.mode) : this.lateral === "LNAV" ? "NAV" : "HDG";
    return { collective, pitch, roll };
  }

  /**
   * The FMS's hover procedure (ScriptedFms.hover): at TDN it requests the transition, which the autopilot flies as TD
   * (when above the gate height or the gate speed) and then TD/H to MRK; a refusal at TDN (TDN NOT POSSIBLE, TDN DIST
   * SHORT) withdraws roll steering, so NAV gives way to HDG (F8); the request withdrawn (TDN FUNCTION LOST, the
   * procedure ended by a direct-to or a new route) cancels a retained TD/H toward MRK: HOV where it is, or ATT.
   */
  private watchHover() {
    const hover = this.fms.hover;
    if (!this.advisory) return;
    if (hover.request !== this.hoverRequest) {
      this.hoverRequest = hover.request;
      const data = hover.requestData;
      if (data) {
        const ra = this.radio;
        const above = ra.status === "NORMAL" && ra.value! > this.profile.gateHeight.value + ALT_CAPTURE_FT;
        this.fmsTransition = data.id;
        if (above || this.indicatedAirspeed > this.profile.gateSpeed.value + 2) { this.engageTransitionDown(); this.pendingTdh = data.mrk; }
        else this.engageTransitionDownToHover(data.mrk);
      }
    }
    if (hover.refused && hover.refused !== this.hoverRefusal && this.lateral === "LNAV") {
      this.lateral = "HDG";
      this.heading = Math.round(norm360(this.fms.heading));
      this.held = true;
      this.record("NAV REMOVED", `${hover.refused}: roll steering withdrawn; HDG HOLD ${String(this.heading).padStart(3, "0")}°T`);
    }
    this.hoverRefusal = hover.refused;
    const retained = this.fmsTransition !== null && (this.pendingTdh !== null || (this.lowHorizontal?.mode === "TDH" && this.lowHorizontal.target !== null));
    // F2: TDN FUNCTION LOST withdraws the request but the autopilot keeps the plan it accepted; only the procedure ending
    // (a direct-to, a new route) or a new procedure executed over it cancels it.
    if (retained && hover.active?.id !== this.fmsTransition) {
      this.pendingTdh = null;
      this.fmsTransition = null;
      const h = this.lowHorizontal;
      if (h?.mode === "TDH") {
        const feedback = this.fms.hoverFeedback;
        this.lowHorizontal = { ...h, mode: feedback ? "HOV" : "ATT", target: feedback?.position ?? null, holding: false, captured: false };
        if (this.lowCollective?.mode === "RHT" && h.hoverDatum !== undefined) this.lowCollective = { mode: "RHT", datum: this.lowCollective.datum, rate: null };
      }
      this.record("TD/H CANCELLED", "the FMS withdrew the transition request");
    }
    // TD done (both axes arrived): TD/H to MRK.
    if (this.pendingTdh && this.lowCollective?.mode === "RHT" && this.lowCollective.rate === null && !this.tdSpeed) {
      const mrk = this.pendingTdh;
      this.pendingTdh = null;
      if (!this.engageTransitionDownToHover(mrk)) this.record("TD/H REFUSED", "outside its window at the end of TD");
    }
  }

  private get radio() { return this.fms.radioHeight; }

  /** RHT: holds the present radio height (at least the minimum use height). Needs a valid radio height. */
  engageRadioHeight() {
    const ra = this.radio;
    if (!this.advisory || ra.status !== "NORMAL" || ra.value! < this.profile.minimumUseHeight.value) return false;
    this.lowCollective = { mode: "RHT", datum: Math.round(ra.value!), rate: null };
    this.altitudeHold = null; this.vsTarget = null; this.goingAround = false;
    this.record("RHT", `${this.lowCollective.datum} FT RA`);
    return true;
  }

  /** The hover height the AFCS selects (the CMA HOVER page reads it): 30 to 200 ft, or refused. */
  selectHoverHeight(feet: number) {
    if (!(feet >= this.profile.hoverHeightMin.value && feet <= this.profile.hoverHeightMax.value)) return false;
    this.hoverHeightFt = Math.round(feet);
    this.record("HOVER HEIGHT", `${this.hoverHeightFt} FT`);
    return true;
  }

  /** HOV: holds the present position (entry below the coordinated-flight speed, with eligible hover feedback). */
  engageHover() {
    const feedback = this.fms.hoverFeedback;
    if (!this.advisory || !feedback || this.indicatedAirspeed >= this.profile.coordinatedLeaveBelow.value) return false;
    this.enterLowSpeed();
    this.lowHorizontal = { mode: "HOV", target: feedback.position, speed: 0, track: this.fms.track, captured: true };
    this.noteFeedback(feedback);
    if (!this.lowCollective && this.radio.status === "NORMAL") this.engageRadioHeight();
    this.record("HOV", "position hold");
    return true;
  }

  /** TD: from cruise, down to the gate height (200 ft RA, never climbing) and back to the gate speed (80). */
  engageTransitionDown() {
    const ra = this.radio;
    if (!this.advisory || ra.status !== "NORMAL" || ra.value! < this.profile.minimumUseHeight.value || this.lowHorizontal) return false;
    this.lowCollective = { mode: "TD", datum: Math.min(this.profile.gateHeight.value, Math.round(ra.value!)), rate: -this.profile.tdDescentRate.value };
    this.altitudeHold = null; this.vsTarget = null; this.goingAround = false;
    this.selectedTas = this.profile.gateSpeed.value;
    this.tdIas = Math.max(this.indicatedAirspeed, this.profile.gateSpeed.value);
    this.tdSpeed = true;
    this.record("TD", `to ${this.lowCollective.datum} FT RA and ${this.profile.gateSpeed.value} KT`);
    return true;
  }

  /**
   * TD/H: from its window (radio height 30 to 210 ft, speed below 85 kt: AW189 values), decelerates to a stop at the
   * nominal rate along the track, or to a target when one is given (closed loop), and descends to the hover height,
   * never climbing; then RHT and HOV. Needs a valid radio height and eligible hover feedback.
   */
  engageTransitionDownToHover(target: LatLon | null = null) {
    const ra = this.radio, feedback = this.fms.hoverFeedback;
    if (!this.advisory || ra.status !== "NORMAL" || !feedback) return false;
    if (ra.value! < this.profile.tdhMinHeight.value || ra.value! > this.profile.tdhMaxHeight.value || this.indicatedAirspeed >= this.profile.tdhMaxSpeedBelow.value) return false;
    const groundSpeed = Math.hypot(feedback.north, feedback.east);
    this.enterLowSpeed();
    const hoverDatum = Math.min(this.hoverHeightFt, Math.round(ra.value!));
    // Toward a target the gate segment comes first: the height is held there until the deceleration starts.
    this.lowHorizontal = { mode: "TDH", target, speed: groundSpeed, track: target ? courseDeg(feedback.position, target) : this.fms.track, holding: target !== null, hoverDatum };
    this.lowCollective = target ? { mode: "RHT", datum: Math.round(ra.value!), rate: null } : { mode: "TDH", datum: hoverDatum, rate: -this.profile.tdhDescentRate.value };
    this.altitudeHold = null; this.vsTarget = null; this.goingAround = false; this.tdSpeed = false;
    this.noteFeedback(feedback);
    this.record("TD/H", `to ${this.lowCollective.datum} FT RA and 0 KT${target ? " at the target" : ""}`);
    return true;
  }

  /**
   * TU (laboratory departure from hover): from HOV, TD/H or RHT below the coordinated-flight speed, with a valid
   * radio height above the minimum use height. The station lock is released; the pitch axis accelerates along the
   * held heading toward 80 kt and the collective climbs to 200 ft RA; each axis completes on its own.
   */
  engageTransitionUp() {
    const ra = this.radio;
    const from = this.lowHorizontal?.mode === "HOV" || this.lowHorizontal?.mode === "TDH" || this.lowCollective?.mode === "RHT";
    if (!this.advisory || !from || this.indicatedAirspeed >= this.profile.coordinatedLeaveBelow.value || ra.status !== "NORMAL" || ra.value! < this.profile.minimumUseHeight.value) return false;
    this.startDeparture();
    // Above the gate height already, TU climbs no further and never descends: it holds the height it has.
    const datum = Math.max(this.profile.gateHeight.value, Math.round(ra.value!));
    this.lowCollective = { mode: "TU", datum, rate: datum > ra.value! ? this.profile.departureClimbRate.value : null };
    this.record("TU", `to ${datum} FT RA and ${this.profile.climbSpeed.value} KT`);
    return true;
  }

  private startDeparture() {
    this.enterLowSpeed();
    this.lvlLost = false;
    this.lowHorizontal = { mode: "TU", target: null, speed: 0, track: this.fms.track };
    this.selectedTas = this.profile.climbSpeed.value;
    // The departure starts from the aircraft's feedback as it is: no stale sample carried over.
    const feedback = this.fms.hoverFeedback;
    this.lastFeedback = feedback ? { at: this.fms.now.getTime(), ...feedback } : null;
    if (!feedback) this.lvlLost = true;
  }

  private enterLowSpeed() {
    if (this.airVelocity) return;
    const heading = this.fms.heading;
    this.airVelocity = { north: this.airspeed * Math.cos(rad(heading)), east: this.airspeed * Math.sin(rad(heading)) };
    this.hoverHeading = heading;
  }

  private leaveLowSpeed(reason: string) {
    this.lowHorizontal = null;
    this.airVelocity = null;
    this.lateral = "HDG";
    this.heading = Math.round(norm360(this.fms.heading));
    this.held = true;
    this.record("HDG", `${String(this.heading).padStart(3, "0")}°T: ${reason}`);
  }

  private noteFeedback(feedback: NonNullable<ScriptedFms["hoverFeedback"]>) {
    this.lastFeedback = { at: this.fms.now.getTime(), ...feedback };
  }

  /**
   * The hover feedback this step, or null when HOV may not use it: none eligible, or a receiver change that is not
   * continuous (more than one tick since the last valid sample, or a position more than 10 m, or a velocity more than
   * 1 kt, from the last sample propagated). Laboratory limits (Astra, rev 3.1), declared at the top of this file.
   */
  private continuousFeedback(dt: number): { feedback: NonNullable<ScriptedFms["hoverFeedback"]> | null; reason: string | null } {
    const feedback = this.fms.hoverFeedback, last = this.lastFeedback;
    if (!feedback) return { feedback: null, reason: "no eligible hover feedback" };
    if (!last || last.source === feedback.source) return { feedback, reason: null };
    const seconds = (this.fms.now.getTime() - last.at) / 1000;
    if (seconds > Math.max(dt, this.profile.hoverTransferTick.value) + 1e-6) return { feedback: null, reason: `GPS${feedback.source} took over ${seconds.toFixed(2)} s after the last sample` };
    const speed = Math.hypot(last.north, last.east);
    const predicted = speed > 1e-9 ? offset(last.position, deg(Math.atan2(last.east, last.north)), (speed * seconds) / 3600) : last.position;
    const jump = distanceNm(predicted, feedback.position) * 1852;
    const velocityStep = Math.hypot(feedback.north - last.north, feedback.east - last.east);
    if (jump > this.profile.hoverTransferPosition.value) return { feedback: null, reason: `GPS${feedback.source} position ${jump.toFixed(1)} m from the last sample` };
    if (velocityStep > this.profile.hoverTransferVelocity.value) return { feedback: null, reason: `GPS${feedback.source} velocity ${velocityStep.toFixed(1)} kt from the last sample` };
    return { feedback, reason: null };
  }

  /**
   * One step of the low-speed regime: the horizontal mode commands a ground (or air) velocity from the hover feedback,
   * the air velocity follows at the acceleration limit within the sideways and rearward limits, the heading turns
   * toward the held heading at the yaw-rate limit, and the ground velocity is the air velocity plus the wind. Returns
   * the new true position, track, ground speed and heading for integrate().
   */
  private integrateLowSpeed(dt: number) {
    const fms = this.fms, air = this.airVelocity!;
    // The true wind moves the aircraft (the plant); the controller never sees it (below).
    const windNorth = -fms.wind.speed * Math.cos(rad(fms.wind.direction)), windEast = -fms.wind.speed * Math.sin(rad(fms.wind.direction));
    const h = this.lowHorizontal!;
    // Feedback: HOV, TD/H and LVL consume it; losing it, or a receiver change that is not continuous, leaves HOV and
    // TD/H for ATT on the latched command (the controller gets no new measurement; the aircraft may drift). LVL, once
    // lost, stays lost until the departure is engaged again: it does not resume by itself when feedback returns.
    if (h.mode !== "ATT" && !(h.mode === "TU" && this.lvlLost)) {
      const { feedback, reason } = this.continuousFeedback(dt);
      if (feedback) this.noteFeedback(feedback);
      else {
        this.lastFeedback = null;
        if (h.mode === "HOV" || h.mode === "TDH") {
          this.lowHorizontal = { ...h, mode: "ATT" };
          this.record("HOV LOST", `${reason}; ATT holds the last air-velocity command`);
        } else if (h.mode === "TU") {
          this.lvlLost = true;
          this.record("LVL LOST", `${reason}; the lateral air velocity is held (ATT); the departure acceleration and climb go on`);
        }
      }
    }
    // The heading: toward the held heading (a crew selection in HDG turns it), at the yaw-rate limit.
    if (this.lateral === "HDG" && !this.held) this.hoverHeading = this.heading;
    const heading = norm360(fms.heading + clamp(angleDiff(fms.heading, this.hoverHeading), -this.profile.lowSpeedYawRate.value * dt, this.profile.lowSpeedYawRate.value * dt));
    const hx = Math.cos(rad(heading)), hy = Math.sin(rad(heading));
    let command = { north: air.north, east: air.east };
    const fb = this.lvlLost && this.lowHorizontal!.mode === "TU" ? null : this.lastFeedback;
    const now = this.lowHorizontal!;
    // The controller's wind: the measured ground velocity (the feedback's 166/174) less the aircraft's own air velocity
    // (its air data). A velocity sensor error therefore steers the aircraft, as it would a real one; truth never does.
    const windEstimate = fb ? { north: fb.north - air.north, east: fb.east - air.east } : null;
    if ((now.mode === "HOV" || now.mode === "TDH") && fb && windEstimate) {
      let ground: { north: number; east: number };
      if (now.mode === "HOV") {
        const e = toLocal(fb.position, now.target!);
        // A recovery becomes a capture once within the capture conditions (50 m, 1 kt).
        if (!now.captured && Math.hypot(e.x, e.y) * 1852 <= 50 && Math.hypot(fb.north, fb.east) <= 1) { now.captured = true; this.record("HOV", "captured at the target"); }
        // Close the measured position error over about 20 s, at up to 10 kt over the ground.
        const wanted = (Math.hypot(e.x, e.y) * 3600) / 20;
        const scale = wanted > 10 ? 10 / wanted : 1;
        ground = { north: ((e.y * 3600) / 20) * scale, east: ((e.x * 3600) / 20) * scale };
      } else {
        const along = now.target ? toLocal(fb.position, now.target) : null;
        const remaining = along ? along.x * Math.sin(rad(now.track)) + along.y * Math.cos(rad(now.track)) : null;
        // Closed loop on the remaining distance to a target (kt/s = GS^2 / (2 d 3600), d in NM), bounded; at or past
        // the target, the upper bound; without a target, the nominal rate.
        // With a target further than the nominal stopping distance, the speed is held (the gate segment) until the
        // stopping distance is reached; then the closed loop.
        const nominalStop = (now.speed * now.speed) / (2 * this.profile.tdhDeceleration.value * 3600);
        const rate = remaining === null ? this.profile.tdhDeceleration.value : remaining <= 0 ? 1.25 : now.holding && remaining > nominalStop ? 0 : clamp((now.speed * now.speed) / (2 * remaining * 3600), 0.5, 1.25);
        // The end of the gate segment: the deceleration starts, and with it the descent to the hover height.
        if (now.holding && rate > 0) {
          now.holding = false;
          this.lowCollective = { mode: "TDH", datum: now.hoverDatum!, rate: -this.profile.tdhDescentRate.value };
          this.record("TD/H", "the gate segment ends: decelerating to MRK and descending to the hover height");
        }
        now.speed = Math.max(0, now.speed - rate * dt);
        // Toward a target, a cross-track correction onto the line through it along the track (closed over about 20 s,
        // at up to 5 kt): the stop is at the target, not beside it.
        const across = along ? along.x * Math.cos(rad(now.track)) - along.y * Math.sin(rad(now.track)) : 0;
        const lateral = clamp((across * 3600) / 20, -5, 5);
        ground = {
          north: now.speed * Math.cos(rad(now.track)) - lateral * Math.sin(rad(now.track)),
          east: now.speed * Math.sin(rad(now.track)) + lateral * Math.cos(rad(now.track)),
        };
        if (now.speed <= 1) {
          const miss = now.target ? distanceNm(fb.position, now.target) * 1852 : 0;
          this.lowHorizontal = { mode: "HOV", target: now.target ?? fb.position, speed: 0, track: now.track, captured: !now.target || miss <= 50 };
          // HOV holds the target; stopped more than 50 m from it, it is a recovery to the target, not an arrival.
          this.record("HOV", !now.target ? "holding where it stopped" : miss <= 50 ? "holding the target" : `recovering to the target, stopped ${miss.toFixed(0)} m from it`);
        }
      }
      command = { north: ground.north - windEstimate.north, east: ground.east - windEstimate.east };
    } else if (now.mode === "TU") {
      // Along the heading: the airspeed toward the climb speed at the departure rate. Across it: the measured lateral
      // ground velocity driven to zero (LVL) while there is feedback; without it, the lateral air velocity is held (ATT).
      const alongAir = air.north * hx + air.east * hy;
      const crossAir = -air.north * hy + air.east * hx;
      const climbTas = tasFromIas(this.profile.climbSpeed.value, fms.altitude);
      const alongCmd = Math.min(climbTas, alongAir + this.profile.departureAccel.value * dt);
      const crossCmd = fb ? crossAir - (-fb.north * hy + fb.east * hx) : crossAir;
      command = { north: alongCmd * hx - crossCmd * hy, east: alongCmd * hy + crossCmd * hx };
    }
    // Sideways and rearward limits in the heading frame, then the acceleration limits per axis (along the heading, and
    // across it at the profile's lateral limit).
    let along = command.north * hx + command.east * hy, cross = -command.north * hy + command.east * hx;
    along = Math.max(along, -this.profile.rearwardLimit.value);
    cross = clamp(cross, -this.profile.sidewaysLimit.value, this.profile.sidewaysLimit.value);
    const airAlong = air.north * hx + air.east * hy, airCross = -air.north * hy + air.east * hx;
    const newAlong = airAlong + clamp(along - airAlong, -this.profile.longitudinalAccel.value * dt, this.profile.longitudinalAccel.value * dt);
    const newCross = airCross + clamp(cross - airCross, -this.profile.lateralAccel.value * dt, this.profile.lateralAccel.value * dt);
    air.north = newAlong * hx - newCross * hy; air.east = newAlong * hy + newCross * hx;
    this.airspeed = Math.hypot(air.north, air.east);
    const groundNorth = air.north + windNorth, groundEast = air.east + windEast;
    const groundSpeed = Math.hypot(groundNorth, groundEast);
    const track = groundSpeed > 0.05 ? norm360(deg(Math.atan2(groundEast, groundNorth))) : fms.track;
    const position = offset(fms.truePosition, track, (groundSpeed * dt) / 3600);
    // TU: heading hold captures on the roll axis through 40 kt, but the low-speed heading controller keeps the aircraft
    // until the coordinated regime at 45 kt (the profile's hysteresis); from there IAS flies on to 80.
    if (this.lowHorizontal?.mode === "TU" && this.indicatedAirspeed >= this.profile.coordinatedEnterAt.value) this.leaveLowSpeed("coordinated flight on the departure");
    return { position, track, groundSpeed, heading };
  }

  /**
   * The collective under the radio-height modes, or null when the ordinary vertical modes fly it. A collective mode
   * that loses its radio height is replaced by ALT HOLD on the barometric altitude at that moment (it does not claim
   * to hold radio height). Low-height protection raises the collective below 75 ft in cruise and 17 ft in the hover
   * modes (AW189 values), and needs a valid radio height.
   */
  private lowCollectiveSpeed(dt: number): number | null {
    const c = this.lowCollective;
    const ra = this.radio;
    if (!c) { if (this.lowHeight !== "OFF") this.lowHeight = null; return null; }
    if (ra.status !== "NORMAL") {
      this.lowCollective = null;
      this.altitudeHold = Math.round(this.fms.altitude);
      this.lowHeight = "OFF";
      this.vertical = "ALT HOLD";
      this.record("RA LOST", `${c.mode === "TDH" ? "TD/H" : c.mode} removed; ALT HOLD ${this.altitudeHold} FT on the barometric altitude`);
      return 0;
    }
    const height = ra.value!;
    const toGo = c.datum - height;
    let vs: number;
    // A transition flies the shared command profile (transition.ts verticalCommand): its rate, braking at the vertical-
    // acceleration limit to rest on the datum (integrate() limits the response to that acceleration). At arrival the
    // datum is held firmly. The mode shows RHT once the height is in its capture band (20 ft, 200 fpm); that is an
    // annunciation, it does not change the command (plan R3-01).
    // The command is taken where this step will leave the aircraft (the distance to go less this step's travel), so a
    // step of any length brakes when the continuous profile would, not a step late.
    // It has arrived at rest on the datum, or this step reaches it: from there the datum is held.
    const travel = (this.fms.verticalSpeed * dt) / 60;
    const reaching = Math.sign(travel) === Math.sign(toGo) && Math.abs(travel) >= Math.abs(toGo);
    if (c.rate !== null && !verticalArrived(toGo, this.fms.verticalSpeed) && !reaching) vs = verticalCommand(toGo - travel, c.rate);
    else { vs = clamp(toGo * RHT_GAIN, -this.profile.maxVerticalSpeed.value, this.profile.maxVerticalSpeed.value); if (c.rate !== null) this.lowCollective = { ...c, rate: null }; }
    if (c.mode !== "RHT" && Math.abs(toGo) <= ALT_CAPTURE_FT && Math.abs(this.fms.verticalSpeed) <= 200) {
      this.lowCollective = { mode: "RHT", datum: c.datum, rate: this.lowCollective!.rate };
      this.record("RHT", `${c.datum} FT RA`);
    }
    const floor = this.lowHorizontal !== null ? this.profile.lowHeightHover.value : this.profile.lowHeightCruise.value;
    if (height < floor) { this.lowHeight = "ACTIVE"; vs = Math.max(vs, (floor - height) * 2 + 100); }
    else this.lowHeight = null;
    this.vertical = "ALT HOLD";
    return vs;
  }

  // ------------------------------------------------------------------ selected and managed lateral guidance

  /** HDG SEL: fly a selected heading. LNAV stays armed if it was, and captures the route when the aircraft nears it. */
  selectHeading(heading: number) {
    // A crew heading selection cancels a retained TD/H plan (R3-02): the stop becomes a hover where the aircraft is, or
    // ATT without feedback. In HOV it only turns the hover heading.
    const tdh = this.lowHorizontal;
    if (tdh?.mode === "TDH") {
      const feedback = this.fms.hoverFeedback;
      this.lowHorizontal = { ...tdh, mode: feedback ? "HOV" : "ATT", target: feedback?.position ?? null };
      this.record("TD/H CANCELLED", `heading selected; ${feedback ? "HOV where it is" : "ATT"}`);
    }
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

  /** Crew handover at a PinS MAP is immediate, including when the bench is paused. */
  proceedFromPins(declaration: Parameters<ScriptedFms["proceedFromPins"]>[0]) {
    if (!this.fms.proceedFromPins(declaration)) return false;
    this.lateral = "HDG"; this.lnavArmed = false; this.gpsLateral = false;
    this.heading = norm360(this.fms.heading); this.held = true;
    if (this.approach === "CAPTURED") { this.altitudeHold = Math.round(this.fms.altitude); this.vsTarget = null; }
    this.approach = "OFF";
    this.last = this.guide();
    this.record("PINS CONTINUATION", "crew declared chart conditions; instrument guidance ended; HDG HOLD");
    this.fms.tick();
    return true;
  }
  get lnavIsArmed() { return this.lnavArmed; }
  get selectedHeading() { return this.heading; }
  get headingHeld() { return this.held; }

  /** Works out the guidance for this instant, and sequences waypoints, holds and patterns as they are reached. */
  private guide(dt = 0): Guidance {
    // With the FMS failed there is no managed guidance to consume: basic heading hold only.
    if (this.fms.hasCondition("fmsFail")) {
      return {
        mode: "HDG", legFrom: null, legTo: null, desiredTrack: null, crossTrack: 0, distanceToGo: null,
        bankCommand: clamp(angleDiff(this.fms.heading, this.heading), -this.bankLimit, this.bankLimit), targetAltitude: this.altitudeHold ?? this.fms.altitude,
      };
    }
    const managed = this.managedGuidance(dt);
    // LNAV with no leg to fly (a discontinuity or the end of the route) is lost: heading hold on the current track.
    if (this.lateral === "LNAV" && managed.mode === "HDG" && managed.desiredTrack === null) {
      this.lateral = "HDG";
      this.heading = Math.round(norm360(this.fms.heading));
      this.held = true;
      if (dt > 0) this.record("LNAV LOST", `${this.fms.pinsContinuation?.active ? "crew flying PinS visual segment" : !this.fms.departureInstrumentReady ? "departure IDF conditions not met" : this.fms.navState.mode === "DR" && !this.fms.navState.airValid ? "position input unavailable" : "no active leg"}; HDG HOLD ${String(this.heading).padStart(3, "0")}°T`);
    }
    if (this.lateral === "LNAV") return managed;
    // Capture when the managed path is close and the aircraft is not heading away from it.
    if (this.lnavArmed && managed.desiredTrack !== null && Math.abs(managed.crossTrack) < 0.6 && Math.abs(angleDiff(this.fms.track, managed.desiredTrack)) < 100) {
      this.lateral = "LNAV";
      this.lnavArmed = false;
      return managed;
    }
    return { ...managed, mode: "HDG", bankCommand: clamp(angleDiff(this.fms.heading, this.heading), -this.bankLimit, this.bankLimit) };
  }

  /** The guidance LNAV would fly. With dt > 0 (and LNAV engaged) it also sequences what the aircraft has reached. */
  private managedGuidance(dt: number): Guidance {
    const fms = this.fms;
    const route = fms.activeRoute;
    const leg = route.legs[0];
    const base = { targetAltitude: this.targetAltitude() };
    const none = { mode: "LNAV" as const, legFrom: null, legTo: null, desiredTrack: null, crossTrack: 0, distanceToGo: null, bankCommand: 0, ...base };
    if (fms.navState.mode === "DR" && !fms.navState.airValid || !fms.approachSteeringValid || !fms.departureInstrumentReady || fms.pinsContinuation?.active) return { ...none, mode: "HDG" };
    const sequencing = dt > 0 && this.lateral === "LNAV";
    this.lead = 0;

    if (this.holdPlan && !(route.hold && leg?.kind === "wpt" && leg.ident === route.hold.fix)) this.holdPlan = null;
    if (this.sarPlan && !(fms.sar.status === "IN PROGRESS")) this.sarPlan = null;

    if (this.holdPlan && route.hold) return { ...this.flyHold(route.hold, sequencing ? dt : 0), ...base, mode: "HOLD" };
    if (this.sarPlan) return { ...this.flySar(sequencing ? dt : 0), ...base, mode: "SAR" };

    // Phase 1 of the hover procedure: while JN is the active leg, the committed joining path is flown to it.
    const join = fms.hoverJoin, procedure = fms.hover.active?.id ?? null;
    const onJoin = leg?.kind === "wpt" && leg.ident === "JN" && join !== null;
    if (this.joinPlan && (!onJoin || this.joinPlan.id !== procedure)) this.joinPlan = null;
    if (!this.joinPlan && onJoin) this.joinPlan = { segments: join!.segments, index: 0, id: procedure! };
    if (this.joinPlan) return { ...this.flyJoin(sequencing ? dt : 0), ...base, mode: "LNAV" };

    if (!leg || leg.kind === "disco") return { ...none, mode: "HDG" };
    if (leg.kind === "cond") return { ...this.flyConditional(leg, route.legs[1], sequencing), ...base };

    const to = fms.coordinates(leg.ident);
    if (!to) return { ...none, mode: "HDG" };
    // A course-to-fix leg is the published course line into the fix; otherwise the line from where the leg began.
    const from = leg.path === "CF" && leg.course !== undefined ? offset(to, leg.course + 180, 30) : fms.activeLegStart;
    const g = (leg.path === "RF" || leg.path === "AF") && leg.arc ? arcGeometry(leg.arc, to, fms.position) : legGeometry(from, to, fms.position);
    // A lateral offset shifts the path flown; the aircraft intercepts the offset track as it would the route.
    const shift = this.offsetApplies(leg) ? route.offset!.nm : 0;
    // With GPS lateral authority on an RNAV final, the cross-track is the selected GPS's 116 deviation from the FAS course
    // (GPS phase 3b), captured or after a vertical loss (GPS-01); watchGpsLateral ends it, announced, when 116 goes.
    const gpsLateral = this.gpsLateral && fms.gpsApproachLateral ? fms.gpsApproach?.lateralFt ?? null : null;
    const crossTrack = gpsLateral !== null ? gpsLateral / 6076.12 : g.crossTrack - shift;
    // Along the FAS course, from which 116 is measured, not the leg from the FAF.
    const desiredTrack = gpsLateral !== null ? fms.finalApproachCourse ?? g.track : g.track;

    // Fly-by: start the turn onto the next leg early; fly-over for holding fixes, search starts and /O waypoints.
    const next = route.legs[1];
    const nextTo = next?.kind === "wpt" ? fms.coordinates(next.ident) : undefined;
    // Search waypoints are fly-by (M300 11-2), but the ladder's entry waypoint is fly-over (11-4): a square or sector
    // search is joined turning early onto its first leg, on the SAR bearing.
    const sarEntry = leg.qualifier === "/S" && fms.sar.active !== null && fms.sar.active !== "LADDER";
    const flyOver = !sarEntry && (leg.qualifier !== undefined || leg.path === "RF" || leg.path === "AF" || !nextTo || next?.kind === "wpt" && (next.path === "RF" || next.path === "AF"));
    const outbound = sarEntry ? fms.sar.sarBearing : next?.kind === "wpt" && next.path === "CF" && next.course !== undefined ? next.course : nextTo ? courseDeg(to, nextTo) : g.track;
    const lead = flyOver ? 0 : turnLead(this.tas, angleDiff(g.track, outbound), this.steeringLimit, this.profile.rollRate.value);
    this.lead = lead;
    if (sequencing && (g.toGo <= lead || g.toGo <= 0.02)) {
      // The altitude planned at the fix becomes the start of the next leg's path.
      const planned = fms.profile().points[0]?.altitude ?? null;
      this.legStartPath = planned !== null && next?.kind === "wpt" ? { to: next.ident, altitude: planned } : null;
      const result = fms.arrive();
      if (result === "hold") this.startHold();
      if (result === "sar") this.startSar(to);
      if (result !== "map") return this.managedGuidance(0);
    }
    // On an arc, bank into the turn the arc needs, and steer out the error on top of it.
    const feedForward = (leg.path === "RF" || leg.path === "AF") && leg.arc
      ? (leg.arc.turn === "R" ? 1 : -1) * deg(Math.atan((this.tas * this.tas) / (68625 * Math.max(0.5, distanceNm(leg.arc.centre, to)))))
      : 0;
    return {
      mode: "LNAV", legFrom: (leg.path === "RF" || leg.path === "AF") ? null : from, legTo: to, desiredTrack, crossTrack, distanceToGo: g.toGo,
      bankCommand: clamp(feedForward + this.steer(desiredTrack, crossTrack, leg), -this.steeringLimit, this.steeringLimit), ...base,
    };
  }

  /** Conditional legs: fly the course or heading until the altitude, the intercept, or (for VM/FM) never. */
  private flyConditional(leg: Extract<Leg, { kind: "cond" }>, next: Leg | undefined, sequencing: boolean): Omit<Guidance, "targetAltitude"> {
    const fms = this.fms;
    const headingLeg = leg.path[0] === "V";
    // A heading leg flies its heading and drifts with the wind; a course or track leg flies its course over the ground.
    const flown = headingLeg ? fms.heading : fms.track;
    const result = { mode: "LNAV" as const, legFrom: null, legTo: null, desiredTrack: headingLeg ? fms.track : leg.course, crossTrack: 0, distanceToGo: null, bankCommand: clamp(angleDiff(flown, leg.course), -this.steeringLimit, this.steeringLimit) };
    if (!sequencing) return result;
    let done = false;
    if ((leg.path === "CA" || leg.path === "FA" || leg.path === "VA") && leg.altitude !== undefined) {
      const baro = fms.validBaroAltitude;
      done = baro !== null && baro >= leg.altitude - 20;
    }
    if (leg.path === "VI" && next?.kind === "wpt") {
      // Intercept: the next leg's line (its course into its fix) is reached.
      const to = fms.coordinates(next.ident);
      const course = next.course ?? (to ? courseDeg(fms.position, to) : leg.course);
      if (to) done = Math.abs(legGeometry(offset(to, course + 180, 30), to, fms.position).crossTrack) < 0.3;
    }
    if (done) { fms.arrive(); return this.managedGuidance(0); }
    return result;
  }

  /** Whether the route's lateral offset applies to this leg: after its start, before its end, never on an approach. */
  private offsetApplies(leg: Extract<Leg, { kind: "wpt" }>) {
    const route = this.fms.activeRoute;
    const offset = route.offset;
    if (!offset || leg.source === "APPR" || leg.source === "MISSED" || (leg.path === "RF" || leg.path === "AF") || leg.qualifier) return false;
    // The offset begins on the first leg after its start waypoint: while the start is still ahead, it does not apply.
    const ahead = route.legs.some(l => l.kind === "wpt" && l.ident === offset.start);
    return !(offset.start && ahead);
  }

  /**
   * The racetrack for the active hold (holds.ts), or null when it cannot be flown. The holding speed is an indicated
   * airspeed (table or chart), taken as the true airspeed at the present altitude; the pattern is sized for that or the
   * true airspeed flown, whichever is faster, so its turns stay within the bank limit either way.
   */
  private holdGeometryNow(hold: Hold) {
    const fix = this.fms.coordinates(hold.fix);
    if (!fix) return null;
    const tas = Math.max(this.tas, tasFromIas(hold.speed, this.fms.altitude));
    const legNm = hold.legDistance ?? ((hold.legTime ?? defaultLegMinutes(this.fms.altitude)) * tas) / 60;
    return holdGeometry(fix, hold.inbound, hold.turn, tas, this.fms.systemWind.speed, legNm, this.steeringLimit);
  }

  /**
   * The entry and the racetrack, built at the first fix passage and rebuilt at each one (M300 10-8). Unflyable (the
   * wind at least the airspeed): UNABLE HOLD, and no hold guidance, so LNAV gives way to heading hold (F8).
   */
  private startHold() {
    const hold = this.fms.activeRoute.hold!;
    const geometry = this.holdGeometryNow(hold);
    if (!geometry) { this.unableHold(); return; }
    const fix = this.fms.coordinates(hold.fix)!;
    const entry = entrySegments(this.fms.holdEntryFlown ?? "DIRECT", fix, hold.inbound, hold.turn, geometry);
    this.holdPlan = { segments: [...entry, ...geometry.racetrack], index: 0, legNm: geometry.legNm, entryEnd: entry.length - 1, designBank: geometry.designBank };
  }

  private unableHold() {
    this.holdPlan = null;
    // Hold guidance is invalid: NAV gives way to a latched heading hold (F8), never shown captured on a path it cannot fly.
    if (this.lateral === "LNAV") {
      this.lateral = "HDG";
      this.heading = Math.round(norm360(this.fms.heading));
      this.held = true;
    }
    this.fms.raiseAlert("UNABLE HOLD");
    this.record("UNABLE HOLD", "the wind is at least the airspeed: no holding pattern can be flown; hold guidance withdrawn");
  }

  /**
   * Flies the hold's ground path: straight legs by cross-track steering, the half circles with the bank their radius
   * needs at the present ground speed plus the cross-track correction. The entry's intercept and each racetrack's
   * inbound leg end at a fix passage, where the FMS decides whether the hold goes on (the racetrack rebuilt) or exits.
   */
  private flyHold(hold: Hold, dt: number): Omit<Guidance, "targetAltitude" | "mode"> {
    const plan = this.holdPlan!;
    const fms = this.fms;
    // The wind rising to the airspeed mid-circuit: no pattern can be flown from here either.
    if (fms.systemWind.speed >= this.tas) {
      this.unableHold();
      return { legFrom: null, legTo: fms.coordinates(hold.fix) ?? null, desiredTrack: fms.track, crossTrack: 0, distanceToGo: 0, bankCommand: 0 };
    }
    const segment = plan.segments[plan.index];
    const last = plan.index === plan.segments.length - 1;
    if (segment.kind === "arc") {
      const g = arcGeometry({ centre: segment.centre, turn: segment.turn }, segment.to, fms.position);
      if (dt > 0 && g.toGo <= 0.02) plan.index += 1;
      const feedForward = (segment.turn === "R" ? 1 : -1) * deg(Math.atan(((fms.groundSpeed * 1.68781) ** 2) / (32.174 * segment.radius * 6076.12)));
      return { legFrom: null, legTo: segment.to, desiredTrack: g.track, crossTrack: g.crossTrack, distanceToGo: g.toGo, bankCommand: clamp(feedForward + this.steer(g.track, g.crossTrack), -this.steeringLimit, this.steeringLimit) };
    }
    const g = legGeometry(segment.from, segment.to, fms.position);
    if (dt > 0 && g.toGo <= 0.02) {
      if (last || plan.index === plan.entryEnd) {
        // A fix passage, at the end of the entry or of a racetrack: the hold goes on (rebuilt from the present airspeed
        // and wind) or exits.
        const result = fms.arrive(last);
        if (result === "hold") {
          const geometry = this.holdGeometryNow(hold);
          if (!geometry) this.unableHold();
          else Object.assign(plan, { segments: geometry.racetrack, index: 0, legNm: geometry.legNm, entryEnd: -1, designBank: geometry.designBank });
        } else {
          this.holdPlan = null;
          const circuits = hold.circuits ?? 0;
          this.record("HOLD EXITED", `${hold.fix}: ${circuits} whole racetrack${circuits === 1 ? "" : "s"} after the entry (EXIT TYPE ${hold.exit}${hold.missed ? ", missed approach" : ""})`);
        }
      } else plan.index += 1;
    }
    // Entry reversals use the holding design bank, as their predicted half-turn does, rather than a tighter turn at
    // the maximum steering envelope. Straight racetrack corrections retain the configured steering authority.
    const cap = plan.index <= plan.entryEnd ? Math.min(plan.designBank, this.steeringLimit) : this.steeringLimit;
    return { legFrom: segment.from, legTo: segment.to, desiredTrack: g.track, crossTrack: g.crossTrack, distanceToGo: g.toGo, bankCommand: clamp(this.steer(g.track, g.crossTrack), -cap, cap) };
  }

  /**
   * Flies the joining path (Phase 1) over the ground as the holds are flown: the arcs with the bank their radius needs at
   * the present ground speed plus the cross-track correction, the straight by cross-track steering. Its end is JN, on
   * the final track: that passage sequences JN, and the route flies the final course to TDN.
   */
  private flyJoin(dt: number): Omit<Guidance, "targetAltitude" | "mode"> {
    const plan = this.joinPlan!;
    const fms = this.fms;
    const segment = plan.segments[plan.index];
    const last = plan.index === plan.segments.length - 1;
    const next = () => {
      if (!last) { plan.index += 1; return; }
      this.joinPlan = null;
      fms.arrive();
    };
    if (segment.kind === "arc") {
      const g = arcGeometry({ centre: segment.centre, turn: segment.turn }, segment.to, fms.position);
      if (dt > 0 && g.toGo <= 0.02) next();
      const feedForward = (segment.turn === "R" ? 1 : -1) * deg(Math.atan(((fms.groundSpeed * 1.68781) ** 2) / (32.174 * segment.radius * 6076.12)));
      return { legFrom: null, legTo: segment.to, desiredTrack: g.track, crossTrack: g.crossTrack, distanceToGo: g.toGo, bankCommand: clamp(feedForward + this.steer(g.track, g.crossTrack), -this.steeringLimit, this.steeringLimit) };
    }
    const g = legGeometry(segment.from, segment.to, fms.position);
    if (dt > 0 && g.toGo <= 0.02) next();
    return { legFrom: segment.from, legTo: segment.to, desiredTrack: g.track, crossTrack: g.crossTrack, distanceToGo: g.toGo, bankCommand: this.steer(g.track, g.crossTrack) };
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
    const lead = after ? turnLead(this.tas, angleDiff(g.track, courseDeg(to, after)), this.steeringLimit, this.profile.rollRate.value) : 0;
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
