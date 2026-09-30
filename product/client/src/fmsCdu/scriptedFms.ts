import { alert } from "./alerts";
import { STANDARD_HPA, errorProblem, formatSetting, indicatedAltitudeFt, settingProblem, type BaroSetting } from "./baro";
import { parseArinc424, type Arinc424Result } from "./arinc424";
import type { ConditionId } from "./conditions";
import { DATALINK_PAGES, DEMO_SMS, DEMO_UPLINKS } from "./datalinkPages";
import { CORE_PAGES } from "./fmsPages";
import {
  START_POSITION, WAYPOINT, arcLength, bearingDeg, bearingIntersection, courseDeg, distanceNm, formatPosition, fromLocal, toLocal, holdEntry, isOutstanding,
  maxSarGroundSpeed, offset,
  type Hold, type HoldEntry, type HoldStatus, type LatLon, type Leg, type LskResult, type Message, type Offset, type Page, type PageId, type Route, type Sar,
  type SarPattern, type Uplink,
} from "./fmsModel";
import { Constellation } from "./gnss";
import { GpsReceiver, residualShares, type FasDataBlock, type GpsInput, type GpsBus } from "./gps";
import { holdTrack, iasFromTas, makingProgress, predictedGroundSpeed, tasFromIas } from "./kinematics";
import {
  ANP_FLOOR_NM, AutoSelection, GPS_DISAGREE_NM, SelectionLog, HAL_NM, approachAuthority, approachWords, fasRequirement, assessReceiver, buildFas, candidates, type ApproachAuthority, type GpsApproachWords, type GpsAssessment,
  type GpsChoice,
} from "./gpsSensors";
import { DEMO_COMPANY_ROUTES, DEMO_NAV_DATA, NavDatabase, type NavData, type NavEntry, type Navaid, type ProcedureHold, type StoredRoute } from "./navData";
import { coldTemperatureCorrection, computeProfile, parseConstraint, type PredictionBasis, type Profile, type ProfileInput, type VerticalPhase } from "./vnav";
import { RNP_DEFAULTS, type FlightPhase, type NavMode } from "./navigation";
import { CivilNavigation, type PositionMeasurement } from "./civilNavigation";
import { BenchRadioReceiver, solveRadio } from "./radioNavigation";
import { sampled, type SensorFrame, type SensorInputPort } from "./sensorPorts";
import { NAV_PAGES } from "./navPages";
import { holdAllowance, holdPathToPassage, piecesHours, predictionEndpoint, type HoldPathReport, type PathPiece } from "./predictions";
import { MAX_BANK } from "./flight";
import type { PredictionStatus } from "./vnav";

/**
 * The RTA (RENDEZVOUS, M300 5-17, A-141; plan E4, R3-03, R3-04): the distance to the fix, the required true airspeed
 * from the wind triangle over the legs to it (and that speed in IAS now), the speed flown, whether it is within the
 * limits in IAS at each leg's planned altitude, the ETA, and the status of the prediction to the fix (KNOWN, or
 * CONDITIONAL with its assumption). `required` is null when there is no computed speed, with the reason: OVERDUE, an
 * UNKNOWN path to the fix, or no airspeed that reaches it.
 */
export type Rendezvous = {
  distance: number | null; required: number | null; requiredIas: number | null; speed: number; achievable: boolean; eta: number | null;
  status: PredictionStatus; reason: string | null;
};
import { PLANNING_PAGES } from "./planningPages";
import { composeRoute, enrouteLegs, findProcedure } from "./procedures";
import { lowestProcedureLimit, procedureSpeedLimit, type ProcedureSpeed } from "./procedureSpeed";
import { ACTIVE_PROFILE, fmsBankLimit, type AircraftProfile } from "./profile";
import { COLUMNS, compose, type CduBackend, type CduScreen, type Lamp, type Line } from "./screen";
import { NO_SURFACE, radioHeight, surfaceById, type Surface } from "./surface";
import {
  EMPTY_USER_DATABASE, USER_WAYPOINT_CAPACITY, memoryUserDatabaseStore, mergeUserDatabase, parseUserDatabase, serializeUserDatabase,
  type UserDatabase, type UserDatabaseStore, type UserScope,
} from "./userDatabase";
import { TACTICAL_PAGES } from "./tacticalPages";
import { checkAtTdn, planTransition } from "./transition";
import { defaultLegMinutes, designBank, holdGeometry, holdingSpeedLimit, radiusAt } from "./holds";
import { JOIN_BEFORE_TDN_NM, joiningPath, type JoinPath } from "./joining";
import type { CduFunction } from "./variants";

/**
 * A scripted CMA-9000 for engineers to exercise the panel before the real operational program is connected.
 *
 * It follows the Operator's Manual rules for the keys (scratchpad entry, CLR, DELETE, +/-, line select entry and
 * copy, MOD/ACT with EXEC and ERASE, PREV/NEXT, BRT, the MSG annunciator) and for the pages it models: direct-to,
 * holds with their standard entry, the VNAV approach path, the search patterns, the tactical approach and the
 * timer. Courses and distances come from a small demonstration navigation database. The aircraft is flown by the
 * flight simulation (flight.ts), which reports its state here and calls arrive() at each waypoint passage. It is
 * labelled as a simulation on its IDENT page and is not a navigation computer.
 */

const PAGES: Record<PageId, Page> = { ...CORE_PAGES, ...PLANNING_PAGES, ...NAV_PAGES, ...TACTICAL_PAGES, ...DATALINK_PAGES };

export type WaypointResolution = { ident: string } | { select: string } | "invalid" | "not-in-database";

const wpt = (ident: string, altitude?: string): Leg => ({ kind: "wpt", ident, altitude });

const demoRoute = (): Route => ({
  origin: "CYOW", dest: "CYUL", coRoute: "OWUL1", flightNo: "LIFE21",
  legs: [
    wpt("MUN", "3000"), wpt("RDG", "4500"), wpt("TOLGU", "4500"),
    // Downwind, base and final to runway 24R (navData.ts): the old TOLGU to FERDI leg overflew the airport and turned
    // 150 degrees at the FAF.
    wpt("DEMEL", "3000"), wpt("ALNIT", "3000"), wpt("ULIDA", "2500"), wpt("FERDI", "1500A"), wpt("RW24R", "168"), wpt("CYUL"),
  ],
});

/** A navigation database cycle: its own dataset, and its effective dates (null when the data does not give them). */
export type NavCycle = { id: string; from: number | null; to: number | null; source: string; db: NavDatabase };

/** A date the data gives (YYYY-MM-DD): the start of the day, or with end the last minute of it; null if none. */
const cycleDate = (text: string, end: boolean) => {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(text);
  if (!m || Number(m[2]) < 1 || Number(m[2]) > 12 || Number(m[3]) < 1 || Number(m[3]) > 31) return null;
  return Date.UTC(Number(m[1]), Number(m[2]) - 1, Number(m[3]), end ? 23 : 0, end ? 59 : 0);
};
const cycleOf = (db: NavDatabase, source: string): NavCycle =>
  ({ id: db.cycle.id, from: cycleDate(db.cycle.from, false), to: cycleDate(db.cycle.to, true), source, db });
const demoCycle = (id: string, from: string, to: string) => cycleOf(new NavDatabase({ ...DEMO_NAV_DATA, cycle: { id, from, to } }), "demonstration data");
const onGlobe = (p: LatLon) => Number.isFinite(p.lat) && Number.isFinite(p.lon) && Math.abs(p.lat) <= 90 && Math.abs(p.lon) <= 180;
const samePlace = (a: LatLon | undefined, b: LatLon | undefined) => a !== undefined && b !== undefined && a.lat === b.lat && a.lon === b.lon;

/** How a FAS block differs from another, for the dataset record: final approach course, path angle, threshold. */
function fasDifference(from: FasDataBlock, to: FasDataBlock | null) {
  if (!to) return "no longer defined";
  const course = (fas: FasDataBlock) => Math.round(bearingDeg(fas.ltp, { lat: fas.ltp.lat + fas.fpapDelta.lat, lon: fas.ltp.lon + fas.fpapDelta.lon }));
  const parts = [
    ...(course(from) !== course(to) ? [`course ${course(from)} to ${course(to)}`] : []),
    ...(from.gpaDeg !== to.gpaDeg ? [`path angle ${from.gpaDeg} to ${to.gpaDeg}`] : []),
    ...(from.ltp.lat !== to.ltp.lat || from.ltp.lon !== to.ltp.lon || from.ltp.heightM !== to.ltp.heightM ? ["threshold moved"] : []),
    ...(from.tchFt !== to.tchFt ? [`TCH ${from.tchFt} to ${to.tchFt}`] : []),
  ];
  return parts.length ? parts.join(", ") : "block contents changed";
}

/** The legs of a route as text (DISC for a gap), for the engineering record. */
const legText = (legs: Route["legs"]) => legs.map(leg => (leg.kind === "wpt" ? leg.ident : leg.kind === "cond" ? leg.path : "DISC")).join(" ");

/**
 * A short fingerprint (FNV-1a) of a route's leg sequence as text (legText: idents, conditional paths, DISC). With the
 * local plan revision it identifies which plan a record changed within this session; it does not cover coordinates,
 * constraints, database provenance or aircraft state, so it does not prove two plans from different runs equal.
 */
function planFingerprint(legs: Route["legs"]) {
  let hash = 0x811c9dc5;
  for (const char of legText(legs)) hash = Math.imul(hash ^ char.charCodeAt(0), 0x01000193) >>> 0;
  return hash.toString(16).padStart(8, "0");
}


export type LegGeometry = { course: number; distance: number } | null;

export class ScriptedFms implements CduBackend {
  private listeners = new Set<() => void>();
  private changes = 0;
  private page: PageId = "IDENT";
  private index = 0;
  private scratch = "";
  private message: Message | null = null;
  /**
   * The latest alert the crew has not acknowledged, which lights MSG. Only CLR on that alert acknowledges it. If
   * something else takes it off the scratchpad (typing, a restart after FMS failure), CLR on an empty scratchpad, or
   * the restart itself, shows it again, so the lamp always has an acknowledgement path (R13).
   */
  private pendingAlert: Message | null = null;
  /** The crew's last SET POS entry on POS INIT, and when it was made (R26). */
  private positionReference: { position: LatLon; at: Date } | null = null;
  private recall: Message[] = [];
  private active: Route = demoRoute();
  private modified: Route | null = null;
  private radios = { com1: "121.500", com1Stby: "126.700", com2: "119.100", com2Stby: "133.600", nav1: "113.90", nav2: "116.70", adf: "0350", tpdr: "1200" };
  private fuel = { quantity: 1850, flow: 540, reserve: 400 };
  private marks: { ident: string; position: LatLon }[] = [];
  private points: Record<string, LatLon> = {};
  /**
   * TDN and MRK of a hover procedure being modified: the modified route resolves them from here, the active route
   * from `points`, so a pending ACTIVATE never moves the active procedure (written to `points` at EXEC).
   */
  private pendingHoverPoints: { JN: LatLon; TDN: LatLon; MRK: LatLon } | null = null;
  /** The joining path of a hover procedure being modified (preview), from the present state; rebuilt at EXEC. */
  private pendingJoin: JoinPath | null = null;
  private level = 6;
  private readonly maxLevel = 10;
  private brighten = true;
  private lastBrt = -Infinity;
  private squawkIdentUntil = 0;
  private here: LatLon = { ...START_POSITION };
  /** Where the aircraft really is; here is where the FMS believes it is. */
  private truth: LatLon = { ...START_POSITION };
  private readonly navigation: CivilNavigation;
  private readonly radioReceiver: BenchRadioReceiver;
  private readonly sensorPort: SensorInputPort | null;
  private sensorSequence = 0;
  private sensorFrame: SensorFrame | null = null;
  readonly predictiveRaim = { ident: null as string | null, eta: null as number | null, requestedAt: null as number | null };
  private readonly raimExcluded = new Set<number>();
  private automaticRaimFor: string | null = null;
  private nav = {
    mode: "GPS" as NavMode, anp: 0.05, dmes: [] as string[], vor: null as string | null, rnpManual: null as number | null, uncertain: false, airValid: true,
    unableSince: null as number | null, unableAlerted: false, integrityAlerted: false, approachIntegrityAlerted: false, armAlerted: false,
    /** The RNAV approach had vertical guidance from the GPS while in the approach phase (to catch its loss, 3b). */
    approachVerticalSeen: false,
    /** The receiver navigating in GPS mode (1 or 2), null otherwise; and whether GPS DISAGREE has been raised. */
    gpsSource: null as 1 | 2 | null, disagreeAlerted: false,
  };
  /**
   * The two simulated CMA-5024 receivers (GPS phase 3a), one sky, different seeds: independent noise and faults. The
   * FMS reads only their buses (gpsSensors.ts); the bench may inject faults and overrides into them directly.
   */
  private readonly constellation = new Constellation(1);
  private readonly receivers: readonly [GpsReceiver, GpsReceiver] = [
    new GpsReceiver({ constellation: this.constellation, seed: 101 }),
    new GpsReceiver({ constellation: this.constellation, seed: 202 }),
  ];
  private gpsChoice: GpsChoice = "AUTO";
  private gpsAssessment: GpsAssessment = { assessed: [], chosen: null };
  /** AUTO receiver selection, approach-aware (gpsSensors.ts, the AeroLink simulator policy), and its last verdict. */
  private autoSelection = new AutoSelection();
  private gpsSelection = { qualified: true, refused: "" };
  private selectionLog = new SelectionLog();
  /** The approach selection last sent to the receivers (its path identifier and CRC), so it is sent once per change. */
  private sentApproach: string | null = null;
  /** The FAS block last sent, for the final approach course the GPS deviations are measured from. */
  private sentFas: FasDataBlock | null = null;
  /** The FAS pinned with the executed plan (pinActive): what the receivers are sent, whatever cycle is active now. */
  private pinnedFas: { fas: FasDataBlock; cycle: string; revision: number } | null = null;
  /** The executed approach's final approach fix and runway, pinned with the plan (pinActive); null without an approach. */
  private executedApproach: { ident: string; faf: string | null; runway: string | null; instrumentEnd: string | null } | null = null;
  /** The satellite the GPS integrity condition faults, so a change of PRN clears the old one. */
  private integrityFaultPrn: number | null = null;
  /** Every change of navigation source: GPS1, GPS2, DME/DME, VOR/DME or DR, when it changed. */
  private sourceLog: { at: Date; source: string }[] = [];
  private armedApproach = false;
  /** Waypoints that move (a ship, a formation lead): position advanced by track and speed as time passes. */
  private moving: Record<string, { track: number; speed: number }> = {};
  private faults: { at: Date; text: string }[] = [];
  private selfTest: { startedAt: number | null; result: "PASS" | "FAIL" | null } = { startedAt: null, result: null };
  /** The other FMS: in dual operation every executed route is cross-loaded to it; in independent operation not. */
  private crossRoute: Route = demoRoute();
  /**
   * Navigation database cycles, the active one first, each with its own dataset. The two demonstration cycles are
   * separate datasets built from the same demonstration data: only their idents and dates differ. A loaded file
   * replaces the inactive one.
   */
  private cycles: NavCycle[] = [demoCycle("DEMO-2609", "2026-09-03", "2026-09-30"), demoCycle("DEMO-2610", "2026-10-01", "2026-10-28")];
  private datasetEvents: { at: Date; action: string; detail: string }[] = [];
  /**
   * Where the database fixes of the active plan were when it became active (EXEC, or the initial plan), and in which
   * cycle. The active plan's consumers (guidance, predictions, sequencing, the pages) use these, not a fresh lookup, so
   * activating another cycle never moves a fix the aircraft is flying.
   */
  /**
   * The active plan's database fixes as they were resolved when it became active: a position, or null for a fix the
   * plan was executed without (unresolved stays unresolved until the crew executes the plan again).
   */
  private pins = new Map<string, LatLon | null>();
  /** The active plan's revision: each EXEC (and each engineering change to it) makes a new one. */
  private planRevision = 0;
  private pinnedIn: NavCycle = this.cycles[0];
  private outOfDateAlerted = false;
  /** The MOVING WPT page's entries before CREATE. */
  movingDraft = { ident: null as string | null, position: null as LatLon | null, motion: null as string | null };
  /** RNDZ: arrive at a waypoint at a time, flying the speed that needs within the limits. */
  readonly rndz = { wpt: null as string | null, time: null as number | null, minSpeed: 60, maxSpeed: 160, active: false, alerted: false };
  /** TDN: a tactical descent to an altitude a distance before a reference, if the angle is flyable. */
  readonly tdn = { targetAltitude: 500, refId: null as string | null, distanceBefore: 1.0, maxAngle: 6, active: false, level: false };
  private perf = { notEnoughAlerted: false, unableAlertedFor: null as string | null };
  private inhibited: string[] = [];
  private gpsSelected = true;
  private injected = new Set<ConditionId>();
  private call: { state: "none" | "ringing" | "active"; from: string; since: number } = { state: "none", from: "", since: 0 };
  private messages: { from: string; text: string; at: Date; read: boolean }[] = [];
  private uplinkList: Uplink[] = [];
  private uplinksSent = 0;
  private enteredHold: HoldEntry | null = null;
  private sequenced: string | null = null;
  /** The latched VNAV phase (updateVerticalPhase), and the cruise altitude it last saw, to notice a new entry. */
  private vphase = { phase: "CLIMB" as VerticalPhase, reason: "initial", cruise: null as number | null };

  /** The active cycle's database. */
  private get db() { return this.cycles[0].db; }
  /** Which of several same-ident entries the crew chose on SELECT DESIRED WPT. */
  private chosen: Record<string, number> = {};
  private selectPending: { ident: string; apply: (ident: string) => LskResult; back: { page: PageId; index: number } } | null = null;
  private pilot: { ident: string; position: LatLon; definition: string }[] = [];
  /** The company routes the database holds (demonstration data); the crew's saved routes are the user database's. */
  private companyRoutes: StoredRoute[] = structuredClone(DEMO_COMPANY_ROUTES);
  /** The user database (E5, userDatabase.ts): user waypoints and user routes, kept in the store for this user and profile. */
  private userDb: UserDatabase = structuredClone(EMPTY_USER_DATABASE);
  private userStore: UserDatabaseStore = memoryUserDatabaseStore();
  private userScope: UserScope = { userId: "local", profileId: "" };
  /** Why the stored user database could not be read at start (it is then left as it was, and not overwritten), or null. */
  userDatabaseProblem: string | null = null;
  /** A NEW USER WPT being entered on USER WPT 1/2: its ident and position, and the reference it was made from. */
  userWaypointDraft: { ident: string | null; position: LatLon | null; ref: { ident: string; position: LatLon } | null } | null = null;
  private secondaryRoute: Route | null = null;
  /** The ident shown on REF NAV DATA, and an airway chosen on RTE 2 waiting for its TO fix. */
  navDataQuery: string | null = null;
  pendingVia: string | null = null;
  private aircraft = { track: courseDeg(START_POSITION, { lat: 45.2150, lon: -75.3900 }), groundSpeed: 120, tas: 120, altitude: 3000, verticalSpeed: 0, crossTrack: 0, trackError: 0, bank: 0, pitch: 0, heading: null as number | null };
  /** Whether each receiver has its baro altitude input (the bench can take it from one). */
  private gpsBaro: [boolean, boolean] = [true, true];
  /** Where the active leg starts: the last waypoint passed, or present position when a direct-to was executed. */
  private legStart: LatLon = { ...START_POSITION };
  private directPending = false;
  private directBypassed: string[] = [];

  get groundSpeed() { return this.aircraft.groundSpeed; }
  /** Truth: the physical height above MSL (rev 3 B1.1). Only the flight simulation and engineering placement write it. */
  get physicalAltitude() { return this.aircraft.altitude; }
  /**
   * The barometric altitude the FMS and the autopilot use (baro.ts): the physical height plus the injected baro error,
   * referenced to the declared QNH. The crew's setting does not change it; it changes what is indicated.
   */
  get altitude() { return this.aircraft.altitude + this.baroSystem.errorFt; }
  /** What the altimeter indicates with the crew's setting: the barometric altitude, corrected to the setting (baro.ts). */
  get indicatedAltitude() { return indicatedAltitudeFt(this.altitude, this.baroSystem.declaredQnhHpa, this.baroSystem.setting); }
  /** The barometric altitude system: the injected error (ft), the declared QNH (hPa) and the crew's setting. */
  get baro(): { errorFt: number; declaredQnhHpa: number; setting: BaroSetting } { return { ...this.baroSystem, setting: { ...this.baroSystem.setting } }; }
  private baroSystem: { errorFt: number; declaredQnhHpa: number; setting: BaroSetting } = { errorFt: 0, declaredQnhHpa: STANDARD_HPA, setting: { kind: "QNH", hPa: STANDARD_HPA } };
  /** The crew sets the altimeter: STD, or a QNH in hPa. Refused (false) outside the altimeter's range. Never moves the aircraft. */
  setBaroSetting(setting: BaroSetting): boolean {
    if (settingProblem(setting) !== null) return false;
    this.baroSystem.setting = setting.kind === "STD" ? { kind: "STD" } : { kind: "QNH", hPa: setting.hPa };
    this.emit();
    return true;
  }
  /**
   * Injects a barometric altitude error, feet (an engineering action, logged with the reason): the altimeter reads the
   * physical height plus it. It never moves the aircraft directly nor changes the radio height; the autopilot holding a
   * barometric altitude flies with it, as it would in an aircraft. Refused (false) beyond the bench's range.
   */
  setBaroError(errorFt: number, reason: string): boolean {
    if (errorProblem(errorFt) !== null) return false;
    this.baroSystem.errorFt = errorFt;
    this.engineering = [...this.engineering, { at: this.now, action: "BARO ERROR", detail: `${reason}: ${errorFt >= 0 ? "+" : ""}${Math.round(errorFt)} FT` }];
    this.emit();
    return true;
  }
  /** Declares the scenario atmosphere's QNH, hPa (an engineering action, logged with the reason). Refused out of range. */
  declareQnh(hPa: number, reason: string): boolean {
    if (settingProblem({ kind: "QNH", hPa }) !== null) return false;
    this.baroSystem.declaredQnhHpa = hPa;
    this.engineering = [...this.engineering, { at: this.now, action: "DECLARE QNH", detail: `${reason}: ${hPa} HPA (the altimeter is set to ${formatSetting(this.baroSystem.setting)})` }];
    this.emit();
    return true;
  }
  get track() { return this.aircraft.track; }
  /** The surface the radio altimeter measures against (surface.ts): none unless a scenario or the bench declares one. */
  private declaredSurface: Surface = NO_SURFACE;
  get surface() { return this.declaredSurface; }
  /** Declares the surface under the flight by its id (surface.ts); false for an unknown id, which changes nothing. */
  declareSurface(id: string) {
    const surface = surfaceById(id);
    if (!surface) return false;
    this.declaredSurface = surface;
    this.emit();
    return true;
  }
  /**
   * The radio altimeter: the aircraft's physical height above the declared surface, NCD off it or above its range,
   * FAIL when failed. Independent of the barometric altitude setting.
   */
  get radioHeight() {
    if (!this.sensorPort) return radioHeight(this.declaredSurface, this.truth, this.physicalAltitude, this.hasCondition("raFail"));
    const sample = this.sensorFrame?.radioHeight;
    const value = sampled(sample, this.now.getTime(), this.sensorMaxAge);
    return sample?.status === "FAIL" ? { status: "FAIL" as const, value: null }
      : value !== null && Number.isFinite(value) && value >= 0 && value <= 2500
        ? { status: "NORMAL" as const, value } : { status: "NCD" as const, value: null };
  }
  /**
   * The heading the aircraft flies (degrees true). With wind it differs from the track by the crab angle (kinematics.ts);
   * before the flight simulation first reports it, the track stands in.
   */
  get heading() { return this.aircraft.heading ?? this.aircraft.track; }
  /**
   * The true airspeed flown now, from the ground velocity and the wind (air = ground − wind), as air data would give it;
   * null when the aircraft is not moving through the air measurably (below 1 kt).
   */
  get trueAirspeed(): number | null {
    const toward = ((this.wind.direction + 180) * Math.PI) / 180, track = (this.track * Math.PI) / 180;
    const north = this.groundSpeed * Math.cos(track) - this.wind.speed * Math.cos(toward);
    const east = this.groundSpeed * Math.sin(track) - this.wind.speed * Math.sin(toward);
    const tas = Math.hypot(north, east);
    return tas >= 1 ? tas : null;
  }
  /**
   * The speed at which the aircraft is closing on the active waypoint (knots, signed: negative when moving away): the
   * ground velocity's component toward it. Progress, for a time to go, is this, not the ground speed's magnitude:
   * drifting away or across is no progress. The ground speed where there is no active waypoint.
   */
  /**
   * What the autopilot publishes to the FMS (plan B3.4): the hover height it has selected, and its longitudinal and
   * lateral ground velocities (VX forward, VY right, knots). The CMA HOVER page shows them (M300 A-75). Null until the
   * flight simulation reports them.
   */
  afcs: { hoverHeight: number; vx: number | null; vy: number | null; ias: number } | null = null;

  /**
   * The CMA transition down to hover (M300 11-18…11-22, A-74…A-76; plan D-T): the mark (MRK), the procedure's state
   * (MOD after ACTIVATE, ACT after EXEC), the final track into the wind (the wind direction frozen at ACTIVATE; below
   * 5 kt, the bearing to MRK), the planned distance from TDN to MRK, and at TDN the transition request the autopilot
   * takes (a counter it watches) or the reason it was refused (TDN NOT POSSIBLE, TDN DIST SHORT). `active` is the
   * executed procedure the aircraft is flying, with an id the autopilot follows: a new mark may be designated and
   * activated over it (M300 A-76), and its EXEC replaces it, which cancels a TD/H retained toward the old MRK.
   */
  readonly hover = {
    mark: null as { ident: string; position: LatLon; label: string | null } | null,
    status: "NONE" as "NONE" | "MOD" | "ACT",
    finalTrack: null as number | null,
    windSpeed: null as number | null,
    dtra: null as number | null,
    request: 0,
    requestData: null as { id: number; mrk: LatLon; finalTrack: number } | null,
    refused: null as string | null,
    /** Why the transition was refused at TDN, as the planner put it (below gate speed, no closure, …). */
    refusedReason: null as string | null,
    functionLost: false,
    active: null as { id: number; mark: { ident: string; position: LatLon; label: string | null }; finalTrack: number; dtra: number; join: JoinPath | null } | null,
    procedures: 0,
  };

  get closureSpeed() {
    const leg = this.active.legs[0];
    const to = leg?.kind === "wpt" ? this.coordinates(leg.ident) : undefined;
    if (!to || distanceNm(this.here, to) < 0.01) return this.groundSpeed;
    return this.groundSpeed * Math.cos(((this.track - courseDeg(this.here, to)) * Math.PI) / 180);
  }
  get verticalSpeed() { return this.aircraft.verticalSpeed; }
  /** Guidance deviations the flight simulation reports: cross-track NM (positive right) and track error degrees. */
  get crossTrack() { return this.aircraft.crossTrack; }
  get trackError() { return this.aircraft.trackError; }
  get activeLegStart() { return this.legStart; }
  /** The active route, whatever a pending modification shows on the pages. Guidance flies this one. */
  get activeRoute(): Route { return this.active; }

  readonly wind = { direction: 270, speed: 12 };
  /** Entries on the VNAV approach page. The FAF altitude sets the vertical path angle to the threshold. */
  /**
   * PLAN DATA (M300 3-19): the transition altitude and level, and the cruise wind and cruise true airspeed used for
   * planning on the ground. CRZ TAS defaults to 130 kt for a ROTOR installation (the profile's planningCruiseTas); it is
   * planning data only, separate from any speed the aircraft is commanded to fly (plan E1, R3-04). v1 predicts only in
   * the air, where the system wind is used, so the cruise wind and TAS are not yet read by the predictions.
   */
  readonly planData = { transAlt: 18000, transLevel: 180, cruiseWind: { direction: 0, speed: 0 }, cruiseTas: 0 };

  readonly vnav = {
    mda: 560, fafAltitude: 1500, runwayElevation: 118, destTemp: null as number | null, qnh: null as string | null,
    /** The planned cruise, the descent path angle, and DES NOW (an early descent to capture the path). */
    cruiseAltitude: 4500, cruiseSpeed: 120, pathAngle: 3.0, desNow: false,
  };
  readonly timer = { alarmAt: null as number | null, countdownEnd: null as number | null };
  readonly sar: Sar = {
    id: { SQUARE: "SQR01", LADDER: "LAD01", SECTOR: "SEC01" }, refId: null, relativeBearing: null, distance: null,
    trackSpacing: 2.0, legLength: 4.0, diameter: 4.0, angle: 30, sarBearing: 90, pending: null, active: null, status: null,
  };
  readonly tact = { refId: null as string | null };
  readonly tactAppr = {
    refId: null as string | null, bearing: 90, iafDistance: 8.0, fafDistance: 4.0, mapDistance: 0.5, vpa: -3.0,
    iafAltitude: 2500, runwayElevation: 118, transitionLevel: 180,
  };
  readonly datalink = { routeRequest: "NONE" as "NONE" | "RECEIVED" | "LOADED", windRequest: "NONE" as "NONE" | "RECEIVED", posReport: null as Date | null };

  private readonly clock: () => Date;

  /** The aircraft profile this FMS and its flight simulation fly (profile.ts); the helicopter profile unless given. */
  readonly aircraftProfile: AircraftProfile;

  constructor(clock: () => Date = () => new Date(), options: { profile?: AircraftProfile; sensors?: SensorInputPort; userDatabase?: { store: UserDatabaseStore; scope: UserScope } } = {}) {
    this.clock = clock;
    this.sensorPort = options.sensors ?? null;
    this.aircraftProfile = options.profile ?? ACTIVE_PROFILE;
    this.navigation = new CivilNavigation(START_POSITION, this.aircraftProfile.parameters);
    this.radioReceiver = new BenchRadioReceiver(this.aircraftProfile.parameters);
    this.userStore = options.userDatabase?.store ?? this.userStore;
    this.userScope = options.userDatabase?.scope ?? { userId: "local", profileId: this.aircraftProfile.id };
    this.loadUserDatabase();
    this.planData.cruiseTas = this.aircraftProfile.parameters.planningCruiseTas.value;
    this.vnav.cruiseSpeed = this.aircraftProfile.parameters.cruiseSpeed.value;
    // The warm airborne seed's ground velocity and air data must describe the same plant. The previous seed treated
    // ground speed as TAS despite a nonzero wind, making the first computed wind spuriously zero.
    const track = this.aircraft.track * Math.PI / 180, toward = (this.wind.direction + 180) * Math.PI / 180;
    const north = this.aircraft.groundSpeed * Math.cos(track) - this.wind.speed * Math.cos(toward);
    const east = this.aircraft.groundSpeed * Math.sin(track) - this.wind.speed * Math.sin(toward);
    this.aircraft.tas = Math.hypot(north, east);
    this.aircraft.heading = (Math.atan2(east, north) * 180 / Math.PI + 360) % 360;
    this.pinActive();
    // The receivers start warm: powered a minute before the session, past self-test, first fix and SBAS acquisition.
    const start = this.now.getTime();
    for (const offset of [60_000, 40_000]) for (const receiver of this.receivers) receiver.step(this.gpsInput(start - offset));
    // The default airborne demonstration also starts with its radios already acquired. Later tuning changes and
    // reacquisition after loss observe the declared delay; cold start uses the initialization workflow.
    this.radioReceiver.tune(this.autoRadioStations(), start - 40_000);
    this.radioReceiver.sample(this.truth, this.physicalAltitude, start - 40_000);
    this.updateNavigation(0);
  }

  // ------------------------------------------------------------------ CduBackend

  subscribe(listener: () => void) {
    this.listeners.add(listener);
    return () => { this.listeners.delete(listener); };
  }

  brightness() { return this.level / this.maxLevel; }

  revision() { return this.changes; }

  lamps(): ReadonlySet<Lamp> {
    // A failed FMS drives nothing but its FAIL annunciator.
    if (this.injected.has("fmsFail")) return new Set<Lamp>(["FAIL"]);
    const lamps = new Set<Lamp>();
    if (this.pendingAlert) lamps.add("MSG");
    if (this.modified) lamps.add("EXEC");
    const lampFor: Partial<Record<ConditionId, Lamp>> = {
      offset: "OFST", independent: "IND", gsmCall: "GSM", sms: "SMS",
      atcUplink: "ATC", tx1: "TX1", tx2: "TX2", vuhf: "V/UHF", hf: "HF", menuRequest: "MENU",
    };
    for (const [condition, lamp] of Object.entries(lampFor) as [ConditionId, Lamp][])
      if (this.hasCondition(condition)) lamps.add(lamp);
    // Navigation annunciators follow the navigation state: POS in dead reckoning, RNP when ANP exceeds RNP, NPA on a
    // non-precision approach. The bench can still force RNP and NPA on.
    if (this.nav.mode === "DR") lamps.add("POS");
    if (this.rnpExceeded) lamps.add("RNP");
    if (this.hasCondition("npa") || this.nonPrecisionApproach) lamps.add("NPA");
    return lamps;
  }

  screen(): CduScreen {
    if (this.injected.has("fmsFail")) return compose([]);
    const page = PAGES[this.page];
    const count = Math.max(1, page.pages(this));
    this.index = Math.min(this.index, count - 1);
    const lines = page.render(this, this.index);
    lines[13] = this.scratchLine();
    return compose(lines);
  }

  press(fn: CduFunction, options: { held?: boolean } = {}) {
    if (this.injected.has("fmsFail")) return;
    this.handle(fn, options);
    this.emit();
  }

  // ------------------------------------------------------------------ test bench

  /** Adds a system alert message, as the real FMS does for an alert condition. */
  raiseAlert(text: string) {
    this.alert(text);
    this.emit();
  }

  hasCondition(id: ConditionId): boolean {
    switch (id) {
      case "offset": return this.active.offset !== undefined;
      case "gsmCall": return this.call.state !== "none";
      case "sms": return this.messages.some(message => !message.read);
      case "atcUplink": return this.uplinkList.some(isOutstanding);
      default: return this.injected.has(id);
    }
  }

  setCondition(id: ConditionId, on: boolean) {
    if (on === this.hasCondition(id)) return;
    switch (id) {
      // Injected as an executed offset, as though the crew had entered and executed it.
      case "offset": if (on) this.active.offset = { nm: -2.0 }; else this.active.offset = undefined; break;
      case "gsmCall": this.call = on ? { state: "ringing", from: "+1 613 555 0142", since: this.now.getTime() } : { state: "none", from: "", since: 0 }; break;
      case "sms":
        if (on) this.messages.unshift({ ...DEMO_SMS[this.messages.length % DEMO_SMS.length], at: this.now, read: false });
        else for (const message of this.messages) message.read = true;
        break;
      case "atcUplink":
        if (on) {
          const text = DEMO_UPLINKS[this.uplinksSent % DEMO_UPLINKS.length];
          this.uplinksSent += 1;
          this.uplinkList.unshift({ id: this.uplinksSent, at: this.now, text, response: "OPEN" });
        } else this.uplinkList = this.uplinkList.filter(uplink => !isOutstanding(uplink));
        break;
      default:
        if (on) this.injected.add(id); else this.injected.delete(id);
        if (on && ["fmsFail", "gpsLost", "gpsIntegrity", "dmeOutage", "independent"].includes(id)) {
          this.recordFault({ fmsFail: "FMS FAILURE", gpsLost: "GPS LOST", gpsIntegrity: "GPS INTEGRITY LOST", dmeOutage: "DME OUTAGE", independent: "X-SIDE SYNC LOST" }[id as string] ?? id);
        }
        // Leaving independent operation resynchronises the other FMS to this one.
        if (!on && id === "independent") this.crossRoute = structuredClone(this.active);
        // Forcing RNP exceeded raises CHECK ANP at once, and that counts as this episode's alert (R11).
        if (on && id === "rnpExceeded") {
          this.alert(alert("CHECK ANP"));
          this.nav = { ...this.nav, unableSince: this.now.getTime(), unableAlerted: true };
        }
        // GPS lost is a shortcut for an RF input fault (antenna or cable) on both receivers (3a.5).
        if (id === "gpsLost") for (const receiver of this.receivers) receiver.injectFault("RF_INPUT", on);
        // GPS integrity is applied every step while on (applyGpsIntegrityCondition); off clears what it set.
        if (id === "gpsIntegrity" && !on) this.clearGpsIntegrityCondition();
        if (id === "gpsLost" || id === "gpsIntegrity" || id === "dmeOutage") this.updateNavigation(0);
        if (on && id === "independent") this.alert(alert("INDEPENDENT OP"));
        // The FMS restarts on its IDENT page when it comes back.
        // An alert still unacknowledged when it fails is shown again, so MSG keeps its acknowledgement path (R13).
        if (!on && id === "fmsFail") { this.open("IDENT"); this.message = this.pendingAlert; }
    }
    this.emit();
  }

  /**
   * Flies the aircraft to the active waypoint and sequences it, as crossing the waypoint would. A hold or a search
   * pattern at that waypoint is entered instead, and each further call flies one more circuit until the pilot exits.
   * This is the bench's Jump, an engineering control: it refuses at a route discontinuity, which only
   * overrideDiscontinuity crosses.
   */
  sequence(): "jumped" | "discontinuity" | "failed" | "end" {
    if (this.injected.has("fmsFail")) return "failed";
    const leg = this.active.legs[0];
    if (!leg) return "end";
    if (leg.kind === "disco") return "discontinuity";
    const at = leg.kind === "wpt" ? this.coordinates(leg.ident) : undefined;
    if (at) { this.truth = { ...at }; this.updateNavigation(0); }
    this.arrive();
    this.emit();
    return "jumped";
  }

  /**
   * Override discontinuity (an engineering control): removes the gap at the head of the active route, so the next
   * leg becomes active, and records the override. A crew would close the gap on LEGS instead.
   */
  overrideDiscontinuity(): boolean {
    if (this.injected.has("fmsFail") || this.active.legs[0]?.kind !== "disco") return false;
    const legs = this.active.legs;
    const before = { revision: this.planRevision, fingerprint: planFingerprint(legs), legs: legText(legs) };
    legs.shift();
    this.planRevision += 1;
    const next = legs[0];
    this.engineering = [...this.engineering, {
      at: this.now, action: "OVERRIDE DISCONTINUITY",
      detail: `gap removed; active leg now ${next?.kind === "wpt" ? next.ident : next?.kind === "cond" ? next.path : "none"}; `
        + `plan rev ${before.revision} (${before.fingerprint}: ${before.legs}) -> rev ${this.planRevision} (${planFingerprint(legs)}: ${legText(legs)})`,
    }];
    this.emit();
    return true;
  }

  /**
   * Places the aircraft (an engineering control, like Jump): where it really is, its track and its altitude, level and
   * wings level. The FMS position follows through its sensors as usual, from its current navigation error. Recorded in
   * the engineering log with the reason. The demonstration start states (kbtvDemo.ts) use it.
   */
  placeAircraft(state: { position: LatLon; track: number; altitude: number }, reason: string) {
    // Crabbed into the wind so the given track is the one flown (the heading when the track cannot be held is the track).
    const hold = holdTrack(this.targetSpeed, state.track, this.wind);
    this.setAircraft({ ...state, tas: this.targetSpeed, heading: hold.feasible ? hold.heading : state.track, groundSpeed: hold.feasible ? hold.groundSpeed : 0, verticalSpeed: 0, crossTrack: 0, trackError: 0, bank: 0, pitch: 0 });
    this.engineering = [...this.engineering, {
      at: this.now, action: "PLACE AIRCRAFT",
      detail: `${reason}: ${formatPosition(state.position)}, track ${Math.round(state.track)}°, ${Math.round(state.altitude)} FT`,
    }];
    this.updateNavigation(0);
    this.emit();
  }

  /** Engineering interventions made in this session, oldest first: they are not crew actions. */
  get engineeringLog(): readonly { at: Date; action: string; detail: string }[] { return this.engineering; }
  private engineering: { at: Date; action: string; detail: string }[] = [];

  /**
   * The aircraft has reached the active waypoint. It is sequenced, unless it is the fix of a hold that is not armed
   * to exit (the hold is entered, or another circuit begins) or the start of the active search pattern. Returns what
   * the aircraft flies next; the flight simulation calls this at each waypoint passage.
   */
  arrive(completedCircuit = false): "route" | "hold" | "sar" | "end" {
    const route = this.active;
    const leg = route.legs[0];
    if (!leg || leg.kind === "disco") { this.alert(alert("END OF ROUTE")); return "end"; }
    // A conditional leg ends where its event happened: the next leg starts from here.
    if (leg.kind === "cond") { this.passLeg(null); return "route"; }
    // A procedure hold (HF, HA, HM on the leg) is armed as the route's hold when its fix is reached, unless the route
    // already holds there (the armed missed-approach hold). A hold on a missed-approach leg is the missed-approach hold
    // (MISSED-HOLD: one racetrack), however the route reached it.
    if (leg.hold && route.hold?.fix !== leg.ident) {
      route.hold = this.holdFromProcedure(leg.ident, leg.hold, "ARMED");
      if (leg.source === "MISSED") route.hold.missed = true;
    }
    const hold = route.hold;
    // A fix crossing in the hold: at the end of the entry (the first crossing after it), or after a whole racetrack.
    if (hold && hold.fix === leg.ident && hold.status === "IN PROGRESS" && completedCircuit) hold.circuits = (hold.circuits ?? 0) + 1;
    if (hold && hold.fix === leg.ident && hold.status === "IN PROGRESS" && this.holdExitReached(hold)) hold.status = "EXIT ARMED";
    if (hold && hold.fix === leg.ident && hold.status !== "EXIT ARMED") {
      if (hold.status === "ARMED") {
        this.enteredHold = this.holdEntryFor(route);
        hold.status = "IN PROGRESS";
        const limit = holdingSpeedLimit(this.altitude, this.aircraftProfile);
        if (limit !== null && hold.speed > limit) this.alert(alert("HIGH HOLDING SPEED"));
      }
      return "hold";
    }
    if (hold && hold.fix === leg.ident) { route.hold = undefined; this.enteredHold = null; }
    if (leg.ident === "TDN" && this.hover.active) this.reachTdn();
    if (leg.qualifier === "/S" && this.sar.active) {
      this.sar.status = "IN PROGRESS";
      return "sar";
    }
    this.passLeg(leg.ident);
    return route.legs.some(next => next.kind !== "disco") ? "route" : "end";
  }

  /** The search pattern has been flown to its end: the route continues after the search pattern waypoint. */
  completeSar() {
    const leg = this.active.legs[0];
    this.sar.active = null;
    this.sar.status = null;
    if (leg?.kind === "wpt" && leg.qualifier === "/S") this.passLeg(leg.ident);
  }

  /** Sequences the active leg: a waypoint (by ident) or, with null, a conditional leg that has ended. */
  private passLeg(ident: string | null) {
    const route = this.active;
    this.legStart = (ident && this.coordinates(ident)) || { ...this.here };
    if (ident) this.sequenced = ident;
    const passed = route.legs.shift();
    // A direct-to-fix leg is flown from wherever the aircraft is when it becomes active.
    const next = route.legs[0];
    if (next?.kind === "wpt" && next.path === "DF") this.legStart = { ...this.here };
    // The offset ends at its end waypoint, where the aircraft returns to the route.
    if (ident && route.offset?.end === ident) route.offset = undefined;
    if (!ident) {
      const pendingCond = this.modified?.legs[0];
      if (pendingCond?.kind === "cond") this.modified?.legs.shift();
      if (!route.legs.some(l => l.kind !== "disco")) this.alert(alert("END OF ROUTE"));
      return;
    }
    // Passing the instrument end (the runway, or a point-in-space approach's MAP) starts the missed approach; its hold
    // is armed so the aircraft holds at the end of it.
    const end = this.instrumentEnd;
    if (passed?.kind === "wpt" && passed.source === "APPR" && (end ? ident === end : /^RW\d{2}/.test(ident))) this.armMissedHold(route);
    const pending = this.modified?.legs[0];
    if (pending?.kind === "wpt" && pending.ident === ident) this.modified?.legs.shift();
    if (!route.legs.some(next => next.kind === "wpt")) this.alert(alert("END OF ROUTE"));
  }

  /** The flight simulation reports the aircraft's state after each step. */
  setAircraft(state: Partial<{ position: LatLon; track: number; heading: number; groundSpeed: number; tas: number; altitude: number; verticalSpeed: number; crossTrack: number; trackError: number; bank: number; pitch: number }>) {
    // Plant state feeds the bench's sensor generators. Only measured navigation may change the FMS position.
    if (state.position) this.truth = state.position;
    const { position: _position, ...rest } = state;
    Object.assign(this.aircraft, rest);
  }

  // ------------------------------------------------------------------ navigation sensors (navigation.ts)

  private autoRadioStations() {
    return this.db.nearby(this.here, 160).filter((entry): entry is Navaid => entry.kind === "navaid" && entry.type !== "NDB"
      && !this.inhibited.includes(entry.ident)).sort((a, b) => distanceNm(this.here, a.position) - distanceNm(this.here, b.position)).slice(0, 6);
  }

  private sampleSensors(): SensorFrame | null {
    if (this.sensorPort) return this.sensorPort.read();
    const now = this.now.getTime(), sequence = ++this.sensorSequence;
    const input = this.gpsInput(now);
    if (this.injected.has("gpsIntegrity")) this.applyGpsIntegrityCondition(input);
    this.sendApproach();
    this.receivers.forEach((receiver, i) => receiver.step(this.gpsBaro[i] ? input : { ...input, baroAltitude: null }));
    this.radioReceiver.tune(this.autoRadioStations(), now);
    const gpsWord = (index: number) => ({ at: now, sequence, status: "NORMAL" as const, value: this.receivers[index].bus() });
    const gps: SensorFrame["gps"] = [gpsWord(0), gpsWord(1)];
    const ra = radioHeight(this.declaredSurface, this.truth, this.physicalAltitude, this.hasCondition("raFail"));
    return { air: { at: now, sequence, status: "NORMAL", value: { headingTrue: this.heading, tasKt: this.aircraft.tas, altitudeFt: this.altitude } },
      attitude: { at: now, sequence, status: "NORMAL", value: { bank: this.aircraft.bank, pitch: this.aircraft.pitch } },
      radioHeight: { at: now, sequence, status: ra.status, value: ra.value },
      gps, radios: this.radioReceiver.sample(this.truth, this.physicalAltitude, now, this.injected.has("dmeOutage")) };
  }

  /** Last input as published, for replay/adapter diagnostics. This is separate from the computed position. */
  get navigationInputs(): SensorFrame | null { return this.sensorFrame ? structuredClone(this.sensorFrame) : null; }

  /** Read a connected simulator before computing guidance so an expired input cannot command one extra step. */
  refreshSensorInput() { this.updateNavigation(0); }

  get raimDeselectedSatellites() { return [...this.raimExcluded].sort((a, b) => a - b); }
  deselectRaimSatellite(prn: number, deselected: boolean) {
    if (!Number.isInteger(prn) || prn < 1 || prn > 32) return;
    if (deselected) this.raimExcluded.add(prn); else this.raimExcluded.delete(prn);
    this.predictiveRaim.requestedAt = this.now.getTime();
  }
  private raimPosition(ident: string): LatLon | undefined {
    const airport = this.db.airport(ident);
    const approach = this.executedApproach;
    const map = approach?.instrumentEnd ?? approach?.runway;
    return airport && ident === this.active.dest && map ? this.coordinates(map) : this.coordinates(ident);
  }
  predictRaimAt(ident: string, automatic = false): boolean {
    if (!this.raimPosition(ident)) return false;
    const airport = this.db.airport(ident);
    const map = airport && ident === this.active.dest ? this.executedApproach?.instrumentEnd ?? this.executedApproach?.runway : null;
    const point = this.profile().points.find(p => p.ident === (map ?? ident));
    this.predictiveRaim.ident = ident;
    this.predictiveRaim.eta = point?.eta ?? null;
    this.predictiveRaim.requestedAt = this.now.getTime();
    if (!automatic) this.setScratch("");
    return true;
  }
  predictRaimEta(text: string): boolean {
    const match = /^(\d{2})(\d{2})Z?$/.exec(text);
    if (!match || Number(match[1]) > 23 || Number(match[2]) > 59 || !this.predictiveRaim.ident) return false;
    const now = this.now, at = new Date(now);
    at.setUTCHours(Number(match[1]), Number(match[2]), 0, 0);
    this.predictiveRaim.eta = at.getTime() < now.getTime() ? at.getTime() + 86_400_000 : at.getTime();
    this.predictiveRaim.requestedAt = now.getTime();
    this.setScratch("");
    return true;
  }
  get predictedRaim(): { at: number; hil: number | null; phase: "APPR" | "TERM" | "ENRT" | "NONE" | "****" }[] {
    const request = this.predictiveRaim;
    const position = request.ident ? this.raimPosition(request.ident) : undefined;
    if (request.eta === null || request.requestedAt === null || !position || this.now.getTime() - request.requestedAt < 1000) return [];
    return [-15, -10, -5, 0, 5, 10, 15].map(minutes => {
      const at = request.eta! + minutes * 60_000;
      const index = this.gpsChoice === "GPS2" ? 1 : this.nav.gpsSource === 2 ? 1 : 0;
      const hil = this.receivers[index].predictRaim(at, position, this.raimDeselectedSatellites);
      const phase = hil === null || this.sensorPort !== null ? "****" : hil <= 0.3 ? "APPR" : hil <= 1 ? "TERM" : hil <= 2 ? "ENRT" : "NONE";
      return { at, hil: this.sensorPort !== null ? null : hil, phase };
    });
  }

  /**
   * Chooses measured navigation, propagates DR from measured air data and the last computed wind, and checks ANP
   * against RNP. A returning position source can cause a reported POSITION SHIFT.
   */
  updateNavigation(dt: number) {
    this.sensorFrame = this.sampleSensors();
    const gps = this.updateGps(this.sensorFrame);
    const previous = this.nav.mode, previousSource = this.nav.gpsSource;
    const now = this.now.getTime();
    const air = sampled(this.sensorFrame?.air, now, this.sensorMaxAge);
    const radio = air ? solveRadio((this.sensorFrame?.radios ?? []).filter(observation => !this.inhibited.includes(observation.station.ident)), this.here, air.altitudeFt, now, this.aircraftProfile.parameters) : null;
    const measurement = (index: number, uncertain = false): PositionMeasurement | null => {
      const assessed = gps.assessed[index], bus = gps.buses[index];
      if (!assessed?.fix || !bus) return null;
      const velocity = (label: "166" | "174") => bus[label].ssm === "NORMAL" && Number.isFinite(bus[label].value) ? bus[label].value : null;
      return { position: assessed.fix, receiver: (index + 1) as 1 | 2,
        anp: uncertain ? Math.max(assessed.hil ?? 0, assessed.hfom ?? 0) : Math.max(ANP_FLOOR_NM, assessed.hfom ?? assessed.hil ?? 0.3),
        northKt: velocity("166"), eastKt: velocity("174") };
    };
    const uncertainOrder = candidates(this.gpsChoice, this.gpsSelected);
    if (previousSource !== null && uncertainOrder.includes(previousSource - 1)) uncertainOrder.sort(index => index === previousSource - 1 ? -1 : 1);
    const uncertainIndex = uncertainOrder.find(index => gps.assessed[index].reason === "INTEGRITY" && gps.assessed[index].fix !== null);
    const predicted = this.navigation.current.position;
    const selection = this.navigation.update({ dt, air, gps: gps.chosen === null ? null : measurement(gps.chosen),
      uncertainGps: uncertainIndex === undefined ? null : measurement(uncertainIndex, true), radio,
      radioApproved: this.flightPhase !== "APPROACH", rnp: this.requiredRnp });
    if (selection.mode !== "DR" && distanceNm(predicted, selection.position) > 0.5) this.alert(alert("POSITION SHIFT"));
    this.here = selection.position;
    const gpsSource = selection.gpsSource;
    this.nav = {
      ...this.nav, mode: selection.mode, dmes: selection.dmes, vor: selection.vor, gpsSource,
      anp: selection.anp, uncertain: selection.uncertain, airValid: selection.airValid,
    };
    const choice = this.gpsSelected ? this.gpsChoice : "OFF";
    for (const index of this.selectionLog.update(this.now, gps.assessed, gpsSource === null ? null : gpsSource - 1, choice, gps.transferred)) {
      if (this.gpsSelected && !(selection.uncertain && gpsSource === index + 1)) this.alert(alert(`GPS${index + 1} NOT USABLE`));
    }
    if (selection.mode !== previous || gpsSource !== previousSource) {
      this.sourceLog = [{ at: this.now, source: gpsSource !== null ? `GPS${gpsSource}` : selection.mode }, ...this.sourceLog].slice(0, 50);
    }
    if (previous === "GPS" && selection.mode !== "GPS") this.alert(alert("GPS NAV LOST"));
    // A receiver the FMS may use has a fix but not the integrity for the phase: GPS POS UNCERTAIN, once per episode.
    if (gps.integrityLost && !this.nav.integrityAlerted) { this.nav.integrityAlerted = true; this.alert(alert("GPS POS UNCERTAIN")); }
    if (!gps.integrityLost) this.nav.integrityAlerted = false;
    // GPS1 and GPS2 both give a fix, whatever their integrity (a spoof passes it), and they differ: GPS DISAGREE.
    const [one, two] = gps.assessed.map(a => a.fix);
    const disagree = one !== null && two !== null && distanceNm(one, two) > GPS_DISAGREE_NM;
    if (disagree && !this.nav.disagreeAlerted) { this.nav.disagreeAlerted = true; this.alert(alert("GPS DISAGREE")); }
    if (!disagree) this.nav.disagreeAlerted = false;

    // ANP above RNP for longer than the time to alert of the phase: CHECK ANP, once per episode. It reads the same
    // effective values as the pages and the lamp (R11).
    const { alertSeconds } = RNP_DEFAULTS[this.flightPhase];
    const performance = this.navPerformance;
    if (performance.anp > performance.rnp) {
      this.nav.unableSince ??= now;
      if (!this.nav.unableAlerted && now - this.nav.unableSince >= alertSeconds * 1000) { this.nav.unableAlerted = true; this.alert(alert("CHECK ANP")); }
    } else { this.nav.unableSince = null; this.nav.unableAlerted = false; }

    const faf = findProcedure(this.db, this.active, "APPROACH")?.faf;
    const first = this.active.legs[0];
    const fafPosition = faf ? this.coordinates(faf) : undefined;
    const nearFaf = first?.kind === "wpt" && first.ident === faf && fafPosition !== undefined && distanceNm(this.here, fafPosition) <= 2;
    const automaticRaim = first?.kind === "wpt" && first.ident === faf && fafPosition !== undefined && distanceNm(this.here, fafPosition) <= 6;
    const raimKey = this.executedApproach ? `${this.planRevision}:${this.executedApproach.ident}` : null;
    if (automaticRaim && raimKey && this.automaticRaimFor !== raimKey) {
      this.automaticRaimFor = raimKey;
      this.predictRaimAt(this.active.dest, true);
    }
    if (nearFaf && !this.armedApproach) {
      if (!this.nav.armAlerted) { this.nav.armAlerted = true; this.alert(alert("ARM APPROACH")); }
    } else if (!nearFaf) this.nav.armAlerted = false;

    // On an RNAV approach, a position without GPS integrity is not good enough to continue.
    const approach = findProcedure(this.db, this.active, "APPROACH");
    const rnavApproach = approach?.approachType === "RNAV" && this.flightPhase === "APPROACH";
    // The approach needs the selected receiver's words to permit it (gpsApproachAuthority: a usable receiver, a valid
    // selected approach, a level, 116), and once it has had vertical guidance (LPV or LNAV/VNAV with 117 valid) in the
    // approach phase, losing it is a loss of approach integrity too (3b, the GPS review's GPS-01 and GPS-06).
    const authority = this.gpsApproachAuthority, vertical = authority.vertical;
    if (rnavApproach && vertical) this.nav.approachVerticalSeen = true;
    if (!rnavApproach) this.nav.approachVerticalSeen = false;
    if (rnavApproach && (selection.mode !== "GPS" || authority.annunciation === "NO APPR" || (this.nav.approachVerticalSeen && !vertical))) {
      if (!this.nav.approachIntegrityAlerted) { this.nav.approachIntegrityAlerted = true; this.alert(alert("NO APPR INTEGRITY")); }
    } else this.nav.approachIntegrityAlerted = false;
  }

  // ------------------------------------------------------------------ GPS receivers (GPS phase 3a)

  /** What the receivers are given: the aircraft's true state, the antenna tilted with its bank and pitch. */
  private gpsInput(time: number): GpsInput {
    return {
      time, position: this.truth, altitude: this.physicalAltitude, baroAltitude: this.altitude, track: this.track,
      groundSpeed: this.groundSpeed, verticalSpeed: this.verticalSpeed, attitude: { bank: this.aircraft.bank, pitch: this.aircraft.pitch, heading: this.heading },
    };
  }

  /** Steps both receivers and judges each bus against the phase's alert limit; chooses the one to navigate on. */
  private updateGps(frame: SensorFrame | null) {
    const time = this.now.getTime();
    const buses = [sampled(frame?.gps[0], time, this.sensorMaxAge), sampled(frame?.gps[1], time, this.sensorMaxAge)];
    const hal = HAL_NM[this.flightPhase];
    const assessed = buses.map(bus => assessReceiver(bus, hal));
    const order = candidates(this.gpsChoice, this.gpsSelected);
    const selection = this.autoSelection.choose({
      choice: this.gpsChoice, selected: this.gpsSelected, assessed, buses, time,
      position: assessed[this.nav.gpsSource === 2 ? 1 : 0]?.fix ?? this.here, positionSource: this.nav.gpsSource === null ? null : this.nav.gpsSource - 1,
      executedCrc: this.pinnedFas?.fas.crc ?? null, sentCrc: this.sentFas?.crc ?? null, approachArmed: this.armedApproach,
    });
    const chosen = selection.chosen;
    this.gpsSelection = { qualified: selection.qualified, refused: selection.refused };
    // A qualified approach transfer is annunciated (AC 20-138D Change 2 §21-2.2(g)); the nav source log records it too.
    if (selection.transferred && chosen !== null) this.alert(alert(`APPR ON GPS${chosen + 1}`));
    // A receiver lost is annunciated whether or not the other takes over: a transfer never hides the failure or the lost
    // redundancy. Its recovery is logged, and by itself changes nothing.
    this.gpsAssessment = { assessed, chosen };
    return { assessed, chosen, buses, transferred: selection.transferred, integrityLost: order.some(index => assessed[index].reason === "INTEGRITY") };
  }

  /**
   * The FAS data block of the active RNAV approach, sent to both receivers whenever the approach changes (GPS phase 3b);
   * no selection when the route has none, or an ILS.
   */
  private sendApproach() {
    // The receivers fly the FAS pinned with the executed plan (pinActive), never one re-derived from a cycle activated since.
    const fas = this.pinnedFas?.fas ?? null;
    const key = fas ? `${fas.referencePathId}:${fas.crc}` : null;
    if (key === this.sentApproach) return;
    this.sentApproach = key;
    this.sentFas = fas;
    for (const receiver of this.receivers) receiver.selectApproach(fas ? { id: fas.referencePathId, fas } : null);
  }

  /**
   * The GPS integrity condition as a satellite fault (3a.5): each receiver keeps only the five highest satellites it
   * can track, and the one RAIM sees best has a 200 m range step. With one degree of freedom that satellite's residual
   * share is at least 1/5 (residualShares), so the step shows as at least 89 m against a threshold of a few metres: always
   * detected, and too little redundancy to exclude, so HIL is a failure warning on both receivers. (A fault on a satellite
   * with a near-zero share, as the lowest has in some geometries, goes undetected: that is the undetected-bias case, not
   * this condition.) Re-chosen each step as the sky moves.
   */
  private applyGpsIntegrityCondition(input: GpsInput) {
    const sky = this.constellation.sky(input.time, input.position, input.altitude, input.attitude, 5)
      .filter(s => s.visible && s.cn0 >= 30).sort((a, b) => b.elevation - a.elevation);
    const kept = sky.slice(0, 5), shares = kept.length === 5 ? residualShares(kept.map(s => s.los)) : [];
    const faulty = shares.length ? kept[shares.indexOf(Math.max(...shares))].prn : null;
    for (const receiver of this.receivers) {
      receiver.deselect(sky.slice(5).map(s => s.prn));
      if (this.integrityFaultPrn !== null && this.integrityFaultPrn !== faulty) receiver.satelliteFault(this.integrityFaultPrn, null);
      if (faulty !== null && faulty !== this.integrityFaultPrn) receiver.satelliteFault(faulty, { kind: "STEP", metres: 200 });
    }
    this.integrityFaultPrn = faulty;
  }

  private clearGpsIntegrityCondition() {
    for (const receiver of this.receivers) {
      receiver.deselect([]);
      if (this.integrityFaultPrn !== null) receiver.satelliteFault(this.integrityFaultPrn, null);
    }
    this.integrityFaultPrn = null;
  }

  /** GPS1 and GPS2, for the bench to read and to inject faults into; call gpsUpdated after changing one. */
  get gps(): readonly GpsReceiver[] { return this.receivers; }
  private get sensorMaxAge() { return this.aircraftProfile.parameters.sensorMaxAge.value * 1000; }
  private gpsBus(index: number): GpsBus | null { return sampled(this.sensorFrame?.gps[index], this.now.getTime(), this.sensorMaxAge); }
  /**
   * The feedback a hover hold may use (plan R3-02): the receiver the preserved navigation policy has selected as usable
   * (the #1243 assessment with #1251's AUTO or manual selection; the FMS in GPS mode), with a valid fix and both
   * velocity words (166 north, 174 east) valid and finite. Null otherwise: never a receiver merely shown for display.
   */
  get hoverFeedback(): { source: 1 | 2; position: LatLon; north: number; east: number } | null {
    const source = this.nav.gpsSource;
    if (source === null) return null;
    const assessed = this.gpsAssessment.assessed[source - 1];
    if (!assessed?.usable || !assessed.fix) return null;
    const bus = this.gpsBus(source - 1);
    const north = bus?.["166"], east = bus?.["174"];
    if (!north || !east || north.ssm !== "NORMAL" || east.ssm !== "NORMAL") return null;
    if (typeof north.value !== "number" || typeof east.value !== "number" || !Number.isFinite(north.value) || !Number.isFinite(east.value)) return null;
    return { source, position: assessed.fix, north: north.value, east: east.value };
  }
  /** How the FMS judged each receiver at the last navigation update, and which it navigates on (index), if any. */
  get gpsStatus(): GpsAssessment { return this.gpsAssessment; }
  get gpsReceiverChoice(): GpsChoice { return this.gpsChoice; }
  /** Whether the approach may be flown on the selected receiver after the last source change, and why a transfer was refused. */
  get gpsApproachSource() { return this.gpsSelection; }
  /** Receivers lost and recovered, and each transfer of the FMS's GPS source with its reason, newest first. */
  get gpsSelectionLog() { return this.selectionLog.entries; }
  get navSourceLog(): readonly { at: Date; source: string }[] { return this.sourceLog; }

  /** GPS NAV (NAV OPTIONS): AUTO, one receiver chosen by hand (no fallback to the other), or GPS deselected. */
  selectGpsReceiver(choice: GpsChoice | "OFF") {
    if (choice === "OFF") this.gpsSelected = false;
    else { this.gpsSelected = true; this.gpsChoice = choice; }
    this.updateNavigation(0);
    this.emit();
  }

  /** Re-reads the receivers after the bench changed one (a fault, an override), without advancing time. */
  gpsUpdated() { this.updateNavigation(0); this.emit(); }

  /** The aircraft attitude the flight simulation reports (bank, flight-path pitch), which tilts the GPS antennas. */
  get attitude() { return { bank: this.aircraft.bank, pitch: this.aircraft.pitch }; }

  /** Gives or takes one receiver's baro altitude input (its air data bus); call gpsUpdated after. */
  setGpsBaro(index: number, available: boolean) { this.gpsBaro[index] = available; }

  /** The phase that sets the default RNP: approach on an approach leg, terminal within 30 NM of either airport. */
  get flightPhase(): FlightPhase {
    const leg = this.active.legs[0];
    if (leg && leg.kind !== "disco" && leg.source === "APPR") return "APPROACH";
    const near = (icao: string) => { const airport = this.db.airport(icao); return airport !== undefined && distanceNm(this.here, airport.position) <= 30; };
    return near(this.active.origin) || near(this.active.dest) ? "TERMINAL" : "EN ROUTE";
  }

  // ------------------------------------------------------------------ vertical profile and predictions (vnav.ts)

  /**
   * Ground speed along a course, from the true airspeed and the wind by the wind triangle (kinematics.ts), or null when
   * the course cannot be flown with progress at that airspeed: the prediction is then unknown, not given a floor.
   */
  groundSpeedOn(course: number, tas = this.plannedSpeed) {
    return predictedGroundSpeed(tas, course, this.wind);
  }

  /** The cold temperature correction to the FAF altitude, from the destination temperature on VNAV (0 at or above ISA). */
  get coldCorrection() {
    const { destTemp, fafAltitude, runwayElevation } = this.vnav;
    return destTemp === null ? 0 : coldTemperatureCorrection(fafAltitude - runwayElevation, destTemp, runwayElevation);
  }

  get fafAltitudeCorrected() { return this.vnav.fafAltitude + this.coldCorrection; }

  /**
   * Valid barometric altitude, or null without fresh connected air data. The default bench's ideal air-data generator
   * has no failure injection; the procedure speed release reads null as invalid (R3-04).
   *
   * As the air-data word carries it: pressure altitude at its 1 ft resolution (ARINC 429 label 203, 17-bit BNR),
   * rounded to the nearest foot (a laboratory decision: the standard fixes the resolution, not the rounding). The
   * truth settles onto a captured altitude without ever reaching it exactly, so the FMS must not compare the raw value.
   */
  get validBaroAltitude(): number | null {
    if (!this.sensorPort) return Math.round(this.altitude);
    const altitude = sampled(this.sensorFrame?.air, this.now.getTime(), this.sensorMaxAge)?.altitudeFt;
    return altitude !== undefined && Number.isFinite(altitude) ? Math.round(altitude) : null;
  }

  /** The procedure speed limit in force on the executed approach (C.5, procedureSpeed.ts), knots indicated; or null. */
  get procedureSpeed(): ProcedureSpeed | null {
    return procedureSpeedLimit(findProcedure(this.db, this.active, "APPROACH"), this.active.approach?.transition, this.active.legs[0], this.validBaroAltitude);
  }

  /** The speed limit at the active fix, in the hold, or in force on the procedure (as true airspeed), if any. */
  private get speedLimit() {
    const leg = this.active.legs[0];
    const constraint = leg?.kind === "wpt" ? leg.speed : undefined;
    // The holding speed is an indicated airspeed (table and chart), flown as the true airspeed at the present altitude.
    const hold = this.active.hold?.status === "IN PROGRESS" ? tasFromIas(this.active.hold.speed, this.altitude) : undefined;
    const procedure = this.procedureSpeed;
    return Math.min(constraint ?? Infinity, hold ?? Infinity, procedure ? tasFromIas(procedure.kt, this.altitude) : Infinity);
  }

  /** The planned speed: cruise, within the speed limit. The predictions (and so the rendezvous) use it. */
  get plannedSpeed() { return Math.min(this.vnav.cruiseSpeed, this.speedLimit); }

  /** The true airspeed flown: the planned speed, or during an active rendezvous the speed that arrives on time. */
  get targetSpeed() {
    const rendezvous = this.rndz.active ? this.rendezvous() : null;
    // An RTA with no computed speed (overdue, or an unknown path) carries the planned speed: it commands nothing.
    return rendezvous === null ? this.plannedSpeed : Math.min(rendezvous.speed, this.speedLimit);
  }

  /**
   * The planned vertical profile and predictions along the active route: altitude, ETA and fuel at each waypoint,
   * the top and end of descent, and the first climb constraint that cannot be met. The E/D is the runway.
   */
  profile(route: Route = this.active): Profile {
    return computeProfile({
      waypoints: this.predictionLegs(route).waypoints, altitude: this.altitude, cruiseAltitude: this.vnav.cruiseAltitude, climbRate: 1000,
      pathAngle: this.vnav.pathAngle, phase: this.vphase.phase, fuel: this.fuel.quantity, fuelFlow: this.fuel.flow, now: this.now.getTime(),
      // Held stationary off the plan (the ground speed below measurable progress): no ETA or EFOB ahead (plan B1.7).
      noProgress: !makingProgress(this.groundSpeed),
    });
  }

  /**
   * The time at the fix of the active route's leg `index`, `miles` ahead, as the pages and the output bus show it. In a
   * manual hold being flown, the prediction's time along the pattern still to fly, so the hold fix is its next crossing
   * (S300 5-17, Astra F1): turning away from the fix does not make it unknown. Elsewhere the distance at the closure
   * speed, with no time without progress toward the fix (C8, C9). Null when there is no time.
   */
  shownEta(index: number, miles: number): number | null {
    const hold = this.active.hold, legs = this.active.legs, leg = legs[index];
    const inHold = hold?.status === "IN PROGRESS" && legs[0]?.kind === "wpt" && legs[0].ident === hold.fix;
    if (inHold && leg?.kind === "wpt") {
      const point = this.profile().points[legs.slice(0, index).filter(l => l.kind === "wpt").length];
      return point?.ident === leg.ident ? point.eta : null;
    }
    return makingProgress(this.closureSpeed) ? this.now.getTime() + (miles / this.closureSpeed) * 3_600_000 : null;
  }

  /** The legs the predictions fly, as computeProfile takes them, with the course of each (for the RTA's wind triangle). */
  private predictionLegs(route: Route) {
    const geometry = this.legGeometry(route);
    // The descent meets the approach: the final approach fix is crossed at its (temperature-corrected) altitude.
    const runwayAt = route.legs.findIndex(leg => leg.kind === "wpt" && /^RW\d{2}/.test(leg.ident));
    // Where the destination-type predictions end (predictions.ts): the MAP, or arrival over the landing site.
    const end = predictionEndpoint(route, this.db);
    // A MANUAL hold with no exit armed: from its fix on, the predictions assume the exit at its next crossing (HOLD-ETA,
    // inferred; M300 5-17), so they are CONDITIONAL (plan R3-03).
    // The missed-approach hold leaves by itself after one racetrack (MISSED-HOLD), so it is timed instead (below).
    const hold = route.hold && route.hold.exit === "MANUAL" && !route.hold.missed && route.hold.status !== "EXIT ARMED" ? route.hold : null;
    let basis: PredictionBasis = "known";
    let lastFix: LatLon | null = this.here;
    const waypoints: ProfileInput["waypoints"] = [];
    const courses: (number | null)[] = [];
    // The procedure speed limit in force on each leg ahead, not only the active one (Astra F2): the limit that leg is
    // flown under (procedureSpeed.ts), as true airspeed at the altitude the leg starts from (the last constraint
    // altitude before it, or the present altitude). The same altitude decides the missed approach release.
    const approach = findProcedure(this.db, route, "APPROACH");
    let planAltitude = this.altitude;
    const procedureTas = (leg: Leg) => {
      const limit = procedureSpeedLimit(approach, route.approach?.transition, leg, planAltitude);
      return limit ? tasFromIas(limit.kt, planAltitude) : Infinity;
    };
    // The ground path of a leg where it is not the straight line to its fix: the manual hold being flown (Astra F1).
    const paths: (PathPiece[] | null)[] = [];
    route.legs.forEach((leg, i) => {
      // Past a discontinuity or a manually terminated leg the path is not defined; after a course or heading leg that
      // ends on an event, the leg into the next fix is estimated from the last fixed point.
      if (leg.kind === "disco") { basis = "unknown"; return; }
      if (leg.kind === "cond") {
        if (leg.path === "VM" || leg.path === "FM") basis = "unknown";
        else if (basis === "known") basis = "estimated";
        if (leg.altitude !== undefined) planAltitude = leg.altitude;
        return;
      }
      const to = this.coordinates(leg.ident, route) ?? null;
      let legDistance = geometry[i]?.distance ?? null, course = geometry[i]?.course;
      if (legDistance === null && basis === "estimated" && lastFix && to) { legDistance = distanceNm(lastFix, to); course = courseDeg(lastFix, to); }
      // The active leg is flown at the planned speed (its constraint, a hold, the procedure limit); a later leg at the
      // cruise speed, its own speed constraint (which applies to the leg into its fix), or the procedure limit in force
      // on it, whichever is lowest.
      const tas = i === 0 ? this.plannedSpeed : Math.min(this.vnav.cruiseSpeed, leg.speed ?? Infinity, procedureTas(leg));
      // The final approach fix is crossed at its (cold-corrected) altitude: the executed approach's FAF, or on the
      // demonstration route without an approach the fix before the runway.
      const isFaf = this.finalApproachFix ? leg.ident === this.finalApproachFix && i < runwayAt : i === runwayAt - 1;
      const constraint = isFaf ? { kind: "AT" as const, altitude: this.fafAltitudeCorrected } : parseConstraint(leg.altitude);
      if (constraint) planAltitude = constraint.kind === "WINDOW" ? constraint.lower : constraint.altitude;
      // In the manual hold, the fix's next crossing is at the end of the path still to fly around the pattern, not the
      // straight line to the fix (S300 5-17: the direct distance and the ETA at the next crossing are different things).
      // The direct distance stays the leg's distance, as shown; the time is the path's, piece by piece through the wind.
      const path = i === 0 && hold?.status === "IN PROGRESS" && leg.ident === hold.fix && to ? this.holdPathToCrossing(hold, to, tas) : null;
      // Timed at the airspeed flown now: under the ADVISORY policy the crew's selected speed, not the planned one.
      const holdTas = this.trueAirspeed ?? tas;
      const pathHours = path ? piecesHours(path, c => this.groundSpeedOn(c, holdTas)) : undefined;
      waypoints.push({
        ident: leg.ident, legDistance, groundSpeed: pathHours === null ? null : this.groundSpeedOn(course ?? this.track, tas),
        ...(pathHours !== undefined && pathHours !== null ? { hours: pathHours } : {}),
        constraint, endOfDescent: i === runwayAt, basis, missed: leg.source === "MISSED",
        ...(hold && leg.ident === hold.fix ? { assumption: "HOLD EXIT NEXT CROSSING" } : {}),
        ...(end && i === end.legIndex ? { endpoint: { kind: end.kind, label: end.label } } : {}),
        ...this.predictedHold(route, leg, to, course ?? this.track, tas),
      });
      courses.push(legDistance === null ? null : course ?? this.track);
      paths.push(path);
      lastFix = to;
    });
    return { waypoints, courses, paths };
  }

  /** Reports the hold path the flight is flying, where it is on it and where the next fix passage is (flight.ts). */
  attachHoldPath(source: () => HoldPathReport | null) { this.holdPathSource = source; }
  private holdPathSource: (() => HoldPathReport | null) | null = null;

  /**
   * The ground path from the aircraft to the manual hold's next fix passage: from the path the flight reports, or, with
   * no flight attached (the hold only just entered, a test), one whole racetrack from the fix at the holding speed.
   */
  private holdPathToCrossing(hold: Hold, fix: LatLon, tas: number): PathPiece[] {
    const report = this.holdPathSource?.() ?? null;
    if (report) return holdPathToPassage(report, this.here);
    const holdTas = Math.max(tas, tasFromIas(hold.speed, this.altitude));
    const legNm = hold.legDistance ?? ((hold.legTime ?? defaultLegMinutes(this.altitude)) * holdTas) / 60;
    const geometry = holdGeometry(fix, hold.inbound, hold.turn, holdTas, this.wind.speed, legNm, MAX_BANK);
    return geometry ? holdPathToPassage({ segments: geometry.racetrack, index: 0, passageAt: geometry.racetrack.length - 1 }, fix) : [];
  }

  /**
   * A hold at a waypoint ahead that leaves by itself, as the predictions time it (predictions.ts holdAllowance): the
   * route's hold at its fix, else a procedure hold coded on the leg (HF, HA) as it will be armed there.
   */
  private predictedHold(route: Route, leg: Extract<Leg, { kind: "wpt" }>, fix: LatLon | null, arrivalTrack: number, tas: number): { hold?: NonNullable<ProfileInput["waypoints"][number]["hold"]> } {
    const hold = route.hold?.fix === leg.ident ? route.hold : leg.hold ? this.holdFromProcedure(leg.ident, leg.hold, "ARMED") : null;
    if (!hold || !fix) return {};
    // Sized at the hold altitude (its target), or the present altitude when it has none.
    const target = parseConstraint(hold.altitude);
    const altitude = target === null ? this.altitude : target.kind === "WINDOW" ? target.lower : target.altitude;
    const allowance = holdAllowance(hold, fix, arrivalTrack, tas, this.wind, altitude);
    if (!allowance) return {};
    return { hold: { hours: allowance.hours, ...(hold.exit === "AT TGT ALT" && !hold.missed ? { target } : {}) } };
  }

  /** The latched VNAV phase, and why it last changed (the flight simulation records each change as a mode event). */
  get verticalPhase(): VerticalPhase { return this.vphase.phase; }
  get verticalPhaseReason() { return this.vphase.reason; }

  /**
   * Latches the VNAV phase. CLIMB becomes CRUISE on reaching the cruise altitude. CLIMB or CRUISE becomes DESCENT at
   * the top of descent (the profile first reports descending) or on DES NOW. DESCENT is left only by an explicit
   * event, never by comparing the altitude with cruise: a cruise altitude entered above the aircraft, or the missed
   * approach becoming the active leg (a go-around, or passing the runway). Both return to CLIMB. A route change that
   * puts climb constraints ahead does not leave the descent; the crew enters a cruise altitude for that.
   */
  private updateVerticalPhase() {
    const v = this.vphase, cruise = this.vnav.cruiseAltitude;
    const raised = v.cruise !== null && cruise !== v.cruise && cruise > this.altitude + 50;
    v.cruise = cruise;
    const set = (phase: VerticalPhase, reason: string) => { v.phase = phase; v.reason = reason; };
    if (v.phase === "DESCENT") {
      const leg = this.active.legs[0];
      const leave = raised ? `cruise altitude ${cruise} entered above the aircraft`
        : leg && leg.kind !== "disco" && leg.source === "MISSED" ? "missed approach" : null;
      if (leave === null) return;
      set("CLIMB", leave);
      // Leaving the descent cancels a DES NOW, which would otherwise start it again.
      this.vnav.desNow = false;
      return;
    }
    if (raised) set("CLIMB", `cruise altitude ${cruise} entered above the aircraft`);
    else if (v.phase === "CLIMB" && this.altitude >= cruise - 50) set("CRUISE", `cruise altitude ${cruise} reached`);
    if (this.vnav.desNow) set("DESCENT", "DES NOW");
    else if (this.profile().descending) set("DESCENT", "top of descent");
  }

  /**
   * Burns fuel for dt seconds and checks the plan: FUEL RESERVE when fuel on board reaches the reserve, NOT ENOUGH
   * FUEL when the prediction at the destination is below it, UNABLE NEXT ALTITUDE (the manual’s wording is not in
   * the alert list, so the advisory is used) when a climb constraint cannot be made.
   */
  updatePerformance(dt: number) {
    for (const [ident, motion] of Object.entries(this.moving)) {
      const at = this.points[ident];
      if (at) this.points[ident] = offset(at, motion.track, (motion.speed * dt) / 3600);
    }
    const rendezvous = this.rndz.active ? this.rendezvous() : null;
    if (rendezvous && rendezvous.required !== null && !rendezvous.achievable && !this.rndz.alerted) { this.rndz.alerted = true; this.alert(alert("RENDEZVOUS UNACHIEVABLE")); }
    if (rendezvous?.achievable) this.rndz.alerted = false;
    // At the target altitude the descent ends and the aircraft levels there until the crew cancels it.
    if (this.tdn.active && this.altitude <= this.tdn.targetAltitude + 20) { this.tdn.active = false; this.tdn.level = true; }
    const before = this.fuel.quantity;
    this.fuel.quantity = Math.max(0, before - (this.fuel.flow * dt) / 3600);
    if (before > this.fuel.reserve && this.fuel.quantity <= this.fuel.reserve) this.alert(alert("FUEL RESERVE"));
    this.updateVerticalPhase();
    const profile = this.profile();
    const atDestination = profile.destination?.fuel ?? null;
    if (atDestination !== null && atDestination < this.fuel.reserve) {
      if (!this.perf.notEnoughAlerted) { this.perf.notEnoughAlerted = true; this.alert(alert("NOT ENOUGH FUEL")); }
    } else this.perf.notEnoughAlerted = false;
    if (profile.unableNext && profile.unableNext !== this.perf.unableAlertedFor) { this.perf.unableAlertedFor = profile.unableNext; this.advisory("UNABLE NEXT ALT"); }
    if (!profile.unableNext) this.perf.unableAlertedFor = null;
  }

  // ------------------------------------------------------------------ tactical: rendezvous, moving waypoints, TDN


  /**
   * The rendezvous: the distance along the route to the waypoint, the time left, and the speed that arrives on time.
   * Achievable when that speed is within the limits; the flown speed is then held within them.
   */
  rendezvous(): Rendezvous | null {
    const { wpt, time } = this.rndz;
    if (!wpt || time === null) return null;
    const profile = this.profile();
    const at = profile.points.findIndex(p => p.ident === wpt);
    if (at < 0) return null;
    const point = profile.points[at];
    // The speed limits in indicated airspeed (plan R3-04): the profile's VMINI and maximum, narrowed by the crew's.
    const minIas = Math.max(ACTIVE_PROFILE.parameters.vmini.value, this.rndz.minSpeed), maxIas = Math.min(ACTIVE_PROFILE.parameters.maximumSpeed.value, this.rndz.maxSpeed);
    const none = (reason: string): Rendezvous => ({ distance: point.distance, required: null, requiredIas: null, speed: this.plannedSpeed, achievable: false, eta: point.eta, status: "UNKNOWN", reason });
    // Scoped to the path to its fix (R3-03): a later unknown segment does not matter; an unknown one before it does.
    if (point.status === "UNKNOWN" || point.distance === null) return none(point.reason ?? "UNKNOWN");
    const hours = (time - this.now.getTime()) / 3_600_000;
    if (hours <= 0) return none("OVERDUE");
    const required = this.requiredTas(at, hours);
    if (required === null) return none("NO TAS REACHES IT");
    // Converted to IAS at each leg's planned altitude (ISA) and compared with the limits in IAS.
    const legs = profile.points.slice(0, at + 1);
    const ias = legs.map(p => iasFromTas(required, p.altitude ?? this.altitude));
    const [low, high] = [Math.min(...ias), Math.max(...ias)];
    const achievable = low >= minIas - 1e-9 && high <= maxIas + 1e-9;
    // Flown now: the required true airspeed, held within the limits at the present altitude.
    const speed = Math.min(tasFromIas(maxIas, this.altitude), Math.max(tasFromIas(minIas, this.altitude), required));
    return {
      distance: point.distance, required, requiredIas: iasFromTas(required, this.altitude), speed, achievable, eta: point.eta,
      status: point.status, reason: !achievable ? (high > maxIas ? "ABOVE MAX SPEED" : "BELOW VMINI") : point.reason,
    };
  }

  /**
   * The true airspeed that flies the legs to the rendezvous fix (prediction legs 0 to `at`) in the time left, through
   * the wind on each leg (the wind triangle, kinematics.ts): the time along the path falls as the airspeed rises, so it
   * is found by bisection. Null when no airspeed up to 400 kt reaches the fix in time.
   */
  private requiredTas(at: number, hours: number): number | null {
    const { waypoints, courses, paths } = this.predictionLegs(this.active);
    // Each leg as its pieces: the straight line to its fix, or the manual hold's path to its next crossing (Astra F1).
    const pieces = waypoints.slice(0, at + 1).flatMap((w, i) => paths[i] ?? [{ distance: w.legDistance ?? 0, course: courses[i] ?? this.track }]);
    // A hold before the fix that leaves by itself takes its time whatever the speed on the legs (holdAllowance).
    const held = waypoints.slice(0, at).reduce((sum, w) => sum + (w.hold?.hours ?? 0), 0);
    const time = (tas: number) => held + pieces.reduce((sum, leg) => {
      if (leg.distance === 0) return sum;
      const gs = predictedGroundSpeed(tas, leg.course, this.wind);
      return gs === null ? Infinity : sum + leg.distance / gs;
    }, 0);
    let low = 1, high = 400;
    if (time(high) > hours) return null;
    for (let i = 0; i < 60; i += 1) { const mid = (low + high) / 2; if (time(mid) > hours) low = mid; else high = mid; }
    return high;
  }

  /** Defines a moving waypoint at a position, moving on a track at a speed. */
  defineMoving(ident: string, position: LatLon, track: number, speed: number) {
    this.points[ident] = position;
    this.moving[ident] = { track, speed };
    this.pilot = [...this.pilot.filter(p => p.ident !== ident), { ident, position, definition: `MOVING ${String(track).padStart(3, "0")}/${speed}KT` }];
  }

  get movingWaypoints() { return this.moving; }

  /** The TDN path angle from present altitude to the target altitude at the point before the reference. */
  tdnAngle(): number | null {
    const ref = this.tdn.refId ? this.coordinates(this.tdn.refId) : undefined;
    if (!ref) return null;
    const run = distanceNm(this.here, ref) - this.tdn.distanceBefore;
    const drop = this.altitude - this.tdn.targetAltitude;
    if (run <= 0 || drop <= 0) return null;
    return (Math.atan(drop / (run * 6076.12)) * 180) / Math.PI;
  }

  /** EXECUTE TDN: begins the tactical descent, or says TDN NOT POSSIBLE when the angle is too steep or undefined. */
  executeTdn(): boolean {
    const angle = this.tdnAngle();
    if (angle === null || angle > this.tdn.maxAngle) { this.alert(alert("TDN NOT POSSIBLE")); return false; }
    this.tdn.active = true;
    this.tdn.level = false;
    return true;
  }

  /** CANCEL TDN: ends the descent, or the level-off at its altitude, and hands the altitude back to VNAV. */
  cancelTdn() {
    this.tdn.active = false;
    this.tdn.level = false;
  }

  // ------------------------------------------------------------------ navigation database cycles

  get activeCycle() { return this.cycles[0]; }
  get inactiveCycle() { return this.cycles[1] ?? null; }

  /** Loads, activations and re-resolutions of navigation data in this session, oldest first. */
  get datasetLog(): readonly { at: Date; action: string; detail: string }[] { return this.datasetEvents; }

  private recordDataset(action: string, detail: string) {
    this.datasetEvents = [...this.datasetEvents, { at: this.now, action, detail }];
  }

  /**
   * Activates the inactive cycle, as the crew does on IDENT when a new cycle becomes effective (or the bench's
   * Activate): the two swap, and the change is recorded. The active plan is NOT re-resolved: its fixes keep the
   * positions pinned when it became active, and the record names those the new cycle places differently or lacks.
   * They take the new cycle's positions only when the crew executes a modification, which shows the route against the
   * new cycle before EXEC; that EXEC is recorded as ROUTE RE-RESOLVED with the fixes that moved.
   */
  swapCycles() {
    if (this.cycles.length < 2) return;
    this.cycles = [this.cycles[1], this.cycles[0]];
    // SELECT DESIRED WPT choices index the previous cycle's entries.
    this.chosen = {};
    this.outOfDateAlerted = false;
    const change = this.resolutionChange(this.pins, ident => this.lookup(ident, this.active));
    const id = this.activeCycle.id;
    const pinnedFas = this.pinnedFas, candidate = pinnedFas ? this.deriveFas() : null;
    const fasDiffers = !!pinnedFas && (!candidate || candidate.crc !== pinnedFas.fas.crc);
    this.recordDataset(`ACTIVATE ${id}`, [
      this.activeCycle.source, `${this.inactiveCycle!.id} now inactive`, "active plan kept as executed: pinned positions, unresolved fixes still unresolved",
      ...(change.moved.length ? [`placed differently in ${id}: ${change.moved.join(", ")}`] : []),
      ...(change.missing.length ? [`absent from ${id}: ${change.missing.join(", ")}`] : []),
      ...(change.resolved.length ? [`newly defined in ${id}, unresolved in the active plan until EXEC: ${change.resolved.join(", ")}`] : []),
      ...(fasDiffers ? [`approach ${pinnedFas!.fas.referencePathId} defined differently in ${id} (${fasDifference(pinnedFas!.fas, candidate!)}): flown as executed until EXEC`] : []),
    ].join("; "));
    this.emit();
  }

  private planIdents() { return new Set(this.active.legs.flatMap(leg => (leg.kind === "wpt" ? [leg.ident] : []))); }

  /**
   * Pins the database fixes of the active plan where the active cycle places them now: on EXEC and for the initial
   * plan. Executing after a cycle change is the crew accepting the new cycle's positions; the fixes that moved are
   * recorded.
   */
  private pinActive() {
    const before = this.pins, cycleChanged = this.pinnedIn !== this.activeCycle, fasBefore = this.pinnedFas;
    this.pins = new Map();
    this.pinnedIn = this.activeCycle;
    this.planRevision += 1;
    for (const ident of this.planIdents()) {
      if (this.ownPoint(ident)) continue;
      this.pins.set(ident, this.lookup(ident, this.active) ?? null);
    }
    // The approach geometry the GPS flies is part of the executed plan: its FAS is derived now, from this cycle, and kept.
    const fas = this.deriveFas();
    this.pinnedFas = fas ? { fas, cycle: this.activeCycle.id, revision: this.planRevision } : null;
    this.pinApproachReference();
    if (!cycleChanged) return;
    const change = this.resolutionChange(before, ident => this.pins.get(ident) ?? undefined);
    const fasChanged = fasBefore && fas && fasBefore.fas.referencePathId === fas.referencePathId && fasBefore.fas.crc !== fas.crc;
    const parts = [
      ...(change.moved.length ? [`moved: ${change.moved.join(", ")}`] : []),
      ...(change.resolved.length ? [`newly resolved: ${change.resolved.join(", ")}`] : []),
      ...(change.missing.length ? [`newly missing: ${change.missing.join(", ")}`] : []),
      ...(fasChanged ? [`approach ${fas.referencePathId} FAS re-resolved: ${fasDifference(fasBefore.fas, fas)}`] : []),
    ];
    if (parts.length) this.recordDataset("ROUTE RE-RESOLVED", `executed plan resolved in ${this.activeCycle.id}; ${parts.join("; ")}`);
  }

  /**
   * The FAS data block for the active route's RNAV approach as the active cycle defines it: the procedure and runway from
   * the cycle, the FAF where the executed plan has it. Derived only when a plan becomes active (pinActive), and to compare.
   */
  private deriveFas(): FasDataBlock | null {
    const approach = findProcedure(this.db, this.active, "APPROACH");
    const runway = approach ? this.db.airport(approach.airport)?.runways.find(entry => entry.ident === approach.runways[0]) : undefined;
    return approach ? buildFas(approach, runway, approach.airport, approach.faf ? this.coordinates(approach.faf) : undefined) : null;
  }

  /**
   * The executed approach's final approach fix and runway, and on a newly executed approach the APPROACH REF values it
   * implies: the FAF altitude from its leg and the runway's threshold elevation. A crew entry on the page stands until a
   * different approach is executed.
   */
  private pinApproachReference() {
    const approach = findProcedure(this.db, this.active, "APPROACH");
    if (!approach) { this.executedApproach = null; return; }
    const changed = this.executedApproach?.ident !== approach.ident;
    this.executedApproach = {
      ident: approach.ident, faf: approach.faf ?? null, runway: approach.runways[0] ?? null,
      instrumentEnd: approach.endpoint?.instrumentEnd.fix ?? approach.runways[0] ?? null,
    };
    if (!changed) return;
    const fafLeg = approach.legs.find(leg => "ident" in leg && leg.ident === approach.faf);
    const fafAltitude = fafLeg && "ident" in fafLeg ? Number(/^(\d{1,5})/.exec(fafLeg.altitude ?? "")?.[1] ?? NaN) : NaN;
    if (Number.isFinite(fafAltitude)) this.vnav.fafAltitude = fafAltitude;
    const runway = this.db.airport(approach.airport)?.runways.find(entry => entry.ident === approach.runways[0]);
    if (runway) this.vnav.runwayElevation = runway.elevation;
  }

  /** The executed approach's final approach fix, or null without an approach (the demonstration route alone). */
  get finalApproachFix() { return this.executedApproach?.faf ?? null; }

  /**
   * The executed approach's instrument end, its missed approach point: the runway on a runway approach, the MAP fix on
   * a point-in-space approach (Procedure.endpoint). Null without an executed approach.
   */
  get instrumentEnd() { return this.executedApproach?.instrumentEnd ?? null; }

  /**
   * On the final approach segment: the final approach fix has been sequenced and the instrument end (the runway, or a
   * point-in-space approach's MAP) is still ahead. Without an executed approach, the runway being the active waypoint
   * (the demonstration route, whose last fix is its FAF).
   */
  get onFinalSegment() {
    const legs = this.active.legs, leg = legs[0];
    if (leg?.kind !== "wpt" || this.sequenced === null) return false;
    const end = this.instrumentEnd;
    const endAt = end ? legs.findIndex(entry => entry.kind === "wpt" && entry.ident === end && entry.source === "APPR")
      : legs.findIndex(entry => entry.kind === "wpt" && /^RW\d{2}/.test(entry.ident));
    if (endAt < 0) return false;
    const faf = this.finalApproachFix;
    if (!faf) return endAt === 0;
    return !legs.slice(0, endAt).some(entry => entry.kind === "wpt" && entry.ident === faf);
  }

  /**
   * The runway the final approach segment leads to, or null: the executed approach's runway while it is ahead (none on
   * a point-in-space approach, whose final ends at its MAP); without an executed approach, the first runway leg ahead.
   */
  get finalRunway() {
    const approach = this.executedApproach;
    if (approach) return approach.runway && this.active.legs.some(entry => entry.kind === "wpt" && entry.ident === approach.runway) ? approach.runway : null;
    const leg = this.active.legs.find(entry => entry.kind === "wpt" && /^RW\d{2}/.test(entry.ident));
    return leg?.kind === "wpt" ? leg.ident : null;
  }

  /** The FAS the executed plan flies, the cycle it was derived from and the plan revision that accepted it. */
  get executedFas() { return this.pinnedFas; }

  /** How the plan's pinned fixes compare with where another source places them: moved, now missing, newly defined. */
  private resolutionChange(pins: Map<string, LatLon | null>, place: (ident: string) => LatLon | undefined) {
    const plan = this.planIdents();
    const moved: string[] = [], missing: string[] = [], resolved: string[] = [];
    for (const [ident, at] of pins) {
      if (!plan.has(ident)) continue;
      const now = place(ident);
      if (at && now && !samePlace(at, now)) moved.push(ident);
      else if (at && !now) missing.push(ident);
      else if (!at && now) resolved.push(ident);
    }
    return { moved, missing, resolved };
  }

  /** Removes a pilot waypoint that was only a step in defining another (a WPTnn made from a position entry). */
  forgetPilot(ident: string) {
    delete this.points[ident];
    this.pilot = this.pilot.filter(p => p.ident !== ident);
  }

  /** Defines a waypoint in the temporary database, from REF NAV DATA: an ident of its own at a position. */
  defineTemporary(ident: string, position: LatLon) {
    this.points[ident] = position;
    this.pilot = [...this.pilot.filter(p => p.ident !== ident), { ident, position, definition: "TEMP DB" }];
  }

  // ------------------------------------------------------------------ maintenance and dual operation

  get faultLog() { return this.faults; }
  get selfTestState() { return this.selfTest; }
  get otherFms() { return this.crossRoute; }

  /** Whether the other FMS holds the same active route: always in dual operation, not necessarily when independent. */
  get crossSideInSync() {
    const signature = (route: Route) => JSON.stringify(route.legs.map(leg => (leg.kind === "wpt" ? leg.ident : leg.kind)));
    return signature(this.crossRoute) === signature(this.active);
  }

  /** SELF TEST: runs for five seconds, then passes unless a fault condition is present. */
  startSelfTest() { this.selfTest = { startedAt: this.now.getTime(), result: null }; }

  private recordFault(text: string) { this.faults = [{ at: this.now, text }, ...this.faults].slice(0, 20); }

  /** On an approach that is not an ILS: the NPA annunciator. */
  get nonPrecisionApproach() {
    return this.flightPhase === "APPROACH" && findProcedure(this.db, this.active, "APPROACH")?.approachType !== "ILS";
  }

  /**
   * The approach the crew is flying, as the FMA, the EFIS bus and the VNAV page name it: an ILS; for an RNAV approach what
   * the selected GPS's words permit (gpsApproachAuthority): its level (305: LPV inside the approach region with the FAS
   * block's limits met, LNAV/VNAV with SBAS, LNAV), LNAV once only lateral guidance remains, and NO APPR when the approach
   * may not be flown (GPS phase 3b, the GPS review's GPS-01 and GPS-06).
   */
  get approachType(): "ILS" | "LPV" | "LNAV/VNAV" | "LNAV" | "NO APPR" | null {
    const approach = findProcedure(this.db, this.active, "APPROACH");
    if (!approach) return null;
    if (approach.approachType === "ILS") return "ILS";
    return this.gpsApproachAuthority.annunciation;
  }

  /**
   * What the selected receiver's words permit on the RNAV approach, and the first veto (gpsSensors approachAuthority):
   * lateral guidance on its 116, descent on its 117. Not GPS navigation, or no RNAV approach, permits nothing.
   */
  get gpsApproachAuthority(): ApproachAuthority {
    const approach = findProcedure(this.db, this.active, "APPROACH");
    const rnav = approach?.approachType === "RNAV";
    if (!rnav || this.nav.mode !== "GPS") return { annunciation: "NO APPR", lateral: false, vertical: false, reason: rnav ? "NO GPS NAVIGATION" : "NO RNAV APPROACH" };
    const chosen = this.gpsAssessment.chosen;
    if (!this.gpsSelection.qualified) return { annunciation: "NO APPR", lateral: false, vertical: false, reason: "GPS SOURCE CHANGE NOT QUALIFIED" };
    // Only an approach the data declares LNAV only is flown without a FAS block; a missing or unreadable one is NO APPR.
    return approachAuthority(chosen === null ? null : this.gpsBus(chosen), chosen === null ? null : this.gpsAssessment.assessed[chosen],
      fasRequirement(approach!, this.pinnedFas?.fas ?? null));
  }

  /**
   * The selected receiver's approach words (116, 117, 201, the scaling, 156 and 305) on an RNAV approach in GPS mode;
   * null otherwise. Guidance on the final approach comes from here once the approach is captured (GPS phase 3b).
   */
  get gpsApproach(): GpsApproachWords | null {
    if (findProcedure(this.db, this.active, "APPROACH")?.approachType !== "RNAV" || this.nav.mode !== "GPS") return null;
    const chosen = this.gpsAssessment.chosen;
    return chosen === null ? null : approachWords(this.gpsBus(chosen));
  }

  /** The final approach course of the FAS block sent (LTP to FPAP), degrees true; null without one. */
  get finalApproachCourse() {
    const fas = this.sentFas;
    return fas ? bearingDeg(fas.ltp, { lat: fas.ltp.lat + fas.fpapDelta.lat, lon: fas.ltp.lon + fas.fpapDelta.lon }) : null;
  }

  /** The GPS gives vertical guidance for the RNAV approach: lateral, a vertical level (LPV, LNAV/VNAV) and 117 valid. */
  get gpsApproachVertical() { return this.gpsApproachAuthority.vertical; }

  /** The GPS gives lateral guidance for the RNAV approach: the approach may be flown and its 116 is valid. */
  get gpsApproachLateral() { return this.gpsApproachAuthority.lateral; }

  /** The approach can be captured and flown down its path: an ILS, or an RNAV approach with GPS vertical guidance. */
  get approachVertical() {
    const type = this.approachType;
    return type === "ILS" || (type !== null && type !== "NO APPR" && this.gpsApproachVertical);
  }

  get approachArmed() { return this.armedApproach; }

  /**
   * APPR: arms the approach; it becomes active on the final approach. Refused when the executed approach codes a speed
   * limit below the profile's VMINI (C.5, Q7): the procedure can still be inspected and planned, but not flown coupled.
   * Returns whether the approach is armed.
   */
  armApproach(on = true) {
    if (on && this.approachRefusal) {
      this.advisory("SPD LIMIT BELOW VMINI");
      this.emit();
      return false;
    }
    this.armedApproach = on;
    this.emit();
    return on;
  }

  /** Why the executed approach may not be flown coupled, or null: a procedure speed limit below VMINI (Q7). */
  get approachRefusal(): string | null {
    const approach = findProcedure(this.db, this.active, "APPROACH");
    const lowest = approach ? lowestProcedureLimit(approach) : null;
    const vmini = this.aircraftProfile.parameters.vmini?.value;
    return lowest !== null && vmini !== undefined && lowest < vmini ? `procedure speed limit below profile VMINI (${lowest} < ${vmini} KIAS)` : null;
  }

  /**
   * TOGA or MISSED APPR before the MAP: the missed approach is requested, but lateral guidance continues along the
   * approach to the MAP, which then sequences the missed approach legs (M300 7-16 item 4; plan R2-03 MA-EARLY and
   * TOGA-EARLY). The approach is disarmed (no descent on its path) and the missed-approach hold armed. The laboratory
   * airline profile, whose VNAV climbs on the missed approach legs, drops the rest of the approach at once instead.
   */
  goAround() {
    if (this.injected.has("fmsFail")) return false;
    const route = this.active;
    const missed = route.legs.findIndex(leg => leg.kind !== "disco" && leg.source === "MISSED");
    // Only from the approach: once the missed approach is being flown there is nothing left to go around from.
    if (missed <= 0) return false;
    if (this.aircraftProfile.verticalPolicy !== "ADVISORY") {
      route.legs.splice(0, missed);
      this.legStart = { ...this.here };
    }
    this.armedApproach = false;
    this.goArounds += 1;
    this.armMissedHold(route);
    this.emit();
    return true;
  }

  /** Accepted go-arounds, so the flight simulation takes the go-around transition however TOGA was pressed. */
  goArounds = 0;
  /** The active plan's revision and fingerprint, as the engineering record states them. */
  get planIdentity() { return { revision: this.planRevision, fingerprint: planFingerprint(this.active.legs) }; }

  private armMissedHold(route: Route) {
    const missedHold = findProcedure(this.db, route, "APPROACH")?.missedHold;
    if (!missedHold || route.hold) return;
    // A coded leg distance takes the place of leg time, and a coded speed limit that of the profile's holding speed
    // (C.6, C16). The S300 flies it for one racetrack, then leaves it (MISSED-HOLD).
    const legDistance = missedHold.legDistanceNm ?? null;
    route.hold = {
      fix: missedHold.fix, turn: missedHold.turn, inbound: missedHold.inbound, legTime: legDistance === null ? this.defaultHoldLegTime() : null, legDistance,
      exit: "MANUAL", speed: missedHold.speedLimit?.kt ?? this.defaultHoldSpeed(), altitude: missedHold.altitude, status: "ARMED", missed: true,
    };
    for (const leg of route.legs) if (leg.kind === "wpt" && leg.ident === missedHold.fix) leg.qualifier = "/H";
  }

  /** RNP in force: the entry the crew made, or the default for the phase of flight. */
  get requiredRnp() { return this.nav.rnpManual ?? RNP_DEFAULTS[this.flightPhase].rnp; }
  get navState() { return this.nav; }
  get truePosition() { return this.truth; }
  get inhibitedNavaids() { return this.inhibited; }
  get gpsNavSelected() { return this.gpsSelected; }
  /**
   * RNP and ANP as every consumer reads them: PROGRESS, NAV STATUS, the EFIS, the RNP annunciator and CHECK ANP (R11).
   * The sensor layer is what navigation computes (the phase or crew RNP, the sources' ANP). A bench condition is a
   * second, named layer on top: forced NPA sets the approach RNP of 0.30 NM, forced RNP exceeded sets an ANP above
   * it. `forced` is true when either applies, and the pages label the value TEST so it is not mistaken for a sensor.
   */
  get navPerformance() {
    const forcedNpa = this.injected.has("npa"), forcedAnp = this.injected.has("rnpExceeded");
    const rnp = forcedNpa ? RNP_DEFAULTS.APPROACH.rnp : this.requiredRnp;
    const anp = forcedAnp ? Math.max(1.35, rnp + 0.35) : this.nav.anp;
    return {
      rnp, anp, sensorRnp: this.requiredRnp, sensorAnp: this.nav.anp, forced: forcedNpa || forcedAnp,
      rnpSource: forcedNpa ? "TEST" as const : this.nav.rnpManual === null ? "PHASE" as const : "MANUAL" as const,
    };
  }

  /** Whether ANP has exceeded RNP (the RNP annunciator), on the effective values every page shows. */
  get rnpExceeded() { const { rnp, anp } = this.navPerformance; return anp > rnp; }

  /** The crew's SET POS reference, or null before one is entered (R26). */
  get positionReferenceEntry() { return this.positionReference; }

  /**
   * SET POS on POS INIT (R26). The entry is recorded as the position reference. In dead reckoning, with no sensor to
   * correct it, it also resets the position estimate to the entry, from where heading/TAS/wind DR continues. It never moves
   * the aircraft itself. With GPS or DME navigating, the sensors keep setting the position.
   */
  initializePosition(position: LatLon) {
    this.positionReference = { position, at: this.now };
    if (this.nav.mode === "DR") {
      this.navigation.initialize(position);
      this.here = { ...position };
    }
    this.emit();
  }

  /** A manual RNP (PROGRESS), or null to return to the default for the phase. */
  setRnp(rnp: number | null) {
    this.nav.rnpManual = rnp;
    if (rnp !== null && rnp > RNP_DEFAULTS[this.flightPhase].rnp) this.alert(alert("VERIFY RNP VALUE"));
    this.updateNavigation(0);
  }

  /** NAV OPTIONS: navaids excluded from position updating, and GPS selected in or out. */
  setInhibited(idents: string[]) { this.inhibited = idents.slice(0, 3); this.updateNavigation(0); }

  /** Advances time-driven state: the timer alarms, the call duration and the clocks on the display. */
  tick() {
    const now = this.now.getTime();
    this.watchHover();
    // A database past the end of its cycle is flagged once; swapping to the next cycle clears it.
    // A cycle whose data gives no dates is never out of date: its end is unknown, not past.
    if (this.activeCycle.to !== null && now > this.activeCycle.to && !this.outOfDateAlerted) { this.outOfDateAlerted = true; this.alert(alert("DATABASE OUT OF DATE")); }
    if (this.selfTest.startedAt !== null && this.selfTest.result === null && now - this.selfTest.startedAt >= 5000) {
      const failing = ["fmsFail", "gpsLost", "dmeOutage"].some(id => this.injected.has(id as ConditionId));
      this.selfTest = { ...this.selfTest, result: failing ? "FAIL" : "PASS" };
    }
    if (this.timer.alarmAt !== null && now >= this.timer.alarmAt) { this.timer.alarmAt = null; this.alert(alert("TIMER ALARM")); }
    if (this.timer.countdownEnd !== null && now >= this.timer.countdownEnd) { this.timer.countdownEnd = null; this.alert(alert("TIMER ALARM")); }
    this.emit();
  }

  // ------------------------------------------------------------------ state read by pages

  get route(): Route { return this.modified ?? this.active; }
  get routeStatus(): "ACT" | "MOD" { return this.modified ? "MOD" : "ACT"; }
  get now() { return this.clock(); }
  get position(): LatLon { return this.here; }
  get radioState() { return this.radios; }
  get fuelState() { return this.fuel; }
  get markList() { return this.marks; }
  get recallList() { return this.recall; }
  get squawkIdent() { return this.clock().getTime() < this.squawkIdentUntil; }
  /** The lateral offset the pages show: the modification's if there is one, otherwise the active route's. */
  get lateralOffset() { return this.route.offset; }
  get callState() { return this.call; }
  get smsList() { return this.messages; }
  get uplinks() { return this.uplinkList; }
  get holdEntryFlown() { return this.enteredHold; }
  /** The last waypoint sequenced: once past the FAF, the approach still measures its path from it. */
  get lastSequenced() { return this.sequenced; }

  /**
   * A waypoint's position: a search or tactical point, a Mark On Top, or the navigation database. A runway belongs to
   * an airport, so it resolves in the context of a route: the active route unless a page asks about the modification.
   */
  coordinates(ident: string, route: Route = this.active): LatLon | undefined {
    const pending = this.pendingHoverPoints;
    if (pending && route !== this.active && (ident === "JN" || ident === "TDN" || ident === "MRK")) return pending[ident];
    const own = this.ownPoint(ident);
    if (own) return own;
    // The active plan flies its fixes as they were resolved when it became active (pinActive): a fix it was executed
    // without stays unresolved, even if a later cycle defines it. Only fixes outside the executed plan are looked up.
    if (route === this.active && this.pins.has(ident)) return this.pins.get(ident) ?? undefined;
    return this.lookup(ident, route);
  }

  private ownPoint(ident: string) {
    return this.points[ident] ?? this.marks.find(mark => mark.ident === ident)?.position ?? this.userDb.waypoints.find(w => w.ident === ident)?.position;
  }

  /** A database position in the active cycle, looked up now: a runway in the context of the route's airports. */
  private lookup(ident: string, route: Route): LatLon | undefined {
    if (/^RW\d{2}[LRC]?$/.test(ident)) {
      return (this.db.runway(ident, route.dest) ?? this.db.runway(ident, route.origin) ?? this.db.runway(ident))?.threshold;
    }
    return this.entryFor(ident)?.position;
  }

  /** The database entry an ident means: the only one, the one chosen on SELECT DESIRED WPT, or the nearest. */
  entryFor(ident: string): NavEntry | undefined {
    const entries = this.db.find(ident);
    if (entries.length <= 1) return entries[0];
    const chosen = this.chosen[ident];
    if (chosen !== undefined && entries[chosen]) return entries[chosen];
    return [...entries].sort((a, b) => distanceNm(this.here, a.position) - distanceNm(this.here, b.position))[0];
  }

  get navdb() { return this.db; }
  get pilotWaypoints() { return this.pilot; }
  /** The routes CO ROUTE can load: the user routes, then the database's company routes not shadowed by one. */
  get storedRoutes() { return [...this.userDb.routes, ...this.companyRoutes.filter(c => !this.userDb.routes.some(u => u.name === c.name))]; }
  get userWaypoints() { return this.userDb.waypoints; }
  get userRoutes() { return this.userDb.routes; }
  get userWaypointsFree() { return USER_WAYPOINT_CAPACITY - this.userDb.waypoints.length; }

  private loadUserDatabase() {
    const text = this.userStore.read(this.userScope);
    if (text === null) return;
    const parsed = parseUserDatabase(text);
    if ("errors" in parsed) { this.userDatabaseProblem = `stored user database not read: ${parsed.errors[0]}`; return; }
    this.userDb = parsed.data;
  }

  /** Writes the user database to its store; false (with an advisory) when the store refuses it. */
  private saveUserDatabase(next: UserDatabase) {
    if (this.userDatabaseProblem) { this.advisory("USER DB NOT SAVED"); return false; }
    try { this.userStore.write(this.userScope, serializeUserDatabase(this.userScope, next, this.now)); }
    catch { this.advisory("USER DB NOT SAVED"); return false; }
    this.userDb = next;
    return true;
  }

  /**
   * Stores a fixed user waypoint (NEW USER WPT, CONFIRM). Refused when the ident is not 1-5 letters or digits, is
   * already in use (a navigation database, pilot or user waypoint: the bench does not store duplicate idents, where the
   * CMA allows them with SELECT WPT), or the database is full (460).
   */
  createUserWaypoint(ident: string, position: LatLon): "invalid" | "in-use" | "full" | "not-saved" | undefined {
    if (!/^[A-Z0-9]{1,5}$/.test(ident)) return "invalid";
    if (this.coordinates(ident)) return "in-use";
    if (this.userWaypointsFree <= 0) return "full";
    if (!this.saveUserDatabase({ ...this.userDb, waypoints: [...this.userDb.waypoints, { ident, position: { ...position }, type: "FIXED" }] })) return "not-saved";
    return undefined;
  }

  /** The user database as a versioned document, to save as a file. */
  exportUserDatabase() { return serializeUserDatabase(this.userScope, this.userDb, this.now); }

  /**
   * Imports a user database document: all of it or nothing. A malformed document, a collision with a stored entry of
   * the same ident, or an ident in use elsewhere writes nothing and says why.
   */
  importUserDatabase(text: string): { imported: { waypoints: number; routes: number } } | { refused: string[] } {
    const parsed = parseUserDatabase(text);
    if ("errors" in parsed) return { refused: parsed.errors };
    const merged = mergeUserDatabase(this.userDb, parsed.data);
    const clashes = parsed.data.waypoints.filter(w => !this.userDb.waypoints.some(u => u.ident === w.ident) && this.coordinates(w.ident))
      .map(w => `${w.ident} is already a navigation database or pilot waypoint`);
    const collisions = [...merged.collisions, ...clashes];
    if (collisions.length) return { refused: collisions };
    if (!this.saveUserDatabase(merged.data)) return { refused: ["the store refused the write"] };
    return { imported: { waypoints: merged.addedWaypoints, routes: merged.addedRoutes } };
  }
  get secondary() { return this.secondaryRoute; }
  get selection() { return this.selectPending; }

  /**
   * Loads navigation data as a new INACTIVE cycle, merged over the active cycle's data (a bench file is usually
   * partial), in place of the inactive cycle. Nothing changes unless the data is valid: data with nothing in it, or
   * with a position off the globe, is refused whole. Its dates are the ones the data gives, or unknown. The crew
   * activates it on IDENT (swapCycles).
   */
  loadNavData(data: NavData, source = data.cycle.id): { loaded: string } | { refused: string } {
    if (!data.entries.length && !data.airways.length && !data.procedures.length) return { refused: `${source}: no usable navigation data` };
    const impossible = data.entries.find(e => !onGlobe(e.position) || (e.kind === "airport" && e.runways.some(r => !onGlobe(r.threshold))));
    if (impossible) return { refused: `${source}: impossible position for ${impossible.ident}` };
    const cycle = cycleOf(this.db.merge(data), source);
    const replaced = this.inactiveCycle;
    this.cycles = [this.activeCycle, cycle];
    this.recordDataset(`LOAD ${cycle.id}`, [
      source, "inactive until activated", ...(replaced ? [`replaces ${replaced.id}`] : []),
      cycle.from === null || cycle.to === null ? "dates unknown" : "dates from the data",
    ].join("; "));
    this.emit();
    return { loaded: cycle.id };
  }

  /**
   * Reads an ARINC 424 file and loads it (loadNavData). A file with no usable records, or with an impossible value
   * anywhere in it, is refused whole with the reason, and nothing changes.
   */
  loadArinc424(text: string, source: string, airports?: string[]): ({ loaded: string } & Pick<Arinc424Result, "read" | "skipped" | "errors">) | { refused: string } {
    // A full CIFP is large: airports limits the load to those airports, their procedures and the fixes they use.
    const result = parseArinc424(text, { airports });
    if (result.invalid.length) return { refused: `${source}: ${result.invalid[0]}` };
    if (result.read === 0) return { refused: `${source}: no usable ARINC 424 records${result.skipped ? ` (${result.skipped} lines not recognised)` : ""}` };
    const outcome = this.loadNavData(result.data, source);
    return "refused" in outcome ? outcome : { ...outcome, read: result.read, skipped: result.skipped, errors: result.errors };
  }

  /**
   * Resolves a waypoint entry. A known ident is itself (a duplicate ident needs SELECT DESIRED WPT first). A latitude
   * and longitude (N4530.0W07530.0), a place/bearing/distance (RDG045/10) or a place-bearing/place-bearing
   * (RDG045/MUN090) entry creates a pilot waypoint, named WPTnn or after the place.
   */
  resolveWaypoint(text: string): WaypointResolution {
    if (WAYPOINT.test(text)) {
      if (!this.coordinates(text)) return "not-in-database";
      const own = this.points[text] !== undefined || this.marks.some(mark => mark.ident === text);
      if (!own && this.db.find(text).length > 1 && this.chosen[text] === undefined) return { select: text };
      return { ident: text };
    }
    const latLon = /^([NS])(\d{2})(\d{2}(?:\.\d)?)?([EW])(\d{3})(\d{2}(?:\.\d)?)?$/.exec(text);
    if (latLon) {
      const lat = Number(latLon[2]) + Number(latLon[3] ?? 0) / 60, lon = Number(latLon[5]) + Number(latLon[6] ?? 0) / 60;
      if (lat > 90 || lon > 180 || Number(latLon[3] ?? 0) >= 60 || Number(latLon[6] ?? 0) >= 60) return "invalid";
      return { ident: this.createPilot("WPT", { lat: latLon[1] === "S" ? -lat : lat, lon: latLon[4] === "W" ? -lon : lon }, text) };
    }
    const pbd = /^([A-Z0-9]{2,5})(\d{3})\/(\d{1,3}(?:\.\d)?)$/.exec(text);
    if (pbd) {
      const place = this.coordinates(pbd[1]);
      const bearing = Number(pbd[2]), distance = Number(pbd[3]);
      if (!place) return "not-in-database";
      if (bearing < 1 || bearing > 360 || distance <= 0) return "invalid";
      return { ident: this.createPilot(pbd[1].slice(0, 3), offset(place, bearing, distance), text) };
    }
    const pbpb = /^([A-Z0-9]{2,5})(\d{3})\/([A-Z0-9]{2,5})(\d{3})$/.exec(text);
    if (pbpb) {
      const p1 = this.coordinates(pbpb[1]), p2 = this.coordinates(pbpb[3]);
      if (!p1 || !p2) return "not-in-database";
      const crossing = bearingIntersection(p1, Number(pbpb[2]), p2, Number(pbpb[4]));
      if (!crossing) return "invalid";
      return { ident: this.createPilot(pbpb[1].slice(0, 3), crossing, text) };
    }
    return "invalid";
  }

  /** Stores a pilot waypoint under the next free name for its prefix (WPT01, RDG01...). */
  createPilot(prefix: string, position: LatLon, definition: string) {
    let n = 1;
    let ident = `${prefix}${String(n).padStart(2, "0")}`;
    while (this.coordinates(ident)) { n += 1; ident = `${prefix}${String(n).padStart(2, "0")}`; }
    this.points[ident] = position;
    this.pilot.push({ ident, position, definition });
    return ident;
  }

  /** Resolves a waypoint entry and uses it, going through SELECT DESIRED WPT first for a duplicate ident. */
  enterWaypoint(text: string, apply: (ident: string) => LskResult): LskResult {
    const resolved = this.resolveWaypoint(text);
    if (typeof resolved === "string") return resolved;
    if ("select" in resolved) {
      this.selectPending = { ident: resolved.select, apply, back: { page: this.page, index: this.index } };
      this.open("SELECT_WPT");
      return;
    }
    return apply(resolved.ident);
  }

  /** SELECT DESIRED WPT: the crew picks one of the same-ident entries, and the entry that asked continues. */
  chooseEntry(index: number): LskResult {
    const pending = this.selectPending;
    if (!pending || !this.db.find(pending.ident)[index]) return;
    this.chosen[pending.ident] = index;
    this.selectPending = null;
    this.open(pending.back.page, pending.back.index);
    return pending.apply(pending.ident);
  }

  /** Selects (or with null, removes) a SID, STAR or approach and its transition, rebuilding the route as a MOD. */
  selectProcedure(kind: "SID" | "STAR" | "APPROACH", ident: string | null, transition?: string) {
    this.modify(route => {
      const enroute = enrouteLegs(route);
      const choice = ident ? { ident, transition } : undefined;
      if (kind === "SID") route.sid = choice;
      else if (kind === "STAR") route.star = choice;
      else route.approach = choice;
      route.legs = composeRoute(route, this.db, enroute);
    });
  }

  selectRunway(ident: string) {
    this.modify(route => {
      route.runway = route.runway === ident ? undefined : ident;
      const sid = findProcedure(this.db, route, "SID");
      if (sid && route.runway && !sid.runways.includes(route.runway)) {
        const enroute = enrouteLegs(route);
        route.sid = undefined;
        route.legs = composeRoute(route, this.db, enroute);
      }
    });
  }

  /** Loads a company route into the active route (as a MOD) or into the secondary flight plan. */
  /** The direction SELECT CO ROUTE loads in (LOAD, 5L: M300 3-11). */
  coRouteLoad: "DIRECT" | "INVERSE" = "DIRECT";
  /** What the last CO ROUTE load reported: fixes not in the database (it was refused), or fixes that have moved. */
  routeLoadReport: string[] = [];

  /**
   * Loads a stored route (CO ROUTE). INVERSE (E6; M300 3-10…3-11 define the INV prefix and the toggle, not the leg
   * semantics) follows the nearest S300 rule, BACKTRACK [M300 11-35], and is inferred and labelled: the origin and
   * destination swap, the waypoints are flown in reverse order, every leg as TF with the first as DF (the route
   * before it is not kept), and airways and altitude constraints, which belong to the direction flown, are not
   * carried. A stored route holds no procedures, conditional legs or arcs to reverse.
   *
   * The fixes are resolved by ident at load time in the active database. A fix that is not there is reported and the
   * load refused, never substituted; a fix a user route recorded at another position is reported as moved (it is
   * flown where the database has it now).
   */
  loadCompanyRoute(name: string, target: "active" | "secondary" = "active", direction: "DIRECT" | "INVERSE" = "DIRECT"): boolean {
    const stored = this.storedRoutes.find(route => route.name === name);
    if (!stored) return false;
    // Looked up in the active database, not through the executed plan's pins (a route other than the active one).
    const lookupRoute = { ...this.active };
    const missing = stored.legs.filter(leg => !this.coordinates(leg.ident, lookupRoute)).map(leg => leg.ident);
    if (missing.length) {
      this.routeLoadReport = missing.map(ident => `${ident} NOT IN DATA BASE`);
      this.recordDataset("ROUTE NOT LOADED", `${name}: ${missing.join(", ")} not in ${this.activeCycle.id}; nothing substituted`);
      this.advisory("NOT IN DATA BASE");
      return false;
    }
    const moved = stored.legs.filter(leg => { const now = leg.position && this.coordinates(leg.ident, lookupRoute); return now && leg.position && distanceNm(now, leg.position) > 0.01; })
      .map(leg => leg.ident);
    this.routeLoadReport = moved.map(ident => `${ident} MOVED`);
    if (moved.length) {
      this.recordDataset("ROUTE FIX MOVED", `${name}: ${moved.join(", ")} moved since the route was saved; flown where ${this.activeCycle.id} has them`);
      this.advisory("ROUTE FIX MOVED");
    }
    const inverse = direction === "INVERSE";
    const legs: Leg[] = inverse
      ? [...stored.legs].reverse().map((leg, i) => ({ kind: "wpt" as const, ident: leg.ident, ...(i === 0 ? { path: "DF" as const } : {}) }))
      : stored.legs.map(leg => ({ kind: "wpt" as const, ident: leg.ident, ...(leg.via ? { via: leg.via } : {}), ...(leg.altitude ? { altitude: leg.altitude } : {}) }));
    const build = (route: Route) => {
      route.origin = inverse ? stored.dest : stored.origin;
      route.dest = inverse ? stored.origin : stored.dest;
      route.coRoute = stored.name;
      route.coRouteInverse = inverse || undefined;
      route.sid = route.star = route.approach = undefined;
      route.hold = undefined;
      route.legs = [...legs, { kind: "wpt", ident: route.dest }];
    };
    if (target === "active") this.modify(build);
    else { const route = structuredClone(this.secondaryRoute ?? this.active); build(route); this.secondaryRoute = route; }
    return true;
  }

  /** SAVE: stores the route's enroute legs under its CO ROUTE name, replacing a stored route of that name. */
  saveCompanyRoute() {
    const route = this.route;
    const legs = enrouteLegs(route).flatMap(leg => (leg.kind === "wpt" ? [{ ident: leg.ident, via: leg.via, altitude: leg.altitude }] : []));
    // A user route (E5): kept in the user database for this user and profile, replacing one of that name.
    const saved: StoredRoute = {
      name: route.coRoute, origin: route.origin, dest: route.dest,
      legs: legs.map(leg => {
        const position = this.coordinates(leg.ident);
        return { ident: leg.ident, ...(leg.via ? { via: leg.via } : {}), ...(leg.altitude ? { altitude: leg.altitude } : {}), ...(position ? { position: { ...position } } : {}) };
      }),
    };
    this.saveUserDatabase({ ...this.userDb, routes: [...this.userDb.routes.filter(stored => stored.name !== route.coRoute), saved] });
  }

  copyActiveToSecondary() { this.secondaryRoute = structuredClone({ ...this.active, hold: undefined }); }

  /** ACTIVATE on SEC FPLN: the secondary flight plan becomes a modification of the active route. */
  activateSecondary() {
    const secondary = this.secondaryRoute;
    if (!secondary) return false;
    this.modify(route => { Object.assign(route, structuredClone(secondary), { hold: undefined }); });
    return true;
  }

  /** Where new enroute legs go: before the arrival, the approach and missed approach, or the destination. */
  enrouteEnd(route: Route) {
    const legs = route.legs;
    const arrival = legs.findIndex(leg => leg.kind !== "disco" && (leg.source === "STAR" || leg.source === "APPR" || leg.source === "MISSED"));
    if (arrival >= 0) return arrival;
    const last = legs.at(-1);
    return last?.kind === "wpt" && last.ident === route.dest ? legs.length - 1 : legs.length;
  }

  /** Course and distance into each leg, from present position. There is no computed leg after a discontinuity. */
  legGeometry(route: Route): LegGeometry[] {
    let from: LatLon | null = this.here;
    return route.legs.map(leg => {
      // After a gap or a conditional leg the start of the next leg is not known in advance.
      if (leg.kind !== "wpt") { from = null; return null; }
      const to = this.coordinates(leg.ident, route) ?? null;
      let result = from && to ? { course: courseDeg(from, to), distance: distanceNm(from, to) } : null;
      if (result && from && to && leg.path === "RF" && leg.arc) result = { course: result.course, distance: arcLength(from, to, leg.arc) };
      if (result && leg.path === "CF" && leg.course !== undefined) result = { ...result, course: leg.course };
      from = to;
      return result;
    });
  }

  /** The entry the aircraft will fly (or flew) into the hold, from the track that arrives at the holding fix. */
  holdEntryFor(route: Route): HoldEntry | null {
    const hold = route.hold;
    if (!hold) return null;
    if (hold.status === "IN PROGRESS" || hold.status === "EXIT ARMED") return this.enteredHold;
    const at = route.legs.findIndex(leg => leg.kind === "wpt" && leg.ident === hold.fix);
    // On the active leg, the course of the leg flown into the fix (from where it began): measured from present position
    // it would turn arbitrary as the aircraft reaches the fix, which is exactly when the entry is chosen.
    const fix = at === 0 && route === this.active ? this.coordinates(hold.fix, route) : undefined;
    const flown = route.legs[0];
    const published = flown?.kind === "wpt" && flown.path === "CF" ? flown.course : undefined;
    const track = fix ? published ?? courseDeg(this.legStart, fix) : at >= 0 ? this.legGeometry(route)[at]?.course : undefined;
    return track === undefined ? null : holdEntry(track, hold.inbound, hold.turn);
  }

  // ------------------------------------------------------------------ state changed by pages

  open(page: PageId, index = 0) {
    if (page === "PREDICT_RAIM" && this.predictiveRaim.ident === null) this.predictRaimAt(this.active.dest);
    if (page === "TACT_APPR" && !this.aircraftProfile.configuration.options.tacticalApproach.configured) {
      this.advisory("NOT CONFIGURED");
      return;
    }
    this.page = page;
    this.index = index;
  }

  /** Starts (or continues) a modification of the active route. */
  modify(change: (route: Route) => void) {
    const route = this.modified ?? structuredClone(this.active);
    change(route);
    this.modified = route;
  }

  /** The ERASE prompt: discards the modification; navigation never left the active route. */
  eraseModification() {
    this.modified = null;
    this.discardHoverModification();
    this.sar.pending = null;
    this.directPending = false;
    this.directBypassed = [];
  }

  setScratch(text: string) { this.scratch = text.slice(0, COLUMNS); }
  setRadio(key: keyof ScriptedFms["radios"], value: string) { this.radios[key] = value; }
  setFuel(key: keyof ScriptedFms["fuel"], value: number) { this.fuel[key] = value; }
  /** Enters, changes or (with null) deletes the lateral offset, as a modification to execute. */
  setOffset(change: Partial<Offset> | null) {
    this.modify(route => {
      if (change === null) route.offset = undefined;
      else if (change.nm !== undefined || route.offset) route.offset = { ...(route.offset ?? { nm: 0 }), ...change };
    });
  }

  /** The FAF altitude constraint, on the VNAV page and on the FAF leg of both the active and modified routes. */
  setFafAltitude(altitude: number) {
    this.vnav.fafAltitude = altitude;
    for (const route of [this.active, this.modified]) {
      const runwayAt = route?.legs.findIndex(leg => leg.kind === "wpt" && /^RW\d{2}/.test(leg.ident)) ?? -1;
      const faf = route?.legs[runwayAt - 1];
      if (faf?.kind === "wpt") faf.altitude = `${altitude}A`;
    }
  }
  definePoint(ident: string, position: LatLon) { this.points[ident] = position; }
  advisory(text: string) { this.message = { text, alert: false }; }

  alert(text: string) {
    const message = { text: text.toUpperCase().slice(0, COLUMNS), alert: true };
    this.recall.unshift(message);
    this.message = message;
    this.pendingAlert = message;
  }

  addMark() {
    const ident = `MRK${String(this.marks.length + 1).padStart(2, "0")}`;
    this.marks.push({ ident, position: { ...this.here } });
    return ident;
  }

  // ------------------------------------------------------------------ transition down to hover (M300 11-18…11-22)

  /**
   * Designates the hover mark (HOVER page 1L/1R, M300 A-75): MARK ON TOP (present position), a waypoint by ident, or
   * coordinates. A new mark over an active procedure offers ACTIVATE again (M300 A-76); not while one is being modified.
   * A moving waypoint is refused.
   */
  designateHoverMark(mark: { ident: string; position: LatLon; label: string | null }) {
    if (this.hover.status === "MOD") return false;
    this.hover.mark = mark;
    return true;
  }

  designateHoverMarkOnTop() {
    const ident = this.addMark();
    return this.designateHoverMark({ ident, position: { ...this.here }, label: "MARK ON TOP POS" });
  }

  designateHoverMarkIdent(ident: string) {
    if (ident in this.moving) return false;
    const position = this.coordinates(ident);
    return position ? this.designateHoverMark({ ident, position, label: null }) : false;
  }

  /**
   * ACTIVATE (M300 A-76): only with a valid radio height. The final track at MRK is into the wind, its direction frozen
   * now (below 5 kt of wind, the bearing from the aircraft to MRK, A-75 NOTE). The transition planned from the present
   * state (transition.ts) places TDN before MRK on the reciprocal of the final track; TDN and MRK, both fly-over, go at
   * the head of the modified route, followed by a discontinuity (M300 11-22). Returns why it was refused, or null.
   */
  activateHover(): string | null {
    const mark = this.hover.mark;
    if (!mark) return "NO MARK";
    if (this.hover.status === "MOD" || (this.hover.active && this.hover.active.mark === mark)) return "NOT ALLOWED";
    const ra = this.radioHeight;
    if (ra.status !== "NORMAL") return "RALT FAILED";
    const finalTrack = this.wind.speed >= 5 ? this.wind.direction : courseDeg(this.here, mark.position);
    const plan = planTransition({
      ias: this.afcs?.ias ?? 0, radioHeight: ra.value, verticalSpeed: this.verticalSpeed,
      headwind: this.wind.speed * Math.cos(((this.wind.direction - finalTrack) * Math.PI) / 180),
      hoverHeight: this.afcs?.hoverHeight ?? 50, elevation: this.altitude - ra.value!,
    });
    if (plan.refused) return plan.reason.toUpperCase();
    const tdn = offset(mark.position, finalTrack + 180, plan.dtraNm);
    const join = offset(tdn, finalTrack + 180, JOIN_BEFORE_TDN_NM);
    this.pendingHoverPoints = { JN: join, TDN: tdn, MRK: mark.position };
    // Phase 1: the joining path from here onto the final track, previewed now and rebuilt from the state at EXEC.
    this.pendingJoin = this.joinFromHere(join, finalTrack);
    this.modify(route => {
      const rest = route.legs.filter(leg => !(leg.kind === "wpt" && (leg.ident === "JN" || leg.ident === "TDN" || leg.ident === "MRK")));
      route.legs = [
        // JN: the end of the joining path, on the final course before TDN, arrived at on the final track (Phase 1).
        { kind: "wpt", ident: "JN", path: "CF", course: finalTrack },
        { kind: "wpt", ident: "TDN", qualifier: "/O", path: "CF", course: finalTrack },
        { kind: "wpt", ident: "MRK", qualifier: "/O" },
        { kind: "disco" },
        ...(rest[0]?.kind === "disco" ? rest.slice(1) : rest),
      ];
    });
    Object.assign(this.hover, { status: "MOD", finalTrack, dtra: plan.dtraNm, windSpeed: null, refused: null, refusedReason: null, functionLost: false, requestData: null });
    return null;
  }

  /**
   * Whether inserting a waypoint at `index` of `legs` would put it between JN and TDN, or TDN and MRK, of a hover
   * procedure (MOD or ACT): refused with !HOVER MRK WPT, so the procedure flies JN, TDN and MRK on the final course.
   */
  splitsHover(legs: readonly Leg[], index: number) {
    if (this.hover.status === "NONE") return false;
    const before = legs[index - 1], after = legs[index];
    const pair = (a: string, b: string) => before?.kind === "wpt" && before.ident === a && after?.kind === "wpt" && after.ident === b;
    return pair("JN", "TDN") || pair("TDN", "MRK");
  }

  /**
   * The joining path (joining.ts) from the present position and track to JN on the final track. Its turns are sized for
   * the true airspeed plus the wind speed at the design bank (rate one, capped at the bank limit), as the holds are.
   */
  private joinFromHere(join: LatLon, finalTrack: number): JoinPath {
    const tas = tasFromIas(Math.max(this.afcs?.ias ?? 0, 1), this.altitude);
    const fastest = tas + this.wind.speed;
    const radius = radiusAt(fastest, designBank(fastest, fmsBankLimit(this.aircraftProfile)));
    return joiningPath(this.here, this.track, join, finalTrack, radius);
  }

  /** The joining path the active hover procedure flies to JN (Phase 1), or null when there is none. */
  get hoverJoin() { return this.hover.active?.join ?? null; }
  /** The joining path of a hover modification, for its preview (the map; LEGS shows JN). */
  get hoverJoinPreview() { return this.hover.status === "MOD" ? this.pendingJoin : null; }

  /** CANCEL (M300 A-76): the hover modification is discarded; the active route, and any active procedure, as they were. */
  cancelHover() {
    if (this.hover.status !== "MOD") return false;
    this.eraseModification();
    return true;
  }

  /** A hover modification erased (CANCEL, or the whole modification): the active procedure, if any, as it was. */
  private discardHoverModification() {
    this.pendingHoverPoints = null;
    this.pendingJoin = null;
    if (this.hover.status !== "MOD") return;
    const active = this.hover.active;
    Object.assign(this.hover, active ? { status: "ACT", mark: active.mark, finalTrack: active.finalTrack, dtra: active.dtra } : { status: "NONE" });
  }

  /**
   * At TDN (fly-over): the checks before the transition (M300 E-17; plan T6). More than 0.2 NM off the final track, or
   * more than 20 degrees from it: TDN NOT POSSIBLE. Otherwise the transition is recomputed from the actual state
   * against the fixed MRK: short of it, TDN DIST SHORT (laboratory). Either refusal disables roll steering, and the
   * procedure must be activated again. Otherwise the wind speed is frozen and the request goes to the autopilot.
   */
  private reachTdn() {
    const { id, mark, finalTrack } = this.hover.active!;
    const e = toLocal(mark.position, this.here);
    const crossTrack = e.x * Math.cos((finalTrack * Math.PI) / 180) - e.y * Math.sin((finalTrack * Math.PI) / 180);
    const trackError = Math.abs(((this.track - finalTrack + 540) % 360) - 180);
    if (Math.abs(crossTrack) > 0.2 || trackError > 20) {
      this.hover.refused = "TDN NOT POSSIBLE";
      this.hover.refusedReason = Math.abs(crossTrack) > 0.2 ? "OFF FINAL TRACK" : "TRACK ERROR";
      this.alert(alert("TDN NOT POSSIBLE"));
      return;
    }
    const ra = this.radioHeight;
    const decision = checkAtTdn({
      ias: this.afcs?.ias ?? 0, radioHeight: ra.status === "NORMAL" ? ra.value : null, verticalSpeed: this.verticalSpeed,
      headwind: this.wind.speed * Math.cos(((this.wind.direction - finalTrack) * Math.PI) / 180),
      hoverHeight: this.afcs?.hoverHeight ?? 50, elevation: ra.status === "NORMAL" ? this.altitude - ra.value! : 0,
    }, distanceNm(this.here, mark.position));
    if (!decision.engage) {
      // Only the three library messages reach the crew; the planner reason is kept for the run record. No radio height
      // is TDN FUNCTION LOST (M300 E-16); the recomputed transition not fitting is TDN DIST SHORT; any other refusal
      // (the state at TDN outside the transition limits) is TDN NOT POSSIBLE (E-17).
      const text = decision.reason === "RADIO HEIGHT INVALID" ? "TDN FUNCTION LOST" : decision.reason === "TDN DIST SHORT" ? "TDN DIST SHORT" : "TDN NOT POSSIBLE";
      this.hover.refused = text;
      this.hover.refusedReason = decision.reason;
      this.alert(alert(text));
      return;
    }
    this.hover.windSpeed = this.wind.speed;
    this.hover.requestData = { id, mrk: mark.position, finalTrack };
    this.hover.request += 1;
  }

  /**
   * Keeps the hover procedure honest each step: all radio altimeters failed is TDN FUNCTION LOST (M300 E-16) and
   * withdraws the transition request; TDN and MRK gone from the active route (a direct-to, a new route) end the
   * procedure, which withdraws the request too (the autopilot cancels a retained TD/H toward it).
   */
  private watchHover() {
    const hover = this.hover;
    if (!hover.active) return;
    if (this.radioHeight.status === "FAIL" && !hover.functionLost) {
      hover.functionLost = true;
      hover.requestData = null;
      this.alert(alert("TDN FUNCTION LOST"));
    }
    const inRoute = this.active.legs.some(leg => leg.kind === "wpt" && (leg.ident === "TDN" || leg.ident === "MRK"));
    const atMark = this.lastSequenced === "MRK";
    if (!inRoute && !atMark) Object.assign(hover, { status: hover.status === "MOD" ? "MOD" : "NONE", active: null, requestData: null });
  }

  squawk() { this.squawkIdentUntil = this.clock().getTime() + 18_000; }

  /**
   * DIRECT-TO from present position. A waypoint already in the route deletes the legs before it; any other known
   * waypoint goes first, followed by a route discontinuity, as the manual shows.
   */
  directTo(ident: string): LskResult {
    const at = this.route.legs.findIndex(leg => leg.kind === "wpt" && leg.ident === ident);
    if (at < 0 && !this.coordinates(ident)) return "not-in-database";
    this.directPending = true;
    // A direct-to must not silently lose the points it bypasses (the Cali lesson): they are kept for ABEAM PTS.
    this.directBypassed = at > 0 ? this.route.legs.slice(0, at).flatMap(leg => (leg.kind === "wpt" ? [leg.ident] : [])) : [];
    if (at >= 0) {
      this.modify(route => {
        route.legs.splice(0, at);
        const first = route.legs[0];
        // Direct from present position: whatever path the leg had (a published course, an arc) no longer applies.
        if (first?.kind === "wpt") route.legs[0] = { ...first, path: undefined, course: undefined, arc: undefined };
      });
      return;
    }
    this.modify(route => { route.legs.unshift({ kind: "wpt", ident }, { kind: "disco" }); });
  }

  /** Whether the pending modification is a direct-to, for the INTC CRS and ABEAM PTS prompts. */
  get directModification() { return this.directPending && this.modified !== null; }
  get bypassedByDirect() { return this.directBypassed; }

  /** INTC CRS: fly the entered course into the active waypoint instead of direct from present position. */
  interceptCourse(course: number) {
    this.modify(route => {
      const leg = route.legs[0];
      if (leg?.kind === "wpt") route.legs[0] = { ...leg, path: "CF", course };
    });
  }

  /** ABEAM PTS: each waypoint the direct-to bypassed becomes a point abeam it on the new direct track. */
  abeamPoints() {
    const target = this.route.legs[0];
    const to = target?.kind === "wpt" ? this.coordinates(target.ident) : undefined;
    if (!to || !this.directBypassed.length) return;
    const direct = toLocal(this.here, to);
    const points = this.directBypassed.flatMap(ident => {
      const at = this.coordinates(ident);
      if (!at) return [];
      const p = toLocal(this.here, at);
      const along = (p.x * direct.x + p.y * direct.y) / (direct.x ** 2 + direct.y ** 2);
      if (along <= 0 || along >= 1) return [];
      const position = fromLocal(this.here, { x: direct.x * along, y: direct.y * along });
      return [{ kind: "wpt" as const, ident: this.createPilot(ident.slice(0, 3), position, `ABEAM ${ident}`) }];
    });
    this.modify(route => { route.legs.splice(0, 0, ...points); });
    this.directBypassed = [];
  }

  /** Defines a hold at a fix as a modification. A fix that is not in the route becomes the next waypoint. */
  defineHold(fix: string): LskResult {
    if (!this.coordinates(fix)) return "not-in-database";
    this.modify(route => {
      let at = route.legs.findIndex(leg => leg.kind === "wpt" && leg.ident === fix);
      if (at < 0) { route.legs.unshift({ kind: "wpt", ident: fix }); at = 0; }
      for (const leg of route.legs) if (leg.kind === "wpt" && leg.qualifier === "/H") delete leg.qualifier;
      const leg = route.legs[at];
      if (leg.kind === "wpt") leg.qualifier = "/H";
      // The inbound course defaults to the course of the leg into the fix.
      const inbound = this.legGeometry(route)[at]?.course ?? 360;
      route.hold = { fix, turn: "RIGHT", inbound, legTime: this.defaultHoldLegTime(), legDistance: null, exit: "MANUAL", speed: this.defaultHoldSpeed(), altitude: "5000A", status: "INACTIVE" };
    });
  }

  /** The helicopter defaults to its holding speed limit for the altitude (M300 10-8); the airline profile to 220 kt. */
  private defaultHoldSpeed() {
    if (this.aircraftProfile.verticalPolicy !== "ADVISORY") return 220;
    return holdingSpeedLimit(this.altitude, this.aircraftProfile) ?? this.aircraftProfile.parameters.holdingSpeedHigh.value;
  }

  /** The helicopter's leg time for the altitude (M300 10-9); the airline profile one minute. */
  private defaultHoldLegTime() { return this.aircraftProfile.verticalPolicy === "ADVISORY" ? defaultLegMinutes(this.altitude) : 1.0; }

  /** A coded procedure hold as the route's hold: its coded exit, leg, speed limit (or the default) and altitude. */
  private holdFromProcedure(fix: string, coded: ProcedureHold, status: HoldStatus): Hold {
    const legDistance = coded.legDistanceNm ?? null;
    return {
      fix, turn: coded.turn, inbound: coded.inbound, legDistance, legTime: legDistance === null ? coded.legTimeMin ?? this.defaultHoldLegTime() : null,
      exit: coded.exit === "ONCE" ? "ONCE" : coded.exit === "AT ALT" ? "AT TGT ALT" : "MANUAL",
      speed: coded.speedLimit?.kt ?? this.defaultHoldSpeed(), altitude: coded.altitude ?? "", status,
    };
  }

  /**
   * At a fix crossing in the hold: whether it leaves now. ONCE leaves at the first crossing after the entry (with a
   * direct entry, after one racetrack; after a teardrop or parallel entry, where the entry ends); the missed-approach
   * hold once a whole racetrack has been flown after the entry (MISSED-HOLD); AT TGT ALT at the first crossing with the
   * target altitude reached (within 100 ft, or on the right side of an at-or-above / at-or-below target; never without
   * a target); MANUAL only when the crew arms EXIT HOLD.
   */
  private holdExitReached(hold: Hold) {
    if (hold.missed) return (hold.circuits ?? 0) >= 1;
    if (hold.exit === "ONCE") return true;
    if (hold.exit !== "AT TGT ALT") return false;
    const target = /^(\d+)([AB]?)$/.exec(hold.altitude);
    if (!target) return false;
    const feet = Number(target[1]);
    if (target[2] === "A") return this.altitude >= feet - 100;
    if (target[2] === "B") return this.altitude <= feet + 100;
    return Math.abs(this.altitude - feet) <= 100;
  }

  changeHold(change: (hold: Hold) => void) {
    this.modify(route => { if (route.hold) change(route.hold); });
  }

  eraseHold() {
    this.modify(route => {
      route.hold = undefined;
      for (const leg of route.legs) if (leg.kind === "wpt" && leg.qualifier === "/H") delete leg.qualifier;
    });
  }

  /** ACTIVATE> on a search pattern page: the pattern start goes in the modification, flown direct from PPOS. */
  activateSar(pattern: SarPattern) {
    const reference = (this.sar.refId && this.coordinates(this.sar.refId)) || this.here;
    const start = this.sar.distance ? offset(reference, this.sar.relativeBearing ?? 0, this.sar.distance) : reference;
    const ident = this.sar.id[pattern];
    this.definePoint(ident, start);
    this.modify(route => {
      route.legs = route.legs.filter(leg => !(leg.kind === "wpt" && leg.qualifier === "/S"));
      route.legs.unshift({ kind: "wpt", ident, qualifier: "/S" });
    });
    this.sar.pending = pattern;
    if (this.groundSpeed > maxSarGroundSpeed(this.sar, pattern)) this.alert(alert("HIGH SAR SPEED"));
  }

  /** INTERRUPT> leaves the active search pattern at once and continues with the rest of the route. */
  interruptSar() {
    this.active.legs = this.active.legs.filter(leg => !(leg.kind === "wpt" && leg.qualifier === "/S"));
    if (this.modified) this.modified.legs = this.modified.legs.filter(leg => !(leg.kind === "wpt" && leg.qualifier === "/S"));
    this.sar.active = null;
    this.sar.status = null;
  }

  /** Loads a route as a modification; used by the FMC COMM route uplink and the tactical approach. */
  replaceLegs(legs: Leg[]) { this.modify(route => { route.legs = structuredClone(legs); }); }

  answerCall() { this.call = { ...this.call, state: "active", since: this.now.getTime() }; }
  endCall() { this.call = { state: "none", from: "", since: 0 }; }
  readMessages() { for (const message of this.messages) message.read = true; }

  // ------------------------------------------------------------------ keys

  private emit() {
    this.changes += 1;
    for (const listener of this.listeners) listener();
  }

  private scratchLine(): Line {
    if (this.message) return { left: { text: this.message.text, color: this.message.alert ? "amber" : "white" } };
    return { left: { text: this.scratch, color: "white" } };
  }

  private execute() {
    const route = this.modified;
    if (!route) return;
    // The hover procedure executes only with a valid radio height (M300 E-27: RALT FAILED); the modification stays.
    const hover = this.hover.status === "MOD" && route.legs.some(leg => leg.kind === "wpt" && leg.ident === "TDN");
    if (hover && this.radioHeight.status !== "NORMAL") { this.alert(alert("RALT FAILED")); return; }
    if (hover) {
      const h = this.hover;
      const joinPoint = this.pendingHoverPoints!.JN;
      Object.assign(this.points, this.pendingHoverPoints);
      this.pendingHoverPoints = null;
      this.pendingJoin = null;
      // Committed on EXEC: the joining path rebuilt from the state now, if JN is still the route's next leg (the crew may
      // have deleted it, to vector onto the final with headings).
      const joins = route.legs[0]?.kind === "wpt" && route.legs[0].ident === "JN";
      h.active = { id: ++h.procedures, mark: h.mark!, finalTrack: h.finalTrack!, dtra: h.dtra!, join: joins ? this.joinFromHere(joinPoint, h.finalTrack!) : null };
      Object.assign(h, { status: "ACT", requestData: null, refused: null, refusedReason: null, functionLost: false });
      // The procedure takes the head of the route: a search pattern being flown is interrupted (its /S leg removed).
      if (this.sar.active) this.interruptSar();
      this.alert(alert("TRANSITION DOWN"));
    } else if (this.hover.status === "MOD") {
      // The hover modification edited until it no longer holds TDN: executed as an ordinary route change, the procedure
      // it would have started is dropped, and the active one (if any) goes on.
      this.discardHoverModification();
    }
    this.pendingHoverPoints = null;
    this.pendingJoin = null;
    if (route.hold?.status === "INACTIVE") route.hold.status = "ARMED";
    if (this.sar.pending) { this.sar.active = this.sar.pending; this.sar.status = "ARMED"; this.sar.pending = null; }
    // A new active waypoint, or a direct-to, starts the active leg at present position.
    const first = (legs: Leg[]) => { const leg = legs[0]; return leg?.kind === "wpt" ? leg.ident : null; };
    if (this.directPending || first(route.legs) !== first(this.active.legs)) this.legStart = { ...this.here };
    this.directPending = false;
    this.directBypassed = [];
    this.active = route;
    this.pinActive();
    this.modified = null;
    // In dual operation the executed route is cross-loaded to the other FMS.
    if (!this.injected.has("independent")) this.crossRoute = structuredClone(route);
  }

  private handle(fn: CduFunction, { held }: { held?: boolean }) {
    if (fn.startsWith("CHAR_")) return this.type(fn.slice(5));
    switch (fn) {
      case "SP": return this.type(" ");
      case "SLASH": return this.type("/");
      case "DOT": return this.type(".");
      case "PLUSMINUS": {
        this.clearMessageForEntry();
        const last = this.scratch.at(-1);
        if (last === "-") this.scratch = `${this.scratch.slice(0, -1)}+`;
        else if (last === "+") this.scratch = `${this.scratch.slice(0, -1)}-`;
        else this.type("-");
        return;
      }
      case "CLR": return this.clear(held === true);
      case "BRT": return this.brt();
      case "NEXT": case "PREV": {
        const count = Math.max(1, PAGES[this.page].pages(this));
        this.index = (this.index + (fn === "NEXT" ? 1 : count - 1)) % count;
        return;
      }
      case "EXEC": return this.execute();
      case "MENU": return this.open("MENU");
      case "INIT_REF": return this.open("INIT_REF");
      case "RTE": return this.open("RTE");
      case "DEP_ARR": return this.open("DEP_ARR");
      case "LEGS": return this.open("LEGS");
      case "PROG": return this.open("PROG");
      case "RADIO": return this.open("RADIO");
      case "TPDR": return this.open("RADIO", 1);
      case "FUEL": return this.open("FUEL");
      case "FIX": return this.open("FIX");
      case "VNAV": return this.open("VNAV");
      case "TACT": return this.open("TACT");
      case "ATC": return this.open("ATC");
      case "FMC_COMM": return this.open("FMC_COMM");
      case "MSG": return this.open("MSG_RECALL");
      case "ANS":
        // Item 20: ANS answers an incoming call and hangs up an active one; otherwise it shows the newest SMS.
        if (this.call.state === "ringing") this.answerCall();
        else if (this.call.state === "active") this.endCall();
        else this.readMessages();
        return this.open("ANS");
      case "HOLD":
        // Item 17: with no hold in the plan, LEGS with "/H" in the scratchpad for the holding fix.
        if (this.route.hold) return this.open("HOLD");
        this.open("LEGS");
        this.scratch = "/H";
        this.message = null;
        return;
      case "MARK":
        this.addMark();
        return this.open("PREDEF", 1);
      case "SQK_IDT":
        this.squawk();
        return this.advisory("TPDR IDENT");
    }
    const lsk = /^LSK([1-6])([LR])$/.exec(fn);
    if (lsk) return this.lineSelect(lsk[2] as "L" | "R", Number(lsk[1]));
  }

  private clearMessageForEntry() {
    if (this.message) { this.message = null; if (this.scratch === "DELETE") this.scratch = ""; }
  }

  private type(ch: string) {
    this.clearMessageForEntry();
    if (this.scratch === "DELETE") this.scratch = "";
    if (this.scratch.length < COLUMNS) this.scratch += ch;
  }

  private clear(held: boolean) {
    if (this.message) {
      // CLR clears alert and advisory messages from the scratchpad, which also acknowledges the alert.
      this.message = null;
      this.pendingAlert = null;
      return;
    }
    if (this.scratch === "DELETE") { this.scratch = ""; return; }
    // An unacknowledged alert that typing took off the scratchpad comes back on an empty scratchpad (R13).
    if (!this.scratch && this.pendingAlert) { this.message = this.pendingAlert; return; }
    if (!this.scratch) { this.scratch = "DELETE"; return; }
    this.scratch = held ? "" : this.scratch.slice(0, -1);
  }

  private brt() {
    // Manual item 13: after 5 s idle the first press always brightens; each press then alternates.
    const now = this.clock().getTime();
    if (now - this.lastBrt > 5000) this.brighten = true;
    else this.brighten = !this.brighten;
    this.lastBrt = now;
    this.level = Math.max(1, Math.min(this.maxLevel, this.level + (this.brighten ? 1 : -1)));
  }

  private lineSelect(side: "L" | "R", row: number) {
    const page = PAGES[this.page];
    if (!page.lsk) return;
    const scratch = this.message ? "" : this.scratch;
    const result = page.lsk(this, side, row, scratch, this.index);
    if (result === "invalid") this.advisory("INVALID ENTRY");
    else if (result === "not-in-database") this.advisory("NOT IN DATA BASE");
    else if (result === "not-allowed") this.advisory(scratch === "DELETE" ? "INVALID DELETE" : "NOT ALLOWED");
  }
}
